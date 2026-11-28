using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>An area (circle or polygon) attached to a location, used later to decide whether a punch happened on site.</summary>
[Audited(RootType = "location", RootKey = nameof(LocationId))]
public sealed class Geofence : TenantEntity
{
    public const int NameMaxLength = 200;

    private Geofence(Guid locationId, string name, GeofenceShape shape)
    {
        LocationId = locationId;
        Name = name;
        ApplyShape(shape);
        IsActive = true;
    }

    /// <summary>For the persistence layer only.</summary>
    private Geofence() => Name = string.Empty;

    public Guid LocationId { get; private set; }

    public string Name { get; private set; }

    public GeofenceKind Kind { get; private set; }

    public decimal? CenterLatitude { get; private set; }

    public decimal? CenterLongitude { get; private set; }

    public int? RadiusMetres { get; private set; }

    public string? PolygonGeoJson { get; private set; }

    public bool IsActive { get; private set; }

    public static Result<Geofence> Create(Guid locationId, string? name, GeofenceShape? shape)
    {
        var validName = ValidateName(name);
        if (validName is null)
        {
            return Result.Failure<Geofence>(NameInvalid());
        }

        return shape is null
            ? Result.Failure<Geofence>(Error.Validation("core.geofence.shape_required", "A circle or a polygon is required."))
            : Result.Success(new Geofence(locationId, validName, shape));
    }

    public Result Update(string? name, GeofenceShape? shape, bool isActive)
    {
        var validName = ValidateName(name);
        if (validName is null)
        {
            return Result.Failure(NameInvalid());
        }

        if (shape is null)
        {
            return Result.Failure(Error.Validation("core.geofence.shape_required", "A circle or a polygon is required."));
        }

        Name = validName;
        ApplyShape(shape);
        IsActive = isActive;
        return Result.Success();
    }

    private void ApplyShape(GeofenceShape shape)
    {
        Kind = shape.Kind;
        CenterLatitude = shape.Center?.Latitude;
        CenterLongitude = shape.Center?.Longitude;
        RadiusMetres = shape.RadiusMetres;
        PolygonGeoJson = shape.PolygonGeoJson;
    }

    private static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > NameMaxLength ? null : trimmed;
    }

    private static Error NameInvalid() => Error.Validation("core.geofence.name_invalid", "A name is 1 to 200 characters.");
}
