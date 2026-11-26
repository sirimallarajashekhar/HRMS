using System.Net;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Users;

namespace Hrms.Core.IntegrationTests.Employee;

/// <summary>Linking a user account to an employee: what makes the personal data scopes work for a real person.</summary>
[Collection(MySqlTestGroup.Name)]
public class UserEmployeeLinkApiTests(EmployeeApiFixture fixture) : EmployeeApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    private async Task<string> UserAdminAsync(TenantSetup s) =>
        (await Host.TokenWithScopeAsync(s.Tenant.TenantId, s.Code, EmployeeApiFixture.Password, ScopeType.Tenant, null, null, Permissions.Identity.UserManage)).Token;

    private async Task<Guid> NewUserAsync(TenantSetup s)
    {
        var user = await Host.AddUserWithPermissionsAsync(s.Tenant.TenantId, $"link-{Guid.NewGuid():N}@example.com", EmployeeApiFixture.Password, Permissions.Employee.EmployeeView);
        return user.Id;
    }

    [Fact]
    public async Task LinkAndUnlink_ChangeWhatTheAccountCanSee()
    {
        var admin = await UserAdminAsync(Acme);
        var employee = await CreateEmployeeAsync(Acme);
        var userId = await NewUserAsync(Acme);

        using var linked = await PutAsync($"/users/{userId}/employee", new { employeeId = Id(employee) }, admin);
        linked.StatusCode.Should().Be(HttpStatusCode.OK, await linked.Content.ReadAsStringAsync());
        (await JsonAsync(linked)).GetProperty("employeeId").GetGuid().Should().Be(Id(employee));

        var stored = await Host.LoadUserAsync(Acme.Tenant.TenantId, userId);
        stored.EmployeeId.Should().Be(Id(employee));

        using var unlinked = await PutAsync($"/users/{userId}/employee", new { employeeId = (Guid?)null }, admin);
        unlinked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Host.LoadUserAsync(Acme.Tenant.TenantId, userId)).EmployeeId.Should().BeNull();
    }

    [Fact]
    public async Task LinkingAnUnknownEmployee_OrOneFromAnotherTenant_Is422()
    {
        var admin = await UserAdminAsync(Acme);
        var userId = await NewUserAsync(Acme);
        var globexEmployee = await CreateEmployeeAsync(Globex);

        using var unknown = await PutAsync($"/users/{userId}/employee", new { employeeId = Guid.NewGuid() }, admin);
        using var foreign = await PutAsync($"/users/{userId}/employee", new { employeeId = Id(globexEmployee) }, admin);

        unknown.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(unknown)).Should().Be("identity.user.employee_not_found");
        ErrorCode(await JsonAsync(foreign)).Should().Be("identity.user.employee_not_found");
    }

    [Fact]
    public async Task OneEmployeeBelongsToOneAccount()
    {
        var admin = await UserAdminAsync(Acme);
        var employee = await CreateEmployeeAsync(Acme);
        var first = await NewUserAsync(Acme);
        var second = await NewUserAsync(Acme);
        using (await PutAsync($"/users/{first}/employee", new { employeeId = Id(employee) }, admin))
        {
        }

        using var clash = await PutAsync($"/users/{second}/employee", new { employeeId = Id(employee) }, admin);
        using var sameAgain = await PutAsync($"/users/{first}/employee", new { employeeId = Id(employee) }, admin);

        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ErrorCode(await JsonAsync(clash)).Should().Be("identity.user.employee_already_linked");
        sameAgain.StatusCode.Should().Be(HttpStatusCode.OK, "linking an account to the employee it already has is not a clash");
    }

    [Fact]
    public async Task AnotherTenantsUser_IsNotFound_AndTheEndpointNeedsThePermission()
    {
        var admin = await UserAdminAsync(Globex);
        var userId = await NewUserAsync(Acme);
        var employee = await CreateEmployeeAsync(Globex);

        using var foreignUser = await PutAsync($"/users/{userId}/employee", new { employeeId = Id(employee) }, admin);
        using var noPermission = await PutAsync($"/users/{userId}/employee", new { employeeId = Id(employee) }, Acme.Viewer);
        using var anonymous = await PutAsync($"/users/{userId}/employee", new { employeeId = Id(employee) }, null);

        foreignUser.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ErrorCode(await JsonAsync(foreignUser)).Should().Be("identity.user.not_found");
        noPermission.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anonymous.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ALinkedAccount_CanReadItsOwnRecord_ThroughTheSelfScope()
    {
        var admin = await UserAdminAsync(Acme);
        var employee = await CreateEmployeeAsync(Acme);
        var (token, userId) = await Host.TokenWithScopeAsync(Acme.Tenant.TenantId, "ACME", EmployeeApiFixture.Password, ScopeType.Self, null, null, Permissions.Employee.EmployeeView);
        using (var before = await GetAsync($"/employees/{Id(employee)}/profile", token))
        {
            before.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using var link = await PutAsync($"/users/{userId}/employee", new { employeeId = Id(employee) }, admin);
        link.StatusCode.Should().Be(HttpStatusCode.OK);

        using var after = await GetAsync($"/employees/{Id(employee)}/profile", token);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ANullBody_Is422()
    {
        var admin = await UserAdminAsync(Acme);
        var userId = await NewUserAsync(Acme);
        using var raw = new HttpRequestMessage(HttpMethod.Put, $"{Api}/users/{userId}/employee") { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") };
        raw.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);

        using var response = await Host.Client.SendAsync(raw);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorCode(await JsonAsync(response)).Should().Be("identity.request.body_required");
    }
}
