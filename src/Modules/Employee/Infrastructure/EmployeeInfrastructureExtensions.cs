using Hrms.Employee.Infrastructure.Persistence;
using Hrms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Employee.Infrastructure;

public static class EmployeeInfrastructureExtensions
{
    /// <summary>The server the schema is written for; fixed so startup never needs a round trip to detect it.</summary>
    public static readonly Version MySqlVersion = new(8, 4, 0);

    /// <summary>
    /// Registers the Employee database and everything built on it: the employee services, the real data-scope expander (which replaces the
    /// self-only default of the identity module), the usage checks that protect Core masters from deletion while employees use them,
    /// the event outbox and its relay. Without a connection string the context fails with a clear message when first used.
    /// </summary>
    public static IServiceCollection AddEmployeeInfrastructure(this IServiceCollection services, string? mysqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<EmployeeDbContext>(options => UseEmployeeMySql(options, mysqlConnectionString));
        services.AddEmployeeServices();
        return services;
    }

    public static DbContextOptionsBuilder UseEmployeeMySql(DbContextOptionsBuilder options, string? mysqlConnectionString)
    {
        if (string.IsNullOrWhiteSpace(mysqlConnectionString))
        {
            throw new InvalidOperationException("The connection string 'ConnectionStrings:Mysql' is not configured.");
        }

        return options.UseMySql(
            MySqlConnectionStrings.ForHrms(mysqlConnectionString),
            new MySqlServerVersion(MySqlVersion),
            mysql => mysql.MigrationsHistoryTable(EmployeeDbContext.MigrationsHistoryTable));
    }
}
