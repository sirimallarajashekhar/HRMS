using Hrms.Persistence.Security;
using Microsoft.Extensions.Options;

namespace Hrms.Core.IntegrationTests.Identity;

/// <summary>A fixed, test-only encryption key so hosts and services built by tests can protect and read MFA secrets.</summary>
public static class TestEncryption
{
    public const string KeyId = "test";

    /// <summary>32 bytes of 0x2A, base64. Never used outside tests.</summary>
    public static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)0x2A, 32).ToArray());

    public static AesGcmFieldEncryptor Encryptor() => new(Options.Create(new FieldEncryptionOptions
    {
        CurrentKeyId = KeyId,
        Keys = [new FieldEncryptionKey { Id = KeyId, Key = Key }],
    }));

    /// <summary>The configuration entries that give a host this key.</summary>
    public static Dictionary<string, string?> Settings() => new()
    {
        ["Encryption:CurrentKeyId"] = KeyId,
        ["Encryption:Keys:0:Id"] = KeyId,
        ["Encryption:Keys:0:Key"] = Key,
    };
}
