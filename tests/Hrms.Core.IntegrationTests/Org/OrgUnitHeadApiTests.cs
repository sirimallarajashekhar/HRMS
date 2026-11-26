using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Core.IntegrationTests.Persistence;

namespace Hrms.Core.IntegrationTests.Org;

/// <summary>Who heads each org unit over time: the data the workflow engine's "unit head" approver rule reads.</summary>
[Collection(MySqlTestGroup.Name)]
public class OrgUnitHeadApiTests(EmployeeApiFixture fixture) : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    // Each test makes its own unit, so they do not depend on each other's history.
    private async Task<Guid> NewUnitAsync(TenantSetup s)
    {
        using var response = await PostAsync(
            "/org-units",
            new { code = $"H-{Guid.NewGuid().ToString("N")[..10]}", name = "Headed unit", orgUnitTypeId = s.DepartmentTypeId, parentId = s.RootUnitId, effectiveFrom = "2020-01-01" },
            s.OrgAdmin);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.Created, json.ToString());
        return Id(json);
    }

    private async Task<JsonElement> HistoryAsync(Guid unit, TenantSetup s)
    {
        using var response = await GetAsync($"/org-units/{unit}/heads", s.OrgAdmin);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, json.ToString());
        return json;
    }

    private Task<HttpResponseMessage> ChangeAsync(Guid unit, Guid? employee, string? from, long? version, string? token = null) =>
        PutAsync($"/org-units/{unit}/head", new { employeeId = employee, effectiveFrom = from, rowVersion = version }, token ?? Acme.OrgAdmin);

    private async Task<JsonElement> ChangedAsync(Guid unit, Guid? employee, string from)
    {
        var version = Version(await HistoryAsync(unit, Acme));
        using var response = await ChangeAsync(unit, employee, from, version);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, json.ToString());
        return json;
    }

    private static List<(Guid Employee, string From, string? To)> Periods(JsonElement history) => history.GetProperty("periods")
        .EnumerateArray()
        .Select(p => (p.GetProperty("employeeId").GetGuid(), p.GetProperty("effectiveFrom").GetString()!, p.GetProperty("effectiveTo").GetString()))
        .ToList();

    [Fact]
    public async Task ChangingTheHead_ClosesTheOldPeriod_AndKeepsHistory_IncludingAGapWithNoHead()
    {
        var unit = await NewUnitAsync(Acme);
        var first = Id(await CreateEmployeeAsync(Acme));
        var second = Id(await CreateEmployeeAsync(Acme));

        await ChangedAsync(unit, first, "2026-01-01");
        await ChangedAsync(unit, second, "2026-06-01");
        await ChangedAsync(unit, null, "2026-09-01");
        var history = await ChangedAsync(unit, first, "2026-10-01");

        Periods(history).Should().Equal(
            (first, "2026-01-01", "2026-05-31"),
            (second, "2026-06-01", "2026-08-31"),
            (first, "2026-10-01", null));
    }

    [Theory]
    [InlineData("2026-03-01")]
    [InlineData("2026-02-01")]
    public async Task AChangeThatDoesNotStartAfterTheLatestPeriod_Is422(string from)
    {
        var unit = await NewUnitAsync(Acme);
        var head = Id(await CreateEmployeeAsync(Acme));
        await ChangedAsync(unit, head, "2026-03-01");

        using var response = await ChangeAsync(unit, head, from, Version(await HistoryAsync(unit, Acme)));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().Be("core.org_unit.head_date_invalid");
    }

    [Fact]
    public async Task RemovingAHeadThatDoesNotExist_AndAMissingBodyOrVersion_Are422()
    {
        var unit = await NewUnitAsync(Acme);
        var version = Version(await HistoryAsync(unit, Acme));

        using var nothing = await ChangeAsync(unit, null, "2026-03-01", version);
        using var noVersion = await ChangeAsync(unit, null, "2026-03-01", null);

        ErrorCode(await JsonAsync(nothing)).Should().Be("core.org_unit.no_head");
        ErrorCode(await JsonAsync(noVersion)).Should().Be("core.row_version_required");
    }

    [Fact]
    public async Task AStaleRowVersion_Is409_SoTwoChangesCannotBothWin()
    {
        var unit = await NewUnitAsync(Acme);
        var head = Id(await CreateEmployeeAsync(Acme));
        var version = Version(await HistoryAsync(unit, Acme));
        using (var won = await ChangeAsync(unit, head, "2026-01-01", version))
        {
            won.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var lost = await ChangeAsync(unit, head, "2026-02-01", version);

        lost.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(lost)).Should().Be("core.org_unit.stale_version");
    }

    [Fact]
    public async Task AnUnknownEmployee_OrOneOfAnotherTenant_CannotBeHead()
    {
        var unit = await NewUnitAsync(Acme);
        var theirs = Id(await CreateEmployeeAsync(Globex));
        var version = Version(await HistoryAsync(unit, Acme));

        using var unknown = await ChangeAsync(unit, Guid.NewGuid(), "2026-01-01", version);
        using var foreign = await ChangeAsync(unit, theirs, "2026-01-01", version);

        ErrorCode(await JsonAsync(unknown)).Should().Be("core.org_unit.head_not_found");
        ErrorCode(await JsonAsync(foreign)).Should().Be("core.org_unit.head_not_found");
    }

    [Fact]
    public async Task AnotherTenantsUnit_IsNotFound_ForReadingAndWriting()
    {
        var unit = await NewUnitAsync(Acme);
        var theirs = Id(await CreateEmployeeAsync(Globex));

        using var read = await GetAsync($"/org-units/{unit}/heads", Globex.OrgAdmin);
        using var write = await ChangeAsync(unit, theirs, "2026-01-01", 1, Globex.OrgAdmin);

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        write.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Periods(await HistoryAsync(unit, Acme)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheEndpointsNeedTheOrgUnitPermissions()
    {
        var unit = await NewUnitAsync(Acme);

        using var read = await GetAsync($"/org-units/{unit}/heads", Acme.Nobody);
        using var write = await ChangeAsync(unit, null, "2026-01-01", 1, Acme.Nobody);
        using var readOnly = await ChangeAsync(unit, null, "2026-01-01", 1, Acme.HrAdmin);

        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        readOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden, "viewing the structure does not allow changing it");
    }
}
