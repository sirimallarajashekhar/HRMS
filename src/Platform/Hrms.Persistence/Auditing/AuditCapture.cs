using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Hrms.Persistence.Auditing;

/// <summary>Turns the pending changes of audited entities into audit entries, to be saved together with them.</summary>
internal static class AuditCapture
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Bookkeeping every tenant entity has; the entry's own columns say who, when and what happened.</summary>
    private static readonly HashSet<string> Bookkeeping =
    [
        nameof(Entity.Id),
        nameof(TenantEntity.TenantId),
        nameof(TenantEntity.CreatedAt),
        nameof(TenantEntity.CreatedBy),
        nameof(TenantEntity.UpdatedAt),
        nameof(TenantEntity.UpdatedBy),
        nameof(TenantEntity.IsDeleted),
        nameof(TenantEntity.RowVersion),
    ];

    public static bool IsAudited(Type type) => type.GetCustomAttribute<AuditedAttribute>(inherit: true) is not null;

    /// <summary>Call after tenant and audit stamping, so a delete has already become a soft delete.</summary>
    public static List<AuditEntry> Capture(ChangeTracker tracker, TenantId tenantId, DateTime now, IActorContext actor)
    {
        var entries = new List<AuditEntry>();

        foreach (var entry in tracker.Entries<TenantEntity>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var audited = entry.Metadata.ClrType.GetCustomAttribute<AuditedAttribute>(inherit: true);
            if (audited is null)
            {
                continue;
            }

            var action = ActionOf(entry);
            var changes = FieldChanges(entry, action);

            // An update that touched only ignored fields is not a change worth a row; a creation or deletion always is.
            if (action == AuditAction.Updated && changes.Count == 0)
            {
                continue;
            }

            var entityType = entry.Metadata.GetTableName()
                             ?? throw new InvalidOperationException($"The audited entity {entry.Metadata.DisplayName()} has no table.");
            var (rootType, rootId) = RootOf(entry, audited, entityType);

            entries.Add(new AuditEntry(
                tenantId, now, entityType, entry.Entity.Id, rootType, rootId, action,
                JsonSerializer.Serialize(changes, Json), actor.ActorId, actor.Origin));
        }

        return entries;
    }

    /// <summary>A security event about <paramref name="subject"/>. Its details use the shape of field changes, with no old values.</summary>
    public static AuditEntry Event(
        EntityEntry<TenantEntity> subject,
        AuditAction action,
        IReadOnlyList<AuditFieldChange> details,
        Guid? actorId,
        AuditOrigin origin,
        TenantId tenantId,
        DateTime now)
    {
        var audited = subject.Metadata.ClrType.GetCustomAttribute<AuditedAttribute>(inherit: true)!;
        var entityType = subject.Metadata.GetTableName()!;
        var (rootType, rootId) = RootOf(subject, audited, entityType);

        return new AuditEntry(
            tenantId, now, entityType, subject.Entity.Id, rootType, rootId, action,
            JsonSerializer.Serialize(details, Json), actorId, origin);
    }

    private static AuditAction ActionOf(EntityEntry<TenantEntity> entry)
    {
        if (entry.State == EntityState.Added)
        {
            return AuditAction.Created;
        }

        var deleted = entry.Property(e => e.IsDeleted);
        return deleted.CurrentValue && !deleted.OriginalValue ? AuditAction.Deleted : AuditAction.Updated;
    }

    private static List<AuditFieldChange> FieldChanges(EntityEntry<TenantEntity> entry, AuditAction action)
    {
        var changes = new List<AuditFieldChange>();
        if (action == AuditAction.Deleted)
        {
            return changes;
        }

        foreach (var property in entry.Properties)
        {
            // Shadow properties are database plumbing (generated columns), not something a person changed.
            var member = property.Metadata.PropertyInfo;
            if (member is null || Bookkeeping.Contains(property.Metadata.Name) || member.IsDefined(typeof(AuditIgnoreAttribute), inherit: true))
            {
                continue;
            }

            var created = action == AuditAction.Created;
            var unchanged = created
                ? property.CurrentValue is null
                : property.Metadata.GetValueComparer().Equals(property.OriginalValue, property.CurrentValue);
            if (unchanged)
            {
                continue;
            }

            var field = JsonNamingPolicy.CamelCase.ConvertName(property.Metadata.Name);
            changes.Add(member.IsDefined(typeof(AuditSensitiveAttribute), inherit: true)
                ? new AuditFieldChange(field, null, null, Sensitive: true)
                : new AuditFieldChange(field, created ? null : property.OriginalValue, property.CurrentValue));
        }

        return changes;
    }

    private static (string Type, Guid Id) RootOf(EntityEntry<TenantEntity> entry, AuditedAttribute audited, string entityType)
    {
        if (audited.RootType is null && audited.RootKey is null)
        {
            return (entityType, entry.Entity.Id);
        }

        if (audited.RootType is null || audited.RootKey is null || entry.Property(audited.RootKey).CurrentValue is not Guid rootId)
        {
            throw new InvalidOperationException(
                $"{entry.Metadata.DisplayName()}: an audit root needs both RootType and RootKey, and the key must hold an id.");
        }

        return (audited.RootType, rootId);
    }
}
