using Hrms.Contracts;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Identity.Infrastructure;

/// <summary>
/// Seeds the identity data a tenant needs to be usable: the permission catalogue (global), the six default roles with their grants,
/// and the first tenant administrator. Every step is idempotent, and it never removes anything: taking a permission away from a role
/// is a deliberate change, not something a re-run should do. Run it within a tenant context.
/// </summary>
public sealed class TenantIdentitySeeder(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    ITenantContext tenantContext,
    TimeProvider timeProvider)
{
    /// <summary>Adds every catalogued permission code that is not in the permission table yet.</summary>
    public async Task<int> SyncPermissionCatalogueAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Permissions.AsNoTracking().Select(p => p.Code).ToListAsync(cancellationToken);
        var missing = Permissions.All.Except(existing, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        foreach (var code in missing)
        {
            db.Permissions.Add(PermissionDefinition.Create(code).Value);
        }

        await db.SaveChangesAsync(cancellationToken);
        return missing.Length;
    }

    /// <summary>Creates the default roles and grants the current tenant lacks; returns how many roles were created.</summary>
    public async Task<int> SeedDefaultRolesAsync(CancellationToken cancellationToken)
    {
        tenantContext.Require();

        var roles = await db.Roles.ToDictionaryAsync(r => r.Code, StringComparer.Ordinal, cancellationToken);
        var grants = await db.RoleGrants.AsNoTracking().Select(g => new { g.RoleId, g.PermissionCode }).ToListAsync(cancellationToken);
        var created = 0;

        foreach (var definition in DefaultRoles.All)
        {
            if (!roles.TryGetValue(definition.Code, out var role))
            {
                role = Role.Create(definition.Code, definition.Name, definition.Description, isSystem: true, definition.RequiresMfa).Value;
                db.Roles.Add(role);
                roles[definition.Code] = role;
                created++;
            }

            var held = grants.Where(g => g.RoleId == role.Id).Select(g => g.PermissionCode).ToHashSet(StringComparer.Ordinal);
            foreach (var code in definition.PermissionCodes.Where(code => !held.Contains(code)))
            {
                db.RoleGrants.Add(RoleGrant.Create(role.Id, code).Value);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return created;
    }

    /// <summary>
    /// Creates the first administrator: an account that must change its password at first sign-in, holding tenant_admin with
    /// tenant-wide scope. The password must satisfy the tenant policy in force today. Fails if the email is already used.
    /// </summary>
    public async Task<Result<Guid>> CreateTenantAdminAsync(string? email, string? displayName, string password, CancellationToken cancellationToken)
    {
        tenantContext.Require();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        var policy = await db.SecurityPolicies.AsNoTracking().ValidOn(today).Select(p => new { p.MinPasswordLength, p.RequireUppercase, p.RequireLowercase, p.RequireDigit, p.RequireSymbol })
            .FirstOrDefaultAsync(cancellationToken);
        var passwordPolicy = policy is null
            ? PasswordPolicy.Default
            : new PasswordPolicy(policy.MinPasswordLength, policy.RequireUppercase, policy.RequireLowercase, policy.RequireDigit, policy.RequireSymbol);

        var violations = passwordPolicy.Validate(password);
        if (violations.Count > 0)
        {
            return Result.Failure<Guid>(Error.Validation("identity.user.password_policy", string.Join(", ", violations)));
        }

        var normalised = UserAccount.NormaliseEmail(email);
        if (normalised is not null && await db.Users.AsNoTracking().AnyAsync(u => u.Email == normalised, cancellationToken))
        {
            return Result.Failure<Guid>(Error.Conflict("identity.user.email_taken", "An account with that email already exists."));
        }

        var user = UserAccount.Create(email, displayName, passwordHasher.Hash(password), now, mustChangePassword: true);
        if (user.IsFailure)
        {
            return Result.Failure<Guid>(user.Error);
        }

        var adminRole = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Code == DefaultRoles.TenantAdmin, cancellationToken);
        if (adminRole is null)
        {
            return Result.Failure<Guid>(Error.Conflict("identity.role.missing", "Seed the default roles before creating the administrator."));
        }

        var scope = UserRoleScope.Create(user.Value.Id, adminRole.Id, ScopeType.Tenant, scopeValue: null).Value;
        db.Users.Add(user.Value);
        db.UserRoleScopes.Add(scope);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(user.Value.Id);
    }
}
