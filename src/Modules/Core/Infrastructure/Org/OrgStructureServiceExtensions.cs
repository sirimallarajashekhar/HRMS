using Hrms.Core.Application.Org;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.Infrastructure.Org;

internal static class OrgStructureServiceExtensions
{
    public static IServiceCollection AddOrgStructure(this IServiceCollection services)
    {
        services.AddScoped<IOrgUnitTypeService, OrgUnitTypeService>();
        services.AddScoped<IOrgUnitService, OrgUnitService>();
        services.AddScoped<IOrgUnitHeadService, OrgUnitHeadService>();
        services.AddScoped<Hrms.Contracts.IOrgUnitHeadLookup, OrgUnitHeadLookup>();
        services.AddScoped<ILocationService, LocationService>();
        services.AddScoped<IGeofenceService, GeofenceService>();
        services.AddScoped<IGradeService, GradeService>();
        services.AddScoped<IDesignationService, DesignationService>();
        services.AddScoped<ICostCentreService, CostCentreService>();
        services.AddScoped<IEmployeeTypeService, EmployeeTypeService>();
        services.AddScoped<Hrms.Contracts.IOrgStructureLookup, OrgStructureLookup>();
        return services;
    }
}
