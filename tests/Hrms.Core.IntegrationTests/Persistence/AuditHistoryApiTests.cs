using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Core.IntegrationTests.Identity;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Users;
using Hrms.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Persistence;

/// <summary>
/// GET /api/v1/audit/{entityType}/{recordId} through the real chain. The point of most tests: history never shows a caller more than
/// the record's own screens would, whatever the audit permission says.
/// </summary>
[Collection(MySqlTestGroup.Name)]
public sealed class AuditHistoryApiTests(EmployeeApiFixture fixture) : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    private static readonly string[] Everything =
        [Permissions.Audit.LogView, .. Permissions.Employee.ReadAll];

    private async Task<string> TokenAsync(TenantSetup setup, ScopeType scope, Guid? linkedEmployee, params string[] permissions) =>
        (await Host.TokenWithScopeAsync(
            setup.Tenant.TenantId, setup.Code, EmployeeApiFixture.Password, scope, null, linkedEmployee, permissions)).Token;

    private async Task<(HttpStatusCode Status, JsonElement Body)> HistoryAsync(string type, Guid id, string token, string query = "")
    {
        using var response = await GetAsync($"/audit/{type}/{id}{query}", token);
        return (response.StatusCode, await JsonAsync(response));
    }

    private static List<string> EntityTypes(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityType").GetString()!).Distinct().Order().ToList();

    [Fact]
    public async Task EmployeeHistory_WithEveryViewPermission_IncludesWhatBelongsToTheEmployee()
    {
        var employee = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenAsync(Acme, ScopeType.Tenant, null, Everything);

        var (status, body) = await HistoryAsync("employee", employee, token);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        EntityTypes(body).Should().Equal("employee", "employee_job", "employee_personal");
        body.GetProperty("total").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task EmployeeHistory_NeverContainsSensitiveValues()
    {
        var employee = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenAsync(Acme, ScopeType.Tenant, null, [.. Everything, Permissions.Identity.SensitiveView]);

        var (_, body) = await HistoryAsync("employee", employee, token);

        var text = body.ToString();
        text.Should().NotContain("1990-05-17").And.NotContain("asha@example.com").And.NotContain("98765");
        var personal = body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("entityType").GetString() == "employee_personal");
        personal.GetProperty("changes").EnumerateArray()
            .Single(c => c.GetProperty("field").GetString() == "dateOfBirth")
            .GetProperty("sensitive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task EmployeeHistory_WithoutThePersonalPermission_LeavesPersonalChangesOut()
    {
        var employee = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenAsync(Acme, ScopeType.Tenant, null, Permissions.Audit.LogView, Permissions.Employee.EmployeeView);

        var (status, body) = await HistoryAsync("employee", employee, token);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        EntityTypes(body).Should().Equal("employee", "employee_job");
        body.GetProperty("total").GetInt32().Should().Be(2, "the total must not reveal hidden entries either");
    }

    [Fact]
    public async Task EmployeeHistory_WhenThePersonalScopeIsNarrowerThanTheEmployeeScope_AppliesEachScopeToItsOwnPart()
    {
        var own = Id(await CreateEmployeeAsync(Acme));
        var other = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenWithTwoRolesAsync(
            own,
            (ScopeType.Tenant, [Permissions.Audit.LogView, Permissions.Employee.EmployeeView]),
            (ScopeType.Self, [Permissions.Employee.PersonalView]));

        var (_, ownHistory) = await HistoryAsync("employee", own, token);
        var (_, otherHistory) = await HistoryAsync("employee", other, token);

        EntityTypes(ownHistory).Should().Equal("employee", "employee_job", "employee_personal");
        EntityTypes(otherHistory).Should().Equal("employee", "employee_job");
    }

    [Fact]
    public async Task EmployeeHistory_ShowsChangeReasons_OnlyToCallersWhoMaySeePersonalData()
    {
        const string reason = "a private matter behind this change";
        Guid employee;
        Host.Client.DefaultRequestHeaders.Add("X-Change-Reason", reason);
        try
        {
            employee = Id(await CreateEmployeeAsync(Acme));
        }
        finally
        {
            Host.Client.DefaultRequestHeaders.Remove("X-Change-Reason");
        }

        var withPersonal = await TokenAsync(Acme, ScopeType.Tenant, null, Everything);
        var withoutPersonal = await TokenAsync(
            Acme, ScopeType.Tenant, null, Permissions.Audit.LogView, Permissions.Employee.EmployeeView);

        var (_, full) = await HistoryAsync("employee", employee, withPersonal);
        var (status, limited) = await HistoryAsync("employee", employee, withoutPersonal);

        full.ToString().Should().Contain(reason);
        status.Should().Be(HttpStatusCode.OK, limited.ToString());
        limited.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        limited.ToString().Should().NotContain(reason);
    }

    [Fact]
    public async Task EmployeeHistory_OutsideTheDataScope_IsNotFound()
    {
        var own = Id(await CreateEmployeeAsync(Acme));
        var other = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenAsync(Acme, ScopeType.Self, own, Everything);

        (await HistoryAsync("employee", own, token)).Status.Should().Be(HttpStatusCode.OK);
        var (status, body) = await HistoryAsync("employee", other, token);

        status.Should().Be(HttpStatusCode.NotFound);
        ErrorCode(body).Should().Be("audit.record.not_found");
    }

    [Fact]
    public async Task History_WithoutTheAuditPermission_IsForbidden()
    {
        var employee = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenAsync(Acme, ScopeType.Tenant, null, [.. Permissions.Employee.ReadAll]);

        (await HistoryAsync("employee", employee, token)).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task History_WithOnlyTheAuditPermission_ShowsNoRecord()
    {
        var employee = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenAsync(Acme, ScopeType.Tenant, null, Permissions.Audit.LogView);

        (await HistoryAsync("employee", employee, token)).Status.Should().Be(HttpStatusCode.NotFound);
        (await HistoryAsync("grade", Acme.GradeId, token)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task History_OfAnotherTenantsRecords_ShowsNothing()
    {
        var employee = Id(await CreateEmployeeAsync(Acme));
        var intruder = await TokenAsync(Globex, ScopeType.Tenant, null, [.. Everything, Permissions.Core.GradeView]);

        (await HistoryAsync("employee", employee, intruder)).Status.Should().Be(HttpStatusCode.NotFound);

        // A master has no existence check of its own: the tenant filter on the log is what keeps the other tenant's rows away.
        var (status, body) = await HistoryAsync("grade", Acme.GradeId, intruder);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("total").GetInt32().Should().Be(0);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("salary")]
    [InlineData("employee_personal")]
    [InlineData("EMPLOYEE")]
    public async Task History_OfATypeThatCannotBeAskedFor_IsNotFound(string type)
    {
        var employee = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenAsync(Acme, ScopeType.Tenant, null, Everything);

        (await HistoryAsync(type, employee, token)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(ScopeType.Self, HttpStatusCode.NotFound)]
    [InlineData(ScopeType.Tenant, HttpStatusCode.OK)]
    public async Task AccountHistory_NeedsATenantWideScope(ScopeType scope, HttpStatusCode expected)
    {
        var (_, userId) = await Host.TokenWithScopeAsync(
            Acme.Tenant.TenantId, "ACME", EmployeeApiFixture.Password, ScopeType.Self, null, null);
        var token = await TokenAsync(Acme, scope, null, Permissions.Audit.LogView, Permissions.Identity.UserView);

        var (status, body) = await HistoryAsync("user_account", userId, token);

        status.Should().Be(expected, body.ToString());
        if (expected == HttpStatusCode.OK)
        {
            EntityTypes(body).Should().Equal("user_account", "user_role_scope");
            body.ToString().Should().NotContain("passwordHash").And.NotContain("securityStamp");
        }
    }

    [Fact]
    public async Task History_IsPagedNewestFirstAndNotCached()
    {
        var created = await CreateEmployeeAsync(Acme);
        var employee = Id(created);
        await ChangeJobAsync(employee, Version(created), new DateOnly(2026, 6, 1), JobBody(Acme, notice: 60));
        var token = await TokenAsync(Acme, ScopeType.Tenant, null, Everything);

        using var response = await GetAsync($"/audit/employee/{employee}?pageSize=2", token);
        var first = await JsonAsync(response);
        var (_, last) = await HistoryAsync("employee", employee, token, "?pageSize=2&page=3");

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        var total = first.GetProperty("total").GetInt32();
        total.Should().BeGreaterThan(3, "the job change adds entries to the three of the creation");
        last.GetProperty("total").GetInt32().Should().Be(total);

        var (_, all) = await HistoryAsync("employee", employee, token, "?pageSize=1000");
        all.GetProperty("pageSize").GetInt32().Should().Be(200, "page size is capped");
        var times = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("occurredAt").GetDateTime()).ToList();
        times.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task History_FiltersByTimeAndRefusesAnEmptyRange()
    {
        var employee = Id(await CreateEmployeeAsync(Acme));
        var token = await TokenAsync(Acme, ScopeType.Tenant, null, Everything);

        var (_, future) = await HistoryAsync("employee", employee, token, "?from=2099-01-01T00:00:00Z");
        var (_, past) = await HistoryAsync("employee", employee, token, "?to=2020-01-01T00:00:00Z");
        var (_, window) = await HistoryAsync("employee", employee, token, "?from=2020-01-01T00:00:00Z&to=2099-01-01T00:00:00Z");
        var (status, problem) = await HistoryAsync("employee", employee, token, "?from=2027-01-01T00:00:00Z&to=2027-01-01T00:00:00Z");

        future.GetProperty("total").GetInt32().Should().Be(0);
        past.GetProperty("total").GetInt32().Should().Be(0);
        window.GetProperty("total").GetInt32().Should().Be(3);
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(problem).Should().Be("audit.history.range_invalid");
    }

    /// <summary>One user holding two roles with different data scopes, which the shared helpers cannot build.</summary>
    private async Task<string> TokenWithTwoRolesAsync(Guid linkedEmployee, params (ScopeType Scope, string[] Permissions)[] roles)
    {
        var tenant = Acme.Tenant.TenantId;
        var email = $"u-{Guid.NewGuid():N}@example.com";
        var user = UserAccount.Create(email, "Two roles", IdentityTestDb.FastHasher().Hash(EmployeeApiFixture.Password), DateTime.UtcNow).Value;
        user.LinkEmployee(linkedEmployee);

        await using (var context = IdentityTestDb.Context(ConnectionString, tenant))
        {
            context.Users.Add(user);
            foreach (var (scope, permissions) in roles)
            {
                var role = Role.Create($"test_{Guid.NewGuid():N}"[..20], "Test role", "Test role", isSystem: false, requiresMfa: false).Value;
                context.Roles.Add(role);
                foreach (var code in permissions)
                {
                    context.RoleGrants.Add(RoleGrant.Create(role.Id, code).Value);
                }

                context.UserRoleScopes.Add(UserRoleScope.Create(user.Id, role.Id, scope, null).Value);
            }

            await context.SaveChangesAsync();
        }

        Host.Services.GetRequiredService<Hrms.Identity.Infrastructure.Security.PermissionCache>().Invalidate(tenant.Value);
        var (status, body) = await Host.PasswordAsync("ACME", email, EmployeeApiFixture.Password);
        status.Should().Be(200, body.ToString());
        return body.GetProperty("access_token").GetString()!;
    }
}
