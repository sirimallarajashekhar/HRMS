using Hrms.Core.Domain.LegalEntities;
using Hrms.Core.Domain.Org;
using Hrms.Core.Domain.Tenants;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure.Persistence;

/// <summary>
/// The Core module's database. Tenant-scoped entities (everything except <see cref="Tenant"/>) get the tenant filter,
/// audit stamping and soft delete from <see cref="TenantDbContext"/>. <see cref="Tenants"/> is deliberately unfiltered
/// (a tenant is the root of isolation): only the tenant directory reads it, never feature code.
/// </summary>
public sealed class CoreDbContext(
    DbContextOptions<CoreDbContext> options,
    ITenantContext tenantContext,
    IActorContext actorContext,
    TimeProvider timeProvider) : TenantDbContext(options, tenantContext, actorContext, timeProvider)
{
    public const string MigrationsHistoryTable = "ef_migrations_history_core";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();

    public DbSet<OrgUnitType> OrgUnitTypes => Set<OrgUnitType>();

    public DbSet<OrgUnit> OrgUnits => Set<OrgUnit>();

    public DbSet<OrgUnitParent> OrgUnitParents => Set<OrgUnitParent>();

    public DbSet<OrgUnitHead> OrgUnitHeads => Set<OrgUnitHead>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Geofence> Geofences => Set<Geofence>();

    public DbSet<Grade> Grades => Set<Grade>();

    public DbSet<Designation> Designations => Set<Designation>();

    public DbSet<CostCentre> CostCentres => Set<CostCentre>();

    public DbSet<EmployeeType> EmployeeTypes => Set<EmployeeType>();

    protected override void ConfigureEntities(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CoreDbContext).Assembly);
}
