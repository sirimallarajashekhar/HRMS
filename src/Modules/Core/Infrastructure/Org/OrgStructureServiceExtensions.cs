using Hrms.Core.Application.Org;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.Infrastructure.Org;

internal static class OrgStructureServiceExtensions
{
    public static IServiceCollection AddOrgStructure(this IServiceCollection services)
    {
        services.AddScoped<IOrgUnitTypeService, OrgUnitTypeService>();
        services.AddScoped<IOrgUnitService, OrgUnitService>();
        services.AddScoped<ILocationService, LocationService>();
        services.AddScoped<IGeofenceService, GeofenceService>();
        services.AddScoped<IGradeService, GradeService>();
        services.AddScoped<IDesignationService, DesignationService>();
        services.AddScoped<ICostCentreService, CostCentreService>();
        services.AddScoped<IEmployeeTypeService, EmployeeTypeService>();
        return services;
    }
}
