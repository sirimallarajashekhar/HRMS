using Hrms.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Hrms.Http.Authorization;

/// <summary>
/// Requires a permission from the catalogue (<see cref="Permissions"/>), for example
/// <c>[RequirePermission(Permissions.Core.LegalEntityView)]</c>. A code that is not in the catalogue is a programming error and
/// fails when the endpoint is first authorised, not by silently denying everyone.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public RequirePermissionAttribute(string permission)
        : base(PolicyPrefix + permission)
    {
        Permission = permission;
    }

    public string Permission { get; }
}
