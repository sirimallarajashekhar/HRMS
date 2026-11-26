using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Employee.Infrastructure;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Employee;

/// <summary>Opens the Employee context against a test database, as a deployment would, for seeding and for looking at what was stored.</summary>
public static class EmployeeTestDb
{
    public static readonly DateTime Start = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    public static EmployeeDbContext Context(string connectionString, TenantId? tenant, TimeProvider? time = null, Guid? actor = null)
    {
        var tenantContext = new TenantContext();
        if (tenant is { } id)
        {
            tenantContext.Set(id);
        }

        var options = (DbContextOptions<EmployeeDbContext>)EmployeeInfrastructureExtensions
            .UseEmployeeMySql(new DbContextOptionsBuilder<EmployeeDbContext>(), connectionString)
            .Options;

        return new EmployeeDbContext(options, tenantContext, new FixedActor(actor), time ?? new FixedTimeProvider(new DateTimeOffset(Start)));
    }
}
