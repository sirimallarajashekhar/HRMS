using Hrms.SharedKernel;

namespace Hrms.Identity.Application;

/// <summary>Who signed in, in the form the token server needs to build a token.</summary>
public sealed record SignedInUser(Guid UserId, Guid TenantId, string SecurityStamp, IReadOnlyList<string> RoleCodes);

/// <summary>
/// The sign-in rules: tenant by code, account state, lockout, password check, and the MFA gate. Every failure carries one of the codes
/// in <see cref="SignInErrors"/>, but the token endpoint answers all of them with the same message, so a caller learns nothing about why.
/// </summary>
public interface IPasswordSignInService
{
    Task<Result<SignedInUser>> SignInAsync(string tenantCode, string email, string password, CancellationToken cancellationToken);

    /// <summary>Re-checks an account for a refresh: it must still exist, be active and unlocked, and hold the same security stamp.</summary>
    Task<Result<SignedInUser>> RevalidateAsync(Guid tenantId, Guid userId, string securityStamp, CancellationToken cancellationToken);
}

public static class SignInErrors
{
    public static readonly Error InvalidCredentials = Error.Validation("identity.signin.invalid_credentials", "The sign-in details are not valid.");

    /// <summary>The account holds a role that requires MFA; refused until the MFA flow exists.</summary>
    public static readonly Error MfaRequired = Error.Forbidden("identity.signin.mfa_required", "A second factor is required.");

    public static readonly Error SessionRevoked = Error.Forbidden("identity.signin.session_revoked", "The session is no longer valid.");
}
