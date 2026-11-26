using Hrms.Contracts;
using Hrms.Core.Application.Org;
using Hrms.Http;
using Hrms.Http.Authorization;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Hrms.Core.Api.Org;

/// <summary>
/// The organisation-structure masters: /org-unit-types, /org-units, /locations (and /locations/{id}/geofences), /grades, /designations,
/// /cost-centres and /employee-types. Reads need the master's view permission, writes its manage permission. Request bodies are capped,
/// lists are paged (at most 200 rows), and a change must carry the rowVersion it was based on.
/// </summary>
public static class OrgStructureEndpoints
{
    /// <summary>Largest request body the org endpoints accept (a polygon is capped at 32 KB of text on top of this).</summary>
    public const long MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapOrgStructureEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        MapMaster<IOrgUnitTypeService, CreateOrgUnitTypeRequest, UpdateOrgUnitTypeRequest, OrgUnitTypeView>(
            routes, "org-unit-types", "Org unit types", Permissions.Core.OrgUnitTypeView, Permissions.Core.OrgUnitTypeManage, relatedParameter: null);
        MapMaster<IGradeService, CreateGradeRequest, UpdateGradeRequest, GradeView>(
            routes, "grades", "Grades", Permissions.Core.GradeView, Permissions.Core.GradeManage, relatedParameter: null);
        MapMaster<IDesignationService, CreateDesignationRequest, UpdateDesignationRequest, DesignationView>(
            routes, "designations", "Designations", Permissions.Core.DesignationView, Permissions.Core.DesignationManage, "gradeId");
        MapMaster<ICostCentreService, CreateCostCentreRequest, UpdateCostCentreRequest, CostCentreView>(
            routes, "cost-centres", "Cost centres", Permissions.Core.CostCentreView, Permissions.Core.CostCentreManage, "legalEntityId");
        MapMaster<IEmployeeTypeService, CreateEmployeeTypeRequest, UpdateEmployeeTypeRequest, EmployeeTypeView>(
            routes, "employee-types", "Employee types", Permissions.Core.EmployeeTypeView, Permissions.Core.EmployeeTypeManage, relatedParameter: null);
        MapMaster<ILocationService, CreateLocationRequest, UpdateLocationRequest, LocationView>(
            routes, "locations", "Locations", Permissions.Core.LocationView, Permissions.Core.LocationManage, relatedParameter: null);

