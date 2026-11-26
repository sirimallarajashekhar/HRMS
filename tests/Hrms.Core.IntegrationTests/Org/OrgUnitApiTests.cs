using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Core.IntegrationTests.Persistence;

namespace Hrms.Core.IntegrationTests.Org;

[Collection(MySqlTestGroup.Name)]
public class OrgUnitApiTests(OrgApiFixture fixture) : OrgApiTestBase(fixture), IClassFixture<OrgApiFixture>
{
    private static readonly string Jan1 = "2026-01-01";

    private async Task<JsonElement> TypeAsync(int level, string? token = null) =>
        await CreateAsync("/org-unit-types", new { code = NewCode(), name = $"Level {level}", level }, token);

    private async Task<JsonElement> UnitAsync(JsonElement type, Guid? parent = null, string from = "2026-01-01", string? token = null) =>
        await CreateAsync("/org-units", new { code = NewCode(), name = "Unit", orgUnitTypeId = Id(type), parentId = parent, effectiveFrom = from }, token);

    /// <summary>Division (level 1) > Department (level 2) > Team (level 3) types, created fresh so tests do not depend on each other.</summary>
    private async Task<(JsonElement Division, JsonElement Department, JsonElement Team)> TypesAsync() =>
        (await TypeAsync(1), await TypeAsync(2), await TypeAsync(3));

    private async Task<HttpResponseMessage> MoveAsync(JsonElement unit, Guid? parent, string from, string? token = null, long? version = null) =>
        await PutAsync($"/org-units/{Id(unit)}/parent", new { parentId = parent, effectiveFrom = from, rowVersion = version ?? Version(unit) }, token ?? AcmeAdmin);

    // --- create --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ARootAndAChild_ReportsTheParentAsOfToday()
    {
        var (division, department, _) = await TypesAsync();
        var root = await UnitAsync(division);

        var child = await UnitAsync(department, Id(root));

        child.GetProperty("parentId").GetGuid().Should().Be(Id(root));
        root.GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
        using var fetched = await GetAsync($"/org-units/{Id(child)}", AcmeViewer);
        (await JsonAsync(fetched)).GetProperty("parentId").GetGuid().Should().Be(Id(root));
    }

    [Fact]
    public async Task Create_WithAParentOnTheSameOrALowerLevel_Is422()
    {
        var (division, department, _) = await TypesAsync();
        var sibling = await UnitAsync(department);

        using var sameLevel = await PostAsync("/org-units", new { code = NewCode(), name = "X", orgUnitTypeId = Id(department), parentId = Id(sibling), effectiveFrom = Jan1 }, AcmeAdmin);
        using var higherUnderLower = await PostAsync("/org-units", new { code = NewCode(), name = "X", orgUnitTypeId = Id(division), parentId = Id(sibling), effectiveFrom = Jan1 }, AcmeAdmin);

        ErrorCode(await JsonAsync(sameLevel)).Should().Be("core.org_unit.parent_level_invalid");
        ErrorCode(await JsonAsync(higherUnderLower)).Should().Be("core.org_unit.parent_level_invalid");
    }

    [Fact]
    public async Task Create_WithAnUnknownTypeOrParent_Is422()
    {
        var (division, department, _) = await TypesAsync();

        using var noType = await PostAsync("/org-units", new { code = NewCode(), name = "X", orgUnitTypeId = Guid.NewGuid() }, AcmeAdmin);
        using var noParent = await PostAsync("/org-units", new { code = NewCode(), name = "X", orgUnitTypeId = Id(department), parentId = Guid.NewGuid() }, AcmeAdmin);

        ErrorCode(await JsonAsync(noType)).Should().Be("core.org_unit.type_not_found");
        ErrorCode(await JsonAsync(noParent)).Should().Be("core.org_unit.parent_not_found");
        _ = division;
    }

    [Fact]
    public async Task Create_UnderAnInactiveParent_Is422()
    {
        var (division, department, _) = await TypesAsync();
        var root = await UnitAsync(division);
        using (await PutAsync($"/org-units/{Id(root)}", new { name = "Closed", isActive = false, rowVersion = Version(root) }, AcmeAdmin))
        {
        }

        using var response = await PostAsync("/org-units", new { code = NewCode(), name = "X", orgUnitTypeId = Id(department), parentId = Id(root) }, AcmeAdmin);

        ErrorCode(await JsonAsync(response)).Should().Be("core.org_unit.parent_inactive");
    }

