using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;

namespace Hrms.Core.IntegrationTests.Org;

[Collection(MySqlTestGroup.Name)]
public class GeofenceApiTests(OrgApiFixture fixture) : OrgApiTestBase(fixture), IClassFixture<OrgApiFixture>
{
    private static readonly JsonElement Square = JsonDocument.Parse(
        "{\"type\":\"Polygon\",\"coordinates\":[[[77.0,12.0],[77.1,12.0],[77.1,12.1],[77.0,12.1],[77.0,12.0]]]}").RootElement.Clone();

    private async Task<JsonElement> LocationAsync(string? token = null) =>
        await CreateAsync("/locations", new { code = NewCode(), name = "Office", addressLine1 = "1 Road", city = "Pune", countryCode = "IN", timeZoneId = "Asia/Kolkata" }, token);

    private static object Circle(string name = "Main gate") => new { name, kind = "circle", centerLatitude = 12.9716m, centerLongitude = 77.5946m, radiusMetres = 150 };

    private static object Polygon(string name = "Campus") => new { name, kind = "polygon", polygon = Square };

    [Fact]
    public async Task Create_ACircle_ReturnsItWithALocationHeader()
    {
        var location = await LocationAsync();

        using var response = await PostAsync($"/locations/{Id(location)}/geofences", Circle(), AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var json = await JsonAsync(response);
        json.GetProperty("kind").GetString().Should().Be("circle");
        json.GetProperty("centerLatitude").GetDecimal().Should().Be(12.9716m);
        json.GetProperty("radiusMetres").GetInt32().Should().Be(150);
        json.GetProperty("polygon").ValueKind.Should().Be(JsonValueKind.Null);
        response.Headers.Location!.ToString().Should().EndWith($"/locations/{Id(location)}/geofences/{Id(json)}");
    }

    [Fact]
    public async Task Create_APolygon_StoresAndReturnsTheGeoJson()
    {
        var location = await LocationAsync();

        var fence = await CreateAsync($"/locations/{Id(location)}/geofences", Polygon());

        fence.GetProperty("kind").GetString().Should().Be("polygon");
        var polygon = fence.GetProperty("polygon");
        polygon.GetProperty("type").GetString().Should().Be("Polygon");
        polygon.GetProperty("coordinates")[0].GetArrayLength().Should().Be(5);
        fence.GetProperty("radiusMetres").ValueKind.Should().Be(JsonValueKind.Null);

        using var fetched = await GetAsync($"/locations/{Id(location)}/geofences/{Id(fence)}", AcmeViewer);
        (await JsonAsync(fetched)).GetProperty("polygon").GetProperty("coordinates")[0].GetArrayLength().Should().Be(5);
    }

    [Theory]
    [InlineData("circle", 12.9, 77.5, 0, "core.geofence.radius_invalid")]
    [InlineData("circle", 12.9, 77.5, 100001, "core.geofence.radius_invalid")]
    [InlineData("circle", 95.0, 77.5, 100, "core.geofence.center_invalid")]
    [InlineData("circle", 12.9, 200.0, 100, "core.geofence.center_invalid")]
    public async Task Create_WithABadCircle_Is422(string kind, double latitude, double longitude, int radius, string code)
    {
        var location = await LocationAsync();

        using var response = await PostAsync($"/locations/{Id(location)}/geofences",
            new { name = "Bad", kind, centerLatitude = (decimal)latitude, centerLongitude = (decimal)longitude, radiusMetres = radius }, AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().Be(code);
    }

    [Fact]
    public async Task Create_WithAnIncompleteCircleOrAnUnknownKind_Is422()
    {
        var location = await LocationAsync();
        var path = $"/locations/{Id(location)}/geofences";

        using var incomplete = await PostAsync(path, new { name = "X", kind = "circle", radiusMetres = 10 }, AcmeAdmin);
        using var unknown = await PostAsync(path, new { name = "X", kind = "square" }, AcmeAdmin);
        using var noPolygon = await PostAsync(path, new { name = "X", kind = "polygon" }, AcmeAdmin);

        ErrorCode(await JsonAsync(incomplete)).Should().Be("core.geofence.circle_incomplete");
        ErrorCode(await JsonAsync(unknown)).Should().Be("core.geofence.kind_invalid");
        ErrorCode(await JsonAsync(noPolygon)).Should().Be("core.geofence.polygon_invalid");
    }

    [Fact]
    public async Task Create_WithABowTiePolygon_Is422()
    {
        var location = await LocationAsync();
        var bowTie = JsonDocument.Parse("{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[4,4],[4,0],[0,4],[0,0]]]}").RootElement.Clone();

        using var response = await PostAsync($"/locations/{Id(location)}/geofences", new { name = "Bow tie", kind = "polygon", polygon = bowTie }, AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().Be("core.geofence.polygon_invalid");
    }

    [Fact]
    public async Task Create_ForAnUnknownLocation_Is404()
    {
        using var response = await PostAsync($"/locations/{Guid.NewGuid()}/geofences", Circle(), AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ErrorCode(await JsonAsync(response)).Should().Be("core.location.not_found");
    }

    [Fact]
    public async Task Update_SwitchingFromCircleToPolygon_ClearsTheCircleAndBumpsTheVersion()
    {
        var location = await LocationAsync();
        var fence = await CreateAsync($"/locations/{Id(location)}/geofences", Circle());

        using var response = await PutAsync($"/locations/{Id(location)}/geofences/{Id(fence)}",
            new { name = "Campus", kind = "polygon", polygon = Square, isActive = true, rowVersion = Version(fence) }, AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var json = await JsonAsync(response);
        json.GetProperty("kind").GetString().Should().Be("polygon");
        json.GetProperty("centerLatitude").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("radiusMetres").ValueKind.Should().Be(JsonValueKind.Null);
        Version(json).Should().Be(Version(fence) + 1);
    }

    [Fact]
    public async Task Update_WithAStaleOrMissingVersion_IsRefused()
    {
        var location = await LocationAsync();
        var fence = await CreateAsync($"/locations/{Id(location)}/geofences", Circle());
        var path = $"/locations/{Id(location)}/geofences/{Id(fence)}";
        using (await PutAsync(path, new { name = "First", kind = "circle", centerLatitude = 1m, centerLongitude = 1m, radiusMetres = 10, isActive = true, rowVersion = Version(fence) }, AcmeAdmin))
        {
        }

        using var stale = await PutAsync(path, new { name = "Second", kind = "circle", centerLatitude = 1m, centerLongitude = 1m, radiusMetres = 10, isActive = true, rowVersion = Version(fence) }, AcmeAdmin);
        using var missing = await PutAsync(path, new { name = "Third", kind = "circle", centerLatitude = 1m, centerLongitude = 1m, radiusMetres = 10, isActive = true }, AcmeAdmin);

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(stale)).Should().Be("core.geofence.stale_version");
        ErrorCode(await JsonAsync(missing)).Should().Be("core.row_version_required");
    }

    [Fact]
    public async Task List_PagesAndSearchesWithinOneLocation()
    {
        var one = await LocationAsync();
        var two = await LocationAsync();
        foreach (var name in new[] { "Gate A", "Gate B", "Gate C" })
        {
            await CreateAsync($"/locations/{Id(one)}/geofences", Circle(name));
        }

        await CreateAsync($"/locations/{Id(two)}/geofences", Circle("Elsewhere"));

        using var paged = await GetAsync($"/locations/{Id(one)}/geofences?pageSize=2&page=2", AcmeViewer);
        using var search = await GetAsync($"/locations/{Id(one)}/geofences?search=Gate%20B", AcmeViewer);

        var page = await JsonAsync(paged);
        page.GetProperty("total").GetInt64().Should().Be(3);
        page.GetProperty("items").GetArrayLength().Should().Be(1);
        (await JsonAsync(search)).GetProperty("total").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task AGeofence_IsOnlyReachableThroughItsOwnLocation()
    {
        var one = await LocationAsync();
        var two = await LocationAsync();
        var fence = await CreateAsync($"/locations/{Id(one)}/geofences", Circle());

        using var wrongLocation = await GetAsync($"/locations/{Id(two)}/geofences/{Id(fence)}", AcmeAdmin);
        using var wrongUpdate = await PutAsync($"/locations/{Id(two)}/geofences/{Id(fence)}",
            new { name = "x", kind = "circle", centerLatitude = 1m, centerLongitude = 1m, radiusMetres = 10, isActive = true, rowVersion = Version(fence) }, AcmeAdmin);
        using var wrongDelete = await DeleteAsync($"/locations/{Id(two)}/geofences/{Id(fence)}", AcmeAdmin);

        wrongLocation.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wrongUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wrongDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_RemovesTheGeofence_AndThenTheLocationCanBeDeleted()
    {
        var location = await LocationAsync();
        var fence = await CreateAsync($"/locations/{Id(location)}/geofences", Circle());

        using var refused = await DeleteAsync($"/locations/{Id(location)}", AcmeAdmin);
        using var deleted = await DeleteAsync($"/locations/{Id(location)}/geofences/{Id(fence)}", AcmeAdmin);
        using var gone = await GetAsync($"/locations/{Id(location)}/geofences/{Id(fence)}", AcmeAdmin);
        using var allowed = await DeleteAsync($"/locations/{Id(location)}", AcmeAdmin);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(refused)).Should().Be("core.location.in_use");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        allowed.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Permissions_AreSeparateFromTheLocationPermissions()
    {
        var location = await LocationAsync();
        var fence = await CreateAsync($"/locations/{Id(location)}/geofences", Circle());
        var path = $"/locations/{Id(location)}/geofences";
        var locationOnly = await Host.TokenWithPermissionsAsync(Acme.TenantId, "ACME", OrgApiFixture.Password, Permissions.Core.LocationView, Permissions.Core.LocationManage);

        (await GetAsync(path, locationOnly)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync(path, Circle(), locationOnly)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(path, AcmeNobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync(path, Circle(), AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DeleteAsync($"{path}/{Id(fence)}", AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync($"{path}/{Id(fence)}", AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnotherTenant_CannotSeeChangeOrAddGeofences()
    {
        var location = await LocationAsync();
        var fence = await CreateAsync($"/locations/{Id(location)}/geofences", Circle());
        var path = $"/locations/{Id(location)}/geofences";

        using var list = await GetAsync(path, GlobexAdmin);
        using var read = await GetAsync($"{path}/{Id(fence)}", GlobexAdmin);
        using var add = await PostAsync(path, Circle("Spy"), GlobexAdmin);
        using var update = await PutAsync($"{path}/{Id(fence)}",
            new { name = "Hijacked", kind = "circle", centerLatitude = 1m, centerLongitude = 1m, radiusMetres = 10, isActive = true, rowVersion = Version(fence) }, GlobexAdmin);
        using var delete = await DeleteAsync($"{path}/{Id(fence)}", GlobexAdmin);

        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        add.StatusCode.Should().Be(HttpStatusCode.NotFound);
        update.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var intact = await GetAsync($"{path}/{Id(fence)}", AcmeAdmin);
        (await JsonAsync(intact)).GetProperty("name").GetString().Should().Be("Main gate");
    }
}
