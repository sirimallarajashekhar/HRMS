using System.Text.RegularExpressions;
using Hrms.Identity.Domain.Security;
using Hrms.SharedKernel;

namespace Hrms.Identity.Domain.Users;

public enum UserStatus
{
    Active = 1,
    Disabled = 2,
}

/// <summary>What a failed sign-in did to the account. Callers must not reveal any of it to the person signing in.</summary>
public sealed record LockoutOutcome(bool LockedNow, DateTime? LockoutUntil);

/// <summary>
/// A person who can sign in to one tenant. Passwords are never stored here, only a hash produced by the infrastructure hasher.
/// The lockout rules live in this entity so every sign-in path (password, refresh, MFA) obeys the same state machine.
/// </summary>
public sealed partial class UserAccount : TenantEntity
{
    public const int EmailMaxLength = 254;
    public const int DisplayNameMaxLength = 200;

    private UserAccount(string email, string displayName, string passwordHash, DateTime nowUtc, Guid? employeeId, bool mustChangePassword)
    {
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        SecurityStamp = NewStamp();
        Status = UserStatus.Active;
        PasswordChangedAt = nowUtc;
        MustChangePassword = mustChangePassword;
        EmployeeId = employeeId;
    }

    /// <summary>For the persistence layer only.</summary>
    private UserAccount()
    {
        Email = DisplayName = PasswordHash = SecurityStamp = string.Empty;
    }

    /// <summary>Lower case, unique per tenant; this is the sign-in name.</summary>
    public string Email { get; private set; }

    public string DisplayName { get; private set; }

    public string PasswordHash { get; private set; }

    /// <summary>Changes whenever the credentials change, so tokens issued before the change can be rejected.</summary>
    public string SecurityStamp { get; private set; }

    public UserStatus Status { get; private set; }

    public int FailedAccessCount { get; private set; }

    public DateTime? LockoutUntil { get; private set; }

    public DateTime PasswordChangedAt { get; private set; }

    public bool MustChangePassword { get; private set; }

    public DateTime? LastLoginAt { get; private set; }

    /// <summary>The employee this account belongs to, once the employee module exists (no foreign key across modules).</summary>
    public Guid? EmployeeId { get; private set; }

    public static Result<UserAccount> Create(
        string? email,
        string? displayName,
        string passwordHash,
        DateTime nowUtc,
        Guid? employeeId = null,
        bool mustChangePassword = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The time must be UTC.", nameof(nowUtc));
        }

        var normalisedEmail = NormaliseEmail(email);
        if (normalisedEmail is null)
        {
            return Result.Failure<UserAccount>(Error.Validation("identity.user.email_invalid", "A valid email address is required."));
        }

        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > DisplayNameMaxLength)
        {
            return Result.Failure<UserAccount>(Error.Validation("identity.user.display_name_invalid", "A display name is 1 to 200 characters."));
        }

        return Result.Success(new UserAccount(normalisedEmail, name, passwordHash, nowUtc, employeeId, mustChangePassword));
    }

    /// <summary>The form used everywhere an email is compared or stored, or null if it is not an email address.</summary>
    public static string? NormaliseEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant() ?? string.Empty;
        return value.Length <= EmailMaxLength && EmailPattern().IsMatch(value) ? value : null;
    }

    public bool IsLockedOut(DateTime nowUtc) => LockoutUntil is { } until && until > nowUtc;

    /// <summary>An account can sign in only while active and not locked out.</summary>
    public bool CanSignIn(DateTime nowUtc) => Status == UserStatus.Active && !IsLockedOut(nowUtc);

    /// <summary>
    /// Counts a wrong password. Attempts made while already locked out are ignored (they cannot extend or reset the lock).
    /// A lock that has expired starts a fresh count. Reaching the limit locks the account and restarts the count.
    /// </summary>
    public LockoutOutcome RegisterFailedAttempt(DateTime nowUtc, LockoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (IsLockedOut(nowUtc))
        {
            return new LockoutOutcome(LockedNow: false, LockoutUntil);
        }

        if (LockoutUntil is not null)
        {
            LockoutUntil = null;
            FailedAccessCount = 0;
        }

        FailedAccessCount++;
        if (FailedAccessCount < policy.MaxFailedAttempts)
        {
            return new LockoutOutcome(LockedNow: false, null);
        }

        LockoutUntil = nowUtc + policy.Duration;
        FailedAccessCount = 0;
        return new LockoutOutcome(LockedNow: true, LockoutUntil);
    }

    public void RegisterSuccessfulSignIn(DateTime nowUtc)
    {
        FailedAccessCount = 0;
        LockoutUntil = null;
        LastLoginAt = nowUtc;
    }

    /// <summary>Replaces the credentials: new hash, new security stamp, counters and lock cleared, forced-change flag cleared.</summary>
    public void ChangePassword(string newPasswordHash, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);
        PasswordHash = newPasswordHash;
        SecurityStamp = NewStamp();
        PasswordChangedAt = nowUtc;
        MustChangePassword = false;
        FailedAccessCount = 0;
        LockoutUntil = null;
    }

    /// <summary>
    /// Stores a fresh hash of the same password (for example after the hashing cost was raised). Unlike
    /// <see cref="ChangePassword"/> this keeps the security stamp, so sessions are not signed out.
    /// </summary>
    public void ReplacePasswordHash(string newPasswordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);
        PasswordHash = newPasswordHash;
    }

    /// <summary>Disabling also rotates the stamp so tokens already issued stop being accepted.</summary>
    public void Disable()
    {
        Status = UserStatus.Disabled;
        SecurityStamp = NewStamp();
    }

    public void Enable() => Status = UserStatus.Active;

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
