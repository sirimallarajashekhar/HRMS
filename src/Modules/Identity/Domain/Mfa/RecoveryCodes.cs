using System.Security.Cryptography;
using System.Text;

namespace Hrms.Identity.Domain.Mfa;

/// <summary>
/// One-time codes for a user who has lost their authenticator. Each is 80 random bits shown as XXXX-XXXX-XXXX-XXXX, so a plain
/// SHA-256 of the code is safe to store (guessing 2^80 values offline is not feasible, unlike guessing a human password).
/// </summary>
public static class RecoveryCodes
{
    public const int CodeCount = 8;

    public static IReadOnlyList<string> Generate(int count = CodeCount) =>
        [.. Enumerable.Range(0, count).Select(_ => Format(Base32.Encode(RandomNumberGenerator.GetBytes(10))))];

    /// <summary>Upper case with separators and spaces removed, so the code can be typed in any reasonable way.</summary>
    public static string Normalise(string? code) =>
        new((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool LooksValid(string normalised) => normalised.Length == 16 && normalised.All(c => char.IsAsciiLetterUpper(c) || (c is >= '2' and <= '7'));

    public static string Hash(string normalisedCode) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalisedCode)));

    private static string Format(string code) => $"{code[..4]}-{code[4..8]}-{code[8..12]}-{code[12..16]}";
}
