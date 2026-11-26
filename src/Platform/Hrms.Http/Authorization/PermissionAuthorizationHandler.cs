using Hrms.Contracts;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Authorization;

namespace Hrms.Http.Authorization;

/// <summary>
/// Succeeds only when the token belongs to the tenant this request resolved to (so a token for tenant A can never be replayed
/// against tenant B) and, if a permission is required, one of the caller roles grants it. Anything unexpected fails closed.
/// </summary>
public sealed partial class PermissionAuthorizationHandler(
    ITenantContext tenantContext,
    IServiceProvider services,
    ILogger<PermissionAuthorizationHandler> logger) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var tokenTenant = context.User.FindFirst(HrmsClaims.TenantId)?.Value;
        if (!TenantId.TryParse(tokenTenant, out var tenantFromToken) || tenantContext.TenantId != tenantFromToken)
        {
            LogTenantMismatch(logger);
            return;
        }

        // A caller who still owes the second factor gets nothing but the endpoints that opted in. Without endpoint information the
        // answer is no.
        if (context.User.HasClaim(HrmsClaims.SessionKind, HrmsClaims.MfaSetupSession)
            && (context.Resource as HttpContext)?.GetEndpoint()?.Metadata.GetMetadata<MfaSetupSessionAllowed>() is null)
        {
            LogLimitedSession(logger);
            return;
        }

        if (requirement.Permission is null)
        {
            context.Succeed(requirement);
            return;
        }

        var roles = context.User.FindAll(HrmsClaims.Role).Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (roles.Length == 0)
        {
            return;
        }

        var cancellation = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        // Resolved only when a permission is actually checked: the middleware builds this handler for every request, anonymous ones
        // included, and the store reaches the database, so a host without one must still serve probes and the API description.
        var permissionStore = services.GetRequiredService<IPermissionStore>();
        var granted = await permissionStore.GetPermissionsAsync(tenantFromToken.Value, roles, cancellation);
        if (granted.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
        else
        {
            LogPermissionDenied(logger, requirement.Permission);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authorization refused: the token tenant does not match the request tenant")]
    private static partial void LogTenantMismatch(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Authorization refused: an MFA set-up session may not call this endpoint")]
    private static partial void LogLimitedSession(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Authorization refused: permission {Permission} not granted")]
    private static partial void LogPermissionDenied(ILogger logger, string permission);
}
