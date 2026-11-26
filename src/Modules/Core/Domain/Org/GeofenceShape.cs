using System.Globalization;
using System.Text;
using System.Text.Json;
using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

public enum GeofenceKind
{
    Circle = 1,
    Polygon = 2,
}

/// <summary>
/// The area of a geofence: a circle (centre and radius in metres) or one polygon ring as GeoJSON. A shape is valid or it does not
/// exist, so a punch check later never meets bad geometry. Limits keep a hostile payload cheap: the polygon text is capped, the vertex
/// count is capped, and the self-intersection test is quadratic only up to that cap.
/// </summary>
public sealed record GeofenceShape
{
    public const int MaxRadiusMetres = 100_000;
    public const int MaxVertices = 500;
    public const int MaxGeoJsonLength = 32_768;

    private GeofenceShape(GeofenceKind kind, GeoPoint? center, int? radiusMetres, string? polygonGeoJson)
    {
        Kind = kind;
        Center = center;
        RadiusMetres = radiusMetres;
        PolygonGeoJson = polygonGeoJson;
    }

    public GeofenceKind Kind { get; }

    public GeoPoint? Center { get; }

    public int? RadiusMetres { get; }

    /// <summary>The normalised GeoJSON Polygon geometry (longitude, latitude order, six decimals); null for a circle.</summary>
    public string? PolygonGeoJson { get; }

    public static Result<GeofenceShape> Circle(decimal latitude, decimal longitude, int radiusMetres)
    {
        var center = GeoPoint.Create(latitude, longitude);
        if (center.IsFailure)
        {
            return Result.Failure<GeofenceShape>(Error.Validation("core.geofence.center_invalid", center.Error.Message));
        }

        return radiusMetres is >= 1 and <= MaxRadiusMetres
            ? Result.Success(new GeofenceShape(GeofenceKind.Circle, center.Value, radiusMetres, null))
            : Fail($"The radius is 1 to {MaxRadiusMetres} metres.", "radius_invalid");
    }

    /// <summary>
    /// Accepts a GeoJSON Polygon geometry with one closed ring (no holes) of 4 to 500 positions in [longitude, latitude] order,
    /// that does not cross itself and encloses some area.
    /// </summary>
    public static Result<GeofenceShape> Polygon(string? geoJson)
    {
        if (string.IsNullOrWhiteSpace(geoJson) || geoJson.Length > MaxGeoJsonLength)
        {
            return Fail($"The polygon is GeoJSON of at most {MaxGeoJsonLength} characters.", "polygon_invalid");
        }

        List<(decimal Lng, decimal Lat)> ring;
        try
        {
            using var document = JsonDocument.Parse(geoJson, new JsonDocumentOptions { MaxDepth = 8 });
            var parsed = ReadRing(document.RootElement);
            if (parsed is null)
            {
                return Fail("The polygon must be a GeoJSON Polygon with one ring of [longitude, latitude] positions.", "polygon_invalid");
            }

            ring = parsed;
        }
        catch (JsonException)
        {
            return Fail("The polygon is not valid JSON.", "polygon_invalid");
        }

        if (ring.Count is < 4 or > MaxVertices + 1)
        {
            return Fail($"A polygon ring has 4 to {MaxVertices + 1} positions (first and last equal).", "polygon_invalid");
        }

        if (ring[0] != ring[^1])
        {
            return Fail("The polygon ring must be closed: the first and last positions are equal.", "polygon_invalid");
        }

        if (HasSelfIntersection(ring) || Math.Abs(SignedArea(ring)) == 0)
        {
            return Fail("The polygon must not cross itself and must enclose some area.", "polygon_invalid");
        }

        return Result.Success(new GeofenceShape(GeofenceKind.Polygon, null, null, Normalise(ring)));
    }

    private static List<(decimal, decimal)>? ReadRing(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "Polygon"
            || !root.TryGetProperty("coordinates", out var rings) || rings.ValueKind != JsonValueKind.Array || rings.GetArrayLength() != 1)
        {
            return null;
        }

