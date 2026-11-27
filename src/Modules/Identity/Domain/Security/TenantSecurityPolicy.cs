using Hrms.SharedKernel;

namespace Hrms.Identity.Domain.Security;

/// <summary>
/// A tenant password and lockout policy, effective-dated like every policy (history is never overwritten: a change closes the
/// current row and adds the next). When a tenant has no row for a date, <see cref="PasswordPolicy.Default"/> and
/// <see cref="LockoutPolicy.Default"/> apply.
/// </summary>
[Audited]
public sealed class TenantSecurityPolicy : EffectiveDatedEntity
{
    public const int MinPasswordLengthFloor = 8;
    public const int MinPasswordLengthCeiling = 128;
    public const int MaxFailedAttemptsFloor = 3;
    public const int MaxFailedAttemptsCeiling = 20;
    public const int LockoutMinutesFloor = 1;
    public const int LockoutMinutesCeiling = 1440;

    private TenantSecurityPolicy(
        DateOnly effectiveFrom,
        int minPasswordLength,
        bool requireUppercase,
        bool requireLowercase,
        bool requireDigit,
        bool requireSymbol,
        int maxFailedAttempts,
        int lockoutMinutes)
        : base(effectiveFrom)
    {
        MinPasswordLength = minPasswordLength;
        RequireUppercase = requireUppercase;
        RequireLowercase = requireLowercase;
        RequireDigit = requireDigit;
        RequireSymbol = requireSymbol;
        MaxFailedAttempts = maxFailedAttempts;
        LockoutMinutes = lockoutMinutes;
    }

    /// <summary>For the persistence layer only.</summary>
    private TenantSecurityPolicy()
    {
    }

    public int MinPasswordLength { get; private set; }

    public bool RequireUppercase { get; private set; }

    public bool RequireLowercase { get; private set; }

    public bool RequireDigit { get; private set; }

    public bool RequireSymbol { get; private set; }

    public int MaxFailedAttempts { get; private set; }

    public int LockoutMinutes { get; private set; }

    public PasswordPolicy ToPasswordPolicy() =>
        new(MinPasswordLength, RequireUppercase, RequireLowercase, RequireDigit, RequireSymbol);

    public LockoutPolicy ToLockoutPolicy() => new(MaxFailedAttempts, TimeSpan.FromMinutes(LockoutMinutes));

    public static Result<TenantSecurityPolicy> Create(
        DateOnly effectiveFrom,
        int minPasswordLength,
        bool requireUppercase,
        bool requireLowercase,
        bool requireDigit,
        bool requireSymbol,
        int maxFailedAttempts,
        int lockoutMinutes)
    {
        if (minPasswordLength is < MinPasswordLengthFloor or > MinPasswordLengthCeiling)
        {
            return Invalid("min_length_invalid", $"The minimum password length is between {MinPasswordLengthFloor} and {MinPasswordLengthCeiling}.");
        }

        if (maxFailedAttempts is < MaxFailedAttemptsFloor or > MaxFailedAttemptsCeiling)
        {
            return Invalid("max_failed_attempts_invalid", $"Failed attempts before lockout is between {MaxFailedAttemptsFloor} and {MaxFailedAttemptsCeiling}.");
        }

        if (lockoutMinutes is < LockoutMinutesFloor or > LockoutMinutesCeiling)
        {
            return Invalid("lockout_minutes_invalid", $"The lockout duration is between {LockoutMinutesFloor} and {LockoutMinutesCeiling} minutes.");
        }

        return Result.Success(new TenantSecurityPolicy(
            effectiveFrom, minPasswordLength, requireUppercase, requireLowercase, requireDigit, requireSymbol, maxFailedAttempts, lockoutMinutes));
    }

    private static Result<TenantSecurityPolicy> Invalid(string reason, string message) =>
        Result.Failure<TenantSecurityPolicy>(Error.Validation($"identity.security_policy.{reason}", message));
}
