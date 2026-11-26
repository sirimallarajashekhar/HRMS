using Hrms.SharedKernel;

namespace Hrms.Identity.Application;

/// <summary>Which employee a user account belongs to (null when it is not linked).</summary>
public sealed record UserEmployeeLink(Guid UserId, Guid? EmployeeId);

/// <summary>
/// Links a user account to the employee it belongs to, which is what makes the Self, DirectReports and AllReports data scopes work for that
/// user. One employee belongs to at most one account; the employee must exist for the tenant.
/// </summary>
public interface IUserEmployeeLinkService
{
    /// <param name="employeeId">The employee to link, or null to remove the link.</param>
    Task<Result<UserEmployeeLink>> SetAsync(Guid userId, Guid? employeeId, CancellationToken cancellationToken);
}
