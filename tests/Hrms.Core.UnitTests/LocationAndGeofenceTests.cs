using FluentAssertions;
using Hrms.Core.Domain.Org;
using Hrms.SharedKernel;

namespace Hrms.Core.UnitTests;

public class LocationAndGeofenceTests
{
    private static LocationDetails Details(
        string? line1 = "1 MG Road", string? city = "Bengaluru", string? country = "IN", string? zone = "Asia/Kolkata", int week = Location.MondayToFriday) =>
        new(line1, null, city, "KA", "560001", country, zone, week, null);

    // --- location ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Location_Create_KeepsTheDetails()
    {
        var location = Location.Create("blr", "Bengaluru HQ", Details()).Value;

        location.Code.Should().Be("BLR");
        location.CountryCode.Should().Be("IN");
        location.TimeZoneId.Should().Be("Asia/Kolkata");
        location.WorkWeekDays.Should().Be(31);
        location.HolidayCalendarId.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "Bengaluru", "IN", "Asia/Kolkata", "address_invalid")]
    [InlineData("1 MG Road", "", "IN", "Asia/Kolkata", "city_invalid")]
    [InlineData("1 MG Road", "Bengaluru", "India", "Asia/Kolkata", "country_invalid")]
    [InlineData("1 MG Road", "Bengaluru", "in", "Not/AZone", "time_zone_invalid")]
    [InlineData("1 MG Road", "Bengaluru", "IN", "", "time_zone_invalid")]
    public void Location_Create_WithBadDetails_Fails(string? line1, string? city, string? country, string? zone, string reason)
    {
        var result = Location.Create("BLR", "HQ", Details(line1, city, country, zone));

        result.Error.Code.Should().Be($"core.location.{reason}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(-5)]
    public void Location_Create_WithAnImpossibleWorkWeek_Fails(int week)
    {
        Location.Create("BLR", "HQ", Details(week: week)).Error.Code.Should().Be("core.location.work_week_invalid");
    }

    [Fact]
    public void Location_Create_WithoutDetails_Fails()
    {
        Location.Create("BLR", "HQ", null).Error.Code.Should().Be("core.location.details_required");
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, true)]
    [InlineData(DayOfWeek.Friday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, false)]
    public void IsWorkingDay_ReadsTheMask_WithMondayFirst(DayOfWeek day, bool expected)
    {
        Location.IsWorkingDay(Location.MondayToFriday, day).Should().Be(expected);
    }

    [Fact]
    public void IsWorkingDay_ASundayOnlyWeek_ReadsTheTopBit()
    {
        Location.IsWorkingDay(0b1000000, DayOfWeek.Sunday).Should().BeTrue();
        Location.IsWorkingDay(0b1000000, DayOfWeek.Monday).Should().BeFalse();
    }

    [Fact]
    public void Location_Update_WithABadZone_ChangesNothing()
    {
        var location = Location.Create("BLR", "HQ", Details()).Value;

        location.Update("New name", Details(zone: "Nope/Nowhere"), isActive: false).IsFailure.Should().BeTrue();

        (location.Name, location.IsActive, location.TimeZoneId).Should().Be(("HQ", true, "Asia/Kolkata"));
    }

    // --- circle --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Circle_WithValidValues_KeepsThem()
    {
        var shape = GeofenceShape.Circle(12.9716m, 77.5946m, 150).Value;

        shape.Kind.Should().Be(GeofenceKind.Circle);
        shape.Center!.Value.Latitude.Should().Be(12.9716m);
        shape.RadiusMetres.Should().Be(150);
        shape.PolygonGeoJson.Should().BeNull();
    }

    [Fact]
    public void Circle_RoundsTheCentreToSixDecimals_AwayFromZero()
    {
        var shape = GeofenceShape.Circle(12.12345650m, -77.1234565m, 10).Value;

        shape.Center!.Value.Latitude.Should().Be(12.123457m);
        shape.Center!.Value.Longitude.Should().Be(-77.123457m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(100_001)]
    public void Circle_WithABadRadius_Fails(int radius)
    {
        GeofenceShape.Circle(12m, 77m, radius).Error.Code.Should().Be("core.geofence.radius_invalid");
    }

    [Theory]
    [InlineData(90.1, 0)]
    [InlineData(-90.1, 0)]
    [InlineData(0, 180.1)]
    [InlineData(0, -180.1)]
    public void Circle_WithACentreOffTheEarth_Fails(double latitude, double longitude)
    {
        GeofenceShape.Circle((decimal)latitude, (decimal)longitude, 100).Error.Code.Should().Be("core.geofence.center_invalid");
    }

    // --- polygon -------------------------------------------------------------------------------------------------------------

    private static string Poly(params (double Lng, double Lat)[] ring) =>
        "{\"type\":\"Polygon\",\"coordinates\":[[" + string.Join(",", ring.Select(p => $"[{p.Lng},{p.Lat}]")) + "]]}";

    private static readonly (double, double)[] Square = [(77.0, 12.0), (77.1, 12.0), (77.1, 12.1), (77.0, 12.1), (77.0, 12.0)];

    [Fact]
    public void Polygon_WithAValidSquare_IsAcceptedAndNormalised()
    {
        var shape = GeofenceShape.Polygon(Poly(Square)).Value;

        shape.Kind.Should().Be(GeofenceKind.Polygon);
        shape.PolygonGeoJson.Should().Be("{\"type\":\"Polygon\",\"coordinates\":[[[77,12],[77.1,12],[77.1,12.1],[77,12.1],[77,12]]]}");
        shape.Center.Should().BeNull();
    }

    [Fact]
    public void Polygon_ThatIsConcaveButValid_IsAccepted()
    {
        var shape = GeofenceShape.Polygon(Poly((0, 0), (4, 0), (4, 4), (2, 1), (0, 4), (0, 0)));

        shape.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Polygon_ThatCrossesItself_IsRefused()
    {
        // A bow tie.
        GeofenceShape.Polygon(Poly((0, 0), (4, 4), (4, 0), (0, 4), (0, 0))).Error.Code.Should().Be("core.geofence.polygon_invalid");
    }

    [Fact]
    public void Polygon_WhoseVertexTouchesAnotherEdge_IsRefused()
    {
        GeofenceShape.Polygon(Poly((0, 0), (4, 0), (4, 4), (2, 0), (0, 4), (0, 0))).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Polygon_ThatFoldsBackAlongItself_IsRefused()
    {
        GeofenceShape.Polygon(Poly((0, 0), (4, 0), (2, 0), (2, 3), (0, 0))).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Polygon_WithNoArea_IsRefused()
    {
        GeofenceShape.Polygon(Poly((0, 0), (1, 1), (2, 2), (0, 0))).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Polygon_ThatIsNotClosed_IsRefused()
    {
        GeofenceShape.Polygon(Poly((0, 0), (4, 0), (4, 4), (0, 4))).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Polygon_WithTooFewPositions_IsRefused()
    {
        GeofenceShape.Polygon(Poly((0, 0), (4, 0), (0, 0))).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Polygon_WithTooManyVertices_IsRefused()
    {
        var ring = Enumerable.Range(0, GeofenceShape.MaxVertices + 5)
            .Select(i => (Lng: 10 + (Math.Cos(i * 0.01) * 0.5), Lat: 10 + (Math.Sin(i * 0.01) * 0.5)))
            .ToList();
        ring.Add(ring[0]);

        GeofenceShape.Polygon(Poly([.. ring])).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Polygon_WithSwappedLatitudeAndLongitudeOutOfRange_IsRefused()
    {
        // Latitude 200 is impossible: the longitude, latitude order was probably reversed.
        GeofenceShape.Polygon(Poly((12, 200), (12.1, 200), (12.1, 201), (12, 200))).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"Point\",\"coordinates\":[1,2]}")]
    [InlineData("{\"type\":\"Polygon\"}")]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,0],[1,1],[0,0]],[[0,0],[1,0],[1,1],[0,0]]]}")]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,\"x\"],[1,1],[0,0]]]}")]
    [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[0,0,5],[1,0],[1,1],[0,0]]]}")]
    public void Polygon_WithMalformedInput_IsRefusedWithAStableCode(string? json)
    {
        GeofenceShape.Polygon(json).Error.Code.Should().Be("core.geofence.polygon_invalid");
    }

    [Fact]
    public void Polygon_WithAnOversizedPayload_IsRefusedBeforeParsing()
    {
        GeofenceShape.Polygon(new string(' ', GeofenceShape.MaxGeoJsonLength + 1)).Error.Code.Should().Be("core.geofence.polygon_invalid");
    }

    [Fact]
    public void Polygon_WithTooDeepNesting_IsRefusedNotCrashed()
    {
        GeofenceShape.Polygon(new string('[', 200) + new string(']', 200)).IsFailure.Should().BeTrue();
    }

    // --- the geofence entity ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Geofence_Create_CopiesTheShape()
    {
        var shape = GeofenceShape.Circle(12.9m, 77.5m, 200).Value;

        var fence = Geofence.Create(Guid.NewGuid(), " Main gate ", shape).Value;

        (fence.Name, fence.Kind, fence.RadiusMetres, fence.CenterLatitude, fence.IsActive).Should().Be(("Main gate", GeofenceKind.Circle, 200, 12.9m, true));
    }

    [Fact]
    public void Geofence_Update_SwitchingFromCircleToPolygon_ClearsTheCircleFields()
    {
        var fence = Geofence.Create(Guid.NewGuid(), "Gate", GeofenceShape.Circle(12.9m, 77.5m, 200).Value).Value;

        fence.Update("Campus", GeofenceShape.Polygon(Poly(Square)).Value, isActive: true).IsSuccess.Should().BeTrue();

        fence.Kind.Should().Be(GeofenceKind.Polygon);
        fence.CenterLatitude.Should().BeNull();
        fence.RadiusMetres.Should().BeNull();
        fence.PolygonGeoJson.Should().NotBeNull();
    }

    [Fact]
    public void Geofence_WithoutAShapeOrName_Fails()
    {
        Geofence.Create(Guid.NewGuid(), "Gate", null).Error.Code.Should().Be("core.geofence.shape_required");
        Geofence.Create(Guid.NewGuid(), " ", GeofenceShape.Circle(1m, 1m, 5).Value).Error.Code.Should().Be("core.geofence.name_invalid");
    }

    [Fact]
    public void GeoPoint_Create_AcceptsTheExtremes()
    {
        GeoPoint.Create(90m, 180m).IsSuccess.Should().BeTrue();
        GeoPoint.Create(-90m, -180m).IsSuccess.Should().BeTrue();
    }
}
