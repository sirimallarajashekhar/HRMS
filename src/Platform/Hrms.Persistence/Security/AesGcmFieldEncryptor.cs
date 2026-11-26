using System.Security.Cryptography;
using System.Text;
using Hrms.SharedKernel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hrms.Persistence.Security;

public sealed class FieldEncryptionKey
{
    public string Id { get; init; } = string.Empty;

    /// <summary>A 256-bit key, base64 encoded. Supply it from the secret store; never commit a production key.</summary>
    public string Key { get; init; } = string.Empty;
}

public sealed class FieldEncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>The key id used for new values. Older keys stay listed so existing values can still be read after a rotation.</summary>
    public string CurrentKeyId { get; init; } = string.Empty;

    public IReadOnlyList<FieldEncryptionKey> Keys { get; init; } = [];

    /// <summary>The id of a local-development key (kept in user secrets or .env, never committed); it is refused anywhere but Development.</summary>
    public const string DevelopmentKeyId = "dev";
}

/// <summary>Refuses to start with no usable key, a malformed key, or the development key outside Development.</summary>
public sealed class FieldEncryptionOptionsValidator(IHostEnvironment environment) : IValidateOptions<FieldEncryptionOptions>
{
    public ValidateOptionsResult Validate(string? name, FieldEncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Keys.Count == 0)
        {
            return Fail("at least one key must be configured");
        }

        if (options.Keys.Select(k => k.Id).Distinct(StringComparer.Ordinal).Count() != options.Keys.Count || options.Keys.Any(k => string.IsNullOrWhiteSpace(k.Id) || k.Id.Contains('.', StringComparison.Ordinal)))
        {
            return Fail("key ids must be unique, non-empty and contain no dot");
        }

        foreach (var key in options.Keys)
        {
            if (!TryDecode(key.Key, out _))
            {
                return Fail($"key '{key.Id}' must be 32 bytes, base64 encoded");
            }
        }

        if (options.Keys.All(k => k.Id != options.CurrentKeyId))
        {
            return Fail("CurrentKeyId must name one of the configured keys");
        }

        if (!environment.IsDevelopment() && options.Keys.Any(k => k.Id == FieldEncryptionOptions.DevelopmentKeyId))
        {
            return Fail("the development key is only allowed in the Development environment");
        }

        return ValidateOptionsResult.Success;
    }

    internal static bool TryDecode(string value, out byte[] key)
    {
        key = [];
        try
        {
            key = Convert.FromBase64String(value);
            return key.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static ValidateOptionsResult Fail(string reason) => ValidateOptionsResult.Fail($"{FieldEncryptionOptions.SectionName}: {reason}.");
}

/// <summary>
/// AES-256-GCM with a random nonce per value. A value is "v1.{keyId}.{base64(nonce|ciphertext|tag)}"; the key id lets old values keep
/// decrypting after a rotation, and the context string is authenticated data, so a ciphertext only opens for the field it was made for.
/// </summary>
public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _currentKeyId;
    private readonly Dictionary<string, byte[]> _keys;

    public AesGcmFieldEncryptor(IOptions<FieldEncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _currentKeyId = options.Value.CurrentKeyId;
        _keys = options.Value.Keys.ToDictionary(
            key => key.Id,
            key => FieldEncryptionOptionsValidator.TryDecode(key.Key, out var bytes)
                ? bytes
                : throw new InvalidOperationException($"Encryption key '{key.Id}' is not a 32-byte base64 value."),
            StringComparer.Ordinal);

        if (!_keys.ContainsKey(_currentKeyId))
        {
            throw new InvalidOperationException("Encryption:CurrentKeyId does not name a configured key.");
        }
    }

    public string Protect(string plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(context);

        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_keys[_currentKeyId], TagSize);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(context));

        var packed = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(packed, 0);
        cipher.CopyTo(packed, NonceSize);
        tag.CopyTo(packed, NonceSize + cipher.Length);
        return $"{Version}.{_currentKeyId}.{Convert.ToBase64String(packed)}";
    }

    public string Unprotect(string protectedValue, string context)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        ArgumentException.ThrowIfNullOrEmpty(context);

        var parts = protectedValue.Split('.', 3);
        if (parts.Length != 3 || parts[0] != Version || !_keys.TryGetValue(parts[1], out var key))
        {
            throw new CryptographicException("The protected value is not in a recognised format or uses an unknown key.");
        }

        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The protected value is not valid.", ex);
        }

        if (packed.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected value is too short.");
        }

        var nonce = packed.AsSpan(0, NonceSize);
        var cipher = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
        var tag = packed.AsSpan(packed.Length - TagSize, TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(context));
        return Encoding.UTF8.GetString(plain);
    }
}
