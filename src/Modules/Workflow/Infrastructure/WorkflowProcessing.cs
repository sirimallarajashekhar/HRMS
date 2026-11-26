using System.Text.Json;
using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Contracts.Workflow;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hrms.Workflow.Infrastructure;

/// <summary>
/// Applies the deadline action of overdue tasks, once per task, one workflow per transaction: a workflow that fails or changes under
/// our feet is left for the next run and does not hold up the others.
/// </summary>
internal sealed partial class DeadlineProcessor(
    WorkflowDbContext db,
    WorkflowEngine engine,
    ApproverResolver resolver,
    IApproverDirectory directory,
    ITenantContext tenant,
    ILogger<DeadlineProcessor> logger)
{
    public const int BatchSize = 500;

    /// <returns>The number of overdue tasks dealt with.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = engine.Now;
        var due = await db.Tasks.AsNoTracking()
            .Where(t => t.Status == WorkflowTaskStatus.Pending && t.OverdueHandledAt == null && t.DueAt <= now)
            .OrderBy(t => t.DueAt)
            .Select(t => t.InstanceId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var handled = 0;
        foreach (var instanceId in due.Distinct())
        {
            try
            {
                handled += await HandleAsync(instanceId, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone acted at the same moment. Nothing was written; the next run looks again.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex, instanceId);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return handled;
    }

    private async Task<int> HandleAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var workflow = await engine.LoadAsync(instanceId, cancellationToken);
        if (workflow is not { Instance.Status: WorkflowStatus.Pending })
        {
            return 0;
        }

        var instance = workflow.Instance;
        var stage = workflow.Current;
        var now = engine.Now;
        var stageTasks = await engine.StageTasksAsync(instance, cancellationToken);
        var overdue = stageTasks
            .Where(t => t.Status == WorkflowTaskStatus.Pending && t.OverdueHandledAt == null && t.DueAt <= now)
            .OrderBy(t => t.Id)
            .ToList();
        if (overdue.Count == 0)
        {
            return 0;
        }

        var accounts = await directory.GetAccountsAsync([.. overdue.Select(t => t.AssigneeUserId)], cancellationToken);
        var active = accounts.Where(a => a.IsActive).Select(a => a.UserId).ToHashSet();
        foreach (var task in overdue)
        {
            var unavailable = !active.Contains(task.AssigneeUserId);
            task.MarkOverdueHandled(now, unavailable);
            if (unavailable)
            {
                engine.Log(instance, WorkflowAction.AssigneeUnavailable, onBehalfOf: task.AssigneeUserId, taskId: task.Id);
            }
        }

        var action = stage.OnTimeout ?? TimeoutAction.Remind;
        if (action == TimeoutAction.Approve && !workflow.Descriptor.AllowAutoApprove)
        {
            // The module has withdrawn the permission since the version was published.
            action = TimeoutAction.Remind;
        }

        switch (action)
        {
            case TimeoutAction.Escalate:
                await EscalateAsync(workflow, stage, stageTasks, overdue, cancellationToken);
                break;

            case TimeoutAction.Reject:
                overdue[0].Decide(WorkflowTaskStatus.Rejected, null, now);
                engine.Log(instance, WorkflowAction.TimedOutRejected, taskId: overdue[0].Id);
                engine.SupersedePending(stageTasks);
                engine.Reject(instance);
                break;

            case TimeoutAction.Approve:
                foreach (var task in overdue)
                {
                    task.Decide(WorkflowTaskStatus.Approved, null, now);
                    engine.Log(instance, WorkflowAction.TimedOutApproved, taskId: task.Id);
                }

                if (StageRules.Evaluate(stage.Mode, stageTasks) == StageResult.Approved)
                {
                    engine.SupersedePending(stageTasks);
                    await engine.AdvanceAsync(workflow, instance.CurrentStage + 1, cancellationToken);
                }

                break;

            default:
                foreach (var task in overdue)
                {
                    Remind(instance, task);
                }

                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return overdue.Count;
    }

    private async Task EscalateAsync(
        RunningWorkflow workflow, WorkflowStage stage, List<WorkflowTask> stageTasks, List<WorkflowTask> overdue, CancellationToken cancellationToken)
    {
        var instance = workflow.Instance;

        // A task that is itself the result of an escalation is only reminded: escalation does not chain.
        foreach (var task in overdue.Where(t => t.Origin == TaskOrigin.Escalation))
        {
            Remind(instance, task);
        }

        var toHandOn = overdue.Where(t => t.Origin != TaskOrigin.Escalation).ToList();
        if (toHandOn.Count == 0)
        {
            return;
        }

        var found = await resolver.ResolveAsync(stage.Escalation!, workflow.Subject, workflow.Payload, engine.Today, cancellationToken);
        var alreadyIn = stageTasks
            .SelectMany(t => t.Status == WorkflowTaskStatus.Pending ? new Guid?[] { t.AssigneeUserId } : [t.DecidedByUserId])
            .ToHashSet();
        var targets = found.UserIds.Where(id => !alreadyIn.Contains(id)).ToList();
        if (targets.Count == 0)
        {
            // The original approvers keep their tasks, so the request can still be decided.
            foreach (var task in toHandOn)
            {
                engine.Log(
                    instance, WorkflowAction.EscalationFailed, onBehalfOf: task.AssigneeUserId, taskId: task.Id,
                    comment: Trim(found.Reason ?? "Everyone the escalation rule found already has a part in this stage."));
                Remind(instance, task);
            }

            return;
        }

        foreach (var task in toHandOn)
        {
            task.Cancel(engine.Now);
            engine.Log(instance, WorkflowAction.Escalated, onBehalfOf: task.AssigneeUserId, taskId: task.Id);
        }

        foreach (var target in targets)
        {
            var task = engine.AddTask(workflow, stage, target, TaskOrigin.Escalation);
            engine.Emit(instance, new WorkflowTaskEscalated(tenant.Require().Value, instance.Id, task.Id, target, engine.Now));
        }

        static string Trim(string text) =>
            text.Length <= WorkflowActionLog.CommentMaxLength ? text : text[..WorkflowActionLog.CommentMaxLength];
    }

    private void Remind(WorkflowInstance instance, WorkflowTask task)
    {
        engine.Log(instance, WorkflowAction.Reminded, onBehalfOf: task.AssigneeUserId, taskId: task.Id);
        engine.Emit(instance, new WorkflowTaskReminderDue(tenant.Require().Value, instance.Id, task.Id, task.AssigneeUserId, engine.Now));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying the deadline of workflow {InstanceId} failed; it will be tried again")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid instanceId);
}

/// <summary>
/// Delivers outbox messages: a decision goes to the owning module's handler, and every event to the publisher. Delivery is at least
/// once and in order within a workflow. A failure is retried with growing delays and then left for an administrator.
/// </summary>
internal sealed partial class WorkflowOutboxRelay(
    WorkflowDbContext db, IServiceScopeFactory scopes, ITenantContext tenant, TimeProvider time, ILogger<WorkflowOutboxRelay> logger)
{
    public const int BatchSize = 500;

    // How many messages of one workflow can go out in one run.
    private const int MaxPasses = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, Type> EventTypes = typeof(IDomainEvent).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsAssignableTo(typeof(IDomainEvent)))
        .ToDictionary(t => t.Name, StringComparer.Ordinal);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var delivered = 0;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var now = time.GetUtcNow().UtcDateTime;

            // Only the oldest undelivered message of each workflow is eligible, so a stuck one holds back only its own workflow.
            var heads = await db.Outbox
                .Where(m => m.ProcessedAt == null && m.Attempts < WorkflowOutboxMessage.MaxAttempts && m.NextAttemptAt <= now
                            && !db.Outbox.Any(e => e.InstanceId == m.InstanceId && e.ProcessedAt == null && e.Sequence < m.Sequence))
                .OrderBy(m => m.NextAttemptAt).ThenBy(m => m.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            var succeeded = 0;
            foreach (var message in heads)
            {
                if (await DeliverAsync(message, cancellationToken))
                {
                    succeeded++;
                }
            }

            db.ChangeTracker.Clear();
            delivered += succeeded;
            if (succeeded == 0)
            {
                break;
            }
        }

        return delivered;
    }

    private async Task<bool> DeliverAsync(WorkflowOutboxMessage message, CancellationToken cancellationToken)
    {
        var succeeded = false;
        try
        {
            if (!EventTypes.TryGetValue(message.EventType, out var type)
                || JsonSerializer.Deserialize(message.Payload, type, Json) is not IDomainEvent domainEvent)
            {
                throw new InvalidOperationException($"Unknown event type {message.EventType}.");
            }

            // A scope of its own, so the handler's work (and any failure in it) stays apart from the relay's.
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant.Require());
            if (domainEvent is WorkflowDecided decided)
            {
                // With no handler the module would never hear the outcome, so that is a failure, not a success.
                var handler = scope.ServiceProvider.GetServices<IWorkflowDecisionHandler>()
                                  .SingleOrDefault(h => h.WorkflowKey == decided.WorkflowKey)
                              ?? throw new MissingDecisionHandlerException(decided.WorkflowKey);
                await handler.HandleAsync(decided.ToDecision(), cancellationToken);
            }

            await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(domainEvent, cancellationToken);
            message.MarkProcessed(time.GetUtcNow().UtcDateTime);
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex, message.Id, message.EventType, message.Attempts + 1);

            // Only the exception type is stored: a message could carry personal data from the module's own failure.
            message.MarkFailed(ex.GetType().Name, time.GetUtcNow().UtcDateTime);
        }

        await db.SaveChangesAsync(cancellationToken);
        return succeeded;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivering workflow message {MessageId} ({EventType}) failed (attempt {Attempt})")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid messageId, string eventType, int attempt);
}

