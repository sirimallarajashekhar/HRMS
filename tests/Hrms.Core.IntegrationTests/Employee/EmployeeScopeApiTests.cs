using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Identity;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Employee;

/// <summary>
/// The data scope in action: who sees whom for Self, DirectReports, AllReports, Department, Location, LegalEntity and Tenant, through the real
/// token server, the real scope service and the real employee queries. The organisation is a small company:
/// CEO ← M1 (unit A, HQ) ← E1 (unit A, HQ), E2 (team A1 of unit A, Pune), leaver L (unit A, HQ, left yesterday);  CEO ← M2 (unit B, Pune) ← E3 (unit B, Pune).
/// </summary>
[Collection(MySqlTestGroup.Name)]
public class EmployeeScopeApiTests(EmployeeApiFixture fixture) : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>, IAsyncLifetime
{
    private static readonly string[] ViewOnly = [Permissions.Employee.EmployeeView];

    private Guid _ceo, _m1, _m2, _e1, _e2, _e3, _leaver;

    private sealed record Company(Guid Ceo, Guid M1, Guid M2, Guid E1, Guid E2, Guid E3, Guid Leaver);

    public async Task InitializeAsync()
    {
        var company = await Fixture.SharedAsync(BuildCompanyAsync);
        (_ceo, _m1, _m2, _e1, _e2, _e3, _leaver) = (company.Ceo, company.M1, company.M2, company.E1, company.E2, company.E3, company.Leaver);
    }

