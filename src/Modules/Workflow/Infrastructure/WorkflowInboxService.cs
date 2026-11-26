using System.Text.Json;
using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Contracts.Workflow;
using Hrms.SharedKernel;
using Hrms.Workflow.Application;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Workflow.Infrastructure;

internal sealed class WorkflowInboxService(
    WorkflowDbContext db, WorkflowEngine engine, WorkflowAccess access, InstanceReader reader, ITenantContext tenant) : IWorkflowInboxService
{
    public async Task<PagedList<TaskView>> ListTasksAsync(Guid userId, bool done, PageRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var empty = new PagedList<TaskView>([], paging.Number, paging.Size, 0);
        if (await access.ActiveAccountAsync(userId, cancellationToken) is not { } account)
        {
            return empty;
        }

        var tasks = db.Tasks.AsNoTracking();
        var instances = db.Instances.AsNoTracking();
        var query = from task in tasks
                    join instance in instances on task.InstanceId equals instance.Id
                    select new { task, instance };

        if (done)
        {
            query = query.Where(r => r.task.Status != WorkflowTaskStatus.Pending
                                     && (r.task.AssigneeUserId == userId || r.task.DecidedByUserId == userId));
        }
        else
        {
            var today = engine.Today;
            var delegations = await access.DelegationsToAsync(userId, today, cancellationToken);
            Guid[] holders = [userId, .. delegations.Select(d => d.DelegatorUserId).Distinct()];
            var myEmployee = account.EmployeeId;

            // The list of holders drives the index; the delegation test then only runs on those rows. Requests the user may never
            // approve are left out, so a delegate does not even see their own.
            query = query.Where(r => r.task.Status == WorkflowTaskStatus.Pending
                                     && holders.Contains(r.task.AssigneeUserId)
                                     && r.instance.RequesterUserId != userId
                                     && (myEmployee == null || r.instance.SubjectEmployeeId != myEmployee)
                                     && (r.task.AssigneeUserId == userId
                                         || db.Delegations.Any(d => d.DelegateUserId == userId
                                                                    && d.DelegatorUserId == r.task.AssigneeUserId
                                                                    && d.RevokedAt == null && d.StartsOn <= today && d.EndsOn >= today
                                                                    && (d.WorkflowKey == string.Empty || d.WorkflowKey == r.instance.WorkflowKey))));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await (done
                ? query.OrderByDescending(r => r.task.DecidedAt).ThenByDescending(r => r.task.Id)
                : query.OrderBy(r => r.task.CreatedAt).ThenBy(r => r.task.Id))
            .Skip(paging.Skip).Take(paging.Size)
            .ToListAsync(cancellationToken);

        var definitionIds = rows.Select(r => r.instance.DefinitionId).Distinct().ToList();
        var names = await db.Stages.AsNoTracking()
            .Where(s => definitionIds.Contains(s.DefinitionId))
            .Select(s => new { s.DefinitionId, s.StageOrder, s.Name })
            .ToDictionaryAsync(s => (s.DefinitionId, s.StageOrder), s => s.Name, cancellationToken);

        var items = rows.ConvertAll(r => Views.Task(r.task, r.instance, names.GetValueOrDefault((r.instance.DefinitionId, r.task.StageOrder), string.Empty), userId));
        return new PagedList<TaskView>(items, paging.Number, paging.Size, total);
    }

    public async Task<Result<TaskDetailView>> GetTaskAsync(Guid userId, Guid taskId, CancellationToken cancellationToken)
    {
        var task = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        var instance = task is null ? null : await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == task.InstanceId, cancellationToken);
        if (task is null || instance is null || await access.ActiveAccountAsync(userId, cancellationToken) is not { } account)
        {
            return Result.Failure<TaskDetailView>(Errors.TaskNotFound());
        }

        var visible = task.DecidedByUserId == userId || task.AssigneeUserId == userId;
        if (!visible && task.Status == WorkflowTaskStatus.Pending && !Subject(instance).Bars(account))
        {
            var delegations = await access.DelegationsToAsync(userId, engine.Today, cancellationToken);
            visible = WorkflowAccess.Holds(userId, delegations, task, instance.WorkflowKey);
        }

        return visible
            ? Result.Success(await DetailAsync(userId, task, instance, cancellationToken))
            : Result.Failure<TaskDetailView>(Errors.TaskNotFound());
    }

    public async Task<Result<TaskDetailView>> ActAsync(Guid userId, Guid taskId, TaskActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var action = request.Action?.Trim().ToUpperInvariant() switch
        {
            "APPROVE" => (WorkflowAction?)WorkflowAction.Approved,
            "REJECT" => WorkflowAction.Rejected,
            "SENDBACK" => WorkflowAction.SentBack,
            "COMMENT" => WorkflowAction.Commented,
            _ => null,
        };
        if (action is not { } chosen)
        {
            return Fail(Error.Validation("workflow.action.invalid", "The action is approve, reject, sendBack or comment."));
        }

        var comment = WorkflowActionLog.CleanComment(request.Comment);
        if (comment.IsFailure)
        {
            return Fail(comment.Error);
        }

        if (chosen == WorkflowAction.Commented && comment.Value.Length == 0)
        {
            return Fail(Error.Validation("workflow.comment.required", "A comment needs some text."));
        }

        var task = await db.Tasks.SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        var workflow = task is null ? null : await engine.LoadAsync(task.InstanceId, cancellationToken);
        if (task is null || workflow is null || await access.ActiveAccountAsync(userId, cancellationToken) is not { } account)
        {
            return Fail(Errors.TaskNotFound());
        }

        var instance = workflow.Instance;
        var delegations = await access.DelegationsToAsync(userId, engine.Today, cancellationToken);
        if (!WorkflowAccess.Holds(userId, delegations, task, instance.WorkflowKey))
        {
            // The person who decided it as a delegate may learn it is closed; anyone else learns nothing.
            return Fail(task.DecidedByUserId == userId ? Errors.NotPending() : Errors.TaskNotFound());
        }

        if (workflow.Subject.Bars(account))
        {
            return Fail(Errors.OwnRequest());
        }

        if (task.Status != WorkflowTaskStatus.Pending || instance.Status != WorkflowStatus.Pending
            || task.Round != instance.Round || task.StageOrder != instance.CurrentStage)
        {
            return Fail(Errors.NotPending());
        }

        Guid? onBehalfOf = task.AssigneeUserId == userId ? null : task.AssigneeUserId;
        if (chosen == WorkflowAction.Commented)
        {
            engine.Log(instance, chosen, userId, onBehalfOf, task.Id, comment.Value);
        }
        else
        {
            var stageTasks = await engine.StageTasksAsync(instance, cancellationToken);
            if (stageTasks.Any(t => t.DecidedByUserId == userId))
            {
                // Otherwise a delegate could supply both approvals of an "everyone must approve" stage.
                return Fail(Error.Conflict("workflow.task.already_decided_by_you", "You have already decided in this stage."));
            }

            await DecideAsync(workflow, task, stageTasks, chosen, userId, onBehalfOf, comment.Value, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Fail(Errors.Changed());
        }

        return Result.Success(await DetailAsync(userId, task, instance, cancellationToken));

        static Result<TaskDetailView> Fail(Error error) => Result.Failure<TaskDetailView>(error);
    }

    public async Task<Result<InstanceView>> GetInstanceAsync(Guid userId, Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null || await access.ActiveAccountAsync(userId, cancellationToken) is null)
        {
            return Result.Failure<InstanceView>(Errors.InstanceNotFound());
        }

        if (!await IsParticipantAsync(userId, instance, cancellationToken))
        {
            var scope = await access.ScopeAsync(userId, Permissions.Workflow.InstanceView, cancellationToken);
            if (scope.IsFailure)
            {
                return Result.Failure<InstanceView>(scope.Error);
            }

            if (!scope.Value.Covers(instance.SubjectEmployeeId))
            {
                return Result.Failure<InstanceView>(Errors.InstanceNotFound());
            }
        }

        return Result.Success(await reader.ReadAsync(instance, cancellationToken));
    }

    public async Task<Result<InstanceView>> WithdrawAsync(Guid userId, Guid instanceId, string? comment, CancellationToken cancellationToken)
    {
        var cleaned = WorkflowActionLog.CleanComment(comment);
        if (cleaned.IsFailure)
        {
            return Result.Failure<InstanceView>(cleaned.Error);
        }

        var workflow = await engine.LoadAsync(instanceId, cancellationToken);
        if (workflow is null || await access.ActiveAccountAsync(userId, cancellationToken) is null)
        {
            return Result.Failure<InstanceView>(Errors.InstanceNotFound());
        }

        var instance = workflow.Instance;
        if (instance.RequesterUserId != userId)
        {
            return Result.Failure<InstanceView>(await IsParticipantAsync(userId, instance, cancellationToken)
                ? Error.Forbidden("workflow.instance.not_requester", "Only the person who raised the request can withdraw it.")
                : Errors.InstanceNotFound());
        }

        if (!instance.IsOpen)
        {
            return Result.Failure<InstanceView>(Error.Conflict("workflow.instance.finished", "The request already has its final decision."));
        }

        var pending = await db.Tasks.Where(t => t.InstanceId == instance.Id && t.Status == WorkflowTaskStatus.Pending).ToListAsync(cancellationToken);
        foreach (var task in pending)
        {
            task.Cancel(engine.Now);
        }

        instance.Withdraw(engine.Now);
        engine.Log(instance, WorkflowAction.Withdrawn, actor: userId, comment: cleaned.Value);
        engine.Emit(instance, new WorkflowWithdrawn(
            tenant.Require().Value, instance.Id, instance.WorkflowKey, instance.EntityType, instance.EntityId, engine.Now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result.Failure<InstanceView>(Errors.Changed());
        }

        return Result.Success(await reader.ReadAsync(instance, cancellationToken));
    }

    private async Task DecideAsync(
        RunningWorkflow workflow,
        WorkflowTask task,
        List<WorkflowTask> stageTasks,
        WorkflowAction action,
        Guid userId,
        Guid? onBehalfOf,
        string comment,
        CancellationToken cancellationToken)
    {
        var instance = workflow.Instance;
        var status = action switch
        {
            WorkflowAction.Approved => WorkflowTaskStatus.Approved,
            WorkflowAction.Rejected => WorkflowTaskStatus.Rejected,
            _ => WorkflowTaskStatus.SentBack,
        };
        task.Decide(status, userId, engine.Now);
        engine.Log(instance, action, userId, onBehalfOf, task.Id, comment);

        if (status == WorkflowTaskStatus.SentBack)
        {
            engine.SupersedePending(stageTasks);
            instance.SendBack();
            engine.Emit(instance, new WorkflowSentBack(
                tenant.Require().Value, instance.Id, instance.WorkflowKey, instance.EntityType, instance.EntityId, engine.Now));
            return;
        }

        switch (StageRules.Evaluate(workflow.Current.Mode, stageTasks))
        {
            case StageResult.Rejected:
                engine.SupersedePending(stageTasks);
                engine.Reject(instance);
                break;
            case StageResult.Approved:
                engine.SupersedePending(stageTasks);
                await engine.AdvanceAsync(workflow, instance.CurrentStage + 1, cancellationToken);
                break;
        }
    }

    private async Task<bool> IsParticipantAsync(Guid userId, WorkflowInstance instance, CancellationToken cancellationToken)
    {
        if (instance.RequesterUserId == userId
            || await db.Tasks.AsNoTracking().AnyAsync(
                t => t.InstanceId == instance.Id && (t.AssigneeUserId == userId || t.DecidedByUserId == userId), cancellationToken))
        {
            return true;
        }

        var delegations = await access.DelegationsToAsync(userId, engine.Today, cancellationToken);
        if (delegations.Count == 0)
        {
            return false;
        }

        var pending = await db.Tasks.AsNoTracking()
            .Where(t => t.InstanceId == instance.Id && t.Status == WorkflowTaskStatus.Pending)
            .ToListAsync(cancellationToken);
        return pending.Any(t => WorkflowAccess.Holds(userId, delegations, t, instance.WorkflowKey));
    }

    private async Task<TaskDetailView> DetailAsync(Guid userId, WorkflowTask task, WorkflowInstance instance, CancellationToken cancellationToken)
    {
        var stageName = await db.Stages.AsNoTracking()
            .Where(s => s.DefinitionId == instance.DefinitionId && s.StageOrder == task.StageOrder)
            .Select(s => s.Name)
            .SingleAsync(cancellationToken);
        return new TaskDetailView(
            Views.Task(task, instance, stageName, userId),
            JsonSerializer.Deserialize<JsonElement>(instance.PayloadJson),
            Views.Camel(instance.Status));
    }

    private static ApprovalSubject Subject(WorkflowInstance instance) => new(instance.SubjectEmployeeId, instance.RequesterUserId);
}

/// <summary>Reads a whole workflow with its tasks and history.</summary>
internal sealed class InstanceReader(WorkflowDbContext db)
{
    public async Task<InstanceView> ReadAsync(WorkflowInstance instance, CancellationToken cancellationToken)
    {
        // A request has at most ten stages per round, so tasks and history stay small enough to return whole.
        var tasks = await db.Tasks.AsNoTracking().Where(t => t.InstanceId == instance.Id)
            .OrderBy(t => t.Round).ThenBy(t => t.StageOrder).ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);
        var history = await db.ActionLog.AsNoTracking().Where(l => l.InstanceId == instance.Id)
            .OrderBy(l => l.Sequence)
            .ToListAsync(cancellationToken);
        return new InstanceView(
            Views.Instance(instance),
            JsonSerializer.Deserialize<JsonElement>(instance.PayloadJson),
            tasks.ConvertAll(Views.InstanceTask),
            history.ConvertAll(Views.History));
    }
}

