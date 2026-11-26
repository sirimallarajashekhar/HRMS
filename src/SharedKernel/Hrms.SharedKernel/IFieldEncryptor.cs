namespace Hrms.SharedKernel;

/// <summary>
/// Encrypts a sensitive value for storage (MFA secrets now; bank accounts and national ids later). The context string is bound into the
/// ciphertext, so a value copied to another row, field or tenant does not decrypt: callers pass something like
/// "user_account.mfa_secret:{tenantId}:{userId}".
/// </summary>
public interface IFieldEncryptor
{
    string Protect(string plaintext, string context);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> if the value was altered or the context differs.</summary>
    string Unprotect(string protectedValue, string context);
}
