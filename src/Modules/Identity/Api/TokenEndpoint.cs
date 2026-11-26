using System.Security.Claims;
using Hrms.Contracts;
using Hrms.Http.Tenancy;
using Hrms.Identity.Application;
using Hrms.SharedKernel;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Hrms.Identity.Api;

/// <summary>
/// The OAuth token endpoint (POST /connect/token). OpenIddict parses and protocol-validates the request; this decides who may be issued
/// a token. Every refused password sign-in gets the identical answer whatever the reason (unknown tenant, unknown or disabled or locked
/// account, wrong password), so the endpoint cannot be used to find out which tenants or accounts exist.
/// </summary>
public static class TokenEndpoint
{
    /// <summary>The custom parameter that names the tenant to sign in to (the tenant code, for example ACME).</summary>
    public const string TenantParameter = "tenant";

    /// <summary>Carries the account security stamp inside refresh tokens only, never in an access token.</summary>
    public const string StampClaim = "sstamp";

    /// <summary>The scope of the enrolment-only session: it can reach the MFA enrolment endpoints and carries no roles.</summary>
    public const string MfaSetupScope = "mfa_setup";

    public const string TotpParameter = "totp_code";
    public const string RecoveryParameter = "recovery_code";

    /// <summary>The OAuth error that tells a client to ask the user for their verification code.</summary>
    public const string MfaRequiredError = "mfa_required";

    public const string RefusedDescription = "The sign-in details are not valid.";

    public static IEndpointRouteBuilder MapTokenEndpoint(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(IdentityServerExtensions.TokenEndpointPath, HandleAsync)
            .AllowAnonymous()
            .AllowWithoutTenant()
            .RequireRateLimiting(IdentityServerExtensions.TokenRateLimitPolicy)
            .ExcludeFromDescription();
        return routes;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IPasswordSignInService signIn,
        IOpenIddictApplicationManager applications,
        ITenantDirectory tenants)
    {
        var request = http.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsPasswordGrantType())
        {
            return await PasswordAsync(http, request, signIn);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await RefreshAsync(http, signIn);
        }

        if (request.IsClientCredentialsGrantType())
        {
            return await ClientCredentialsAsync(http, request, applications, tenants);
        }

        return Refuse(Errors.UnsupportedGrantType, "The grant type is not supported.");
    }

    private static async Task<IResult> PasswordAsync(HttpContext http, OpenIddictRequest request, IPasswordSignInService signIn)
    {
        var tenant = request.GetParameter(TenantParameter)?.ToString();
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password) || string.IsNullOrWhiteSpace(tenant))
        {
            return Refuse(Errors.InvalidRequest, "The username, password and tenant are required.");
        }

        var proof = new MfaProof(request.GetParameter(TotpParameter)?.ToString(), request.GetParameter(RecoveryParameter)?.ToString());
        var result = await signIn.SignInAsync(tenant, request.Username, request.Password, proof, http.RequestAborted);
        if (result.IsFailure)
        {
            // The one answer that differs: the password was right and the account has MFA but no code came with it, so the client
            // must ask for one. Every other cause, a wrong code included, gets the same refusal.
            return result.Error == SignInErrors.MfaCodeRequired
                ? Refuse(MfaRequiredError, "A verification code is required.")
                : Refuse(Errors.InvalidGrant, RefusedDescription);
        }

        var user = result.Value;
        if (user.EnrollmentOnly)
        {
            // Password right, MFA mandatory but not set up: an enrolment-only session (no roles, no refresh token).
            return SignIn(BuildPrincipal(user.UserId.ToString("D"), user.TenantId, [], user.SecurityStamp, [MfaSetupScope]));
        }

        return SignIn(BuildPrincipal(user.UserId.ToString("D"), user.TenantId, user.RoleCodes, user.SecurityStamp, request.GetScopes()));
    }

    private static async Task<IResult> RefreshAsync(HttpContext http, IPasswordSignInService signIn)
    {
        var authenticated = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var principal = authenticated.Principal;

        var subject = principal?.GetClaim(Claims.Subject);
        var tenant = principal?.GetClaim(HrmsClaims.TenantId);
        var stamp = principal?.GetClaim(StampClaim);
        if (principal is null || !Guid.TryParse(subject, out var userId) || !Guid.TryParse(tenant, out var tenantId) || string.IsNullOrEmpty(stamp))
        {
            return Refuse(Errors.InvalidGrant, "The refresh token is not valid.");
        }

        // The account must still exist, be active and unlocked, and hold the same stamp (a password change or disable rotates it).
        var result = await signIn.RevalidateAsync(tenantId, userId, stamp, http.RequestAborted);
        if (result.IsFailure)
        {
            return Refuse(Errors.InvalidGrant, "The session is no longer valid.");
        }

        // Roles are read afresh, so a role change reaches the user at their next refresh.
        return SignIn(BuildPrincipal(subject!, result.Value.TenantId, result.Value.RoleCodes, result.Value.SecurityStamp, principal.GetScopes()));
    }

    private static async Task<IResult> ClientCredentialsAsync(
        HttpContext http, OpenIddictRequest request, IOpenIddictApplicationManager applications, ITenantDirectory tenants)
    {
        var application = request.ClientId is null ? null : await applications.FindByClientIdAsync(request.ClientId, http.RequestAborted);
        if (application is null)
        {
            return Refuse(Errors.InvalidClient, "The client is not valid.");
        }

        var binding = OAuthClientBinding.Read(await applications.GetPropertiesAsync(application, http.RequestAborted));
        if (binding is not { } bound || !await tenants.IsActiveAsync(bound.TenantId, http.RequestAborted))
        {
            // A client with no tenant binding, or whose tenant is suspended, never gets a token.
            return Refuse(Errors.InvalidClient, "The client is not valid.");
        }

        return SignIn(BuildPrincipal(request.ClientId!, bound.TenantId, bound.Roles, stamp: null, request.GetScopes()));
    }

    private static ClaimsPrincipal BuildPrincipal(
        string subject, Guid tenantId, IReadOnlyList<string> roles, string? stamp, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, subject));
        identity.AddClaim(new Claim(HrmsClaims.TenantId, tenantId.ToString("D")));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(Claims.Role, role));
        }

        if (stamp is not null)
        {
            identity.AddClaim(new Claim(StampClaim, stamp));
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);

        // Subject, tenant and roles go into the access token. The stamp has no destination, so only the refresh token keeps it.
        foreach (var claim in principal.Claims)
        {
            claim.SetDestinations(claim.Type == StampClaim ? [] : [Destinations.AccessToken]);
        }

        return principal;
    }

    private static IResult SignIn(ClaimsPrincipal principal) =>
        Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    private static IResult Refuse(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
