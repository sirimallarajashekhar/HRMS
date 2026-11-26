using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Contracts.Workflow;
using Hrms.Persistence;
using Hrms.Workflow.Application;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hrms.Workflow.Infrastructure;

public static class WorkflowInfrastructureExtensions
{
    /// <summary>The server the schema is written for; fixed so startup never needs a round trip to detect it.</summary>
    public static readonly Version MySqlVersion = new(8, 4, 0);

    /// <summary>
    /// Registers the workflow database, the engine and <see cref="IWorkflowService"/>. The host also registers the modules that answer the
    /// approver lookups, a <see cref="WorkflowKeyDescriptor"/> singleton for every key its modules own, and (in the worker) their
    /// decision handlers.
    /// </summary>
    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services, string? mysqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<WorkflowDbContext>(options => UseWorkflowMySql(options, mysqlConnectionString));

        services.AddSingleton<WorkflowKeyCatalogue>();
        services.AddHostedService<CatalogueCheck>();
        services.AddScoped<ApproverResolver>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<WorkflowAccess>();
        services.AddScoped<InstanceReader>();
        services.AddScoped<IWorkflowService, WorkflowService>();
        services.AddScoped<IWorkflowInboxService, WorkflowInboxService>();
        services.AddScoped<IWorkflowAdminService, WorkflowAdminService>();
        services.AddScoped<IWorkflowDefinitionService, WorkflowDefinitionService>();
        services.AddScoped<IDelegationService, DelegationService>();
        services.AddScoped<IMasterUsageCheck, WorkflowUnitTypeUsageCheck>();
        services.TryAddSingleton<IEventPublisher, LoggingWorkflowEventPublisher>();
        return services;
    }

    /// <summary>Adds deadlines and result delivery. Register it in the worker host only.</summary>
    public static IServiceCollection AddWorkflowProcessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<DeadlineProcessor>();
        services.AddScoped<WorkflowOutboxRelay>();
        services.AddSingleton<WorkflowBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkflowBackgroundService>());
        return services;
    }

    public static DbContextOptionsBuilder UseWorkflowMySql(DbContextOptionsBuilder options, string? mysqlConnectionString)
    {
        if (string.IsNullOrWhiteSpace(mysqlConnectionString))
        {
            throw new InvalidOperationException("The connection string 'ConnectionStrings:Mysql' is not configured.");
        }

        return options.UseMySql(
            MySqlConnectionStrings.ForHrms(mysqlConnectionString),
            new MySqlServerVersion(MySqlVersion),
            mysql => mysql.MigrationsHistoryTable(WorkflowDbContext.MigrationsHistoryTable));
    }
}

/// <summary>Builds the key catalogue as the host starts, so a bad or duplicated declaration stops it there.</summary>
internal sealed class CatalogueCheck(WorkflowKeyCatalogue catalogue) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = catalogue.All.Count;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The default publisher: logs the type and tenant. Notifications (P0.10) replace it.</summary>
internal sealed partial class LoggingWorkflowEventPublisher(ILogger<LoggingWorkflowEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        LogEvent(logger, domainEvent.GetType().Name, domainEvent.TenantId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Domain event {EventType} for tenant {TenantId}")]
    private static partial void LogEvent(ILogger logger, string eventType, Guid tenantId);
}