/// <summary>Mapping from entities to the views the API returns.</summary>
internal static class Views
{
    public static string Camel<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Parses the enum words the API accepts (names only, any case, never numbers).</summary>
    public static bool TryParse<T>(string? text, out T value)
        where T : struct, Enum
    {
        value = default;
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed)
               && Enum.GetNames<T>().Contains(trimmed, StringComparer.OrdinalIgnoreCase)
               && Enum.TryParse(trimmed, ignoreCase: true, out value);
    }

    public static TaskView Task(WorkflowTask task, WorkflowInstance instance, string stageName, Guid viewer) => new(
        task.Id, instance.Id, instance.WorkflowKey, instance.EntityType, instance.EntityId, instance.SubjectEmployeeId, instance.RequesterUserId,
        task.StageOrder, stageName, Camel(task.Status), task.DueAt, task.CreatedAt, task.AssigneeUserId,
        task.AssigneeUserId == viewer ? null : task.AssigneeUserId);

    public static InstanceSummaryView Instance(WorkflowInstance i) => new(
        i.Id, i.WorkflowKey, i.EntityType, i.EntityId, i.SubjectEmployeeId, i.RequesterUserId, Camel(i.Status), i.CurrentStage, i.Round,
        i.BlockedReason, i.CreatedAt, i.CompletedAt, i.DefinitionVersion);

    public static InstanceTaskView InstanceTask(WorkflowTask t) => new(
        t.Id, t.StageOrder, t.Round, t.AssigneeUserId, Camel(t.Status), t.DueAt, t.DecidedByUserId, t.DecidedAt, t.AssigneeUnavailable);

    public static ActionLogView History(WorkflowActionLog l) => new(
        l.Sequence, l.StageOrder, l.Round, Camel(l.Action), l.ActorUserId, l.OnBehalfOfUserId, l.TaskId, l.Comment, l.OccurredAt);

    public static DelegationView Delegation(Delegation d) => new(
        d.Id, d.DelegatorUserId, d.DelegateUserId, d.WorkflowKey.Length == 0 ? null : d.WorkflowKey, d.StartsOn, d.EndsOn, d.RevokedAt);
}
