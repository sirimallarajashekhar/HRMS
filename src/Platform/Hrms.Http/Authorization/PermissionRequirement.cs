using Microsoft.AspNetCore.Authorization;

namespace Hrms.Http.Authorization;

/// <summary>
/// The caller must be authenticated, belong to the tenant the request resolved to, and (when
/// <see cref="Permission"/> is set) hold that permission through one of their roles.
/// </summary>
public sealed class PermissionRequirement(string? permission) : IAuthorizationRequirement
{
    public string? Permission { get; } = permission;
}