    private async Task<Company> BuildCompanyAsync()
    {
        var ceo = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "Cee", "Eo", unit: Acme.RootUnitId, location: Acme.LocationId)));
        var m1 = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "Em", "One", manager: ceo, unit: Acme.DepartmentAId, location: Acme.LocationId)));
        var m2 = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "Em", "Two", manager: ceo, unit: Acme.DepartmentBId, location: Acme.SecondLocationId)));
        var e1 = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "Ee", "One", manager: m1, unit: Acme.DepartmentAId, location: Acme.LocationId)));
        var e2 = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "Ee", "Two", manager: m1, unit: Acme.SubUnitOfAId, location: Acme.SecondLocationId)));
        var e3 = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "Ee", "Three", manager: m2, unit: Acme.DepartmentBId, location: Acme.SecondLocationId)));
        var leaver = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "El", "Leaver", manager: m1, unit: Acme.DepartmentAId, location: Acme.LocationId)));
        using var left = await PutAsync($"/employees/{leaver}/separation", new { type = "resignation", lastWorkingDay = Iso(Today.AddDays(-1)) }, Acme.HrAdmin);
        left.StatusCode.Should().Be(HttpStatusCode.OK, await left.Content.ReadAsStringAsync());
        return new Company(ceo, m1, m2, e1, e2, e3, leaver);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> TokenAsync(ScopeType scope, Guid? value, Guid? linkedEmployee) =>
        (await Host.TokenWithScopeAsync(Acme.Tenant.TenantId, "ACME", EmployeeApiFixture.Password, scope, value, linkedEmployee, ViewOnly)).Token;

    private async Task<List<Guid>> VisibleAsync(string token, string status = "all")
    {
        var ids = new List<Guid>();
        for (var page = 1; ; page++)
        {
            using var response = await GetAsync($"/employees?status={status}&pageSize=200&page={page}", token);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var json = await JsonAsync(response);
            ids.AddRange(json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            if (ids.Count >= json.GetProperty("total").GetInt64())
            {
                return ids;
            }
        }
    }

    private static readonly Func<Guid[], Guid[]> Mine = ids => ids;

    [Fact]
    public async Task Self_SeesOnlyTheOwnRecord_AndOthersAreNotFound()
    {
        var token = await TokenAsync(ScopeType.Self, null, _e1);

        (await VisibleAsync(token)).Should().Equal(_e1);
        using var other = await GetAsync($"/employees/{_e2}/profile", token);
        using var own = await GetAsync($"/employees/{_e1}/profile", token);
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        own.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DirectReports_SeesTheTeam_NotTheManager_NotLeavers_NotOtherTeams()
    {
        var token = await TokenAsync(ScopeType.DirectReports, null, _m1);

        var visible = await VisibleAsync(token);

        visible.Should().BeEquivalentTo(Mine([_e1, _e2]), "a leaver leaves their manager's scope, and other teams were never in it");
        using var outside = await GetAsync($"/employees/{_e3}/profile", token);
        using var self = await GetAsync($"/employees/{_m1}/profile", token);
        using var leaver = await GetAsync($"/employees/{_leaver}/jobs", token);
        outside.StatusCode.Should().Be(HttpStatusCode.NotFound);
        self.StatusCode.Should().Be(HttpStatusCode.NotFound, "direct reports do not include the manager");
        leaver.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AllReports_SeesTheWholeReportingChain_ButNotTheTopOrLeavers()
    {
        var token = await TokenAsync(ScopeType.AllReports, null, _ceo);

        var visible = await VisibleAsync(token);

        visible.Should().BeEquivalentTo(Mine([_m1, _m2, _e1, _e2, _e3]));
    }

    [Fact]
    public async Task AllReports_OfAMiddleManager_StopsAtTheirOwnTree()
    {
        var token = await TokenAsync(ScopeType.AllReports, null, _m2);

        (await VisibleAsync(token)).Should().Equal(_e3);
    }

    [Fact]
    public async Task Department_IncludesSubUnits_AndKeepsLeavers()
    {
        var token = await TokenAsync(ScopeType.Department, Acme.DepartmentAId, null);

        var visible = await VisibleAsync(token);

        visible.Should().BeEquivalentTo(Mine([_m1, _e1, _e2, _leaver]), "unit A, its team A1 and the person who left from it");
    }

    [Fact]
    public async Task Location_SeesThePeopleWhoseCurrentJobIsThere()
    {
        var token = await TokenAsync(ScopeType.Location, Acme.SecondLocationId, null);

        var visible = await VisibleAsync(token);

        visible.Should().BeEquivalentTo(Mine([_m2, _e2, _e3]));
    }

    [Fact]
    public async Task LegalEntity_SeesEveryoneInThatEntity()
    {
        var token = await TokenAsync(ScopeType.LegalEntity, Acme.LegalEntityId, null);

        var visible = await VisibleAsync(token);

        visible.Should().Contain([_ceo, _m1, _m2, _e1, _e2, _e3, _leaver]);
        var other = await TokenAsync(ScopeType.LegalEntity, Acme.SecondLegalEntityId, null);
        (await VisibleAsync(other)).Should().NotContain([_ceo, _m1, _e1]);
    }

    [Fact]
    public async Task Tenant_SeesEveryone_IncludingLeavers()
    {
        var visible = await VisibleAsync(Acme.Viewer);

        visible.Should().Contain([_ceo, _m1, _m2, _e1, _e2, _e3, _leaver]);
    }

    [Fact]
    public async Task AnAccountNotLinkedToAnEmployee_SeesNothingThroughPersonalScopes()
    {
        foreach (var scope in new[] { ScopeType.Self, ScopeType.DirectReports, ScopeType.AllReports })
        {
            var token = await TokenAsync(scope, null, null);

            (await VisibleAsync(token)).Should().BeEmpty($"{scope} needs an employee to be relative to");
            using var profile = await GetAsync($"/employees/{_e1}/profile", token);
            profile.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task AScopeWithoutItsValue_SeesNothing()
    {
        var department = await TokenAsync(ScopeType.Department, Guid.NewGuid(), null);

        (await VisibleAsync(department)).Should().BeEmpty("a department that does not exist matches nobody");
    }

    [Fact]
    public async Task TheScopeFollowsTheJobHistory_ANewManagerMovesTheReportOverToday()
    {
        // Two managers of their own, so the shared company stays as the other tests expect it.
        var bossA = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "Boss", "Alpha")));
        var bossB = Id(await CreateEmployeeAsync(Acme, CreateBody(Acme, "Boss", "Beta")));
        var created = await CreateEmployeeAsync(Acme, CreateBody(Acme, "Moving", "Report", manager: bossA));
        var id = Id(created);
        var tokenA = await TokenAsync(ScopeType.DirectReports, null, bossA);
        var tokenB = await TokenAsync(ScopeType.DirectReports, null, bossB);
        (await VisibleAsync(tokenA)).Should().Contain(id);

        await ChangeJobAsync(id, Version(created), Today, JobBody(Acme, manager: bossB), "transfer");

        (await VisibleAsync(tokenA)).Should().NotContain(id);
        (await VisibleAsync(tokenB)).Should().Contain(id);
    }

    [Fact]
    public async Task ScopesAreUnioned_AndAreAppliedPerPermission()
    {
        var (token, userId) = await Host.TokenWithScopeAsync(Acme.Tenant.TenantId, "ACME", EmployeeApiFixture.Password, ScopeType.Self, null, _e1, ViewOnly);
        await using (var identity = IdentityTestDb.Context(ConnectionString, Acme.Tenant.TenantId))
        {
            var roleId = await identity.UserRoleScopes.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.RoleId).SingleAsync();
            identity.UserRoleScopes.Add(UserRoleScope.Create(userId, roleId, ScopeType.Location, Acme.SecondLocationId).Value);
            await identity.SaveChangesAsync();
        }

        var visible = await VisibleAsync(token);

        visible.Should().BeEquivalentTo(Mine([_e1, _m2, _e2, _e3]), "the own record plus everyone at the second location");
        // The role holds only the employee view permission, so personal data stays closed whatever the scope says.
        using var profile = await GetAsync($"/employees/{_e1}/profile", token);
        (await JsonAsync(profile)).GetProperty("personal").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheScopeServiceItself_ExpandsEachKindForOtherModules()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(Acme.Tenant.TenantId);
        var expander = scope.ServiceProvider.GetRequiredService<IEmployeeScopeExpander>();

        var self = await expander.ExpandAsync(_e1, [new DataScopeGrant(ScopeKind.Self)], CancellationToken.None);
        var direct = await expander.ExpandAsync(_ceo, [new DataScopeGrant(ScopeKind.DirectReports)], CancellationToken.None);
        var all = await expander.ExpandAsync(_ceo, [new DataScopeGrant(ScopeKind.AllReports)], CancellationToken.None);
        var unlinked = await expander.ExpandAsync(null, [new DataScopeGrant(ScopeKind.AllReports), new DataScopeGrant(ScopeKind.DirectReports)], CancellationToken.None);

        self.Should().BeEquivalentTo(Mine([_e1]));
        direct.Should().BeEquivalentTo(Mine([_m1, _m2]));
        all.Should().BeEquivalentTo(Mine([_m1, _m2, _e1, _e2, _e3]));
        unlinked.Should().BeEmpty();
    }
}
