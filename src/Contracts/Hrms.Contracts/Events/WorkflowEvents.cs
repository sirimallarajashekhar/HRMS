using Hrms.Contracts.Workflow;

namespace Hrms.Contracts.Events;

/// <summary>Someone has a new approval task.</summary>
public sealed record WorkflowTaskAssigned(Guid TenantId, Guid InstanceId, Guid TaskId, Guid AssigneeUserId, DateTime? DueAt, DateTime OccurredAt) : IDomainEvent;

/// <summary>An overdue task was handed on; <paramref name="TaskId"/> is the new task.</summary>
public sealed record WorkflowTaskEscalated(Guid TenantId, Guid InstanceId, Guid TaskId, Guid AssigneeUserId, DateTime OccurredAt) : IDomainEvent;

/// <summary>A task passed its deadline and the stage asks for a reminder.</summary>
public sealed record WorkflowTaskReminderDue(Guid TenantId, Guid InstanceId, Guid TaskId, Guid AssigneeUserId, DateTime OccurredAt) : IDomainEvent;

/// <summary>A stage has nobody who may approve it; an administrator has to act.</summary>
public sealed record WorkflowBlocked(Guid TenantId, Guid InstanceId, string WorkflowKey, int Stage, DateTime OccurredAt) : IDomainEvent;

/// <summary>
/// A request was approved, rejected, sent back or withdrawn. The engine also hands each of these to the owning module's
/// <see cref="IWorkflowDecisionHandler"/>. Ids only, as with every event.
/// </summary>
public abstract record WorkflowDecided(
    Guid TenantId, Guid InstanceId, string WorkflowKey, string EntityType, Guid EntityId, DateTime OccurredAt) : IDomainEvent
{
    public abstract WorkflowOutcome Outcome { get; }

    public WorkflowDecision ToDecision() => new(TenantId, InstanceId, WorkflowKey, EntityType, EntityId, Outcome, OccurredAt);
}

public sealed record WorkflowApproved(Guid TenantId, Guid InstanceId, string WorkflowKey, string EntityType, Guid EntityId, DateTime OccurredAt)
    : WorkflowDecided(TenantId, InstanceId, WorkflowKey, EntityType, EntityId, OccurredAt)
{
    public override WorkflowOutcome Outcome => WorkflowOutcome.Approved;
}

public sealed record WorkflowRejected(Guid TenantId, Guid InstanceId, string WorkflowKey, string EntityType, Guid EntityId, DateTime OccurredAt)
    : WorkflowDecided(TenantId, InstanceId, WorkflowKey, EntityType, EntityId, OccurredAt)
{
    public override WorkflowOutcome Outcome => WorkflowOutcome.Rejected;
}

public sealed record WorkflowSentBack(Guid TenantId, Guid InstanceId, string WorkflowKey, string EntityType, Guid EntityId, DateTime OccurredAt)
    : WorkflowDecided(TenantId, InstanceId, WorkflowKey, EntityType, EntityId, OccurredAt)
{
    public override WorkflowOutcome Outcome => WorkflowOutcome.SentBack;
}

public sealed record WorkflowWithdrawn(Guid TenantId, Guid InstanceId, string WorkflowKey, string EntityType, Guid EntityId, DateTime OccurredAt)
    : WorkflowDecided(TenantId, InstanceId, WorkflowKey, EntityType, EntityId, OccurredAt)
{
    public override WorkflowOutcome Outcome => WorkflowOutcome.Withdrawn;
}
