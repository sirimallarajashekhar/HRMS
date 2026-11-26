using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Core.Infrastructure;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.Core.IntegrationTests.Identity;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Http.Tenancy;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hrms.Core.IntegrationTests.Core;

/// <summary>
/// The whole chain with nothing faked: X-Tenant-Id header, tenant middleware, the real tenant directory, CoreDbContext and
/// MySQL built from the real migrations. The endpoint exists only in this test host: the real endpoints arrive with P0.4
/// permissions, and an endpoint without a permission check would break the deny-by-default rule.
/// </summary>
[Collection(MySqlTestGroup.Name)]
public class TenantIsolationPipelineTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await CoreTestDb.CreateMigratedAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<IHost> StartAsync()
    {
        var host = new HostBuilder()
            .UseEnvironment("Development")
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(TestEncryption.Settings().Concat(new Dictionary<string, string?>
                {
                    ["Tenancy:HeaderResolver:Enabled"] = "true",
                })))
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddProblemDetails();
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton<Serilog.IDiagnosticContext>(new Serilog.Extensions.Hosting.DiagnosticContext(Serilog.Log.Logger));
                    services.AddHrmsTenancy();
                    services.AddHrmsPersistence(_connectionString);
                    services.AddCoreInfrastructure(_connectionString);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseHrmsTenancy();
                    app.UseEndpoints(endpoints => endpoints.MapGet("/legal-entities", async (CoreDbContext db, CancellationToken ct) =>
                        Results.Ok(await db.LegalEntities.AsNoTracking().OrderBy(e => e.Code).Select(e => e.Code).ToListAsync(ct))));
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private static HttpRequestMessage Get(TenantId? tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/legal-entities");
        if (tenant is { } id)
        {
            request.Headers.Add(HeaderTenantResolver.HeaderName, id.ToString());
        }

        return request;
    }

    private static async Task<string[]> CodesAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<string[]>(await response.Content.ReadAsStringAsync())!;

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    [Fact]
    public async Task Request_AsTenantA_ReturnsOnlyTenantAsLegalEntities_AndTenantBGetsOnlyItsOwn()
    {
        var a = await CoreTestDb.SeedTenantAsync(_connectionString);
        var b = await CoreTestDb.SeedTenantAsync(_connectionString);
        await CoreTestDb.SeedLegalEntityAsync(_connectionString, a.TenantId, "A-ONE");
        await CoreTestDb.SeedLegalEntityAsync(_connectionString, a.TenantId, "A-TWO");
        await CoreTestDb.SeedLegalEntityAsync(_connectionString, b.TenantId, "B-ONE");
        using var host = await StartAsync();
        var client = host.GetTestClient();

        var asA = await client.SendAsync(Get(a.TenantId));
        var asB = await client.SendAsync(Get(b.TenantId));

        asA.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CodesAsync(asA)).Should().Equal("A-ONE", "A-TWO");
        (await CodesAsync(asB)).Should().Equal("B-ONE");
    }

    [Fact]
    public async Task Request_ForASuspendedTenant_Returns403AndNoData()
    {
        var suspended = await CoreTestDb.SeedTenantAsync(_connectionString, suspended: true);
        await CoreTestDb.SeedLegalEntityAsync(_connectionString, suspended.TenantId, "SECRET");
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(Get(suspended.TenantId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be("tenant.forbidden");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("SECRET");
    }

    [Fact]
    public async Task Request_ForAnUnknownTenant_Returns403LikeASuspendedOne()
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(Get(PersistenceTestDb.NewTenant()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).Should().Be("tenant.forbidden");
    }

    [Fact]
    public async Task Request_WithoutATenant_Returns400()
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(Get(tenant: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response)).Should().Be("tenant.missing");
    }

    [Fact]
    public async Task Requests_ForManyTenantsAtOnce_NeverMixTheirData()
    {
        var tenants = new List<(Hrms.Core.Domain.Tenants.Tenant Tenant, string Code)>();
        for (var i = 0; i < 10; i++)
        {
            var tenant = await CoreTestDb.SeedTenantAsync(_connectionString);
            var code = $"ONLY-{i}";
            await CoreTestDb.SeedLegalEntityAsync(_connectionString, tenant.TenantId, code);
            tenants.Add((tenant, code));
        }

        using var host = await StartAsync();
        var client = host.GetTestClient();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 60).Select(async n =>
            {
                var (tenant, code) = tenants[n % tenants.Count];
                var response = await client.SendAsync(Get(tenant.TenantId));
                return (Expected: code, Actual: await CodesAsync(response));
            }));

        foreach (var (expected, actual) in results)
        {
            actual.Should().Equal(expected);
        }
    }
}
