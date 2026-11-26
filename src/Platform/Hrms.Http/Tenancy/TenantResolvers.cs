using Hrms.Contracts;
using Hrms.SharedKernel;

namespace Hrms.Http.Tenancy;

/// <summary>
/// Reads the tenant from the X-Tenant-Id header. It trusts the caller completely, so it exists for local development
/// and tests only: <see cref="TenancyOptionsValidator"/> stops the host from starting with it outside Development.
/// </summary>
public sealed class HeaderTenantResolver : ITenantResolver
{
    public const string HeaderName = "X-Tenant-Id";

    public ValueTask<TenantResolution> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var values = context.Request.Headers[HeaderName];
        // A blank header is the same as no header, whichever way the server surfaces it.
        if (values.Count == 0 || (values.Count == 1 && string.IsNullOrWhiteSpace(values[0])))
        {
            return ValueTask.FromResult(TenantResolution.NotProvided);
        }

        var resolution = values.Count == 1 && TenantId.TryParse(values[0], out var tenantId)
            ? TenantResolution.Resolved(tenantId)
            : TenantResolution.Malformed;
        return ValueTask.FromResult(resolution);
    }
}

/// <summary>Used when no real resolver is configured: nothing can be resolved, so tenant-scoped requests are rejected (fails closed).</summary>
public sealed class NoTenantResolver : ITenantResolver
{
    public ValueTask<TenantResolution> ResolveAsync(HttpContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(TenantResolution.NotProvided);
}

/// <summary>Default directory: no tenant is active. The Core module registers the real one (a later registration wins).</summary>
public sealed class DenyAllTenantDirectory : ITenantDirectory
{
    public ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
