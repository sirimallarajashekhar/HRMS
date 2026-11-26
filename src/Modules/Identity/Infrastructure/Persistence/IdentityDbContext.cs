using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Identity.Infrastructure.Persistence;

/// <summary>
/// The Identity module database: users, roles, grants, scopes and the tenant security policy. Everything is tenant-scoped
/// (filter, audit, soft delete, row_version from <see cref="TenantDbContext"/>) except <see cref="Permissions"/>, the global catalogue.
/// </summary>
public sealed class IdentityDbContext(
    DbContextOptions<IdentityDbContext> options,
    ITenantContext tenantContext,
    IActorContext actorContext,
    TimeProvider timeProvider) : TenantDbContext(options, tenantContext, actorContext, timeProvider)
{
    public const string MigrationsHistoryTable = "ef_migrations_history_identity";

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RoleGrant> RoleGrants => Set<RoleGrant>();

    public DbSet<UserRoleScope> UserRoleScopes => Set<UserRoleScope>();

    public DbSet<TenantSecurityPolicy> SecurityPolicies => Set<TenantSecurityPolicy>();

    /// <summary>The global permission catalogue; deliberately not tenant-scoped.</summary>
    public DbSet<PermissionDefinition> Permissions => Set<PermissionDefinition>();

    protected override void ConfigureEntities(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
}
