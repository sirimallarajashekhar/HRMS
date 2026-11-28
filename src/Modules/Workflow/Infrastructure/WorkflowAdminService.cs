using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Contracts.Workflow;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Hrms.Workflow.Application;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Workflow.Infrastructure;

/// <summary>
/// Repairs by an administrator. None of them can be used to approve something oneself: the caller may not manage a workflow they
/// raised or are the subject of, may not give a task to themselves, and the new approver goes through the same exclusions as any other.
/// </summary>
internal sealed class WorkflowAdminService(
    WorkflowDbContext db, WorkflowEngine engine, WorkflowAccess access, WorkflowScopes scopes, InstanceReader reader, ITenantContext tenant) : IWorkflowAdminService
{
    public async Task<Result<PagedList<InstanceSummaryView>>> ListInstancesAsync(
        Guid userId, string? status, string? workflowKey, PageRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var scope = await scopes.ForAsync(userId, Permissions.Workflow.InstanceView, cancellationToken);
        if (scope.IsFailure)
        {
            return Result.Failure<PagedList<InstanceSummaryView>>(scope.Error);
        }

        var query = db.Instances.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Views.TryParse<WorkflowStatus>(status, out var wanted))
            {
                return Result.Failure<PagedList<InstanceSummaryView>>(Error.Validation("workflow.status.invalid", "That status does not exist."));
            }

            query = query.Where(i => i.Status == wanted);
        }

        if (!string.IsNullOrWhiteSpace(workflowKey))
        {
            query = query.Where(i => i.WorkflowKey == workflowKey);
        }

        if (!scope.Value.Everyone)
        {
            var ids = scope.Value.EmployeeIds!;
            query = query.Where(i => ids.Contains(i.SubjectEmployeeId));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Skip(paging.Skip).Take(paging.Size)
            .ToListAsync(cancellationToken);
        return Result.Success(new PagedList<InstanceSummaryView>(rows.ConvertAll(Views.Instance), paging.Number, paging.Size, total));
    }

    public async Task<Result<InstanceView>> ReassignTaskAsync(Guid userId, Guid taskId, AssignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var task = await db.Tasks.SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null)
        {
            return Result.Failure<InstanceView>(Errors.TaskNotFound());
        }

        var managed = await ManagedAsync(userId, task.InstanceId, Errors.TaskNotFound(), cancellationToken);
        if (managed.IsFailure)
        {
            return Result.Failure<InstanceView>(managed.Error);
        }

        var workflow = managed.Value;
        var instance = workflow.Instance;
        if (task.Status != WorkflowTaskStatus.Pending || instance.Status != WorkflowStatus.Pending)
        {
            return Result.Failure<InstanceView>(Errors.NotPending());
        }

        var stageTasks = await engine.StageTasksAsync(instance, cancellationToken);
        var reason = await CheckNewApproverAsync(userId, workflow, stageTasks, request, cancellationToken);
        if (reason.IsFailure)
        {
            return Result.Failure<InstanceView>(reason.Error);
        }

        task.Cancel(engine.Now);
        engine.Log(instance, WorkflowAction.Reassigned, actor: userId, onBehalfOf: request.UserId, taskId: task.Id, comment: reason.Value);
        Give(workflow, request.UserId);
        return await SaveAsync(instance, cancellationToken);
    }

    public async Task<Result<InstanceView>> AssignBlockedAsync(Guid userId, Guid instanceId, AssignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var managed = await ManagedAsync(userId, instanceId, Errors.InstanceNotFound(), cancellationToken);
        if (managed.IsFailure)
        {
            return Result.Failure<InstanceView>(managed.Error);
        }

        var workflow = managed.Value;
        var instance = workflow.Instance;
        if (instance.Status != WorkflowStatus.Blocked)
        {
            return Result.Failure<InstanceView>(NotBlocked());
        }

        var stageTasks = await engine.StageTasksAsync(instance, cancellationToken);
        var reason = await CheckNewApproverAsync(userId, workflow, stageTasks, request, cancellationToken);
        if (reason.IsFailure)
        {
            return Result.Failure<InstanceView>(reason.Error);
        }

        instance.EnterStage(instance.CurrentStage);
        engine.Log(instance, WorkflowAction.Reassigned, actor: userId, onBehalfOf: request.UserId, comment: reason.Value);
        Give(workflow, request.UserId);
        return await SaveAsync(instance, cancellationToken);
    }

    public async Task<Result<InstanceView>> RetryBlockedAsync(Guid userId, Guid instanceId, CancellationToken cancellationToken)
    {
        var managed = await ManagedAsync(userId, instanceId, Errors.InstanceNotFound(), cancellationToken);
        if (managed.IsFailure)
        {
            return Result.Failure<InstanceView>(managed.Error);
        }

        var workflow = managed.Value;
        var instance = workflow.Instance;
        if (instance.Status != WorkflowStatus.Blocked)
        {
            return Result.Failure<InstanceView>(NotBlocked());
        }

        var from = Math.Max(instance.CurrentStage, 1);
        engine.Log(instance, WorkflowAction.Retried, actor: userId);
        await engine.AdvanceAsync(workflow, from, cancellationToken);
        return await SaveAsync(instance, cancellationToken);
    }

    public async Task<Result<PagedList<FailedDeliveryView>>> ListFailedDeliveriesAsync(Guid userId, PageRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var scope = await scopes.ForAsync(userId, Permissions.Workflow.InstanceManage, cancellationToken);
        if (scope.IsFailure)
        {
            return Result.Failure<PagedList<FailedDeliveryView>>(scope.Error);
        }

        var query = from message in db.Outbox.AsNoTracking()
                    join instance in db.Instances.AsNoTracking() on message.InstanceId equals instance.Id
                    where message.ProcessedAt == null && message.Attempts >= WorkflowOutboxMessage.MaxAttempts
                    select new { message, instance };
        if (!scope.Value.Everyone)
        {
            var ids = scope.Value.EmployeeIds!;
            query = query.Where(r => ids.Contains(r.instance.SubjectEmployeeId));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(r => r.message.OccurredAt).ThenBy(r => r.message.Id)
            .Skip(paging.Skip).Take(paging.Size)
            .ToListAsync(cancellationToken);
        var items = rows.ConvertAll(r => new FailedDeliveryView(
            r.message.Id, r.instance.Id, r.instance.WorkflowKey, r.instance.EntityId, r.message.EventType, r.message.Attempts,
            r.message.LastError, r.message.OccurredAt));
        return Result.Success(new PagedList<FailedDeliveryView>(items, paging.Number, paging.Size, total));
    }

    public async Task<Result> RetryDeliveryAsync(Guid userId, Guid deliveryId, CancellationToken cancellationToken)
    {
        var notFound = Error.NotFound("workflow.delivery.not_found", "The delivery does not exist.");
        var message = await db.Outbox.SingleOrDefaultAsync(m => m.Id == deliveryId, cancellationToken);
        var subject = message is null
            ? null
            : await db.Instances.AsNoTracking().Where(i => i.Id == message.InstanceId)
                .Select(i => (Guid?)i.SubjectEmployeeId).SingleOrDefaultAsync(cancellationToken);
        if (message is null || subject is not { } employeeId)
        {
            return Result.Failure(notFound);
        }

        var scope = await scopes.ForAsync(userId, Permissions.Workflow.InstanceManage, cancellationToken);
        if (scope.IsFailure)
        {
            return Result.Failure(scope.Error);
        }

        if (!scope.Value.Covers(employeeId))
        {
            return Result.Failure(notFound);
        }

        var retried = message.Retry(engine.Now);
        if (retried.IsFailure)
        {
            return retried;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            return Result.Failure(Errors.Changed());
        }

        return Result.Success();
    }

    /// <summary>Loads a workflow the caller may repair: in their scope, and neither raised by them nor about them.</summary>
    private async Task<Result<RunningWorkflow>> ManagedAsync(Guid userId, Guid instanceId, Error notFound, CancellationToken cancellationToken)
    {
        var workflow = await engine.LoadAsync(instanceId, cancellationToken);
        if (workflow is null)
        {
            return Result.Failure<RunningWorkflow>(notFound);
        }

        var scope = await scopes.ForAsync(userId, Permissions.Workflow.InstanceManage, cancellationToken);
        if (scope.IsFailure)
        {
            return Result.Failure<RunningWorkflow>(scope.Error);
        }

        if (!scope.Value.Covers(workflow.Instance.SubjectEmployeeId))
        {
            return Result.Failure<RunningWorkflow>(notFound);
        }

        var account = await access.ActiveAccountAsync(userId, cancellationToken);
        return account is null || workflow.Subject.Bars(account)
            ? Result.Failure<RunningWorkflow>(Error.Forbidden(
                "workflow.admin.own_request", "You cannot manage a request you raised or that is about you."))
            : Result.Success(workflow);
    }

    /// <returns>The cleaned reason.</returns>
    private async Task<Result<string>> CheckNewApproverAsync(
        Guid userId, RunningWorkflow workflow, List<WorkflowTask> stageTasks, AssignRequest request, CancellationToken cancellationToken)
    {
        var reason = WorkflowActionLog.CleanComment(request.Reason);
        if (reason.IsFailure)
        {
            return reason;
        }

        if (reason.Value.Length == 0)
        {
            return Invalid("workflow.assign.reason_required", "Say why the approver is being changed.");
        }

        if (request.UserId == userId)
        {
            return Result.Failure<string>(Error.Forbidden("workflow.assign.to_self", "You cannot give an approval to yourself."));
        }

        var account = await access.ActiveAccountAsync(request.UserId, cancellationToken);
        if (account is null)
        {
            return Invalid("workflow.assign.user_not_found", "The user does not exist or is not active.");
        }

        if (workflow.Subject.Bars(account))
        {
            return Result.Failure<string>(Errors.OwnRequest());
        }

        return stageTasks.Any(t => (t.Status == WorkflowTaskStatus.Pending && t.AssigneeUserId == request.UserId) || t.DecidedByUserId == request.UserId)
            ? Invalid("workflow.assign.already_approver", "That user already has a part in this stage.")
            : reason;

        static Result<string> Invalid(string code, string message) => Result.Failure<string>(Error.Validation(code, message));
    }

    private void Give(RunningWorkflow workflow, Guid assignee)
    {
        var task = engine.AddTask(workflow, workflow.Current, assignee, TaskOrigin.Reassignment);
        engine.Emit(workflow.Instance, new WorkflowTaskAssigned(
            tenant.Require().Value, workflow.Instance.Id, task.Id, assignee, task.DueAt, engine.Now));
    }

    private async Task<Result<InstanceView>> SaveAsync(WorkflowInstance instance, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            db.ChangeTracker.Clear();
            return Result.Failure<InstanceView>(Errors.Changed());
        }

        return Result.Success(await reader.ReadAsync(instance, cancellationToken));
    }

    private static Error NotBlocked() => Error.Conflict("workflow.instance.not_blocked", "The workflow is not blocked.");
}
