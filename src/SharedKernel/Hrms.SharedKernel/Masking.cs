namespace Hrms.SharedKernel;

public enum SensitiveKind
{
    BankAccount,
    NationalId,
    Phone,
    Email,
}

/// <summary>
/// Hides sensitive fields from callers without the view_sensitive permission (CLAUDE.md rule 8). Masking keeps just enough to recognise
/// a value (the last few characters) and never reveals more than a third of a short one, so a short value is not masked into itself.
/// </summary>
public static class Masking
{
    public const string Redacted = "***";

    private const char MaskChar = 'X';
    private const int MaxVisibleCharacters = 4;

    public static string? Mask(string? value, SensitiveKind kind)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return kind == SensitiveKind.Email ? MaskEmail(value) : MaskTail(value);
    }

    /// <summary>The value for callers who may see it, the masked form for everyone else.</summary>
    public static string? MaskUnless(bool canView, string? value, SensitiveKind kind) => canView ? value : Mask(value, kind);

    /// <summary>For numbers such as pay: callers who may not see it get no value at all, not a masked one.</summary>
    public static T? HideUnless<T>(bool canView, T? value)
        where T : struct => canView ? value : null;

    private static string MaskTail(string value)
    {
        var visible = Math.Min(MaxVisibleCharacters, value.Length / 3);
        return string.Concat(new string(MaskChar, value.Length - visible), value.AsSpan(value.Length - visible));
    }

    private static string MaskEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 || at == value.Length - 1 ? Redacted : $"{value[0]}{Redacted}{value[at..]}";
    }
}
