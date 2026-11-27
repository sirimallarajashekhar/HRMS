using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>
/// Where an org unit hangs in the tree for a period: one row per period, never overwritten (a move closes the open row and adds
/// the next). A null parent means the unit is a root for that period. A tenant may have several roots.
/// </summary>
[Audited(RootType = "org_unit", RootKey = nameof(OrgUnitId))]
public sealed class OrgUnitParent : EffectiveDatedEntity
{
    private OrgUnitParent(Guid orgUnitId, Guid? parentId, DateOnly effectiveFrom)
        : base(effectiveFrom)
    {
        OrgUnitId = orgUnitId;
        ParentId = parentId;
    }

    /// <summary>For the persistence layer only.</summary>
    private OrgUnitParent()
    {
    }

    public Guid OrgUnitId { get; private set; }

    public Guid? ParentId { get; private set; }

    public static OrgUnitParent Create(Guid orgUnitId, Guid? parentId, DateOnly effectiveFrom) => new(orgUnitId, parentId, effectiveFrom);
}
