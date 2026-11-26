namespace Hrms.Contracts;

/// <summary>
/// Resolves the permissions a set of roles grants within a tenant. The identity module implements it from role_permission with a
/// short cache, so a changed role takes effect within about a minute; until it does, the default denies everything.
/// </summary>
public interface IPermissionStore
{
    ValueTask<IReadOnlySet<string>> GetPermissionsAsync(
        Guid tenantId,
        IReadOnlyCollection<string> roleCodes,
        CancellationToken cancellationToken);
}
