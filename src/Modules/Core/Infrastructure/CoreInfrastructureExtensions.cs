using Hrms.Contracts;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.Infrastructure;

public static class CoreInfrastructureExtensions
{
    /// <summary>The server the schema is written for; fixed so startup never needs a round trip to detect it.</summary>
    public static readonly Version MySqlVersion = new(8, 4, 0);

    /// <summary>
    /// Registers the Core database and the real tenant directory. Call after AddHrmsTenancy so this directory replaces its
    /// deny-all default. Without a connection string the context fails with a clear message when first used, not at startup.
    /// </summary>
    public static IServiceCollection AddCoreInfrastructure(this IServiceCollection services, string? mysqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<CoreDbContext>(options => UseCoreMySql(options, mysqlConnectionString));
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        return services;
    }

    public static DbContextOptionsBuilder UseCoreMySql(DbContextOptionsBuilder options, string? mysqlConnectionString)
    {
        if (string.IsNullOrWhiteSpace(mysqlConnectionString))
        {
            throw new InvalidOperationException("The connection string 'ConnectionStrings:Mysql' is not configured.");
        }

        return options.UseMySql(
            MySqlConnectionStrings.ForHrms(mysqlConnectionString),
            new MySqlServerVersion(MySqlVersion),
            mysql => mysql.MigrationsHistoryTable(CoreDbContext.MigrationsHistoryTable));
    }
}
