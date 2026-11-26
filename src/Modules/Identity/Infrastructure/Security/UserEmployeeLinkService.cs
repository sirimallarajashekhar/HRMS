using Hrms.Contracts;
using Hrms.Identity.Application;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Identity.Infrastructure.Security;

internal sealed class UserEmployeeLinkService(IdentityDbContext db, IEmployeeDirectory employees) : IUserEmployeeLinkService
{
    public async Task<Result<UserEmployeeLink>> SetAsync(Guid userId, Guid? employeeId, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<UserEmployeeLink>(Error.NotFound("identity.user.not_found", "The user does not exist."));
        }

        if (employeeId is { } employee)
        {
            if (employee == Guid.Empty || !await employees.ExistsAsync(employee, cancellationToken))
            {
                return Result.Failure<UserEmployeeLink>(Error.Validation("identity.user.employee_not_found", "The employee does not exist."));
            }

            if (await db.Users.AsNoTracking().AnyAsync(u => u.EmployeeId == employee && u.Id != userId, cancellationToken))
            {
                return Result.Failure<UserEmployeeLink>(Error.Conflict("identity.user.employee_already_linked", "Another account is already linked to that employee."));
            }
        }

        user.LinkEmployee(employeeId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<UserEmployeeLink>(Error.Conflict("identity.user.stale_version", "The account was changed by someone else; try again."));
        }

        return Result.Success(new UserEmployeeLink(userId, user.EmployeeId));
    }
}

/// <summary>Used when no employee module is registered: nobody exists, so nothing can be linked.</summary>
internal sealed class NoEmployeeDirectory : IEmployeeDirectory
{
    public Task<bool> ExistsAsync(Guid employeeId, CancellationToken cancellationToken) => Task.FromResult(false);
}
