using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.Domain.LegalEntities;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.IntegrationTests.Core;
using Hrms.Core.IntegrationTests.Identity;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Users;
using Hrms.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Employee;

/// <summary>Everything a test needs from one tenant: its organisation structure (created through the real API) and ready-made tokens.</summary>
public sealed record TenantSetup(
    string Code,
    Tenant Tenant,
    Guid LegalEntityId,
    Guid SecondLegalEntityId,
    Guid ForeignLegalEntityId,
    Guid LocationId,
    Guid SecondLocationId,
    Guid DivisionTypeId,
    Guid DepartmentTypeId,
    Guid RootUnitId,
    Guid DepartmentAId,
    Guid DepartmentBId,
    Guid SubUnitOfAId,
    Guid DesignationId,
    Guid GradeId,
    Guid EmployeeTypeId,
    Guid CostCentreId,
    string HrAdmin,
    string HrSensitive,
    string Viewer,
    string Nobody,
    string OrgAdmin);

/// <summary>
/// The real chain (token server, tenancy, permissions, data scopes, MySQL from the real migrations, org and employee endpoints), started once
/// per test class with two tenants whose organisation structure and employee code rules are set up through the API.
/// </summary>
public sealed class EmployeeApiFixture(MySqlFixture mysql) : IAsyncLifetime
{
    public const string Password = "correct-horse-battery";

    private static readonly Dictionary<string, string?> FastHashing = new()
    {
        ["Identity:PasswordHashing:IterationCount"] = Hrms.Identity.Infrastructure.Security.PasswordHashingOptions.MinimumIterationCount
            .ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static readonly string[] OrgPermissions = [.. Permissions.Core.OrgStructureView, .. Permissions.Core.OrgStructureManage];

    public string ConnectionString { get; private set; } = string.Empty;

    public TokenServerTestHost Host { get; private set; } = null!;

    public TenantSetup Acme { get; private set; } = null!;

    public TenantSetup Globex { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        ConnectionString = await TokenServerTestHost.CreateDatabaseAsync(mysql);
        Host = await TokenServerTestHost.StartAsync(ConnectionString, FastHashing);
        await Host.EnsureClientsAsync();
        Acme = await SetUpTenantAsync("ACME");
        Globex = await SetUpTenantAsync("GLOBEX");
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Lazy<Task<object>>> _shared = new();

    /// <summary>Builds something once per test class (for example a small company of employees) and hands the same instance to every test.</summary>
    public async Task<T> SharedAsync<T>(Func<Task<T>> create)
        where T : notnull =>
        (T)await _shared.GetOrAdd(typeof(T), _ => new Lazy<Task<object>>(async () => await create())).Value;
    private async Task<TenantSetup> SetUpTenantAsync(string code)
    {
        var tenant = await TokenServerTestHost.SeedTenantAsync(ConnectionString, code);
        var id = tenant.TenantId;

        var legalEntity = await SeedLegalEntityAsync(id, $"{code}-IN", "IN", "INR", "IN");
        var second = await SeedLegalEntityAsync(id, $"{code}-IN2", "IN", "INR", "IN");
        var foreign = await SeedLegalEntityAsync(id, $"{code}-US", "US", "USD", "US");

        var orgAdmin = await Host.TokenWithPermissionsAsync(id, code, Password, OrgPermissions);
        var hrPermissions = Permissions.Employee.ReadAll.Concat(Permissions.Employee.ManageAll).Concat(Permissions.Core.OrgStructureView).ToArray();

        async Task<Guid> CreateAsync(string path, object body)
        {
            using var response = await Host.SendAsync(HttpMethod.Post, "/api/v1" + path, orgAdmin, body);
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Created, text);
            return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
        }

        var location = await CreateAsync("/locations", new { code = "HQ", name = "Head office", addressLine1 = "1 MG Road", city = "Bengaluru", countryCode = "IN", timeZoneId = "Asia/Kolkata" });
        var location2 = await CreateAsync("/locations", new { code = "PUNE", name = "Pune office", addressLine1 = "2 FC Road", city = "Pune", countryCode = "IN", timeZoneId = "Asia/Kolkata" });
        var divisionType = await CreateAsync("/org-unit-types", new { code = "DIV", name = "Division", level = 1 });
        var departmentType = await CreateAsync("/org-unit-types", new { code = "DEPT", name = "Department", level = 2 });
        var teamType = await CreateAsync("/org-unit-types", new { code = "TEAM", name = "Team", level = 3 });
        var root = await CreateAsync("/org-units", new { code = "ROOT", name = "Company", orgUnitTypeId = divisionType, effectiveFrom = "2020-01-01" });
        var deptA = await CreateAsync("/org-units", new { code = "DEPT-A", name = "Department A", orgUnitTypeId = departmentType, parentId = root, effectiveFrom = "2020-01-01" });
        var deptB = await CreateAsync("/org-units", new { code = "DEPT-B", name = "Department B", orgUnitTypeId = departmentType, parentId = root, effectiveFrom = "2020-01-01" });
        var teamOfA = await CreateAsync("/org-units", new { code = "TEAM-A1", name = "Team A1", orgUnitTypeId = teamType, parentId = deptA, effectiveFrom = "2020-01-01" });
        var designation = await CreateAsync("/designations", new { code = "ENG", name = "Engineer" });
        var grade = await CreateAsync("/grades", new { code = "G1", name = "Grade 1", rank = 1 });
        var employeeType = await CreateAsync("/employee-types", new { code = "PERM", name = "Permanent", isPayrollEligible = true });
        var costCentre = await CreateAsync("/cost-centres", new { code = "CC1", name = "Delivery" });

        var hr = await Host.TokenWithScopeAsync(id, code, Password, ScopeType.Tenant, null, null, hrPermissions);
        var sensitive = await Host.TokenWithScopeAsync(id, code, Password, ScopeType.Tenant, null, null, [.. hrPermissions, Permissions.Identity.SensitiveView]);
        var viewer = await Host.TokenWithScopeAsync(id, code, Password, ScopeType.Tenant, null, null, Permissions.Employee.EmployeeView);
        var nobody = await Host.TokenWithScopeAsync(id, code, Password, ScopeType.Tenant, null, null);

        foreach (var (entity, prefix) in new[] { (legalEntity, "EMP"), (second, "ENT"), (foreign, "USX") })
        {
            using var rule = await Host.SendAsync(HttpMethod.Put, $"/api/v1/employee-code-rules/{entity}", hr.Token, new { prefix, width = 5, nextNumber = 1 });
            rule.StatusCode.Should().Be(HttpStatusCode.OK, await rule.Content.ReadAsStringAsync());
        }

        return new TenantSetup(
            code, tenant, legalEntity, second, foreign, location, location2, divisionType, departmentType, root, deptA, deptB, teamOfA,
            designation, grade, employeeType, costCentre, hr.Token, sensitive.Token, viewer.Token, nobody.Token, orgAdmin);
    }

    public async Task<Guid> SeedLegalEntityAsync(TenantId tenant, string code, string country, string currency, string pack)
    {
        var entity = LegalEntity.Create(code, "Legal entity " + code, country, currency, 4, pack, "en-IN", "Asia/Kolkata").Value;
        await using var context = CoreTestDb.Context(ConnectionString, tenant);
        context.LegalEntities.Add(entity);
        await context.SaveChangesAsync();
        return entity.Id;
    }
}

/// <summary>Helpers for the employee API tests; derive and add <c>IClassFixture&lt;EmployeeApiFixture&gt;</c>.</summary>
public abstract class EmployeeApiTestBase(EmployeeApiFixture fixture)
{
    protected const string Api = "/api/v1";

