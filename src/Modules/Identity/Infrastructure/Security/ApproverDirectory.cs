using Hrms.Contracts;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>Who can be asked to approve. Reads through the tenant-filtered context, so another tenant's users and roles do not exist.</summary>
internal sealed class ApproverDirectory(IdentityDbContext db) : IApproverDirectory
{
    public async Task<IReadOnlyList<ApproverAccount>> GetAccountsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return [];
        }

        return await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new ApproverAccount(u.Id, u.Status == UserStatus.Active, u.EmployeeId))
            .ToListAsync(cancellationToken);
    }

    public Task<ApproverAccount?> FindByEmployeeAsync(Guid employeeId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking()
            .Where(u => u.EmployeeId == employeeId)
            .Select(u => new ApproverAccount(u.Id, u.Status == UserStatus.Active, u.EmployeeId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> RoleExistsAsync(string roleCode, CancellationToken cancellationToken) =>
        db.Roles.AsNoTracking().AnyAsync(r => r.Code == roleCode, cancellationToken);

    public async Task<IReadOnlyList<ApproverAccount>> FindWithRoleAtLocationAsync(
        string roleCode, Guid locationId, int limit, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active
                        && db.UserRoleScopes.Any(s => s.UserId == u.Id
                                                      && s.ScopeType == ScopeType.Location
                                                      && s.ScopeValue == locationId
                                                      && db.Roles.Any(r => r.Id == s.RoleId && r.Code == roleCode)))
            .OrderBy(u => u.Id)
            .Take(limit)
            .Select(u => new ApproverAccount(u.Id, true, u.EmployeeId))
            .ToListAsync(cancellationToken);
}
