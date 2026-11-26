namespace Hrms.Contracts;

/// <summary>
/// Finds an active tenant by the code a person types at sign-in. Unknown and suspended tenants both return null, so the
/// sign-in page cannot be used to learn which tenants exist.
/// </summary>
public interface ITenantLookup
{
    ValueTask<Guid?> FindActiveTenantIdByCodeAsync(string tenantCode, CancellationToken cancellationToken);
}
