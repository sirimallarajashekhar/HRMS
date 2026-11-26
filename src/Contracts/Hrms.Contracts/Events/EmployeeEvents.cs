namespace Hrms.Contracts.Events;

/// <summary>A fact that happened in one tenant. Events carry ids and dates only, never names or other personal data.</summary>
public interface IDomainEvent
{
    Guid TenantId { get; }

    DateTime OccurredAt { get; }
}

/// <summary>An employee record was created (published when it is created).</summary>
public sealed record EmployeeCreated(Guid TenantId, Guid EmployeeId, Guid LegalEntityId, DateOnly JoiningDate, DateTime OccurredAt) : IDomainEvent;

/// <summary>
/// An employee's job (entity, location, unit, designation, grade, type, manager or cost centre) changed. Published on the day the
/// change takes effect, not when it was entered, so a future-dated promotion announces itself on its effective date.
/// </summary>
public sealed record EmployeeJobChanged(Guid TenantId, Guid EmployeeId, Guid JobId, DateOnly EffectiveFrom, string Reason, DateTime OccurredAt) : IDomainEvent;

/// <summary>An employee's last working day has passed (published the day after it, when they are no longer employed).</summary>
public sealed record EmployeeSeparated(Guid TenantId, Guid EmployeeId, DateOnly LastWorkingDay, DateTime OccurredAt) : IDomainEvent;

/// <summary>
/// Delivers events to other modules. The default implementation only logs; a message-broker implementation replaces it when the first
/// consumer that lives in another process exists. Delivery is at least once, so consumers must be idempotent.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}
