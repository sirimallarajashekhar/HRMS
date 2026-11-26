namespace Hrms.Identity.Domain.Security;

/// <summary>After <see cref="MaxFailedAttempts"/> consecutive wrong passwords an account is locked for <see cref="Duration"/>.</summary>
public sealed record LockoutPolicy(int MaxFailedAttempts, TimeSpan Duration)
{
    public static LockoutPolicy Default { get; } = new(MaxFailedAttempts: 5, Duration: TimeSpan.FromMinutes(15));
}