    [Fact]
    public async Task Create_WithADuplicateCode_Is409_AndOtherTenantsMayReuseIt()
    {
        var (division, _, _) = await TypesAsync();
        var globexType = await TypeAsync(1, GlobexAdmin);
        var code = NewCode();
        await CreateAsync("/org-units", new { code, name = "A", orgUnitTypeId = Id(division) });

        using var duplicate = await PostAsync("/org-units", new { code, name = "B", orgUnitTypeId = Id(division) }, AcmeAdmin);
        using var otherTenant = await PostAsync("/org-units", new { code, name = "C", orgUnitTypeId = Id(globexType) }, GlobexAdmin);

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(duplicate)).Should().Be("core.org_unit.code_taken");
        otherTenant.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_WithAnInvalidCodeOrName_Is422()
    {
        var (division, _, _) = await TypesAsync();

        using var code = await PostAsync("/org-units", new { code = "no good", name = "X", orgUnitTypeId = Id(division) }, AcmeAdmin);
        using var name = await PostAsync("/org-units", new { code = NewCode(), name = " ", orgUnitTypeId = Id(division) }, AcmeAdmin);

        ErrorCode(await JsonAsync(code)).Should().Be("core.org_unit.code_invalid");
        ErrorCode(await JsonAsync(name)).Should().Be("core.org_unit.name_invalid");
    }