        var points = new List<(decimal, decimal)>();
        foreach (var position in rings[0].EnumerateArray())
        {
            if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() != 2
                || position[0].ValueKind != JsonValueKind.Number || position[1].ValueKind != JsonValueKind.Number
                || !position[0].TryGetDecimal(out var lng) || !position[1].TryGetDecimal(out var lat))
            {
                return null;
            }

            var point = GeoPoint.Create(lat, lng);
            if (point.IsFailure)
            {
                return null;
            }

            points.Add((point.Value.Longitude, point.Value.Latitude));
            if (points.Count > MaxVertices + 1)
            {
                return points;
            }
        }

        return points;
    }

    private static bool HasSelfIntersection(List<(decimal Lng, decimal Lat)> ring)
    {
        var segments = ring.Count - 1;
        for (var i = 0; i < segments; i++)
        {
            for (var j = i + 1; j < segments; j++)
            {
                var adjacent = j == i + 1 || (i == 0 && j == segments - 1);
                if (adjacent)
                {
                    // Neighbours share one end; they may only overlap if they fold back along the same line.
                    if (Overlaps(ring[i], ring[i + 1], ring[j], ring[j + 1]))
                    {
                        return true;
                    }

                    continue;
                }

                if (Intersects(ring[i], ring[i + 1], ring[j], ring[j + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Overlaps((decimal X, decimal Y) a, (decimal X, decimal Y) b, (decimal X, decimal Y) c, (decimal X, decimal Y) d)
    {
        if (Cross(a, b, c) != 0 || Cross(a, b, d) != 0)
        {
            return false;
        }

        // Collinear: they overlap beyond the single shared end when their projections share more than a point.
        var useX = a.X != b.X;
        decimal Key((decimal X, decimal Y) p) => useX ? p.X : p.Y;
        var (lo1, hi1) = (Math.Min(Key(a), Key(b)), Math.Max(Key(a), Key(b)));
        var (lo2, hi2) = (Math.Min(Key(c), Key(d)), Math.Max(Key(c), Key(d)));
        return Math.Min(hi1, hi2) > Math.Max(lo1, lo2);
    }

    private static bool Intersects((decimal X, decimal Y) a, (decimal X, decimal Y) b, (decimal X, decimal Y) c, (decimal X, decimal Y) d)
    {
        var d1 = Math.Sign(Cross(a, b, c));
        var d2 = Math.Sign(Cross(a, b, d));
        var d3 = Math.Sign(Cross(c, d, a));
        var d4 = Math.Sign(Cross(c, d, b));

        if (d1 * d2 < 0 && d3 * d4 < 0)
        {
            return true; // a proper crossing
        }

        // Otherwise they only meet when an end of one lies on the other.
        return (d1 == 0 && OnSegment(a, b, c))
               || (d2 == 0 && OnSegment(a, b, d))
               || (d3 == 0 && OnSegment(c, d, a))
               || (d4 == 0 && OnSegment(c, d, b));
    }

    private static decimal Cross((decimal X, decimal Y) a, (decimal X, decimal Y) b, (decimal X, decimal Y) p) =>
        ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    private static bool OnSegment((decimal X, decimal Y) a, (decimal X, decimal Y) b, (decimal X, decimal Y) p) =>
        p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);

    private static decimal SignedArea(List<(decimal Lng, decimal Lat)> ring)
    {
        decimal sum = 0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            sum += (ring[i].Lng * ring[i + 1].Lat) - (ring[i + 1].Lng * ring[i].Lat);
        }

        return sum / 2;
    }

    private static string Normalise(List<(decimal Lng, decimal Lat)> ring)
    {
        var text = new StringBuilder("{\"type\":\"Polygon\",\"coordinates\":[[");
        for (var i = 0; i < ring.Count; i++)
        {
            text.Append(i == 0 ? "[" : ",[")
                .Append(ring[i].Lng.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                .Append(ring[i].Lat.ToString("0.######", CultureInfo.InvariantCulture)).Append(']');
        }

        return text.Append("]]}").ToString();
    }

    private static Result<GeofenceShape> Fail(string message, string reason) =>
        Result.Failure<GeofenceShape>(Error.Validation($"core.geofence.{reason}", message));
}
