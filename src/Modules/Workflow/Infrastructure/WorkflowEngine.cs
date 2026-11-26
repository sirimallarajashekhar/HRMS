using System.Text.Json;
using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Contracts.Workflow;
using Hrms.SharedKernel;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Workflow.Infrastructure;

/// <summary>A running workflow with what is needed to move it: its stages and its key's declaration.</summary>
internal sealed record RunningWorkflow(WorkflowInstance Instance, IReadOnlyList<WorkflowStage> Stages, WorkflowKeyDescriptor Descriptor)
{
    public WorkflowStage Stage(int order) => Stages.Single(s => s.StageOrder == order);

    public WorkflowStage Current => Stage(Instance.CurrentStage);

    public ApprovalSubject Subject => new(Instance.SubjectEmployeeId, Instance.RequesterUserId);

    public IReadOnlyDictionary<string, object> Payload => WorkflowPayload.Read(Descriptor, Instance.PayloadJson);
}

/// <summary>
/// The one place a workflow moves forward. User actions, administrator repairs and the deadline job all go through it, so the safety
/// rules hold whichever way a request is pushed. It changes tracked entities and never saves: the caller saves once, so a change, its
/// history and its events commit together or not at all.
/// </summary>
internal sealed class WorkflowEngine(
    WorkflowDbContext db, ApproverResolver resolver, WorkflowKeyCatalogue catalogue, ITenantContext tenantContext, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DateTime Now => time.GetUtcNow().UtcDateTime;

    // From the request, not the instance: an instance created in this unit of work has no tenant stamped yet.
    private Guid TenantId => tenantContext.Require().Value;

    public DateOnly Today => DateOnly.FromDateTime(Now);

    /// <returns>Null when the instance does not exist for the tenant, or its key is no longer declared by any module.</returns>
    public async Task<RunningWorkflow?> LoadAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null || catalogue.Find(instance.WorkflowKey) is not { } descriptor)
        {
            return null;
        }

        return new RunningWorkflow(instance, await StagesAsync(instance.DefinitionId, cancellationToken), descriptor);
    }

    public async Task<IReadOnlyList<WorkflowStage>> StagesAsync(Guid definitionId, CancellationToken cancellationToken) =>
        await db.Stages.AsNoTracking().Where(s => s.DefinitionId == definitionId).OrderBy(s => s.StageOrder).ToListAsync(cancellationToken);

    /// <summary>The tasks of the current stage in the current round, tracked.</summary>
    public async Task<List<WorkflowTask>> StageTasksAsync(WorkflowInstance instance, CancellationToken cancellationToken) =>
        await db.Tasks
            .Where(t => t.InstanceId == instance.Id && t.Round == instance.Round && t.StageOrder == instance.CurrentStage)
            .ToListAsync(cancellationToken);

    public WorkflowActionLog Log(
        WorkflowInstance instance, WorkflowAction action, Guid? actor = null, Guid? onBehalfOf = null, Guid? taskId = null, string? comment = null)
    {
        var entry = instance.Record(action, Now, actor, onBehalfOf, taskId, string.IsNullOrEmpty(comment) ? null : comment);
        db.ActionLog.Add(entry);
        return entry;
    }

    public void Emit(WorkflowInstance instance, IDomainEvent domainEvent)
    {
        var type = domainEvent.GetType();
        db.Outbox.Add(WorkflowOutboxMessage.Create(
            instance.Id, instance.NextEventSequence(), type.Name, JsonSerializer.Serialize(domainEvent, type, Json), domainEvent.OccurredAt));
    }

    /// <summary>
    /// Opens the first stage from <paramref name="fromStage"/> that applies; when none is left, the request is approved. A request is
    /// only ever approved if somebody (or an allowed timeout) approved a stage in this round: as a last line of defence it blocks
    /// otherwise, although a published definition always has a stage without a condition.
    /// </summary>
    public async Task AdvanceAsync(RunningWorkflow workflow, int fromStage, CancellationToken cancellationToken)
    {
        var instance = workflow.Instance;
        var payload = workflow.Payload;
        foreach (var stage in workflow.Stages.Where(s => s.StageOrder >= fromStage))
        {
            instance.EnterStage(stage.StageOrder);
            if (stage.ConditionJson is { } json)
            {
                var condition = StageCondition.Parse(json, workflow.Descriptor);
                var applies = condition.IsSuccess ? condition.Value.Evaluate(payload) : Result.Failure<bool>(condition.Error);
                if (applies.IsFailure)
                {
                    // Never skip a stage because its condition could not be read: that would let a request pass it by.
                    Block(instance, $"The condition of stage {stage.StageOrder} could not be evaluated: {applies.Error.Message}");
                    return;
                }

                if (!applies.Value)
                {
                    Log(instance, WorkflowAction.StageSkipped);
                    continue;
                }
            }

            await OpenStageAsync(workflow, stage, payload, cancellationToken);
            return;
        }

        if (!await AnyStageApprovedAsync(instance, cancellationToken))
        {
            Block(instance, "No stage applied to the request, so nobody has approved it.");
            return;
        }

        instance.Approve(Now);
        Log(instance, WorkflowAction.Completed);
        Emit(instance, new WorkflowApproved(
            TenantId, instance.Id, instance.WorkflowKey, instance.EntityType, instance.EntityId, Now));
    }

    public void Reject(WorkflowInstance instance)
    {
        instance.Reject(Now);
        Log(instance, WorkflowAction.Completed);
        Emit(instance, new WorkflowRejected(
            TenantId, instance.Id, instance.WorkflowKey, instance.EntityType, instance.EntityId, Now));
    }

    public void SupersedePending(IEnumerable<WorkflowTask> tasks)
    {
        foreach (var task in tasks.Where(t => t.Status == WorkflowTaskStatus.Pending))
        {
            task.Supersede(Now);
        }
    }

    /// <summary>Adds a task and records and announces it. A new task of a freshly created instance gets the instance's tenant on save.</summary>
    public WorkflowTask AddTask(RunningWorkflow workflow, WorkflowStage stage, Guid assignee, TaskOrigin origin)
    {
        var instance = workflow.Instance;
        var task = WorkflowTask.Create(instance.Id, stage.StageOrder, instance.Round, assignee, origin, stage.SlaHours, Now);
        db.Tasks.Add(task);
        Log(instance, WorkflowAction.TaskAssigned, onBehalfOf: assignee, taskId: task.Id);
        return task;
    }

    private async Task OpenStageAsync(
        RunningWorkflow workflow, WorkflowStage stage, IReadOnlyDictionary<string, object> payload, CancellationToken cancellationToken)
    {
        var instance = workflow.Instance;
        var found = await resolver.ResolveAsync(stage.Approver, workflow.Subject, payload, Today, cancellationToken);
        var origin = TaskOrigin.Rule;
        var reason = found.Reason;
        if (!found.IsResolved && stage.Fallback is { } fallback)
        {
            found = await resolver.ResolveAsync(fallback, workflow.Subject, payload, Today, cancellationToken);
            origin = TaskOrigin.Fallback;
            reason = $"{reason} Fallback: {found.Reason}";
        }

        if (!found.IsResolved)
        {
            Block(instance, $"Stage {stage.StageOrder}: {reason}");
            return;
        }

        Log(instance, WorkflowAction.StageOpened);
        foreach (var assignee in found.UserIds)
        {
            var task = AddTask(workflow, stage, assignee, origin);
            Emit(instance, new WorkflowTaskAssigned(TenantId, instance.Id, task.Id, assignee, task.DueAt, Now));
        }
    }

    private void Block(WorkflowInstance instance, string reason)
    {
        instance.Block(reason);
        Log(instance, WorkflowAction.Blocked, comment: instance.BlockedReason);
        Emit(instance, new WorkflowBlocked(TenantId, instance.Id, instance.WorkflowKey, instance.CurrentStage, Now));
    }

    private async Task<bool> AnyStageApprovedAsync(WorkflowInstance instance, CancellationToken cancellationToken)
    {
        // The approval that completed the last stage may not be saved yet, so look at the tracked tasks as well as the stored ones.
        var tracked = db.ChangeTracker.Entries<WorkflowTask>()
            .Any(e => e.Entity.InstanceId == instance.Id && e.Entity.Round == instance.Round && e.Entity.Status == WorkflowTaskStatus.Approved);
        return tracked || await db.Tasks.AsNoTracking().AnyAsync(
            t => t.InstanceId == instance.Id && t.Round == instance.Round && t.Status == WorkflowTaskStatus.Approved, cancellationToken);
    }
}
