using Hrms.Identity.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Hrms.Identity.Infrastructure.Security;

public sealed class PasswordHashingOptions
{
    public const string SectionName = "Identity:PasswordHashing";

    /// <summary>
    /// PBKDF2-HMAC-SHA512 iterations. 210,000 is the OWASP guidance for this algorithm; raising it later makes
    /// <see cref="PasswordCheck.SuccessRehashNeeded"/> upgrade each hash the next time its owner signs in.
    /// </summary>
    public int IterationCount { get; init; } = 210_000;

    /// <summary>Below this the hashes are too cheap to attack; options validation refuses to start with less.</summary>
    public const int MinimumIterationCount = 100_000;
}

/// <summary>
/// Password hashing through the ASP.NET Core Identity hasher alone (its versioned PBKDF2 format, per-password salt and
/// constant-time comparison); none of Identity's user or store framework is used. Only this class knows the algorithm.
/// </summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private sealed class HashSubject;

    private static readonly HashSubject Subject = new();

    private readonly PasswordHasher<HashSubject> _hasher;
    private readonly Lazy<string> _dummyHash;

    public IdentityPasswordHasher(IOptions<PasswordHashingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hasher = new PasswordHasher<HashSubject>(Options.Create(new PasswordHasherOptions { IterationCount = options.Value.IterationCount }));
        _dummyHash = new Lazy<string>(() => _hasher.HashPassword(Subject, Guid.NewGuid().ToString("N")), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string DummyHash => _dummyHash.Value;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return _hasher.HashPassword(Subject, password);
    }

    public PasswordCheck Verify(string passwordHash, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        ArgumentNullException.ThrowIfNull(password);

        try
        {
            return _hasher.VerifyHashedPassword(Subject, passwordHash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
                _ => PasswordCheck.Failed,
            };
        }
        catch (FormatException)
        {
            // The Identity hasher throws on a stored hash that is not valid Base64. A damaged hash must read as a wrong password,
            // never as a server error that tells an attacker which account has a broken record.
            return PasswordCheck.Failed;
        }
    }
}

public sealed class PasswordHashingOptionsValidator : IValidateOptions<PasswordHashingOptions>
{
    public ValidateOptionsResult Validate(string? name, PasswordHashingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.IterationCount >= PasswordHashingOptions.MinimumIterationCount
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{PasswordHashingOptions.SectionName}:IterationCount must be at least {PasswordHashingOptions.MinimumIterationCount}.");
    }
}
