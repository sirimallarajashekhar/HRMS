using Hrms.SharedKernel;

namespace Hrms.Identity.Application;

/// <summary>
/// Who signed in, in the form the token server needs to build a token. <see cref="EnrollmentOnly"/> marks the one case where the
/// password was right but the account holds an MFA-required role without MFA set up: it gets a token that can reach the enrolment
/// endpoints and nothing else (no roles, no refresh token) until it has enrolled and signed in again with a code.
/// </summary>
public sealed record SignedInUser(Guid UserId, Guid TenantId, string SecurityStamp, IReadOnlyList<string> RoleCodes, bool EnrollmentOnly = false);

/// <summary>The second factor presented with a password: a TOTP code from the authenticator, or a one-time recovery code.</summary>
public sealed record MfaProof(string? TotpCode, string? RecoveryCode)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(TotpCode) && string.IsNullOrWhiteSpace(RecoveryCode);
}

/// <summary>
/// The sign-in rules: tenant by code, account state, lockout, password check, and the MFA gate. Every failure carries one of the codes
/// in <see cref="SignInErrors"/>; the token endpoint answers all but <see cref="SignInErrors.MfaCodeRequired"/> with the same message, so a
/// caller learns nothing about why.
/// </summary>
public interface IPasswordSignInService
{
    Task<Result<SignedInUser>> SignInAsync(string tenantCode, string email, string password, MfaProof? mfa, CancellationToken cancellationToken);

    /// <summary>Re-checks an account for a refresh: it must still exist, be active and unlocked, and hold the same security stamp.</summary>
    Task<Result<SignedInUser>> RevalidateAsync(Guid tenantId, Guid userId, string securityStamp, CancellationToken cancellationToken);
}

public static class SignInErrors
{
    public static readonly Error InvalidCredentials = Error.Validation("identity.signin.invalid_credentials", "The sign-in details are not valid.");

    /// <summary>
    /// The password was right and the account has MFA, but no code came with it. Revealed only after a correct password, so it tells an
    /// attacker nothing they could not already do with that password.
    /// </summary>
    public static readonly Error MfaCodeRequired = Error.Forbidden("identity.signin.mfa_code_required", "A verification code is required.");

    /// <summary>The account holds an MFA-required role but has no MFA, so a refresh cannot continue a session that never passed MFA.</summary>
    public static readonly Error MfaRequired = Error.Forbidden("identity.signin.mfa_required", "A second factor is required.");

    public static readonly Error SessionRevoked = Error.Forbidden("identity.signin.session_revoked", "The session is no longer valid.");
}
