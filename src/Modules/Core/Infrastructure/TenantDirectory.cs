using Hrms.Contracts;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure;

/// <summary>
/// Answers "may this tenant use the system" and "which active tenant has this code" from the tenant table. Unknown and suspended
/// tenants both answer false or null, so a caller cannot tell which tenants exist. It runs before the tenant is resolved, which is
/// why it reads the unfiltered table.
/// </summary>
public sealed class TenantDirectory(CoreDbContext db) : ITenantDirectory, ITenantLookup
{
    public async ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken);

    public async ValueTask<Guid?> FindActiveTenantIdByCodeAsync(string tenantCode, CancellationToken cancellationToken)
    {
        var code = tenantCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || code.Length > Tenant.CodeMaxLength)
        {
            return null;
        }

        return await db.Tenants
            .AsNoTracking()
            .Where(t => t.Code == code && t.Status == TenantStatus.Active)
            .Select(t => (Guid?)t.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
