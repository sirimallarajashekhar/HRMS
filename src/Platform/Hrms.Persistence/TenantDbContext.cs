using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hrms.Persistence;

/// <summary>
/// Base DbContext for every module. It enforces the tenancy rules in one place so no handler can forget them:
/// <list type="bullet">
/// <item>every <see cref="TenantEntity"/> query is filtered by the current tenant and by soft delete (fails closed: no tenant, no rows);</item>
/// <item>on save, tenant_id and the audit columns are stamped, a row of another tenant is refused, and deletes become soft deletes;</item>
/// <item>row_version is a concurrency token bumped on every update;</item>
/// <item>tables and columns are snake_case, DateTime is UTC only, and every decimal must declare its precision (no silent rounding).</item>
/// </list>
/// Derived contexts override <see cref="ConfigureEntities"/> (never OnModelCreating) so the conventions run after their configuration.
/// If they override ConfigureConventions they must call the base method.
/// </summary>
public abstract class TenantDbContext(
    DbContextOptions options,
    ITenantContext tenantContext,
    IActorContext actorContext,
    TimeProvider timeProvider) : DbContext(options)
{
    /// <summary>The tenant the filters compare against. Empty when none is resolved, which no row can match.</summary>
    protected TenantId CurrentTenantId => tenantContext.TenantId ?? default;

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureEntities(modelBuilder);
        ApplyConventions(modelBuilder);
    }

    /// <summary>Map the module's entities here (tables, keys, indexes). Tenancy and naming conventions are applied afterwards.</summary>
    protected virtual void ConfigureEntities(ModelBuilder modelBuilder)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdConverter>();
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenantAndAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenantAndAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyConventions(ModelBuilder modelBuilder)
    {
        var undeclaredDecimals = new List<string>();

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            // Only names still at their convention default are renamed, so an explicit ToTable/HasColumnName always wins.
            if (entityType.BaseType is null
                && entityType.GetTableName() is not null
                && IsConventionDefault(((IConventionEntityType)entityType).GetTableNameConfigurationSource()))
            {
                entityType.SetTableName(SnakeCase.From(entityType.ClrType.Name));
            }

            foreach (var property in entityType.GetProperties())
            {
                if (IsConventionDefault(((IConventionProperty)property).GetColumnNameConfigurationSource()))
                {
                    property.SetColumnName(SnakeCase.From(property.Name));
                }

                if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                {
                    if (property.GetPrecision() is null)
                    {
                        undeclaredDecimals.Add($"{entityType.DisplayName()}.{property.Name}");
                    }
                }
            }

            if (entityType.BaseType is null && entityType.ClrType.IsAssignableTo(typeof(TenantEntity)))
            {
                typeof(TenantDbContext)
                    .GetMethod(nameof(ConfigureTenantEntity), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [modelBuilder]);
            }
        }

        if (undeclaredDecimals.Count > 0)
        {
            throw new InvalidOperationException(
                "Every decimal property must declare its precision and scale (HasPrecision); MySQL would otherwise round silently. Missing: "
                + string.Join(", ", undeclaredDecimals));
        }
    }

    private static bool IsConventionDefault(ConfigurationSource? source) => source is null or ConfigurationSource.Convention;

    private void ConfigureTenantEntity<T>(ModelBuilder modelBuilder)
        where T : TenantEntity
    {
        var entity = modelBuilder.Entity<T>();
        entity.Property(e => e.Id).ValueGeneratedNever();
        entity.Property(e => e.RowVersion).IsConcurrencyToken();
        entity.HasQueryFilter(e => !e.IsDeleted && e.TenantId == CurrentTenantId);
    }

    private void StampTenantAndAudit()
    {
        var entries = ChangeTracker.Entries<TenantEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var tenantId = tenantContext.Require();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var actorId = actorContext.ActorId;

        foreach (var entry in entries)
        {
            var entityName = entry.Metadata.ClrType.Name;
            var owner = entry.Property(e => e.TenantId);

            if (entry.State == EntityState.Added)
            {
                if (owner.CurrentValue != default && owner.CurrentValue != tenantId)
                {
                    throw new CrossTenantWriteException(entityName);
                }

                owner.CurrentValue = tenantId;
                entry.Property(e => e.CreatedAt).CurrentValue = now;
                entry.Property(e => e.CreatedBy).CurrentValue = actorId;
                entry.Property(e => e.RowVersion).CurrentValue = 1;
                continue;
            }

            // Modified or Deleted: the row must already belong to the current tenant, and its owner can never change.
            if (owner.OriginalValue != tenantId || owner.CurrentValue != tenantId)
            {
                throw new CrossTenantWriteException(entityName);
            }

            entry.Property(e => e.CreatedAt).IsModified = false;
            entry.Property(e => e.CreatedBy).IsModified = false;

            if (entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified;
                entry.Property(e => e.IsDeleted).CurrentValue = true;
            }

            entry.Property(e => e.UpdatedAt).CurrentValue = now;
            entry.Property(e => e.UpdatedBy).CurrentValue = actorId;
            var version = entry.Property(e => e.RowVersion);
            version.CurrentValue = version.OriginalValue + 1;
        }
    }

    private sealed class TenantIdConverter()
        : ValueConverter<TenantId, Guid>(v => v.Value, v => new TenantId(v));
}
