using System.Text.RegularExpressions;
using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>
/// The shape every organisation-structure master shares: a code that is unique per tenant (upper case, so "ENG" and "eng" cannot
/// both exist), a name, and an active flag. Inactive rows stay readable and keep working where they are already used; they just
/// cannot be chosen for new assignments (the employee module enforces that from P0.6).
/// </summary>
[Audited]
public abstract partial class CodedMaster : TenantEntity
{
    public const int CodeMaxLength = 32;
    public const int NameMaxLength = 200;

    protected CodedMaster(string code, string name)
    {
        Code = code;
        Name = name;
        IsActive = true;
    }

    /// <summary>For the persistence layer only.</summary>
    protected CodedMaster() => Code = Name = string.Empty;

    /// <summary>Upper case, unique per tenant among live rows.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Normalises and validates the identity fields; <paramref name="master"/> names the master in the error code.</summary>
    protected static Result<(string Code, string Name)> ValidateIdentity(string master, string? code, string? name)
    {
        var normalised = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(normalised))
        {
            return Result.Failure<(string, string)>(Error.Validation($"core.{master}.code_invalid",
                "A code is 1 to 32 characters: letters, digits, hyphens and underscores, starting with a letter or digit."));
        }

        return ValidateName(name) is { } validName
            ? Result.Success((normalised, validName))
            : Result.Failure<(string, string)>(NameInvalid(master));
    }

    /// <summary>Changes the name and the active flag; nothing is applied when the name is invalid.</summary>
    protected Result ApplyIdentity(string master, string? name, bool isActive)
    {
        if (ValidateName(name) is not { } validName)
        {
            return Result.Failure(NameInvalid(master));
        }

        Name = validName;
        IsActive = isActive;
        return Result.Success();
    }

    private static Error NameInvalid(string master) => Error.Validation($"core.{master}.name_invalid", "A name is 1 to 200 characters.");

    private static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > NameMaxLength ? null : trimmed;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,31}$")]
    private static partial Regex CodePattern();
}
