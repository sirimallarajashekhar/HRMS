using Hrms.Contracts;
using Hrms.SharedKernel;

namespace Hrms.Http.Authorization;

/// <summary>Whether the current caller may see sensitive fields unmasked (the identity.sensitive.view permission).</summary>
public interface ISensitiveDataAccess
{
    ValueTask<bool> CanViewSensitiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Answers from the caller token and roles, once per request. It says yes only for an authenticated caller whose token belongs to the
/// tenant of the request and whose roles grant the permission; anything else, including no request at all, is no.
/// </summary>
public sealed class HttpSensitiveDataAccess(
    IHttpContextAccessor httpContextAccessor,
    ITenantContext tenantContext,
    IServiceProvider services) : ISensitiveDataAccess
{
    private bool? _answer;

    public async ValueTask<bool> CanViewSensitiveAsync(CancellationToken cancellationToken)
    {
        _answer ??= await ComputeAsync(cancellationToken);
        return _answer.Value;
    }

    private async Task<bool> ComputeAsync(CancellationToken cancellationToken)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true
            || !TenantId.TryParse(user.FindFirst(HrmsClaims.TenantId)?.Value, out var tokenTenant)
            || tenantContext.TenantId != tokenTenant)
        {
            return false;
        }

        var roles = user.FindAll(HrmsClaims.Role).Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (roles.Length == 0)
        {
            return false;
        }

        var store = services.GetRequiredService<IPermissionStore>();
        var granted = await store.GetPermissionsAsync(tokenTenant.Value, roles, cancellationToken);
        return granted.Contains(Permissions.Identity.SensitiveView);
    }
}
