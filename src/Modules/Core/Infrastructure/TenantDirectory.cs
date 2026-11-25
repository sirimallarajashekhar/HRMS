using Hrms.Contracts;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure;

/// <summary>
/// Answers "may this tenant use the system" from the tenant table. Unknown and suspended tenants both answer false, so a
/// caller cannot tell which tenants exist. It runs before the tenant is resolved, which is why it reads the unfiltered table.
/// </summary>
public sealed class TenantDirectory(CoreDbContext db) : ITenantDirectory
{
    public async ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, cancellationToken);
}
