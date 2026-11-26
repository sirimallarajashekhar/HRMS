using Hrms.SharedKernel;

namespace Hrms.Identity.Domain.Users;

/// <summary>
/// A one-time code that lets a user in when they cannot produce a TOTP code. Only the hash is stored. Using one marks it used; a used
/// code never works again. Confirming MFA creates a fresh set and retires any earlier ones.
/// </summary>
public sealed class RecoveryCode : TenantEntity
{
    private RecoveryCode(Guid userId, string codeHash)
    {
        UserId = userId;
        CodeHash = codeHash;
    }

    /// <summary>For the persistence layer only.</summary>
    private RecoveryCode()
    {
        CodeHash = string.Empty;
    }

    public Guid UserId { get; private set; }

    /// <summary>Upper-case hex SHA-256 of the normalised code.</summary>
    public string CodeHash { get; private set; }

    public DateTime? UsedAt { get; private set; }

    public bool IsUsed => UsedAt is not null;

    public static RecoveryCode Create(Guid userId, string codeHash)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        return new RecoveryCode(userId, codeHash);
    }

    /// <summary>False if the code was already used.</summary>
    public bool TryUse(DateTime nowUtc)
    {
        if (IsUsed)
        {
            return false;
        }

        UsedAt = nowUtc;
        return true;
    }
}
