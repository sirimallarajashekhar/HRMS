using System.Text.Json;
using Hrms.Core.Application.Org;
using Hrms.Core.Domain.Org;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure.Org;

/// <summary>Geofences of one location. Every call names the location, and a geofence under another location (or tenant) is simply not found.</summary>
internal sealed class GeofenceService(CoreDbContext db) : IGeofenceService
{
    public async Task<Result<PagedResult<GeofenceView>>> ListAsync(Guid locationId, MasterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await LocationExistsAsync(locationId, cancellationToken))
        {
            return Result.Failure<PagedResult<GeofenceView>>(LocationNotFound());
        }

        var paging = query.Normalised();
        var rows = db.Geofences.AsNoTracking().Where(g => g.LocationId == locationId);
        if (paging.Search is { } search)
        {
            rows = rows.Where(g => g.Name.Contains(search));
        }

        if (paging.Active is { } active)
        {
            rows = rows.Where(g => g.IsActive == active);
        }

        var total = await rows.LongCountAsync(cancellationToken);
        var entities = await rows
            .OrderBy(g => g.Name).ThenBy(g => g.Id)
            .Skip((paging.Page - 1) * paging.PageSize)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResult<GeofenceView>([.. entities.Select(ToView)], paging.Page, paging.PageSize, total));
    }

    public async Task<Result<GeofenceView>> GetAsync(Guid locationId, Guid id, CancellationToken cancellationToken)
    {
        var geofence = await db.Geofences.AsNoTracking().SingleOrDefaultAsync(g => g.LocationId == locationId && g.Id == id, cancellationToken);
        return geofence is null ? Result.Failure<GeofenceView>(NotFound()) : Result.Success(ToView(geofence));
    }

    public async Task<Result<GeofenceView>> CreateAsync(Guid locationId, CreateGeofenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await LocationExistsAsync(locationId, cancellationToken))
        {
            return Result.Failure<GeofenceView>(LocationNotFound());
        }

        var shape = BuildShape(request.Kind, request.CenterLatitude, request.CenterLongitude, request.RadiusMetres, request.Polygon);
        if (shape.IsFailure)
        {
            return Result.Failure<GeofenceView>(shape.Error);
        }

        var geofence = Geofence.Create(locationId, request.Name, shape.Value);
        if (geofence.IsFailure)
        {
            return Result.Failure<GeofenceView>(geofence.Error);
        }

        db.Geofences.Add(geofence.Value);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(ToView(geofence.Value));
    }

    public async Task<Result<GeofenceView>> UpdateAsync(Guid locationId, Guid id, UpdateGeofenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RowVersion is not { } version)
        {
            return Result.Failure<GeofenceView>(Error.Validation("core.row_version_required", "The rowVersion the change is based on is required."));
        }

        var geofence = await db.Geofences.SingleOrDefaultAsync(g => g.LocationId == locationId && g.Id == id, cancellationToken);
        if (geofence is null)
        {
            return Result.Failure<GeofenceView>(NotFound());
        }

        if (geofence.RowVersion != version)
        {
            return Result.Failure<GeofenceView>(Stale());
        }

        var shape = BuildShape(request.Kind, request.CenterLatitude, request.CenterLongitude, request.RadiusMetres, request.Polygon);
        if (shape.IsFailure)
        {
            return Result.Failure<GeofenceView>(shape.Error);
        }

        var applied = geofence.Update(request.Name, shape.Value, request.IsActive);
        if (applied.IsFailure)
        {
            return Result.Failure<GeofenceView>(applied.Error);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<GeofenceView>(Stale());
        }

        return Result.Success(ToView(geofence));
    }

    public async Task<Result> DeleteAsync(Guid locationId, Guid id, CancellationToken cancellationToken)
    {
        var geofence = await db.Geofences.SingleOrDefaultAsync(g => g.LocationId == locationId && g.Id == id, cancellationToken);
        if (geofence is null)
        {
            return Result.Failure(NotFound());
        }

        db.Geofences.Remove(geofence);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Stale());
        }

        return Result.Success();
    }

    private static Result<GeofenceShape> BuildShape(string? kind, decimal? latitude, decimal? longitude, int? radius, JsonElement? polygon)
    {
        switch (kind?.Trim().ToLowerInvariant())
        {
            case "circle":
                return latitude is { } lat && longitude is { } lng && radius is { } r
                    ? GeofenceShape.Circle(lat, lng, r)
                    : Result.Failure<GeofenceShape>(Error.Validation("core.geofence.circle_incomplete", "A circle needs centerLatitude, centerLongitude and radiusMetres."));
            case "polygon":
                return polygon is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } shape
                    ? GeofenceShape.Polygon(shape.GetRawText())
                    : Result.Failure<GeofenceShape>(Error.Validation("core.geofence.polygon_invalid", "A polygon needs a GeoJSON Polygon geometry."));
            default:
                return Result.Failure<GeofenceShape>(Error.Validation("core.geofence.kind_invalid", "The kind is \"circle\" or \"polygon\"."));
        }
    }

    private static GeofenceView ToView(Geofence g) => new(
        g.Id,
        g.LocationId,
        g.Name,
        g.Kind == GeofenceKind.Circle ? "circle" : "polygon",
        g.CenterLatitude,
        g.CenterLongitude,
        g.RadiusMetres,
        g.PolygonGeoJson is null ? null : JsonDocument.Parse(g.PolygonGeoJson).RootElement.Clone(),
        g.IsActive,
        g.RowVersion);

    private Task<bool> LocationExistsAsync(Guid locationId, CancellationToken cancellationToken) =>
        db.Locations.AsNoTracking().AnyAsync(l => l.Id == locationId, cancellationToken);

    private static Error LocationNotFound() => Error.NotFound("core.location.not_found", "The record does not exist.");

    private static Error NotFound() => Error.NotFound("core.geofence.not_found", "The record does not exist.");

    private static Error Stale() =>
        Error.Conflict("core.geofence.stale_version", "The record was changed by someone else. Reload it and apply your change again.");
}
