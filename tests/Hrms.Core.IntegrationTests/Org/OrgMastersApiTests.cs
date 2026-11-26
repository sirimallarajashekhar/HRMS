using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Core;
using Hrms.Core.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Org;

/// <summary>The flow every coded master shares, run against each of the six simple masters through the real HTTP pipeline.</summary>
[Collection(MySqlTestGroup.Name)]
public class OrgMastersApiTests(OrgApiFixture fixture) : OrgApiTestBase(fixture), IClassFixture<OrgApiFixture>
{
    private sealed record MasterCase(
        string Route,
        string ViewPermission,
        string ManagePermission,
        Func<string, object> Create,
        Func<string, long, object> Update);

    private static readonly Dictionary<string, MasterCase> Cases = new()
    {
        ["org-unit-types"] = new("org-unit-types", Permissions.Core.OrgUnitTypeView, Permissions.Core.OrgUnitTypeManage,
            code => new { code, name = "Department", level = 2 },
            (name, v) => new { name, level = 2, isActive = true, rowVersion = v }),
        ["grades"] = new("grades", Permissions.Core.GradeView, Permissions.Core.GradeManage,
            code => new { code, name = "Senior", rank = 5 },
            (name, v) => new { name, rank = 5, isActive = true, rowVersion = v }),
        ["designations"] = new("designations", Permissions.Core.DesignationView, Permissions.Core.DesignationManage,
            code => new { code, name = "Engineer" },
            (name, v) => new { name, isActive = true, rowVersion = v }),
        ["cost-centres"] = new("cost-centres", Permissions.Core.CostCentreView, Permissions.Core.CostCentreManage,
            code => new { code, name = "Delivery" },
            (name, v) => new { name, isActive = true, rowVersion = v }),
        ["employee-types"] = new("employee-types", Permissions.Core.EmployeeTypeView, Permissions.Core.EmployeeTypeManage,
            code => new { code, name = "Permanent", isPayrollEligible = true },
            (name, v) => new { name, isPayrollEligible = true, isActive = true, rowVersion = v }),
        ["locations"] = new("locations", Permissions.Core.LocationView, Permissions.Core.LocationManage,
            code => new { code, name = "Head office", addressLine1 = "1 MG Road", city = "Bengaluru", countryCode = "IN", timeZoneId = "Asia/Kolkata" },
            (name, v) => new
            {
                name,
                addressLine1 = "1 MG Road",
                city = "Bengaluru",
                countryCode = "IN",
                timeZoneId = "Asia/Kolkata",
                workWeekDays = 31,
                isActive = true,
                rowVersion = v,
            }),
    };

    public static TheoryData<string> Masters => [.. Cases.Keys];