    // --- update ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangesNameAndActive_ButNotTheParent_AndRefusesAStaleVersion()
    {
        var (division, department, _) = await TypesAsync();
        var root = await UnitAsync(division);
        var child = await UnitAsync(department, Id(root));

        using var ok = await PutAsync($"/org-units/{Id(child)}", new { name = "Renamed", isActive = true, rowVersion = Version(child) }, AcmeAdmin);
        using var stale = await PutAsync($"/org-units/{Id(child)}", new { name = "Again", isActive = true, rowVersion = Version(child) }, AcmeAdmin);

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await JsonAsync(ok);
        json.GetProperty("name").GetString().Should().Be("Renamed");
        json.GetProperty("parentId").GetGuid().Should().Be(Id(root));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(stale)).Should().Be("core.org_unit.stale_version");
    }

    // --- moving, history, cycles ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Move_ReparentsFromADate_AndKeepsTheOldArrangementAsHistory()
    {
        var (division, department, _) = await TypesAsync();
        var a = await UnitAsync(division);
        var b = await UnitAsync(division);
        var unit = await UnitAsync(department, Id(a));

        using var moved = await MoveAsync(unit, Id(b), "2026-06-01");

        moved.StatusCode.Should().Be(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
        (await JsonAsync(moved)).GetProperty("parentId").GetGuid().Should().Be(Id(b));

        using var history = await GetAsync($"/org-units/{Id(unit)}/parents", AcmeViewer);
        var rows = (await JsonAsync(history)).EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        rows[0].GetProperty("parentId").GetGuid().Should().Be(Id(a));
        rows[0].GetProperty("effectiveFrom").GetString().Should().Be("2026-01-01");
        rows[0].GetProperty("effectiveTo").GetString().Should().Be("2026-05-31", "the old period closes the day before the move");
        rows[1].GetProperty("parentId").GetGuid().Should().Be(Id(b));
        rows[1].GetProperty("effectiveTo").ValueKind.Should().Be(JsonValueKind.Null);

        // The past is still answerable: before the move the unit hung under A.
        using var before = await GetAsync($"/org-units/{Id(unit)}?asOf=2026-03-01", AcmeViewer);
        using var after = await GetAsync($"/org-units/{Id(unit)}?asOf=2026-07-01", AcmeViewer);
        (await JsonAsync(before)).GetProperty("parentId").GetGuid().Should().Be(Id(a));
        (await JsonAsync(after)).GetProperty("parentId").GetGuid().Should().Be(Id(b));
    }

    [Fact]
    public async Task Move_ToBecomeARoot_Works()
    {
        var (division, department, _) = await TypesAsync();
        var root = await UnitAsync(division);
        var unit = await UnitAsync(department, Id(root));

        using var moved = await MoveAsync(unit, null, "2026-02-01");

        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(moved)).GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Move_UnderItselfOrADescendant_IsRefused_AndNothingChanges()
    {
        // The level rule (a parent sits on a strictly higher level) already makes a loop impossible through the API; the cycle check in
        // OrgTree is the backstop for data that bypassed it, covered by its own unit tests. Either way the caller gets a 422 and no change.
        var (division, department, team) = await TypesAsync();
        var top = await UnitAsync(division);
        var middle = await UnitAsync(department, Id(top));
        var leaf = await UnitAsync(team, Id(middle));

        using var underItself = await MoveAsync(top, Id(top), "2026-06-01");
        using var underDescendant = await MoveAsync(top, Id(leaf), "2026-06-01");

        underItself.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        underDescendant.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(underDescendant)).Should().BeOneOf("core.org_unit.cycle", "core.org_unit.parent_level_invalid");
        using var history = await GetAsync($"/org-units/{Id(top)}/parents", AcmeViewer);
        (await JsonAsync(history)).GetArrayLength().Should().Be(1);
    }


    [Fact]
    public async Task Move_NotAfterTheCurrentArrangementStarted_Is422()
    {
        var (division, department, _) = await TypesAsync();
        var a = await UnitAsync(division);
        var unit = await UnitAsync(department, Id(a), from: "2026-03-01");

        using var response = await MoveAsync(unit, null, "2026-03-01");

        ErrorCode(await JsonAsync(response)).Should().Be("core.org_unit.parent_change_not_after_latest");
    }

    [Fact]
    public async Task Move_WithAStaleVersion_Is409()
    {
        var (division, department, _) = await TypesAsync();
        var a = await UnitAsync(division);
        var unit = await UnitAsync(department, Id(a));
        using (await PutAsync($"/org-units/{Id(unit)}", new { name = "Changed", isActive = true, rowVersion = Version(unit) }, AcmeAdmin))
        {
        }

        using var response = await MoveAsync(unit, null, "2026-06-01");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Move_TwoMovesBasedOnTheSameVersion_OnlyOneWins()
    {
        var (division, department, _) = await TypesAsync();
        var a = await UnitAsync(division);
        var b = await UnitAsync(division);
        var c = await UnitAsync(division);
        var unit = await UnitAsync(department, Id(a));

        var results = await Task.WhenAll(
            Task.Run(async () => { using var r = await MoveAsync(unit, Id(b), "2026-06-01"); return r.StatusCode; }),
            Task.Run(async () => { using var r = await MoveAsync(unit, Id(c), "2026-06-01"); return r.StatusCode; }));

        results.Count(s => s == HttpStatusCode.OK).Should().Be(1, string.Join(",", results));
        results.Count(s => s == HttpStatusCode.Conflict).Should().Be(1, string.Join(",", results));
        using var history = await GetAsync($"/org-units/{Id(unit)}/parents", AcmeViewer);
        (await JsonAsync(history)).GetArrayLength().Should().Be(2, "exactly one new period was recorded");
    }

    // --- reading ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_FiltersByTypeAndParent_AndPages()
    {
        var (division, department, _) = await TypesAsync();
        var root = await UnitAsync(division);
        var other = await UnitAsync(division);
        var kids = new List<JsonElement>();
        for (var i = 0; i < 3; i++)
        {
            kids.Add(await UnitAsync(department, Id(root)));
        }

        await UnitAsync(department, Id(other));

        using var byParent = await GetAsync($"/org-units?parentId={Id(root)}&pageSize=2&page=2", AcmeViewer);
        using var byType = await GetAsync($"/org-units?typeId={Id(division)}", AcmeViewer);

        var page = await JsonAsync(byParent);
        page.GetProperty("total").GetInt64().Should().Be(3);
        page.GetProperty("items").GetArrayLength().Should().Be(1);
        (await JsonAsync(byType)).GetProperty("total").GetInt64().Should().Be(2);
        _ = kids;
    }

    [Fact]
    public async Task Tree_ListsPlacedUnitsOnADate_AndExcludesUnitsNotYetPlaced()
    {
        var (division, department, _) = await TypesAsync();
        var root = await UnitAsync(division);
        var child = await UnitAsync(department, Id(root), from: "2026-02-01");
        var future = await UnitAsync(department, Id(root), from: "2099-01-01");

        using var response = await GetAsync("/org-units/tree?asOf=2026-03-01", AcmeViewer);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await JsonAsync(response)).EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain([Id(root), Id(child)]).And.NotContain(Id(future));
        var node = (await JsonAsync(await GetAsync("/org-units/tree?asOf=2026-03-01", AcmeViewer))).EnumerateArray().Single(n => n.GetProperty("id").GetGuid() == Id(child));
        node.GetProperty("parentId").GetGuid().Should().Be(Id(root));
        node.GetProperty("level").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Tree_RunsAFixedNumberOfQueries_NoMatterHowManyUnitsThereAre()
    {
        var (division, department, _) = await TypesAsync();
        var root = await UnitAsync(division);
        for (var i = 0; i < 3; i++)
        {
            await UnitAsync(department, Id(root));
        }

        var before = await SelectCountAsync();
        (await GetAsync("/org-units/tree", AcmeViewer)).Dispose();
        var small = await SelectCountAsync() - before;

        for (var i = 0; i < 40; i++)
        {
            await UnitAsync(department, Id(root));
        }

        before = await SelectCountAsync();
        (await GetAsync("/org-units/tree", AcmeViewer)).Dispose();
        var large = await SelectCountAsync() - before;

        large.Should().Be(small, "the whole tree is one projected query however many units exist (no N+1)");
    }

    // --- delete ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_AUnitWithChildren_Is409_ButALeafCanGo_ThenTheParentCan()
    {
        var (division, department, _) = await TypesAsync();
        var root = await UnitAsync(division);
        var child = await UnitAsync(department, Id(root));

        using var refused = await DeleteAsync($"/org-units/{Id(root)}", AcmeAdmin);
        using var leaf = await DeleteAsync($"/org-units/{Id(child)}", AcmeAdmin);
        using var parent = await DeleteAsync($"/org-units/{Id(root)}", AcmeAdmin);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(refused)).Should().Be("core.org_unit.in_use");
        leaf.StatusCode.Should().Be(HttpStatusCode.NoContent);
        parent.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_AUnitAnotherModuleStillUses_Is409()
    {
        var (division, _, _) = await TypesAsync();
        var unit = await UnitAsync(division);
        var usage = Host.Services.GetService(typeof(FakeUsage)) as FakeUsage;
        usage!.InUse.Add(Id(unit));

        using var response = await DeleteAsync($"/org-units/{Id(unit)}", AcmeAdmin);
        usage.InUse.Remove(Id(unit));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- permissions and isolation ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Endpoints_NeedTheRightPermission()
    {
        var (division, _, _) = await TypesAsync();
        var unit = await UnitAsync(division);
        var path = $"/org-units/{Id(unit)}";

        (await GetAsync("/org-units", AcmeNobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync("/org-units/tree", AcmeNobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync($"{path}/parents", AcmeNobody)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync("/org-units", new { code = NewCode(), name = "X", orgUnitTypeId = Id(division) }, AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PutAsync(path, new { name = "X", isActive = true, rowVersion = 1 }, AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await MoveAsync(unit, null, "2026-06-01", AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DeleteAsync(path, AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(path, AcmeViewer)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnotherTenant_CannotSeeMoveOrDeleteUnits_NorHangThemUnderItsOwn()
    {
        var (division, department, _) = await TypesAsync();
        var acmeRoot = await UnitAsync(division);
        var acmeUnit = await UnitAsync(department, Id(acmeRoot));
        var globexDivision = await TypeAsync(1, GlobexAdmin);
        var globexDepartment = await TypeAsync(2, GlobexAdmin);
        var globexRoot = await UnitAsync(globexDivision, token: GlobexAdmin);

        using var read = await GetAsync($"/org-units/{Id(acmeUnit)}", GlobexAdmin);
        using var history = await GetAsync($"/org-units/{Id(acmeUnit)}/parents", GlobexAdmin);
        using var move = await MoveAsync(acmeUnit, Id(globexRoot), "2026-06-01", GlobexAdmin);
        using var update = await PutAsync($"/org-units/{Id(acmeUnit)}", new { name = "Hijacked", isActive = true, rowVersion = Version(acmeUnit) }, GlobexAdmin);
        using var delete = await DeleteAsync($"/org-units/{Id(acmeUnit)}", GlobexAdmin);
        using var underForeignParent = await PostAsync("/org-units", new { code = NewCode(), name = "Spy", orgUnitTypeId = Id(globexDepartment), parentId = Id(acmeRoot) }, GlobexAdmin);
        using var foreignType = await PostAsync("/org-units", new { code = NewCode(), name = "Spy", orgUnitTypeId = Id(department) }, GlobexAdmin);
        using var tree = await GetAsync("/org-units/tree", GlobexAdmin);

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        history.StatusCode.Should().Be(HttpStatusCode.NotFound);
        update.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        move.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ErrorCode(await JsonAsync(underForeignParent)).Should().Be("core.org_unit.parent_not_found");
        ErrorCode(await JsonAsync(foreignType)).Should().Be("core.org_unit.type_not_found");
        var visible = (await JsonAsync(tree)).EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).ToList();
        visible.Should().Contain(Id(globexRoot)).And.NotContain([Id(acmeRoot), Id(acmeUnit)]);
        using var intact = await GetAsync($"/org-units/{Id(acmeUnit)}", AcmeAdmin);
        (await JsonAsync(intact)).GetProperty("name").GetString().Should().Be("Unit");
    }
}
