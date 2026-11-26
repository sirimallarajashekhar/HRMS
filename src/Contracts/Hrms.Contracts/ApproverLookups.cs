namespace Hrms.Contracts;

/// <summary>Where an employee sits on a date.</summary>
public sealed record EmployeePosition(Guid EmployeeId, Guid LegalEntityId, Guid LocationId, Guid OrgUnitId, Guid? ManagerId);

/// <summary>The employee module's answer to "where does this person sit", used to find approvers.</summary>
public interface IEmployeePositionLookup
{
    /// <returns>Null when the tenant has no such employee, or they are not employed on the date.</returns>
    Task<EmployeePosition?> GetPositionAsync(Guid employeeId, DateOnly asOf, CancellationToken cancellationToken);
}

/// <summary>One unit on the way up the organisation tree, with its head on the date asked about.</summary>
public sealed record OrgUnitHeadLink(Guid OrgUnitId, Guid OrgUnitTypeId, Guid? HeadEmployeeId);

/// <summary>Core's answer to "who heads this part of the organisation".</summary>
public interface IOrgUnitHeadLookup
{
    /// <returns>The unit followed by each unit above it on the date; empty when the tenant has no such unit.</returns>
    Task<IReadOnlyList<OrgUnitHeadLink>> GetHeadChainAsync(Guid orgUnitId, DateOnly asOf, CancellationToken cancellationToken);
}

/// <summary>A user account as the workflow engine sees it.</summary>
public sealed record ApproverAccount(Guid UserId, bool IsActive, Guid? EmployeeId);

/// <summary>The identity module's answers about who can be asked to approve. Every call is limited to the current tenant.</summary>
public interface IApproverDirectory
{
    /// <summary>The accounts among <paramref name="userIds"/> that exist in the tenant.</summary>
    Task<IReadOnlyList<ApproverAccount>> GetAccountsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>The account linked to an employee, if there is one (active or not).</summary>
    Task<ApproverAccount?> FindByEmployeeAsync(Guid employeeId, CancellationToken cancellationToken);

    Task<bool> RoleExistsAsync(string roleCode, CancellationToken cancellationToken);

    /// <summary>
    /// Active users who hold the role scoped to exactly this location. People who hold it for the whole tenant or a legal entity are not
    /// included. At most <paramref name="limit"/> are returned.
    /// </summary>
    Task<IReadOnlyList<ApproverAccount>> FindWithRoleAtLocationAsync(string roleCode, Guid locationId, int limit, CancellationToken cancellationToken);
}
