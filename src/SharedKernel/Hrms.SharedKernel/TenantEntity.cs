namespace Hrms.SharedKernel;

/// <summary>
/// Base of every business table: tenant_id, audit columns, soft delete and an optimistic-concurrency token.
/// The persistence layer owns these columns: it stamps them on save and filters on them in every query.
/// Domain code never sets them.
/// </summary>
public abstract class TenantEntity : Entity
{
    public TenantId TenantId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTime? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }

    /// <summary>Incremented on every update; an update that loses the race fails instead of overwriting.</summary>
    public long RowVersion { get; private set; }
}
