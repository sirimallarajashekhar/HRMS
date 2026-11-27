using Hrms.Contracts;
using Hrms.Identity.Application;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.Identity.Infrastructure.Security;
using Hrms.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hrms.Identity.Infrastructure;

public static class IdentityInfrastructureExtensions
{
    /// <summary>The server the schema is written for; fixed so startup never needs a round trip to detect it.</summary>
    public static readonly Version MySqlVersion = new(8, 4, 0);

    /// <summary>
    /// Registers the Identity database, the password hasher, the cached permission store (which replaces the deny-all default in
    /// either registration order) and the tenant seeder. Without a connection string the context fails with a clear message when
    /// first used, not at startup.
    /// </summary>
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, string? mysqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<IdentityDbContext>(options => UseIdentityMySql(options, mysqlConnectionString));

        services.AddOptions<PasswordHashingOptions>().BindConfiguration(PasswordHashingOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PasswordHashingOptions>, PasswordHashingOptionsValidator>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();

        services.AddSingleton<PermissionCache>();
        services.AddScoped<IPermissionStore, PermissionStore>();
        services.AddScoped<TenantIdentitySeeder>();
        services.AddScoped<SecurityPolicyReader>();
        services.AddScoped<IPasswordSignInService, PasswordSignInService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IUserEmployeeLinkService, UserEmployeeLinkService>();
        services.AddScoped<IAuditRecordAccess, IdentityAuditAccess>();
        services.TryAddScoped<IEmployeeDirectory, NoEmployeeDirectory>();

        // The employee module (P0.6) registers the real expander, which replaces this default because it is added with TryAdd here.
        services.TryAddScoped<IEmployeeScopeExpander, SelfOnlyScopeExpander>();
        services.AddScoped<IDataScopeService, DataScopeService>();
        services.AddScoped<IApproverDirectory, ApproverDirectory>();
        services.AddScoped<OAuthClientSeeder>();

        // The token server stores (applications, authorizations, tokens). The server and validation halves are configured by the API layer.
        services.AddOpenIddict().AddCore(core => core.UseEntityFrameworkCore().UseDbContext<IdentityDbContext>().ReplaceDefaultEntities<Guid>());
        return services;
    }

    /// <summary>
    /// For a background host that only needs to ask who can approve: the Identity database and the directory, without the sign-in
    /// services or the token stores.
    /// </summary>
    public static IServiceCollection AddIdentityDirectory(this IServiceCollection services, string? mysqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<IdentityDbContext>(options => UseIdentityMySql(options, mysqlConnectionString));
        services.AddScoped<IApproverDirectory, ApproverDirectory>();
        return services;
    }

    public static DbContextOptionsBuilder UseIdentityMySql(DbContextOptionsBuilder options, string? mysqlConnectionString)
    {
        if (string.IsNullOrWhiteSpace(mysqlConnectionString))
        {
            throw new InvalidOperationException("The connection string 'ConnectionStrings:Mysql' is not configured.");
        }

        return options.UseMySql(
            MySqlConnectionStrings.ForHrms(mysqlConnectionString),
            new MySqlServerVersion(MySqlVersion),
            mysql => mysql.MigrationsHistoryTable(IdentityDbContext.MigrationsHistoryTable))
            .UseOpenIddict<Guid>();
    }
}
