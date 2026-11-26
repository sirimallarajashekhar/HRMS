using Hrms.Core.Infrastructure;
using Hrms.Employee.Infrastructure;
using Hrms.Persistence;
using Hrms.SharedKernel;

namespace Hrms.Worker;

public static class WorkerServices
{
    /// <summary>
    /// Everything the background jobs need, and nothing that serves a web request: a module's user-facing services depend on
    /// things only the API host has (the signed-in user, data scopes), and Development checks every registration at startup. A test
    /// builds exactly this, so a registration the worker cannot satisfy fails the build.
    /// </summary>
    public static IServiceCollection AddHrmsWorker(this IServiceCollection services, string? mysqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Hangfire and queue consumers are registered here from P0.2 / P1.3 onwards.
        services.AddHostedService<HeartbeatService>();

        // There is no web request here, so each job sets the tenant context itself, and the actor is the system.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IActorContext, SystemActorContext>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddHrmsPersistence(mysqlConnectionString);
        services.AddCoreInfrastructure(mysqlConnectionString);

        // The employee module: events that become due, and the outbox relay.
        services.AddEmployeeBackground(mysqlConnectionString);
        services.AddEmployeeEventProcessing();
        return services;
    }
}
