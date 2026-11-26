using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>
/// Who heads an org unit for a period: effective-dated and never overwritten, like the unit's place in the tree. A unit may have no
/// head for any stretch of time. The employee is a plain id: employees belong to another module.
/// </summary>
public sealed class OrgUnitHead : EffectiveDatedEntity
{
    private OrgUnitHead(Guid orgUnitId, Guid employeeId, DateOnly effectiveFrom)
        : base(effectiveFrom)
    {
        OrgUnitId = orgUnitId;
        EmployeeId = employeeId;
    }

    /// <summary>For the persistence layer only.</summary>
    private OrgUnitHead()
    {
    }

    public Guid OrgUnitId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public static OrgUnitHead Create(Guid orgUnitId, Guid employeeId, DateOnly effectiveFrom) => new(orgUnitId, employeeId, effectiveFrom);

    /// <summary>
    /// A change starts after the latest period began and, if that period has already been closed, after it ended; and ending a headship
    /// needs one that is still open.
    /// </summary>
    public static Result CheckChange(OrgUnitHead? latest, Guid? newEmployeeId, DateOnly effectiveFrom)
    {
        if (newEmployeeId is null && latest is not { EffectiveTo: null })
        {
            return Result.Failure(Error.Validation("core.org_unit.no_head", "The unit has no head to remove."));
        }

        return latest is not null && effectiveFrom <= (latest.EffectiveTo ?? latest.EffectiveFrom)
            ? Result.Failure(Error.Validation("core.org_unit.head_date_invalid", "The change must start after the latest head period."))
            : Result.Success();
    }
}
