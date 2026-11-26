namespace Hrms.SharedKernel;

/// <summary>Scoped holder for the tenant of the current request or job. It can be set once.</summary>
public sealed class TenantContext : ITenantContext
{
    public TenantId? TenantId { get; private set; }

    public void Set(TenantId tenantId)
    {
        if (TenantId is not null)
        {
            throw new InvalidOperationException("The tenant for this operation has already been set.");
        }

        TenantId = tenantId;
    }
}