internal sealed class MissingDecisionHandlerException(string workflowKey)
    : InvalidOperationException($"No decision handler is registered for the workflow '{workflowKey}' in this host.");

/// <summary>
/// Runs deadlines and delivery for every active tenant on a short timer, under a database lock so only one worker instance does it
/// at a time.
/// </summary>
public sealed partial class WorkflowBackgroundService(
    IServiceScopeFactory scopes, IJobLock jobLock, ILogger<WorkflowBackgroundService> logger) : BackgroundService
{
    public const string LockName = "workflow-processing";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over every active tenant. Returns false when another instance holds the lock.</summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var lease = await jobLock.TryAcquireAsync(LockName, cancellationToken);
        if (lease is null)
        {
            return false;
        }

        await using (lease)
        {
            IReadOnlyList<Guid> tenants;
            await using (var outer = scopes.CreateAsyncScope())
            {
                tenants = await outer.ServiceProvider.GetRequiredService<IActiveTenantSource>().ListActiveTenantIdsAsync(cancellationToken);
            }

            foreach (var tenantId in tenants)
            {
                try
                {
                    await ProcessTenantAsync(tenantId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTenantFailed(logger, ex, tenantId);
                }
            }
        }

        return true;
    }

    /// <returns>Overdue tasks dealt with, and messages delivered.</returns>
    public async Task<(int Overdue, int Delivered)> ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(new TenantId(tenantId));

        var overdue = await scope.ServiceProvider.GetRequiredService<DeadlineProcessor>().RunAsync(cancellationToken);
        var delivered = await scope.ServiceProvider.GetRequiredService<WorkflowOutboxRelay>().RunAsync(cancellationToken);
        if (overdue + delivered > 0)
        {
            LogProcessed(logger, tenantId, overdue, delivered);
        }

        return (overdue, delivered);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The workflow processing tick failed; it will try again")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing workflows failed for tenant {TenantId}")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {TenantId}: {Overdue} overdue tasks handled and {Delivered} messages delivered")]
    private static partial void LogProcessed(ILogger logger, Guid tenantId, int overdue, int delivered);
}
