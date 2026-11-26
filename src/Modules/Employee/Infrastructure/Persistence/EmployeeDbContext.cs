using Hrms.Employee.Domain;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Employee.Infrastructure.Persistence;

/// <summary>
/// The Employee module's database. Every entity is tenant-scoped, so the tenant filter, audit stamping and soft delete come from
/// <see cref="TenantDbContext"/>. References to Core masters (location, org unit, grade...) are plain ids without foreign keys: modules
/// never read each other's tables, and they are checked through the Core lookup contract when written.
/// </summary>
public sealed class EmployeeDbContext(
    DbContextOptions<EmployeeDbContext> options,
    ITenantContext tenantContext,
    IActorContext actorContext,
    TimeProvider timeProvider) : TenantDbContext(options, tenantContext, actorContext, timeProvider)
{
    public const string MigrationsHistoryTable = "ef_migrations_history_employee";

    public DbSet<EmployeeRecord> Employees => Set<EmployeeRecord>();

    public DbSet<EmployeeJob> Jobs => Set<EmployeeJob>();

    public DbSet<EmployeePersonal> Personal => Set<EmployeePersonal>();

    public DbSet<EmployeeCodeRule> CodeRules => Set<EmployeeCodeRule>();

    public DbSet<Separation> Separations => Set<Separation>();

    public DbSet<StatutoryId> StatutoryIds => Set<StatutoryId>();

    public DbSet<EmployeeDocument> Documents => Set<EmployeeDocument>();

    public DbSet<EmployeeDevice> Devices => Set<EmployeeDevice>();

    public DbSet<EmployeeConsent> Consents => Set<EmployeeConsent>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void ConfigureEntities(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EmployeeDbContext).Assembly);
}