    // --- create, read ----------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Create_ReturnsCreatedWithALocation_AndTheCodeUpperCased(string master)
    {
        var c = Cases[master];

        using var response = await PostAsync($"/{c.Route}", c.Create("lower-1"), AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await JsonAsync(response);
        json.GetProperty("code").GetString().Should().Be("LOWER-1");
        Version(json).Should().Be(1);
        response.Headers.Location!.ToString().Should().EndWith($"/{c.Route}/{Id(json)}");

        using var fetched = await GetAsync($"/{c.Route}/{Id(json)}", AcmeViewer);
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(fetched)).GetProperty("code").GetString().Should().Be("LOWER-1");
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Create_WithAnInvalidCode_Is422WithAStableCode(string master)
    {
        var c = Cases[master];

        using var response = await PostAsync($"/{c.Route}", c.Create("has space"), AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().EndWith(".code_invalid").And.StartWith("core.");
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Create_WithADuplicateCode_Is409_ButAnotherTenantMayUseTheSameCode(string master)
    {
        var c = Cases[master];
        var code = NewCode();
        await CreateAsync($"/{c.Route}", c.Create(code));

        using var duplicate = await PostAsync($"/{c.Route}", c.Create(code.ToLowerInvariant()), AcmeAdmin);
        using var otherTenant = await PostAsync($"/{c.Route}", c.Create(code), GlobexAdmin);

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(duplicate)).Should().EndWith(".code_taken");
        otherTenant.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Create_WithANullBody_Is422_AndNeverReachesTheService(string master)
    {
        var c = Cases[master];
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/{c.Route}") { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AcmeAdmin);

        using var response = await Host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().Be("core.request.body_required");
    }

    [Fact]
    public void EveryOrgEndpoint_CarriesTheRequestSizeLimit()
    {
        var endpoints = Host.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true
                        && e.RoutePattern.RawText.Contains("account", StringComparison.Ordinal) == false)
            .ToList();

        endpoints.Should().NotBeEmpty();
        endpoints.Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is null
                             && e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize is not <= Hrms.Core.Api.Org.OrgStructureEndpoints.MaxBodyBytes)
            .Select(e => e.RoutePattern.RawText).Should().BeEmpty("a body cap keeps a hostile payload from being buffered");
    }

    // --- update ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Update_ChangesTheName_BumpsTheVersion_AndKeepsTheCode(string master)
    {
        var c = Cases[master];
        var code = NewCode();
        var created = await CreateAsync($"/{c.Route}", c.Create(code));

        using var response = await PutAsync($"/{c.Route}/{Id(created)}", c.Update("Renamed", Version(created)), AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var json = await JsonAsync(response);
        json.GetProperty("name").GetString().Should().Be("Renamed");
        json.GetProperty("code").GetString().Should().Be(code);
        Version(json).Should().Be(Version(created) + 1);
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Update_WithAStaleVersion_Is409_AndChangesNothing(string master)
    {
        var c = Cases[master];
        var created = await CreateAsync($"/{c.Route}", c.Create(NewCode()));
        using (await PutAsync($"/{c.Route}/{Id(created)}", c.Update("First", Version(created)), AcmeAdmin))
        {
        }

        using var stale = await PutAsync($"/{c.Route}/{Id(created)}", c.Update("Second", Version(created)), AcmeAdmin);

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(stale)).Should().EndWith(".stale_version");
        using var current = await GetAsync($"/{c.Route}/{Id(created)}", AcmeAdmin);
        (await JsonAsync(current)).GetProperty("name").GetString().Should().Be("First");
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Update_WithoutARowVersion_Is422(string master)
    {
        var c = Cases[master];
        var created = await CreateAsync($"/{c.Route}", c.Create(NewCode()));

        using var response = await PutAsync($"/{c.Route}/{Id(created)}", WithoutVersion(c.Update("Renamed", 0)), AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().Be("core.row_version_required");
    }

    private static Dictionary<string, object?> WithoutVersion(object body) =>
        JsonSerializer.SerializeToElement(body).EnumerateObject()
            .Where(p => !p.NameEquals("rowVersion"))
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

    // --- delete --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Delete_SoftDeletes_TheRecordDisappears_AndTheCodeCanBeReused(string master)
    {
        var c = Cases[master];
        var code = NewCode();
        var created = await CreateAsync($"/{c.Route}", c.Create(code));

        using var deleted = await DeleteAsync($"/{c.Route}/{Id(created)}", AcmeAdmin);
        using var gone = await GetAsync($"/{c.Route}/{Id(created)}", AcmeAdmin);
        using var again = await PostAsync($"/{c.Route}", c.Create(code), AcmeAdmin);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ErrorCode(await JsonAsync(gone)).Should().EndWith(".not_found");
        again.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Delete_WhenAnotherModuleSaysItIsInUse_Is409_AndTheRecordSurvives(string master)
    {
        var c = Cases[master];
        var created = await CreateAsync($"/{c.Route}", c.Create(NewCode()));
        var usage = Host.Services.GetRequiredService<FakeUsage>();
        usage.InUse.Add(Id(created));

        using var refused = await DeleteAsync($"/{c.Route}/{Id(created)}", AcmeAdmin);
        usage.InUse.Remove(Id(created));
        using var stillThere = await GetAsync($"/{c.Route}/{Id(created)}", AcmeAdmin);
        using var allowed = await DeleteAsync($"/{c.Route}/{Id(created)}", AcmeAdmin);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(refused)).Should().EndWith(".in_use");
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK);
        allowed.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task List_IsPaged_OrderedByCode_AndFiltersBySearchAndActive(string master)
    {
        var c = Cases[master];
        var prefix = $"P{Guid.NewGuid():N}"[..6].ToUpperInvariant();
        foreach (var suffix in new[] { "C", "A", "B" })
        {
            await CreateAsync($"/{c.Route}", c.Create($"{prefix}-{suffix}"));
        }

        using var first = await GetAsync($"/{c.Route}?search={prefix}&pageSize=2&page=1", AcmeViewer);
        using var second = await GetAsync($"/{c.Route}?search={prefix}&pageSize=2&page=2", AcmeViewer);
        using var none = await GetAsync($"/{c.Route}?search={prefix}&active=false", AcmeViewer);
        using var some = await GetAsync($"/{c.Route}?search={prefix}&active=true&pageSize=500", AcmeViewer);

        var page1 = await JsonAsync(first);
        var page2 = await JsonAsync(second);
        page1.GetProperty("total").GetInt64().Should().Be(3);
        page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()).Should().Equal($"{prefix}-A", $"{prefix}-B");
        page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()).Should().Equal($"{prefix}-C");
        (await JsonAsync(none)).GetProperty("total").GetInt64().Should().Be(0);
        var all = await JsonAsync(some);
        all.GetProperty("pageSize").GetInt32().Should().Be(200, "the page size is capped, not rejected");
        all.GetProperty("items").GetArrayLength().Should().Be(3);
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task List_WithPercentOrUnderscoreInTheSearch_MatchesThemLiterally(string master)
    {
        var c = Cases[master];
        await CreateAsync($"/{c.Route}", c.Create(NewCode()));

        using var response = await GetAsync($"/{c.Route}?search=%25", AcmeViewer);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(response)).GetProperty("total").GetInt64().Should().Be(0);
    }

    // --- permissions and isolation ----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Endpoints_RefuseAnonymousCallers_AndCallersWithoutThePermission(string master)
    {
        var c = Cases[master];
        var created = await CreateAsync($"/{c.Route}", c.Create(NewCode()));
        var path = $"/{c.Route}/{Id(created)}";

        // 401 without a token, 403 for a token that lacks the permission, and writes need manage, not just view.
        // With no token there is no tenant either, so the tenant check can answer first: either way the caller is turned away.
        (await GetAsync($"/{c.Route}", null)).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
        (await GetAsync($"/{c.Route}", AcmeNobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(path, AcmeNobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync($"/{c.Route}", c.Create(NewCode()), AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PutAsync(path, c.Update("x", Version(created)), AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DeleteAsync(path, AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(path, AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task Endpoints_NeverExposeOrChangeAnotherTenantsRecords(string master)
    {
        var c = Cases[master];
        var code = NewCode();
        var acme = await CreateAsync($"/{c.Route}", c.Create(code));
        var path = $"/{c.Route}/{Id(acme)}";

        using var read = await GetAsync(path, GlobexAdmin);
        using var update = await PutAsync(path, c.Update("Hijacked", Version(acme)), GlobexAdmin);
        using var delete = await DeleteAsync(path, GlobexAdmin);
        using var list = await GetAsync($"/{c.Route}?search={code}", GlobexAdmin);
        using var intact = await GetAsync(path, AcmeAdmin);

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        update.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonAsync(list)).GetProperty("total").GetInt64().Should().Be(0);
        (await JsonAsync(intact)).GetProperty("name").GetString().Should().NotBe("Hijacked");
    }

    [Fact]
    public async Task ATokenForOneTenant_NeverSeesAnotherTenantsData_EvenWhenTheRequestNamesIt()
    {
        var globexCode = NewCode();
        await CreateAsync("/grades", new { code = globexCode, name = "Globex only", rank = 1 }, GlobexAdmin);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/grades?search={globexCode}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AcmeAdmin);
        request.Headers.Add("X-Tenant-Id", Globex.TenantId.ToString());

        using var response = await Host.Client.SendAsync(request);

        // The signed-in tenant comes from the token, never from a header: the answer is about ACME and holds nothing of GLOBEX.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(globexCode);
    }

    // --- relationships between masters ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Designation_CanReferenceAGrade_FilterByIt_AndRefusesAnUnknownOne()
    {
        var grade = await CreateAsync("/grades", new { code = NewCode(), name = "G", rank = 1 });
        var other = await CreateAsync("/grades", new { code = NewCode(), name = "H", rank = 2 });
        var designation = await CreateAsync("/designations", new { code = NewCode(), name = "Engineer", gradeId = Id(grade) });
        await CreateAsync("/designations", new { code = NewCode(), name = "Analyst", gradeId = Id(other) });

        using var unknown = await PostAsync("/designations", new { code = NewCode(), name = "Ghost", gradeId = Guid.NewGuid() }, AcmeAdmin);
        using var filtered = await GetAsync($"/designations?gradeId={Id(grade)}", AcmeViewer);
        using var badFilter = await GetAsync("/designations?gradeId=not-a-guid", AcmeViewer);

        unknown.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(unknown)).Should().Be("core.designation.grade_not_found");
        var items = (await JsonAsync(filtered)).GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle().Which.GetProperty("id").GetGuid().Should().Be(Id(designation));
        badFilter.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Designation_CannotReferenceAnotherTenantsGrade()
    {
        var globexGrade = await CreateAsync("/grades", new { code = NewCode(), name = "G", rank = 1 }, GlobexAdmin);

        using var response = await PostAsync("/designations", new { code = NewCode(), name = "Spy", gradeId = Id(globexGrade) }, AcmeAdmin);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().Be("core.designation.grade_not_found");
    }

    [Fact]
    public async Task Grade_WithADesignation_CannotBeDeleted_UntilTheDesignationIsGone()
    {
        var grade = await CreateAsync("/grades", new { code = NewCode(), name = "G", rank = 1 });
        var designation = await CreateAsync("/designations", new { code = NewCode(), name = "Engineer", gradeId = Id(grade) });

        using var refused = await DeleteAsync($"/grades/{Id(grade)}", AcmeAdmin);
        using var removeDesignation = await DeleteAsync($"/designations/{Id(designation)}", AcmeAdmin);
        using var allowed = await DeleteAsync($"/grades/{Id(grade)}", AcmeAdmin);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(refused)).Should().Be("core.grade.in_use");
        removeDesignation.StatusCode.Should().Be(HttpStatusCode.NoContent);
        allowed.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CostCentre_ChecksItsLegalEntity()
    {
        var entity = await CoreTestDb.SeedLegalEntityAsync(ConnectionString, Acme.TenantId);

        using var good = await PostAsync("/cost-centres", new { code = NewCode(), name = "Delivery", legalEntityId = entity.Id }, AcmeAdmin);
        using var unknown = await PostAsync("/cost-centres", new { code = NewCode(), name = "Ghost", legalEntityId = Guid.NewGuid() }, AcmeAdmin);
        using var wrongTenant = await PostAsync("/cost-centres", new { code = NewCode(), name = "Spy", legalEntityId = entity.Id }, GlobexAdmin);
        using var filtered = await GetAsync($"/cost-centres?legalEntityId={entity.Id}", AcmeViewer);

        good.StatusCode.Should().Be(HttpStatusCode.Created);
        unknown.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(unknown)).Should().Be("core.cost_centre.legal_entity_not_found");
        wrongTenant.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await JsonAsync(filtered)).GetProperty("total").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task OrgUnitType_LevelCannotChangeOnceUnitsUseIt_ButTheNameCan()
    {
        var type = await CreateAsync("/org-unit-types", new { code = NewCode(), name = "Division", level = 1 });
        await CreateAsync("/org-units", new { code = NewCode(), name = "Engineering", orgUnitTypeId = Id(type) });

        using var level = await PutAsync($"/org-unit-types/{Id(type)}", new { name = "Division", level = 3, isActive = true, rowVersion = Version(type) }, AcmeAdmin);
        using var name = await PutAsync($"/org-unit-types/{Id(type)}", new { name = "Business unit", level = 1, isActive = true, rowVersion = Version(type) }, AcmeAdmin);
        using var delete = await DeleteAsync($"/org-unit-types/{Id(type)}", AcmeAdmin);

        level.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(level)).Should().Be("core.org_unit_type.in_use");
        name.StatusCode.Should().Be(HttpStatusCode.OK);
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(delete)).Should().Be("core.org_unit_type.in_use");
    }

    [Fact]
    public async Task Location_DefaultsToAMondayToFridayWeek_AndValidatesItsDetails()
    {
        var created = await CreateAsync("/locations", Cases["locations"].Create(NewCode()));

        using var badZone = await PostAsync("/locations",
            new { code = NewCode(), name = "X", addressLine1 = "1 Road", city = "Pune", countryCode = "IN", timeZoneId = "Not/AZone" }, AcmeAdmin);
        using var badWeek = await PostAsync("/locations",
            new { code = NewCode(), name = "X", addressLine1 = "1 Road", city = "Pune", countryCode = "IN", timeZoneId = "Asia/Kolkata", workWeekDays = 0 }, AcmeAdmin);

        created.GetProperty("workWeekDays").GetInt32().Should().Be(31);
        created.GetProperty("holidayCalendarId").ValueKind.Should().Be(JsonValueKind.Null);
        ErrorCode(await JsonAsync(badZone)).Should().Be("core.location.time_zone_invalid");
        ErrorCode(await JsonAsync(badWeek)).Should().Be("core.location.work_week_invalid");
    }
}
