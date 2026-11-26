using System.Collections.Concurrent;
using Hrms.Contracts;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>
/// Per-tenant cache of "role code to permission codes" with a short time to live, so authorization does not query the database on
/// every request and a changed role takes effect within <see cref="TimeToLive"/>. Role changes call <see cref="Invalidate"/>
/// to make it immediate in this process.
/// </summary>
public sealed class PermissionCache(TimeProvider timeProvider)
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, IReadOnlySet<string>> Map)> _entries = new();

    public bool TryGet(Guid tenantId, out IReadOnlyDictionary<string, IReadOnlySet<string>> map)
    {
        if (_entries.TryGetValue(tenantId, out var entry) && entry.ExpiresAt > timeProvider.GetUtcNow())
        {
            map = entry.Map;
            return true;
        }

        map = default!; // never read: callers use it only when this returns true
        return false;
    }

    public void Set(Guid tenantId, IReadOnlyDictionary<string, IReadOnlySet<string>> map) =>
        _entries[tenantId] = (timeProvider.GetUtcNow() + TimeToLive, map);

    public void Invalidate(Guid tenantId) => _entries.TryRemove(tenantId, out _);
}

/// <summary>
/// Resolves the permissions the caller roles grant. It reads through <see cref="IdentityDbContext"/>, which is filtered to the current
/// tenant, so it refuses to answer for any other tenant than the one the request resolved to.
/// </summary>
public sealed class PermissionStore(IdentityDbContext db, PermissionCache cache, ITenantContext tenantContext) : IPermissionStore
{
    public async ValueTask<IReadOnlySet<string>> GetPermissionsAsync(
        Guid tenantId, IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        if (tenantContext.TenantId?.Value != tenantId)
        {
            throw new InvalidOperationException("Permissions can only be resolved for the tenant of the current request.");
        }

        if (!cache.TryGet(tenantId, out var map))
        {
            map = await LoadAsync(cancellationToken);
            cache.Set(tenantId, map);
        }

        var granted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roleCodes)
        {
            if (map.TryGetValue(role, out var permissions))
            {
                granted.UnionWith(permissions);
            }
        }

        return granted;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await db.RoleGrants
            .AsNoTracking()
            .Join(db.Roles.AsNoTracking(), grant => grant.RoleId, role => role.Id, (grant, role) => new { role.Code, grant.PermissionCode })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.Code, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group.Select(row => row.PermissionCode).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
    }
}
