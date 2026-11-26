using System.Security.Claims;
using Hrms.Http;
using Hrms.Http.Authorization;
using Hrms.Identity.Application;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Hrms.Identity.Api;

public sealed record MfaEnrollmentResponse(string Secret, string ProvisioningUri);

public sealed record ConfirmMfaRequest(string? Code);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record DisableMfaRequest(string? Password, string? Code);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>
/// Self-service account endpoints: MFA enrolment and removal, and password change. They are open to any signed-in user of the tenant
/// (including the enrolment-only session of someone who must set up MFA), they act only on the caller's own account, and they are
/// rate limited per user because they check passwords and codes.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var account = routes.MapGroup("/account")
            .AllowAnyAuthenticatedUser()
            .AllowMfaSetupSession()
            .RequireRateLimiting(IdentityServerExtensions.AccountRateLimitPolicy)
            .WithTags("Account");

        account.MapPost("/mfa/enrollment", BeginEnrollmentAsync)
            .WithSummary("Starts MFA set-up and returns the secret and the otpauth URI for the authenticator app.");
        account.MapPost("/mfa/enrollment/confirm", ConfirmEnrollmentAsync)
            .WithSummary("Confirms MFA set-up with a code and returns the one-time recovery codes (shown only once).");
        account.MapPost("/mfa/disable", DisableAsync)
            .WithSummary("Turns MFA off after re-checking the password and a code; refused when a role requires MFA.");
        account.MapPost("/password", ChangePasswordAsync)
            .WithSummary("Changes the password after checking the current one and the tenant password policy.");
        return routes;
    }

    private static async Task<IResult> BeginEnrollmentAsync(ClaimsPrincipal caller, IAccountService accounts, HttpContext http, CancellationToken cancellationToken)
    {
        if (UserId(caller) is not { } userId)
        {
            return NotAUser();
        }

        var result = await accounts.BeginMfaEnrollmentAsync(userId, cancellationToken);
        NoStore(http);
        return result.ToHttpResult(enrollment => Results.Ok(new MfaEnrollmentResponse(enrollment.Secret, enrollment.ProvisioningUri)));
    }

    private static async Task<IResult> ConfirmEnrollmentAsync(
        ConfirmMfaRequest request, ClaimsPrincipal caller, IAccountService accounts, HttpContext http, CancellationToken cancellationToken)
    {
        if (UserId(caller) is not { } userId)
        {
            return NotAUser();
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Invalid("A verification code is required.");
        }

        var result = await accounts.ConfirmMfaEnrollmentAsync(userId, request.Code, cancellationToken);
        NoStore(http);
        return result.ToHttpResult(codes => Results.Ok(new RecoveryCodesResponse(codes)));
    }

    private static async Task<IResult> DisableAsync(DisableMfaRequest request, ClaimsPrincipal caller, IAccountService accounts, CancellationToken cancellationToken)
    {
        if (UserId(caller) is not { } userId)
        {
            return NotAUser();
        }

        if (string.IsNullOrEmpty(request.Password) || string.IsNullOrWhiteSpace(request.Code))
        {
            return Invalid("The password and a verification code are required.");
        }

        return (await accounts.DisableMfaAsync(userId, request.Password, request.Code, cancellationToken))
            .ToHttpResult(() => Results.NoContent());
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, ClaimsPrincipal caller, IAccountService accounts, CancellationToken cancellationToken)
    {
        if (UserId(caller) is not { } userId)
        {
            return NotAUser();
        }

        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            return Invalid("The current and the new password are required.");
        }

        return (await accounts.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, cancellationToken))
            .ToHttpResult(() => Results.NoContent());
    }

    /// <summary>The caller user id, or null when the token is not a user (for example an integration client).</summary>
    private static Guid? UserId(ClaimsPrincipal caller) =>
        Guid.TryParse(caller.FindFirst(Claims.Subject)?.Value, out var id) ? id : null;

    private static IResult NotAUser() =>
        Error.Forbidden("identity.account.not_a_user", "This operation is only available to signed-in users.").ToProblem();

    private static IResult Invalid(string message) => Error.Validation("identity.account.invalid_request", message).ToProblem();

    /// <summary>Secrets and recovery codes are shown once and must never be cached by the client or a proxy.</summary>
    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
