using Hrms.Contracts;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hrms.Http.Authorization;

/// <summary>
/// The default permission store: grants nothing, so a host that forgot to register the identity module denies every
/// permission-protected request instead of allowing it.
/// </summary>
public sealed class DenyAllPermissionStore : IPermissionStore
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    public ValueTask<IReadOnlySet<string>> GetPermissionsAsync(
        Guid tenantId, IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Empty);
}

public static class AuthorizationExtensions
{
    /// <summary>
    /// Deny by default: an endpoint with no authorization metadata still requires an authenticated caller in the resolved tenant.
    /// Endpoints must say what they need with RequirePermission, AllowAnyAuthenticatedUser or AllowAnonymous.
    /// </summary>
    public static IServiceCollection AddHrmsAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options => options.FallbackPolicy = PermissionPolicyProvider.Build(permission: null));
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.TryAddScoped<IPermissionStore, DenyAllPermissionStore>();
        services.AddHttpContextAccessor();
        services.AddScoped<ISensitiveDataAccess, HttpSensitiveDataAccess>();
        // The signed-in user becomes the audit actor (replaces the system default registered with tenancy).
        services.AddScoped<HttpActorContext>();
        services.Replace(ServiceDescriptor.Scoped<IActorContext>(sp => sp.GetRequiredService<HttpActorContext>()));
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<HttpActorContext>());
        return services;
    }

    /// <summary>The endpoint needs this permission from the catalogue.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(new RequirePermissionAttribute(permission));

    /// <summary>The endpoint is for any signed-in user of the tenant (for example their own profile) and needs no permission.</summary>
    public static TBuilder AllowAnyAuthenticatedUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.AuthenticatedOnlyPolicy);
}
