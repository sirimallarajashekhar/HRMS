using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Hrms.Identity.Infrastructure.Persistence;

/// <summary>
/// The Identity module database: users, roles, grants, scopes and the tenant security policy, plus the OpenIddict token server stores.
/// The identity entities are tenant-scoped (filter, audit, soft delete, row_version from <see cref="TenantDbContext"/>); the permission
/// catalogue and the OAuth application, authorization, scope and token tables are platform data shared by every tenant.
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

    protected override void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);

        // The token server stores. Renamed from OpenIddict's PascalCase plurals to the snake_case singular the schema uses.
        modelBuilder.UseOpenIddict<Guid>();
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication<Guid>>().ToTable("oauth_application");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization<Guid>>().ToTable("oauth_authorization");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreScope<Guid>>().ToTable("oauth_scope");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken<Guid>>().ToTable("oauth_token");
    }
}
