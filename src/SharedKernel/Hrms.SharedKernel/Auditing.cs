namespace Hrms.SharedKernel;

/// <summary>
/// Changes to this entity are written to the audit log. Every mapped property is recorded with its values unless it says otherwise
/// (<see cref="AuditSensitiveAttribute"/>, <see cref="AuditIgnoreAttribute"/>); an architecture test lists the properties recorded in
/// the clear, so a new one fails the build until someone has decided how it is logged.
/// </summary>
/// <remarks>
/// A record that belongs to another (an employee's job belongs to the employee) names that owner, so the owner's history includes it.
/// Without one the record is its own root.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class AuditedAttribute : Attribute
{
    /// <summary>The entity type (table name) of the owning record, or null when this record is its own root.</summary>
    public string? RootType { get; init; }

    /// <summary>The property holding the owning record's id. Required with <see cref="RootType"/>.</summary>
    public string? RootKey { get; init; }
}

/// <summary>The audit log records that this value changed, and neither the old nor the new value.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class AuditSensitiveAttribute : Attribute;

/// <summary>The audit log does not mention this property: secrets, and bookkeeping that would only be noise.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class AuditIgnoreAttribute : Attribute;

public enum AuditActorType
{
    /// <summary>A background job, seeding or a command-line task.</summary>
    System = 1,

    /// <summary>A signed-in person.</summary>
    User = 2,

    /// <summary>An integration signed in with its own client credentials.</summary>
    Client = 3,

    /// <summary>A request without a signed-in caller, such as a sign-in attempt.</summary>
    Anonymous = 4,
}

/// <summary>Where a change came from, beyond the acting user's id.</summary>
public sealed record AuditOrigin(
    AuditActorType ActorType,
    string? ClientId = null,
    string? IpAddress = null,
    string? Reason = null,
    string? CorrelationId = null)
{
    public static AuditOrigin System { get; } = new(AuditActorType.System);
}
