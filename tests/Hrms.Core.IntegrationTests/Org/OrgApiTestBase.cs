using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.IntegrationTests.Identity;
using Hrms.Core.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Org;

/// <summary>The ids another module would report as in use; one per test host.</summary>
public sealed class FakeUsage
{
    public HashSet<Guid> InUse { get; } = [];
}

/// <summary>A fake of another module's usage check (the employee module registers the real ones from P0.6).</summary>
public sealed class FakeUsageCheck(string master, FakeUsage usage) : IMasterUsageCheck
{
    public string Master { get; } = master;

    public Task<bool> IsInUseAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(usage.InUse.Contains(id));
}

/// <summary>
/// The real chain (token server, tenant resolution, permission authorization, MySQL from the real migrations) with the org endpoints
/// mapped, started once per test class: two tenants and ready-made tokens, an administrator (every org permission), a viewer (view only)
/// and an employee (nothing). Tests use unique codes, so they can share the host and database.
/// </summary>
public sealed class OrgApiFixture(MySqlFixture mysql) : IAsyncLifetime
{
    public const string Password = "correct-horse-battery";

    private static readonly Dictionary<string, string?> FastHashing = new()
    {
        ["Identity:PasswordHashing:IterationCount"] = Hrms.Identity.Infrastructure.Security.PasswordHashingOptions.MinimumIterationCount
            .ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static readonly string[] AllOrgPermissions = [.. Permissions.Core.OrgStructureView, .. Permissions.Core.OrgStructureManage];

    public string ConnectionString { get; private set; } = string.Empty;

    public TokenServerTestHost Host { get; private set; } = null!;

    public Tenant Acme { get; private set; } = null!;

    public Tenant Globex { get; private set; } = null!;

    public string AcmeAdmin { get; private set; } = string.Empty;

    public string AcmeViewer { get; private set; } = string.Empty;

    public string AcmeNobody { get; private set; } = string.Empty;

    public string GlobexAdmin { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await TokenServerTestHost.CreateDatabaseAsync(mysql);
        Acme = await TokenServerTestHost.SeedTenantAsync(ConnectionString, "ACME");
        Globex = await TokenServerTestHost.SeedTenantAsync(ConnectionString, "GLOBEX");
        Host = await TokenServerTestHost.StartAsync(ConnectionString, FastHashing, configureServices: services =>
        {
            services.AddSingleton<FakeUsage>();
            foreach (var kind in new[]
                     {
                         MasterKinds.OrgUnitType, MasterKinds.OrgUnit, MasterKinds.Grade, MasterKinds.Designation, MasterKinds.CostCentre,
                         MasterKinds.EmployeeType, MasterKinds.Location,
                     })
            {
                services.AddScoped<IMasterUsageCheck>(sp => new FakeUsageCheck(kind, sp.GetRequiredService<FakeUsage>()));
            }
        });
        await Host.EnsureClientsAsync();

        AcmeAdmin = await Host.TokenWithPermissionsAsync(Acme.TenantId, "ACME", Password, AllOrgPermissions);
        AcmeViewer = await Host.TokenWithPermissionsAsync(Acme.TenantId, "ACME", Password, [.. Permissions.Core.OrgStructureView]);
        AcmeNobody = await Host.TokenWithPermissionsAsync(Acme.TenantId, "ACME", Password);
        GlobexAdmin = await Host.TokenWithPermissionsAsync(Globex.TenantId, "GLOBEX", Password, AllOrgPermissions);
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();
}

/// <summary>Helpers for the org API tests; derive and add <c>IClassFixture&lt;OrgApiFixture&gt;</c>.</summary>
public abstract class OrgApiTestBase(OrgApiFixture fixture)
{
    protected const string Api = "/api/v1";

    protected OrgApiFixture Fixture { get; } = fixture;

    protected TokenServerTestHost Host => Fixture.Host;

    protected Tenant Acme => Fixture.Acme;

    protected Tenant Globex => Fixture.Globex;

    protected string AcmeAdmin => Fixture.AcmeAdmin;

    protected string AcmeViewer => Fixture.AcmeViewer;

    protected string AcmeNobody => Fixture.AcmeNobody;

    protected string GlobexAdmin => Fixture.GlobexAdmin;

    protected string ConnectionString => Fixture.ConnectionString;

    protected static string NewCode() => $"C{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    protected Task<HttpResponseMessage> GetAsync(string path, string? token) => Host.SendAsync(HttpMethod.Get, Api + path, token);

    protected Task<HttpResponseMessage> PostAsync(string path, object body, string? token) => Host.SendAsync(HttpMethod.Post, Api + path, token, body);

    protected Task<HttpResponseMessage> PutAsync(string path, object body, string? token) => Host.SendAsync(HttpMethod.Put, Api + path, token, body);

    protected Task<HttpResponseMessage> DeleteAsync(string path, string? token) => Host.SendAsync(HttpMethod.Delete, Api + path, token);

    protected static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    protected static string ErrorCode(JsonElement problem) => problem.GetProperty("code").GetString()!;

    /// <summary>Posts, expects 201 and returns the body.</summary>
    protected async Task<JsonElement> CreateAsync(string path, object body, string? token = null)
    {
        using var response = await PostAsync(path, body, token ?? AcmeAdmin);
        var json = await JsonAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.Created, json.ToString());
        return json;
    }

    protected static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();

    protected static long Version(JsonElement element) => element.GetProperty("rowVersion").GetInt64();

    /// <summary>How many SELECT statements the server has run so far (all databases); tests run one at a time, so a difference is reliable.</summary>
    protected async Task<long> SelectCountAsync()
    {
        var root = new MySqlConnectionStringBuilder(ConnectionString) { UserID = "root", Database = string.Empty };
        await using var connection = new MySqlConnection(root.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SHOW GLOBAL STATUS LIKE 'Com_select'", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return long.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
    }
}
