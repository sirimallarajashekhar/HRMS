using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.Persistence.Auditing;

namespace Hrms.Identity.Infrastructure.Security;

/// <summary>
/// The security events of an account in the audit log. Field changes such as turning MFA on or off, or changing the password, are
/// captured like any other change; these are the things that change no field worth recording. Attempts on unknown, disabled or locked
/// accounts are deliberately not recorded: they would let anyone fill the log, and add work that only some refusals do.
/// </summary>
internal static class SecurityEvents
{
    public const string DuringSignIn = "sign_in";
    public const string DuringAccountChange = "account_change";

    public const string Password = "password";
    public const string Totp = "totp";
    public const string RecoveryCode = "recovery_code";

    public static void SignedIn(IdentityDbContext db, UserAccount user, string? secondFactor, bool enrollmentOnly)
    {
        List<AuditFieldChange> details = [Detail("factor", Password)];
        if (secondFactor is not null)
        {
            details.Add(Detail("secondFactor", secondFactor));
        }

        if (enrollmentOnly)
        {
            details.Add(Detail("session", "mfa_setup_only"));
        }

        db.RecordSecurityEvent(user, AuditAction.SignedIn, details, provenActorId: user.Id);
    }

    /// <param name="factor">Which of the things presented was wrong. The value presented is never recorded.</param>
    public static void Refused(IdentityDbContext db, UserAccount user, LockoutOutcome outcome, string during, string factor)
    {
        if (!outcome.Counted)
        {
            return;
        }

        db.RecordSecurityEvent(user, AuditAction.CredentialRefused, [Detail("during", during), Detail("factor", factor)]);
        if (outcome.LockedNow)
        {
            db.RecordSecurityEvent(user, AuditAction.LockedOut, [Detail("until", outcome.LockoutUntil)]);
        }
    }

    private static AuditFieldChange Detail(string name, object? value) => new(name, null, value);
}
