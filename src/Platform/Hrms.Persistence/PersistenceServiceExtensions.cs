using System.Data.Common;
using Hrms.Persistence.Sql;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Hrms.Persistence;

public static class PersistenceServiceExtensions
{
    /// <summary>
    /// Registers the shared MySQL data source and the tenant-bound Dapper connection factory. Call once from the host.
    /// The data source is created on first use, so a host without a database still starts (health, ping, API description);
    /// using the database without a connection string fails with a clear message instead.
    /// </summary>
    public static IServiceCollection AddHrmsPersistence(this IServiceCollection services, string? mysqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<DbDataSource>(_ => string.IsNullOrWhiteSpace(mysqlConnectionString)
            ? throw new InvalidOperationException("The connection string 'ConnectionStrings:Mysql' is not configured.")
            : new MySqlDataSourceBuilder(MySqlConnectionStrings.ForHrms(mysqlConnectionString)).Build());

        services.AddScoped<TenantDbConnectionFactory>();
        return services;
    }
}
