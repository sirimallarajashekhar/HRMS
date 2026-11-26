using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hrms.Identity.Domain.Mfa;

/// <summary>
/// Time-based one-time passwords per RFC 6238 (6 digits, 30-second steps), the format every authenticator app supports.
/// HMAC-SHA1 is the algorithm authenticator apps universally implement, so it is used here as the RFC specifies; the secret is 160 bits
/// and a code is only ever compared in constant time. A code that was already accepted for a step cannot be accepted again (replay).
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    public const int SecretBytes = 20;

    /// <summary>Codes from this many steps either side of now are accepted, to tolerate clock drift between phone and server.</summary>
    public const int DriftWindowSteps = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    public static string Compute(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // RFC 6238 specifies HMAC-SHA1 and authenticator apps only implement it; the 160-bit secret and short code lifetime are the protection.
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the time step the code matches, or null if it matches none inside the drift window or only a step that was already used.
    /// The caller stores the returned step as the last used one.
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var presented = Encoding.ASCII.GetBytes(code);
        var current = StepAt(now);
        long? match = null;

        // Every step in the window is checked, with no early exit, so the time taken does not say which one matched.
        for (var step = current - DriftWindowSteps; step <= current + DriftWindowSteps; step++)
        {
            var expected = Encoding.ASCII.GetBytes(Compute(secret, step));
            if (CryptographicOperations.FixedTimeEquals(presented, expected) && (lastUsedStep is null || step > lastUsedStep))
            {
                match = step;
            }
        }

        return match;
    }

    /// <summary>The otpauth:// URI an authenticator app reads (usually from a QR code) to add the account.</summary>
    public static string ProvisioningUri(string issuer, string account, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}";
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}"
               + $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }
}

/// <summary>RFC 4648 base32 without padding, the encoding authenticator apps expect for the shared secret.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }

    public static byte[]? TryDecode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var output = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