    protected EmployeeApiFixture Fixture { get; } = fixture;

    protected TokenServerTestHost Host => Fixture.Host;

    protected TenantSetup Acme => Fixture.Acme;

    protected TenantSetup Globex => Fixture.Globex;

    protected string ConnectionString => Fixture.ConnectionString;

    protected static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    protected static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    protected Task<HttpResponseMessage> GetAsync(string path, string? token) => Host.SendAsync(HttpMethod.Get, Api + path, token);

    protected Task<HttpResponseMessage> PostAsync(string path, object? body, string? token) => Host.SendAsync(HttpMethod.Post, Api + path, token, body);

    protected Task<HttpResponseMessage> PutAsync(string path, object body, string? token) => Host.SendAsync(HttpMethod.Put, Api + path, token, body);

    protected Task<HttpResponseMessage> DeleteAsync(string path, string? token) => Host.SendAsync(HttpMethod.Delete, Api + path, token);

    protected static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    protected static string ErrorCode(JsonElement problem) => problem.GetProperty("code").GetString()!;

    protected static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();

    protected static long Version(JsonElement element) => element.GetProperty("rowVersion").GetInt64();

    protected static object JobBody(
        TenantSetup s,
        Guid? legalEntity = null,
        Guid? unit = null,
        Guid? location = null,
        Guid? manager = null,
        int notice = 30,
        Guid? designation = null) => new
        {
            legalEntityId = legalEntity ?? s.LegalEntityId,
            locationId = location ?? s.LocationId,
            orgUnitId = unit ?? s.DepartmentAId,
            designationId = designation ?? s.DesignationId,
            employeeTypeId = s.EmployeeTypeId,
            gradeId = s.GradeId,
            costCentreId = s.CostCentreId,
            managerId = manager,
            probationEnd = "2099-01-01",
            noticePeriodDays = notice,
        };

    protected static object CreateBody(
        TenantSetup s,
        string first = "Asha",
        string last = "Rao",
        DateOnly? joining = null,
        Guid? manager = null,
        Guid? legalEntity = null,
        Guid? unit = null,
        Guid? location = null) => new
        {
            joiningDate = Iso(joining ?? new DateOnly(2026, 1, 1)),
            personal = new { firstName = first, lastName = last, dateOfBirth = "1990-05-17", gender = "female", personalEmail = "asha@example.com", phone = "+91 98765 43210" },
            job = JobBody(s, legalEntity, unit, location, manager),
        };

    /// <summary>Creates an employee as the tenant's HR administrator and returns the summary.</summary>
    protected async Task<JsonElement> CreateEmployeeAsync(TenantSetup s, object? body = null)
    {
        using var response = await PostAsync("/employees", body ?? CreateBody(s), s.HrAdmin);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.Created, json.ToString());
        return json;
    }

    protected async Task<JsonElement> ProfileAsync(Guid id, string token, string query = "")
    {
        using var response = await GetAsync($"/employees/{id}/profile{query}", token);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, json.ToString());
        return json;
    }

    protected async Task<JsonElement> ChangeJobAsync(Guid employee, long version, DateOnly from, object job, string reason = "promotion", string? token = null)
    {
        using var response = await PostAsync($"/employees/{employee}/jobs", new { effectiveFrom = Iso(from), reason, job, rowVersion = version }, token ?? Acme.HrAdmin);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, json.ToString());
        return json;
    }
}
