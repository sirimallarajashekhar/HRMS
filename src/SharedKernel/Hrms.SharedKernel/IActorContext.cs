namespace Hrms.SharedKernel;

/// <summary>Who is performing the operation, for the created_by / updated_by audit columns.</summary>
public interface IActorContext
{
    /// <summary>The acting user, or null for the system (background jobs, seeding) until authentication arrives in P0.4.</summary>
    Guid? ActorId { get; }

    /// <summary>What the audit log records about the caller besides <see cref="ActorId"/>. The system unless an implementation says otherwise.</summary>
    AuditOrigin Origin => AuditOrigin.System;
}

/// <summary>Default actor: the system.</summary>
public sealed class SystemActorContext : IActorContext
{
    public Guid? ActorId => null;
}
