using Hrms.Contracts;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure;

/// <summary>Lists active tenants for background jobs. Reads the tenant table, which only platform code may do.</summary>
internal sealed class ActiveTenantSource(CoreDbContext db) : IActiveTenantSource
{
    public async Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active).Select(t => t.Id).ToListAsync(cancellationToken);
}
