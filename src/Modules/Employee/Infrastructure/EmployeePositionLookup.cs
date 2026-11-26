using Hrms.Contracts;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Employee.Infrastructure;

/// <summary>Where an employee sits on a date, for other modules (the workflow engine finds approvers with it).</summary>
internal sealed class EmployeePositionLookup(EmployeeDbContext db) : IEmployeePositionLookup
{
    public async Task<EmployeePosition?> GetPositionAsync(Guid employeeId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var employed = await db.Employees.AsNoTracking().AnyAsync(
            e => e.Id == employeeId && e.JoiningDate <= asOf && (e.LastWorkingDay == null || e.LastWorkingDay >= asOf), cancellationToken);
        if (!employed)
        {
            return null;
        }

        return await db.Jobs.AsNoTracking().ValidOn(asOf)
            .Where(j => j.EmployeeId == employeeId)
            .Select(j => new EmployeePosition(j.EmployeeId, j.LegalEntityId, j.LocationId, j.OrgUnitId, j.ManagerId))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
