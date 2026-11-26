using System.Text.RegularExpressions;
using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Tenants;

public enum TenantStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>
/// A customer organisation. The tenant is the root of isolation, so it is not itself tenant-scoped: the row is looked up by id
/// when a request arrives, and only the tenant directory reads it. Suspending a tenant locks everyone in it out at once.
/// </summary>
public sealed partial class Tenant : Entity
{
    public const int CodeMaxLength = 32;
    public const int NameMaxLength = 200;

    private Tenant(string code, string name, DateTime createdAt)
    {
        Code = code;
        Name = name;
        Status = TenantStatus.Active;
        CreatedAt = createdAt;
    }

    /// <summary>For the persistence layer only.</summary>
    private Tenant()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Short stable identifier, upper case, unique across all tenants.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public TenantStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public TenantId TenantId => new(Id);

    public static Result<Tenant> Create(string? code, string? name, DateTime createdAtUtc)
    {
        var normalisedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(normalisedCode))
        {
            return Result.Failure<Tenant>(Error.Validation(
                "core.tenant.code_invalid", "A tenant code is 2 to 32 characters: letters, digits and hyphens, starting with a letter or digit."));
        }

        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > NameMaxLength)
        {
            return Result.Failure<Tenant>(Error.Validation("core.tenant.name_invalid", "A tenant name is 1 to 200 characters."));
        }

        if (createdAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The creation time must be UTC.", nameof(createdAtUtc));
        }

        return Result.Success(new Tenant(normalisedCode, trimmedName, createdAtUtc));
    }

    public void Suspend() => Status = TenantStatus.Suspended;

    public void Activate() => Status = TenantStatus.Active;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,31}$")]
    private static partial Regex CodePattern();
}
