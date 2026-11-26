using Hrms.SharedKernel;

namespace Hrms.Identity.Application;

/// <summary>What the user needs to add the account to an authenticator app.</summary>
public sealed record MfaEnrollment(string Secret, string ProvisioningUri);

/// <summary>
/// Self-service account operations for the signed-in user of the current tenant: set up and turn off MFA, change the password.
/// Every operation acts on the caller's own account; none takes another user's id.
/// </summary>
public interface IAccountService
{
    /// <summary>Generates a new secret and stores it encrypted as a pending enrolment. Fails if MFA is already on.</summary>
    Task<Result<MfaEnrollment>> BeginMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Confirms the enrolment with a valid code, switches MFA on and returns the one-time recovery codes (shown only now).</summary>
    Task<Result<IReadOnlyList<string>>> ConfirmMfaEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken);

    /// <summary>Turns MFA off after re-checking the password and a code. Refused when a role of the user requires MFA.</summary>
    Task<Result> DisableMfaAsync(Guid userId, string password, string code, CancellationToken cancellationToken);

    /// <summary>Changes the password after checking the current one and the tenant password policy; ends the user's other sessions.</summary>
    Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken);
}

public static class AccountErrors
{
    public static readonly Error UserNotFound = Error.NotFound("identity.account.not_found", "The account was not found.");
    public static readonly Error WrongPassword = Error.Validation("identity.account.wrong_password", "The current password is not correct.");
    public static readonly Error WrongCode = Error.Validation("identity.mfa.wrong_code", "The verification code is not correct.");
    public static readonly Error MfaNotEnabled = Error.Conflict("identity.mfa.not_enabled", "MFA is not set up for this account.");
    public static readonly Error MfaRequiredByRole = Error.Forbidden("identity.mfa.required_by_role", "A role on this account requires MFA, so it cannot be turned off.");

    public static Error PasswordPolicy(IEnumerable<string> violations) =>
        Error.Validation("identity.account.password_policy", string.Join(",", violations));
}
