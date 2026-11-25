namespace Hrms.Contracts;

/// <summary>
/// Answers whether a tenant may use the system. The Core module implements it from its tenant table; the HTTP pipeline
/// asks it before any tenant-scoped request runs. Unknown and suspended tenants are indistinguishable to callers.
/// </summary>
public interface ITenantDirectory
{
    ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken);
}
