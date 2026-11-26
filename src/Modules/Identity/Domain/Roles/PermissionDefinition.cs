using Hrms.Contracts;
using Hrms.SharedKernel;

namespace Hrms.Identity.Domain.Roles;

/// <summary>
/// A row of the global permission catalogue (the permission table). The code constants in Hrms.Contracts.Permissions are the source of
/// truth; this table mirrors them so grants can reference a real permission. It is platform data, shared by every tenant.
/// </summary>
public sealed class PermissionDefinition
{
    private PermissionDefinition(string code, string module)
    {
        Code = code;
        Module = module;
    }

    /// <summary>For the persistence layer only.</summary>
    private PermissionDefinition()
    {
        Code = Module = string.Empty;
    }

    public string Code { get; private set; }

    /// <summary>The first segment of the code, for grouping in screens.</summary>
    public string Module { get; private set; }

    public static Result<PermissionDefinition> Create(string? code) =>
        Permissions.IsKnown(code)
            ? Result.Success(new PermissionDefinition(code!, code![..code!.IndexOf('.', StringComparison.Ordinal)]))
            : Result.Failure<PermissionDefinition>(Error.Validation(
                "identity.permission.unknown", "That permission is not in the permission catalogue."));
}
