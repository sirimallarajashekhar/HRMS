using System.Text.Encodings.Web;
using Hrms.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
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

/// <summary>
/// Placeholder authentication scheme used until the JWT scheme is wired in with the token server: it never authenticates anyone,
/// so protected endpoints answer 401. It is replaced, not extended, when real authentication arrives.
/// </summary>
public sealed class NoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "HrmsNoAuthentication";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
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
        return services;
    }

    public static AuthenticationBuilder AddHrmsNoAuthentication(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>(NoAuthenticationHandler.SchemeName, _ => { });
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