        MapGeofences(routes);
        MapOrgUnits(routes);
        return routes;
    }

    private static void MapMaster<TService, TCreate, TUpdate, TView>(
        IEndpointRouteBuilder routes,
        string route,
        string tag,
        string viewPermission,
        string managePermission,
        string? relatedParameter)
        where TService : IMasterService<TCreate, TUpdate, TView>
        where TUpdate : IUpdateRequest
        where TView : IIdentified
    {
        var group = routes.MapGroup($"/{route}").WithTags(tag).WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (HttpRequest http, TService service, int? page, int? pageSize, string? search, bool? active, CancellationToken ct) =>
            {
                var (relatedId, relatedError) = ReadRelated(http, relatedParameter);
                if (relatedError is not null)
                {
                    return relatedError.ToProblem();
                }

                var query = new MasterQuery(page ?? 1, pageSize ?? MasterQuery.DefaultPageSize, search, active, relatedId);
                return Results.Ok(await service.ListAsync(query, ct));
            })
            .RequirePermission(viewPermission)
            .WithSummary($"Lists {tag.ToLowerInvariant()}, paged (at most {MasterQuery.MaxPageSize} per page), optionally filtered by search text and active flag.");

        group.MapGet("/{id:guid}", async (Guid id, TService service, CancellationToken ct) =>
                (await service.GetAsync(id, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(viewPermission)
            .WithSummary("Gets one record; the rowVersion in it is what an update must send back.");

        group.MapPost(string.Empty, async (TCreate? request, HttpRequest http, TService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.CreateAsync(request, ct)).ToHttpResult(view => Results.Created($"{http.Path.Value!.TrimEnd('/')}/{view.Id}", view)))
            .RequirePermission(managePermission)
            .WithSummary("Creates a record. The code is unique per tenant and is stored upper case.");

        group.MapPut("/{id:guid}", async (Guid id, TUpdate? request, TService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.UpdateAsync(id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(managePermission)
            .WithSummary("Changes a record. The code never changes; send the rowVersion you read, a stale one gets 409.");

        group.MapDelete("/{id:guid}", async (Guid id, TService service, CancellationToken ct) =>
                (await service.DeleteAsync(id, ct)).ToHttpResult(() => Results.NoContent()))
            .RequirePermission(managePermission)
            .WithSummary("Soft deletes a record that nothing uses (409 when it is in use; deactivate it instead).");
    }

    private static IResult BodyRequired() => Error.Validation("core.request.body_required", "A JSON body is required.").ToProblem();

    /// <summary>Reads the optional related-id filter; a value that is not a GUID is a 422, not a silently ignored filter.</summary>
    private static (Guid? Id, Error? Error) ReadRelated(HttpRequest http, string? parameter)
    {
        if (parameter is null || !http.Query.TryGetValue(parameter, out var raw) || raw.Count == 0 || string.IsNullOrEmpty(raw[0]))
        {
            return (null, null);
        }

        return Guid.TryParse(raw[0], out var id)
            ? (id, null)
            : (null, Error.Validation("core.query.invalid", $"{parameter} must be a GUID."));
    }

    // --- geofences ---------------------------------------------------------------------------------------------------------------

    private static void MapGeofences(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/locations/{locationId:guid}/geofences").WithTags("Geofences")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (Guid locationId, IGeofenceService service, int? page, int? pageSize, string? search, bool? active, CancellationToken ct) =>
                (await service.ListAsync(locationId, new MasterQuery(page ?? 1, pageSize ?? MasterQuery.DefaultPageSize, search, active), ct))
                .ToHttpResult(result => Results.Ok(result)))
            .RequirePermission(Permissions.Core.GeofenceView)
            .WithSummary("Lists the geofences of a location, paged.");

        group.MapGet("/{id:guid}", async (Guid locationId, Guid id, IGeofenceService service, CancellationToken ct) =>
                (await service.GetAsync(locationId, id, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Core.GeofenceView)
            .WithSummary("Gets one geofence of a location.");

        group.MapPost(string.Empty, async (Guid locationId, CreateGeofenceRequest? request, HttpRequest http, IGeofenceService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.CreateAsync(locationId, request, ct)).ToHttpResult(view => Results.Created($"{http.Path.Value!.TrimEnd('/')}/{view.Id}", view)))
            .RequirePermission(Permissions.Core.GeofenceManage)
            .WithSummary("Creates a circle (centre and radius in metres) or a polygon (GeoJSON Polygon, one ring) geofence.");

        group.MapPut("/{id:guid}", async (Guid locationId, Guid id, UpdateGeofenceRequest? request, IGeofenceService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.UpdateAsync(locationId, id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Core.GeofenceManage)
            .WithSummary("Changes a geofence; send the rowVersion you read.");

        group.MapDelete("/{id:guid}", async (Guid locationId, Guid id, IGeofenceService service, CancellationToken ct) =>
                (await service.DeleteAsync(locationId, id, ct)).ToHttpResult(() => Results.NoContent()))
            .RequirePermission(Permissions.Core.GeofenceManage)
            .WithSummary("Soft deletes a geofence.");
    }

    // --- org units ---------------------------------------------------------------------------------------------------------------

    private static void MapOrgUnits(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/org-units").WithTags("Org units")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapGet(string.Empty, async (IOrgUnitService service, int? page, int? pageSize, string? search, bool? active, Guid? typeId, Guid? parentId, DateOnly? asOf, CancellationToken ct) =>
                Results.Ok(await service.ListAsync(
                    new OrgUnitQuery(new MasterQuery(page ?? 1, pageSize ?? MasterQuery.DefaultPageSize, search, active), typeId, parentId, asOf), ct)))
            .RequirePermission(Permissions.Core.OrgUnitView)
            .WithSummary("Lists org units with their parent as of a date (asOf, default today), paged, filtered by type, parent, search text and active flag.");

        group.MapGet("/tree", async (IOrgUnitService service, DateOnly? asOf, CancellationToken ct) =>
                Results.Ok(await service.TreeAsync(asOf, ct)))
            .RequirePermission(Permissions.Core.OrgUnitView)
            .WithSummary("Every unit placed in the tree on a date, as flat nodes with their parent id (one query, not paged by design).");

        group.MapGet("/{id:guid}", async (Guid id, DateOnly? asOf, IOrgUnitService service, CancellationToken ct) =>
                (await service.GetAsync(id, asOf, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Core.OrgUnitView)
            .WithSummary("Gets one org unit with its parent as of a date.");

        group.MapGet("/{id:guid}/parents", async (Guid id, IOrgUnitService service, CancellationToken ct) =>
                (await service.ParentHistoryAsync(id, ct)).ToHttpResult(history => Results.Ok(history)))
            .RequirePermission(Permissions.Core.OrgUnitView)
            .WithSummary("The unit's parent over time, oldest first.");

        group.MapPost(string.Empty, async (CreateOrgUnitRequest? request, HttpRequest http, IOrgUnitService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.CreateAsync(request, ct)).ToHttpResult(view => Results.Created($"{http.Path.Value!.TrimEnd('/')}/{view.Id}", view)))
            .RequirePermission(Permissions.Core.OrgUnitManage)
            .WithSummary("Creates a unit under a parent (or as a root) from a date. Its type is fixed; the parent must be on a higher level.");

        group.MapPut("/{id:guid}", async (Guid id, UpdateOrgUnitRequest? request, IOrgUnitService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.UpdateAsync(id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Core.OrgUnitManage)
            .WithSummary("Changes the name or active flag; send the rowVersion you read. Use /parent to move the unit.");

        group.MapPut("/{id:guid}/parent", async (Guid id, MoveOrgUnitRequest? request, IOrgUnitService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.MoveAsync(id, request, ct)).ToHttpResult(view => Results.Ok(view)))
            .RequirePermission(Permissions.Core.OrgUnitManage)
            .WithSummary("Moves the unit under another parent (null makes it a root) from a date after its current arrangement; refuses cycles. History is kept.");

        group.MapGet("/{id:guid}/heads", async (Guid id, IOrgUnitHeadService service, CancellationToken ct) =>
                (await service.HistoryAsync(id, ct)).ToHttpResult(history => Results.Ok(history)))
            .RequirePermission(Permissions.Core.OrgUnitView)
            .WithSummary("The heads of the unit over time, oldest first, with the rowVersion for the next change.");

        group.MapPut("/{id:guid}/head", async (Guid id, ChangeOrgUnitHeadRequest? request, IOrgUnitHeadService service, CancellationToken ct) =>
                request is null ? BodyRequired() : (await service.ChangeAsync(id, request, ct)).ToHttpResult(history => Results.Ok(history)))
            .RequirePermission(Permissions.Core.OrgUnitManage)
            .WithSummary("Sets who heads the unit from a date (null leaves it without a head). History is kept.");

        group.MapDelete("/{id:guid}", async (Guid id, IOrgUnitService service, CancellationToken ct) =>
                (await service.DeleteAsync(id, ct)).ToHttpResult(() => Results.NoContent()))
            .RequirePermission(Permissions.Core.OrgUnitManage)
            .WithSummary("Soft deletes a unit that has no child units and is not in use.");
    }
}
