using Hrms.SharedKernel;

namespace Hrms.Persistence.Auditing;

public enum AuditAction
{
    Created = 1,
    Updated = 2,
    Deleted = 3,

    // Security events (see TenantDbContext.RecordSecurityEvent). They say something happened to an account, not that a field changed.

    /// <summary>The account signed in.</summary>
    SignedIn = 4,

    /// <summary>A wrong password or code was presented and counted against the account.</summary>
    CredentialRefused = 5,

    /// <summary>That wrong attempt locked the account.</summary>
    LockedOut = 6,
}

/// <summary>
/// One change to one record, with its field changes as JSON. Append-only: it is not a <see cref="TenantEntity"/> because it has no
/// update, soft delete or row version, so <see cref="TenantDbContext"/> filters it by tenant explicitly and refuses to change it.
/// </summary>
public sealed class AuditEntry : Entity
{
    public const int EntityTypeMaxLength = 64;
    public const int ClientIdMaxLength = 100;
    public const int IpAddressMaxLength = 45;
    public const int ReasonMaxLength = 500;
    public const int CorrelationIdMaxLength = 64;

    internal AuditEntry(
        TenantId tenantId,
        DateTime occurredAt,
        string entityType,
        Guid recordId,
        string rootType,
        Guid rootId,
        AuditAction action,
        string changes,
        Guid? actorId,
        AuditOrigin origin)
    {
        TenantId = tenantId;
        OccurredAt = occurredAt;
        EntityType = entityType;
        RecordId = recordId;
        RootType = rootType;
        RootId = rootId;
        Action = action;
        Changes = changes;
        ActorId = actorId;
        ActorType = origin.ActorType;
        ClientId = Truncate(origin.ClientId, ClientIdMaxLength);
        IpAddress = Truncate(origin.IpAddress, IpAddressMaxLength);
        Reason = Truncate(origin.Reason, ReasonMaxLength);
        CorrelationId = Truncate(origin.CorrelationId, CorrelationIdMaxLength);
    }

    /// <summary>For the persistence layer only.</summary>
    private AuditEntry() => EntityType = RootType = Changes = string.Empty;

    public TenantId TenantId { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public string EntityType { get; private set; }

    public Guid RecordId { get; private set; }

    public string RootType { get; private set; }

    public Guid RootId { get; private set; }

    public AuditAction Action { get; private set; }

    /// <summary>A JSON array of field changes (see <see cref="AuditFieldChange"/>).</summary>
    public string Changes { get; private set; }

    public AuditActorType ActorType { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ClientId { get; private set; }

    public string? IpAddress { get; private set; }

    public string? Reason { get; private set; }

    public string? CorrelationId { get; private set; }

    private static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}

/// <summary>One field of a change. A sensitive field carries no values, only the fact that it changed.</summary>
public sealed record AuditFieldChange(string Field, object? Old, object? New, bool Sensitive = false);
