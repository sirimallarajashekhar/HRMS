using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>A cost centre that payroll and expenses are booked against, optionally owned by one legal entity. Flat: there is no hierarchy.</summary>
public sealed class CostCentre : CodedMaster
{
    public const string Master = "cost_centre";

    private CostCentre(string code, string name, Guid? legalEntityId)
        : base(code, name) => LegalEntityId = legalEntityId;

    /// <summary>For the persistence layer only.</summary>
    private CostCentre()
    {
    }

    public Guid? LegalEntityId { get; private set; }

    public static Result<CostCentre> Create(string? code, string? name, Guid? legalEntityId)
    {
        var identity = ValidateIdentity(Master, code, name);
        return identity.IsFailure
            ? Result.Failure<CostCentre>(identity.Error)
            : Result.Success(new CostCentre(identity.Value.Code, identity.Value.Name, legalEntityId));
    }

    public Result Update(string? name, Guid? legalEntityId, bool isActive)
    {
        var applied = ApplyIdentity(Master, name, isActive);
        if (applied.IsSuccess)
        {
            LegalEntityId = legalEntityId;
        }

        return applied;
    }
}
