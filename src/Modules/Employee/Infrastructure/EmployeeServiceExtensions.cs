using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Employee.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hrms.Employee.Infrastructure;

public static class EmployeeServiceExtensions
{
    internal static IServiceCollection AddEmployeeServices(this IServiceCollection services)
    {
        services.AddScoped<EmployeeAccess>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<ISeparationService, SeparationService>();
        services.AddScoped<ICodeRuleService, CodeRuleService>();
        services.AddScoped<IStatutoryIdService, StatutoryIdService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IConsentService, ConsentService>();
        services.AddScoped<IAuditRecordAccess, EmployeeAuditAccess>();
        services.AddSingleton<IStatutoryIdProtector, StatutoryIdProtector>();

        // The real scope expander replaces the identity module's self-only default, whichever module registered first.
        services.Replace(ServiceDescriptor.Scoped<IEmployeeScopeExpander, EmployeeScopeExpander>());

        services.AddEmployeeLookups();

        // Core refuses to delete a master that a job period still points at.
        foreach (var kind in new[]
                 {
                     MasterKinds.Location, MasterKinds.OrgUnit, MasterKinds.Grade, MasterKinds.Designation, MasterKinds.CostCentre, MasterKinds.EmployeeType,
                 })
        {
            services.AddScoped<IMasterUsageCheck>(sp => new JobReferenceUsageCheck(sp.GetRequiredService<Persistence.EmployeeDbContext>(), kind));
        }

        // Defaults that other modules (country packs, a message broker) replace.
        services.TryAddScoped<ICurrentUser, NoCurrentUser>();
        services.TryAddScoped<IStatutoryIdValidator, BasicStatutoryIdValidator>();
        return services;
    }

    /// <summary>What other modules ask of this one, and the default publisher. Needed by the API and by the worker.</summary>
    internal static IServiceCollection AddEmployeeLookups(this IServiceCollection services)
    {
        services.AddScoped<IEmployeeDirectory, EmployeeDirectory>();
        services.AddScoped<IEmployeePositionLookup, EmployeePositionLookup>();
        services.TryAddSingleton<IEventPublisher, LoggingEventPublisher>();
        return services;
    }

    /// <summary>Adds the background service that emits due events and relays the outbox. Register it in the worker host only.</summary>
    public static IServiceCollection AddEmployeeEventProcessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<EmployeeEventService>();
        services.AddHostedService(sp => sp.GetRequiredService<EmployeeEventService>());
        return services;
    }
}
