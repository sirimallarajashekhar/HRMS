namespace Hrms.Identity.Api;

public sealed class CertificateOptions
{
    /// <summary>Path to a PKCS#12 (.pfx) file with a private key.</summary>
    public string? Path { get; init; }

    /// <summary>The file password. Supply it from the secret store, never from a committed file.</summary>
    public string? Password { get; init; }
}

public sealed class IdentityServerOptions
{
    public const string SectionName = "Identity:Server";

    /// <summary>Access tokens are short because they are not checked against the database on every request.</summary>
    public int AccessTokenMinutes { get; init; } = 10;

    /// <summary>Refresh tokens expire this long after sign-in (not extended by use), so a stolen one cannot live forever.</summary>
    public int RefreshTokenDays { get; init; } = 14;

    /// <summary>Seconds a just-used refresh token may be presented again, for a client that lost the response. 0 means never.</summary>
    public int RefreshReuseLeewaySeconds { get; init; }

    public int TokenRequestsPerMinute { get; init; } = 20;

    /// <summary>Required outside Development. Development uses throw-away keys, so tokens do not survive a restart.</summary>
    public CertificateOptions SigningCertificate { get; init; } = new();

    public CertificateOptions EncryptionCertificate { get; init; } = new();

    public void Validate()
    {
        Check(AccessTokenMinutes is >= 1 and <= 60, nameof(AccessTokenMinutes), "1 to 60");
        Check(RefreshTokenDays is >= 1 and <= 90, nameof(RefreshTokenDays), "1 to 90");
        Check(RefreshReuseLeewaySeconds is >= 0 and <= 60, nameof(RefreshReuseLeewaySeconds), "0 to 60");
        Check(TokenRequestsPerMinute is >= 1 and <= 1000, nameof(TokenRequestsPerMinute), "1 to 1000");
    }

    private static void Check(bool valid, string name, string range)
    {
        if (!valid)
        {
            throw new InvalidOperationException($"{SectionName}:{name} must be {range}.");
        }
    }
}
