using System.Data.Common;
using Hrms.Persistence.Security;
using Hrms.Persistence.Sql;
using Hrms.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        // Field encryption for sensitive values. The host refuses to start without a valid key (and without the dev key being absent in
        // production), so a value is never stored unencrypted because a key was forgotten.
        services.AddOptions<FieldEncryptionOptions>().BindConfiguration(FieldEncryptionOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<FieldEncryptionOptions>, FieldEncryptionOptionsValidator>();
        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        return services;
    }
}
