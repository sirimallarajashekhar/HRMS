using System.Text.RegularExpressions;
using Hrms.Contracts;
using Hrms.SharedKernel;

namespace Hrms.Identity.Domain.Roles;

/// <summary>
/// A named set of permissions, defined per tenant. System roles are the shipped defaults: they can be renamed but their MFA
/// requirement cannot be switched off, so a tenant cannot quietly weaken the protection on its own administrators.
/// </summary>
[Audited]
public sealed partial class Role : TenantEntity
{
    public const int CodeMaxLength = 64;
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    private Role(string code, string name, string? description, bool isSystem, bool requiresMfa)
    {
        Code = code;
        Name = name;
        Description = description;
        IsSystem = isSystem;
        RequiresMfa = requiresMfa;
    }

    /// <summary>For the persistence layer only.</summary>
    private Role()
    {
        Code = Name = string.Empty;
    }

    /// <summary>Lower snake case, unique per tenant; this is the value carried in the token role claim.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public bool IsSystem { get; private set; }

    /// <summary>Users holding this role must sign in with a second factor.</summary>
    public bool RequiresMfa { get; private set; }

    public static Result<Role> Create(string? code, string? name, string? description, bool isSystem, bool requiresMfa)
    {
        var normalisedCode = code?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(normalisedCode))
        {
            return Result.Failure<Role>(Error.Validation(
                "identity.role.code_invalid", "A role code is 2 to 64 characters in lower snake case, for example hr_admin."));
        }

        var checkedName = ValidateName(name);
        if (checkedName is null)
        {
            return Result.Failure<Role>(Error.Validation("identity.role.name_invalid", "A role name is 1 to 100 characters."));
        }

        if (ValidateDescription(description) is null && description is not null)
        {
            return Result.Failure<Role>(Error.Validation("identity.role.description_invalid", "A role description is at most 500 characters."));
        }

        return Result.Success(new Role(normalisedCode, checkedName, description?.Trim(), isSystem, requiresMfa));
    }

    /// <summary>Renames the role and updates its MFA requirement; a system role cannot have MFA switched off.</summary>
    public Result Update(string? name, string? description, bool requiresMfa)
    {
        var checkedName = ValidateName(name);
        if (checkedName is null)
        {
            return Result.Failure(Error.Validation("identity.role.name_invalid", "A role name is 1 to 100 characters."));
        }

        if (ValidateDescription(description) is null && description is not null)
        {
            return Result.Failure(Error.Validation("identity.role.description_invalid", "A role description is at most 500 characters."));
        }

        if (IsSystem && RequiresMfa && !requiresMfa)
        {
            return Result.Failure(Error.Forbidden("identity.role.system_mfa_required", "A system role cannot stop requiring MFA."));
        }

        Name = checkedName;
        Description = description?.Trim();
        RequiresMfa = requiresMfa;
        return Result.Success();
    }

    private static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > NameMaxLength ? null : trimmed;
    }

    private static string? ValidateDescription(string? description) =>
        description is null || description.Trim().Length <= DescriptionMaxLength ? description?.Trim() ?? string.Empty : null;

    [GeneratedRegex("^[a-z][a-z0-9_]{1,63}$")]
    private static partial Regex CodePattern();
}

/// <summary>Grants one catalogued permission to one role (stored in the role_permission table).</summary>
[Audited(RootType = "role", RootKey = nameof(RoleId))]
public sealed class RoleGrant : TenantEntity
{
    private RoleGrant(Guid roleId, string permissionCode)
    {
        RoleId = roleId;
        PermissionCode = permissionCode;
    }

    /// <summary>For the persistence layer only.</summary>
    private RoleGrant()
    {
        PermissionCode = string.Empty;
    }

    public Guid RoleId { get; private set; }

    public string PermissionCode { get; private set; }

    public static Result<RoleGrant> Create(Guid roleId, string? permissionCode)
    {
        if (roleId == Guid.Empty)
        {
            throw new ArgumentException("A role id is required.", nameof(roleId));
        }

        return Permissions.IsKnown(permissionCode)
            ? Result.Success(new RoleGrant(roleId, permissionCode!))
            : Result.Failure<RoleGrant>(Error.Validation(
                "identity.role_permission.unknown_permission", "That permission is not in the permission catalogue."));
    }
}
