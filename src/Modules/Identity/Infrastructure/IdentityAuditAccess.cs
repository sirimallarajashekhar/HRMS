using Hrms.Contracts;

namespace Hrms.Identity.Infrastructure;

/// <summary>
/// Accounts, roles and the security policy have no narrower view than the whole tenant, so their history needs a signed-in user whose
/// scope for the permission is tenant-wide.
/// </summary>
internal sealed class IdentityAuditAccess(IDataScopeService scopes, ICurrentUser user) : IAuditRecordAccess
{
    public IReadOnlyList<AuditRecordType> RecordTypes { get; } =
    [
        new("user_account",
        [
            new("user_account", Permissions.Identity.UserView),
            new("user_role_scope", Permissions.Identity.UserView),
        ]),
        new("role", [new("role", Permissions.Identity.RoleView), new("role_permission", Permissions.Identity.RoleView)]),
        new("tenant_security_policy", [new("tenant_security_policy", Permissions.Identity.SecurityPolicyManage)]),
    ];

    public async Task<bool> IsInScopeAsync(string rootType, Guid recordId, string permission, CancellationToken cancellationToken) =>
        user.UserId is { } userId && (await scopes.ResolveAsync(userId, permission, cancellationToken)).IsTenantWide;
}
