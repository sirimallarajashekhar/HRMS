using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;

namespace Hrms.Core.IntegrationTests.Identity;

/// <summary>
/// Throw-away token server certificates, so tests that run the real host as Production (which refuses to start without real keys)
/// can still start it. Created once per test run in the temp folder; nothing here is ever used outside tests.
/// </summary>
public static class TestCertificates
{
    private const string Password = "test-only-pfx-password";

    private static readonly Lazy<(string Signing, string Encryption)> Files = new(() => (Create(), Create()));

    /// <summary>Runs the host as Production with valid certificates and an encryption key configured.</summary>
    public static IWebHostBuilder UseProductionWithCertificates(this IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var (signing, encryption) = Files.Value;
        return builder
            .UseEnvironment("Production")
            .UseSetting("Identity:Server:SigningCertificate:Path", signing)
            .UseSetting("Identity:Server:SigningCertificate:Password", Password)
            .UseSetting("Identity:Server:EncryptionCertificate:Path", encryption)
            .UseSetting("Identity:Server:EncryptionCertificate:Password", Password)
            .UseSetting("Encryption:CurrentKeyId", TestEncryption.KeyId)
            .UseSetting("Encryption:Keys:0:Id", TestEncryption.KeyId)
            .UseSetting("Encryption:Keys:0:Key", TestEncryption.Key);
    }

    private static string Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=hrms-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(Path.GetTempPath(), $"hrms-test-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return path;
    }
}
