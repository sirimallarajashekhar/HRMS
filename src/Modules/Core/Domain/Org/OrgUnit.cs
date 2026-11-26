using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>
/// A node of the organisation tree. The unit itself holds identity only; where it hangs in the tree is an effective-dated
/// <see cref="OrgUnitParent"/> series so a reorganisation keeps history. The unit's type is fixed at creation.
/// </summary>
public sealed class OrgUnit : CodedMaster
{
    public const string Master = "org_unit";

    private OrgUnit(string code, string name, Guid orgUnitTypeId)
        : base(code, name) => OrgUnitTypeId = orgUnitTypeId;

    /// <summary>For the persistence layer only.</summary>
    private OrgUnit()
    {
    }

    public Guid OrgUnitTypeId { get; private set; }

    public static Result<OrgUnit> Create(string? code, string? name, Guid orgUnitTypeId)
    {
        var identity = ValidateIdentity(Master, code, name);
        return identity.IsFailure
            ? Result.Failure<OrgUnit>(identity.Error)
            : Result.Success(new OrgUnit(identity.Value.Code, identity.Value.Name, orgUnitTypeId));
    }

    public Result Update(string? name, bool isActive) => ApplyIdentity(Master, name, isActive);
}
