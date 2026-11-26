namespace Hrms.Contracts;

/// <summary>The organisation-structure masters other modules may reference, by the name they report usage under.</summary>
public static class MasterKinds
{
    public const string OrgUnitType = "org_unit_type";
    public const string OrgUnit = "org_unit";
    public const string Location = "location";
    public const string Grade = "grade";
    public const string Designation = "designation";
    public const string CostCentre = "cost_centre";
    public const string EmployeeType = "employee_type";
}

/// <summary>
/// Lets a module that references a Core master (the employee module references grades, designations, locations and more) say
/// whether a row is still in use, so Core can refuse to delete it without reading another module's tables. Register one per
/// master kind a module references; Core asks every registered check for the kind being deleted and refuses if any says yes.
/// Implementations read only their own tables, filtered by the current tenant.
/// </summary>
public interface IMasterUsageCheck
{
    /// <summary>One of <see cref="MasterKinds"/>.</summary>
    string Master { get; }

    Task<bool> IsInUseAsync(Guid id, CancellationToken cancellationToken);
}
