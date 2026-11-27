using System.Globalization;
using System.Text.RegularExpressions;
using Hrms.SharedKernel;

namespace Hrms.Employee.Domain;

/// <summary>
/// How employee codes are made for one legal entity: a prefix, a zero-padded running number and the next number to hand out.
/// The number only moves forward (a code is never reused), and a number wider than the padding simply gets longer. Codes are unique
/// per tenant, so two entities must use different prefixes; the unique index is the final guard.
/// </summary>
[Audited]
public sealed partial class EmployeeCodeRule : TenantEntity
{
    public const int PrefixMaxLength = 12;
    public const int MaxWidth = 12;

    private EmployeeCodeRule(Guid legalEntityId, string prefix, int width, long nextNumber)
    {
        LegalEntityId = legalEntityId;
        Prefix = prefix;
        Width = width;
        NextNumber = nextNumber;
    }

    /// <summary>For the persistence layer only.</summary>
    private EmployeeCodeRule() => Prefix = string.Empty;

    public Guid LegalEntityId { get; private set; }

    public string Prefix { get; private set; }

    public int Width { get; private set; }

    /// <summary>The number the next employee gets.</summary>
    [AuditIgnore]
    public long NextNumber { get; private set; }

    public static Result<EmployeeCodeRule> Create(Guid legalEntityId, string? prefix, int width, long startNumber)
    {
        var valid = Validate(prefix, width, startNumber, minimumNext: 1);
        return valid.IsFailure
            ? Result.Failure<EmployeeCodeRule>(valid.Error)
            : Result.Success(new EmployeeCodeRule(legalEntityId, valid.Value, width, startNumber));
    }

    /// <summary>Changes the pattern for future codes; the next number cannot go back below where it is now.</summary>
    public Result Update(string? prefix, int width, long nextNumber)
    {
        var valid = Validate(prefix, width, nextNumber, minimumNext: NextNumber);
        if (valid.IsFailure)
        {
            return valid;
        }

        Prefix = valid.Value;
        Width = width;
        NextNumber = nextNumber;
        return Result.Success();
    }

    /// <summary>Hands out the next code and moves the counter on. Call inside the transaction that saves the employee.</summary>
    public string Allocate()
    {
        var code = Format(Prefix, NextNumber, Width);
        NextNumber++;
        return code;
    }

    public static string Format(string prefix, long number, int width) =>
        prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    private static Result<string> Validate(string? prefix, int width, long next, long minimumNext)
    {
        var normalised = prefix?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalised.Length > PrefixMaxLength || (normalised.Length > 0 && !PrefixPattern().IsMatch(normalised)))
        {
            return Fail("prefix_invalid", "The prefix is up to 12 letters, digits or hyphens.");
        }

        if (width is < 1 or > MaxWidth)
        {
            return Fail("width_invalid", $"The width is 1 to {MaxWidth} digits.");
        }

        if (next < minimumNext || next > 999_999_999_999)
        {
            return Fail("next_number_invalid", $"The next number is at least {minimumNext} and never goes back.");
        }

        return Result.Success(normalised);
    }

    private static Result<string> Fail(string reason, string message) => Result.Failure<string>(Validation.Invalid("code_rule", reason, message));

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]*$")]
    private static partial Regex PrefixPattern();
}
