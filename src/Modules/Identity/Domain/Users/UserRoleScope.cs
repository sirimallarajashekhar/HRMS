using Hrms.SharedKernel;

namespace Hrms.Identity.Domain.Users;

/// <summary>How far a role reaches for one user: whose records the permissions apply to.</summary>
public enum ScopeType
{
    /// <summary>Only the user own records.</summary>
    Self = 1,

    /// <summary>The people who report directly to the user.</summary>
    DirectReports = 2,

    /// <summary>Everyone below the user in the reporting chain.</summary>
    AllReports = 3,

    /// <summary>One department (scope value = org unit id).</summary>
    Department = 4,

    /// <summary>One location (scope value = location id).</summary>
    Location = 5,

    /// <summary>One legal entity (scope value = legal entity id).</summary>
    LegalEntity = 6,

    /// <summary>Everyone in the tenant.</summary>
    Tenant = 7,
}

/// <summary>
/// Gives a user a role with a scope. The same role can be held with several scopes (for example HR admin for two locations).
/// Scopes that point at one thing (department, location, legal entity) need its id; the others must not carry one.
/// "No value" is stored as an empty Guid so that a unique index treats two identical grants as the duplicates they are.
/// </summary>
public sealed class UserRoleScope : TenantEntity
{
    private UserRoleScope(Guid userId, Guid roleId, ScopeType scopeType, Guid scopeValue)
    {
        UserId = userId;
        RoleId = roleId;
        ScopeType = scopeType;
        ScopeValue = scopeValue;
    }

    /// <summary>For the persistence layer only.</summary>
    private UserRoleScope()
    {
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public ScopeType ScopeType { get; private set; }

    /// <summary>The department, location or legal entity id; <see cref="Guid.Empty"/> when the scope type needs none.</summary>
    public Guid ScopeValue { get; private set; }

    public static bool NeedsValue(ScopeType scopeType) => scopeType is ScopeType.Department or ScopeType.Location or ScopeType.LegalEntity;

    public static Result<UserRoleScope> Create(Guid userId, Guid roleId, ScopeType scopeType, Guid? scopeValue)
    {
        if (userId == Guid.Empty || roleId == Guid.Empty)
        {
            throw new ArgumentException("A user id and a role id are required.");
        }

        if (!Enum.IsDefined(scopeType))
        {
            return Result.Failure<UserRoleScope>(Error.Validation("identity.user_role_scope.scope_type_invalid", "That scope type does not exist."));
        }

        var hasValue = scopeValue is { } value && value != Guid.Empty;
        if (NeedsValue(scopeType) && !hasValue)
        {
            return Result.Failure<UserRoleScope>(Error.Validation(
                "identity.user_role_scope.value_required", $"The {scopeType} scope needs the id of the {scopeType.ToString().ToLowerInvariant()}."));
        }

        if (!NeedsValue(scopeType) && hasValue)
        {
            return Result.Failure<UserRoleScope>(Error.Validation(
                "identity.user_role_scope.value_not_allowed", $"The {scopeType} scope does not take a value."));
        }

        return Result.Success(new UserRoleScope(userId, roleId, scopeType, hasValue ? scopeValue!.Value : Guid.Empty));
    }
}
