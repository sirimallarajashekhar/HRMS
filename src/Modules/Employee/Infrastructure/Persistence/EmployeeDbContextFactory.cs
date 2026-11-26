using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hrms.Employee.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef`. Adding a migration never connects; to apply or script against a real server set
/// HRMS_MIGRATIONS_CONNECTION. The tenant context is empty because migrations never run tenant queries.
/// </summary>
internal sealed class EmployeeDbContextFactory : IDesignTimeDbContextFactory<EmployeeDbContext>
{
    public const string ConnectionVariable = "HRMS_MIGRATIONS_CONNECTION";

    public EmployeeDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable)
                         ?? "Server=127.0.0.1;Port=3306;Database=hrms;User ID=design;Password=design";

        var options = EmployeeInfrastructureExtensions
            .UseEmployeeMySql(new DbContextOptionsBuilder<EmployeeDbContext>(), connection);

        return new EmployeeDbContext(
            (DbContextOptions<EmployeeDbContext>)options.Options,
            new TenantContext(),
            new SystemActorContext(),
            TimeProvider.System);
    }
}
