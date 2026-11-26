using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>Permanent, contract, intern and so on. The payroll flag lets later modules include or skip a type without naming it in code.</summary>
public sealed class EmployeeType : CodedMaster
{
    public const string Master = "employee_type";

    private EmployeeType(string code, string name, bool isPayrollEligible)
        : base(code, name) => IsPayrollEligible = isPayrollEligible;

    /// <summary>For the persistence layer only.</summary>
    private EmployeeType()
    {
    }

    public bool IsPayrollEligible { get; private set; }

    public static Result<EmployeeType> Create(string? code, string? name, bool isPayrollEligible)
    {
        var identity = ValidateIdentity(Master, code, name);
        return identity.IsFailure
            ? Result.Failure<EmployeeType>(identity.Error)
            : Result.Success(new EmployeeType(identity.Value.Code, identity.Value.Name, isPayrollEligible));
    }

    public Result Update(string? name, bool isPayrollEligible, bool isActive)
    {
        var applied = ApplyIdentity(Master, name, isActive);
        if (applied.IsSuccess)
        {
            IsPayrollEligible = isPayrollEligible;
        }

        return applied;
    }
}
