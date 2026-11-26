using Hrms.SharedKernel;

namespace Hrms.Employee.Domain;

/// <summary>Small shared checks for the employee entities, so each entity reports the same kind of error the same way.</summary>
internal static class Validation
{
    public static Error Invalid(string area, string reason, string message) => Error.Validation($"employee.{area}.{reason}", message);

    /// <summary>Trims; returns null for empty, and reports through <paramref name="tooLong"/> when over the limit.</summary>
    public static string? Optional(string? value, int maxLength, out bool tooLong)
    {
        var trimmed = value?.Trim();
        tooLong = trimmed is { Length: > 0 } && trimmed.Length > maxLength;
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Trims; null when empty or over the limit.</summary>
    public static string? Required(string? value, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 0 || trimmed.Length > maxLength ? null : trimmed;
    }
}
