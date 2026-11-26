namespace Hrms.Identity.Domain.Security;

/// <summary>
/// What a password must satisfy, per tenant. Violations are reported as stable codes so a client can show each one;
/// the password itself is never logged or echoed. The upper bound exists so a huge password cannot be used to burn CPU in the hasher.
/// </summary>
public sealed record PasswordPolicy(
    int MinLength,
    bool RequireUppercase,
    bool RequireLowercase,
    bool RequireDigit,
    bool RequireSymbol)
{
    public const int MaxLength = 128;

    public const string TooShort = "password.too_short";
    public const string TooLong = "password.too_long";
    public const string MissingUppercase = "password.missing_uppercase";
    public const string MissingLowercase = "password.missing_lowercase";
    public const string MissingDigit = "password.missing_digit";
    public const string MissingSymbol = "password.missing_symbol";

    /// <summary>Length matters more than composition, so the default asks for length only; tenants can tighten it.</summary>
    public static PasswordPolicy Default { get; } = new(MinLength: 12, RequireUppercase: false, RequireLowercase: false, RequireDigit: false, RequireSymbol: false);

    /// <summary>Returns the violated rules (empty when the password is acceptable).</summary>
    public IReadOnlyList<string> Validate(string? password)
    {
        var value = password ?? string.Empty;
        var violations = new List<string>();

        if (value.Length < MinLength)
        {
            violations.Add(TooShort);
        }

        if (value.Length > MaxLength)
        {
            violations.Add(TooLong);
        }

        if (RequireUppercase && !value.Any(char.IsUpper))
        {
            violations.Add(MissingUppercase);
        }

        if (RequireLowercase && !value.Any(char.IsLower))
        {
            violations.Add(MissingLowercase);
        }

        if (RequireDigit && !value.Any(char.IsDigit))
        {
            violations.Add(MissingDigit);
        }

        if (RequireSymbol && !value.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)))
        {
            violations.Add(MissingSymbol);
        }

        return violations;
    }
}
