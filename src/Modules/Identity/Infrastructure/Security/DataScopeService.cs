using Hrms.Contracts;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>
/// Resolves a user data scope for a permission from their role scopes. A scope counts only when the role that carries it grants the
/// permission, the role and the grant are live, and the account is active. Everything reads through the tenant-filtered context, so a
/// user can only ever hold scopes of their own tenant.
/// </summary>
public sealed class DataScopeService(IdentityDbContext db, IEmployeeScopeExpander expander) : IDataScopeService
{
    public async Task<DataScope> ResolveAsync(Guid userId, string permission, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        var active = await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken);
        if (!active)
        {
            return DataScope.None(userId);
        }

        var rows = await db.UserRoleScopes.AsNoTracking()
            .Where(scope => scope.UserId == userId)
            .Join(db.Roles.AsNoTracking(), scope => scope.RoleId, role => role.Id, (scope, role) => new { scope.RoleId, scope.ScopeType, scope.ScopeValue })
            .Join(
                db.RoleGrants.AsNoTracking().Where(grant => grant.PermissionCode == permission),
                row => row.RoleId,
                grant => grant.RoleId,
                (row, grant) => new { row.ScopeType, row.ScopeValue })
            .Distinct()
            .ToListAsync(cancellationToken);

        var grants = rows
            .Select(row => new DataScopeGrant(Map(row.ScopeType), row.ScopeValue == Guid.Empty ? null : row.ScopeValue))
            .ToList();
        return new DataScope(userId, grants);
    }

    public async Task<IReadOnlySet<Guid>> GetVisibleEmployeeIdsAsync(Guid userId, string permission, CancellationToken cancellationToken)
    {
        var scope = await ResolveAsync(userId, permission, cancellationToken);
        if (scope.IsEmpty)
        {
            return new HashSet<Guid>();
        }

        var employeeId = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.EmployeeId).SingleOrDefaultAsync(cancellationToken);
        return await expander.ExpandAsync(employeeId, scope.Grants, cancellationToken);
    }

    private static ScopeKind Map(ScopeType type) => type switch
    {
        ScopeType.Self => ScopeKind.Self,
        ScopeType.DirectReports => ScopeKind.DirectReports,
        ScopeType.AllReports => ScopeKind.AllReports,
        ScopeType.Department => ScopeKind.Department,
        ScopeType.Location => ScopeKind.Location,
        ScopeType.LegalEntity => ScopeKind.LegalEntity,
        ScopeType.Tenant => ScopeKind.Tenant,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown scope type."),
    };
}

/// <summary>
/// The default expander until the employee module exists: it can answer "just me" and nothing else. Any wider scope fails loudly so a
/// module cannot silently show too little or, worse, too much.
/// </summary>
public sealed class SelfOnlyScopeExpander : IEmployeeScopeExpander
{
    public Task<IReadOnlySet<Guid>> ExpandAsync(Guid? employeeId, IReadOnlyList<DataScopeGrant> grants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var ids = new HashSet<Guid>();
        foreach (var grant in grants)
        {
            if (grant.Kind != ScopeKind.Self)
            {
                throw new NotSupportedException(
                    $"The {grant.Kind} scope needs the employee module (P0.6), which registers the real IEmployeeScopeExpander.");
            }

            if (employeeId is { } own)
            {
                ids.Add(own);
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(ids);
    }
}
