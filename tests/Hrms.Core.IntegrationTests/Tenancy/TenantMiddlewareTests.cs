using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Http;
using Hrms.Http.Tenancy;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Hosting;

namespace Hrms.Core.IntegrationTests.Tenancy;

public class TenantMiddlewareTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
            {
                Events.Add(logEvent);
            }
        }
    }

    private sealed class FakeDirectory(bool active) : ITenantDirectory
    {
        public ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken) => ValueTask.FromResult(active);
    }

    private static async Task<IHost> StartAsync(
        string environment = "Development",
        bool headerResolver = true,
        bool tenantIsActive = true,
        CollectingSink? sink = null)
    {
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink ?? new CollectingSink())
            .CreateLogger();

        var host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tenancy:HeaderResolver:Enabled"] = headerResolver ? "true" : "false",
                }))
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddProblemDetails();
                    services.AddHrmsTenancy();
                    services.AddSingleton<IDiagnosticContext>(new DiagnosticContext(logger));
                    services.AddScoped<ITenantDirectory>(_ => new FakeDirectory(tenantIsActive));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseHrmsTenancy();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/needs-tenant", async (ITenantContext tenant, HttpContext http) =>
                        {
                            await Task.Delay(Random.Shared.Next(1, 15), http.RequestAborted); // let concurrent requests interleave
                            logger.Information("handled");
                            return Results.Ok(new { tenant = tenant.TenantId?.ToString() });
                        });
                        endpoints.MapGet("/open", () => "ok").AllowWithoutTenant();
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private static HttpRequestMessage Get(string path, params string[] tenantHeaderValues)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (tenantHeaderValues.Length > 0)
        {
            request.Headers.Add(HeaderTenantResolver.HeaderName, tenantHeaderValues);
        }

        return request;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    [Fact]
    public async Task Request_WithoutTenant_Returns400TenantMissing()
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(Get("/needs-tenant"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be("tenant.missing");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Request_WithMalformedTenant_Returns400TenantInvalid(string header)
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(Get("/needs-tenant", header));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be("tenant.invalid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Request_WithBlankTenantHeader_IsTreatedAsMissing(string header)
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(Get("/needs-tenant", header));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be("tenant.missing");
    }

    [Fact]
    public async Task Request_WithTwoTenantHeaders_Returns400TenantInvalid()
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(
            Get("/needs-tenant", Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be("tenant.invalid");
    }

    [Fact]
    public async Task Request_ForUnknownOrSuspendedTenant_Returns403WithoutRevealingWhy()
    {
        using var host = await StartAsync(tenantIsActive: false);

        var response = await host.GetTestClient().SendAsync(Get("/needs-tenant", Guid.CreateVersion7().ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(response)).Should().Be("tenant.forbidden");
    }

    [Fact]
    public async Task Request_ForActiveTenant_RunsWithThatTenantAndLogsItsId()
    {
        var sink = new CollectingSink();
        using var host = await StartAsync(sink: sink);
        var tenant = Guid.CreateVersion7();

        var response = await host.GetTestClient().SendAsync(Get("/needs-tenant", tenant.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(tenant.ToString());
        var handled = sink.Events.Should().ContainSingle(e => e.MessageTemplate.Text == "handled").Subject;
        handled.Properties["TenantId"].ToString().Should().Be($"\"{tenant}\"");
    }

    [Fact]
    public async Task Requests_RunningConcurrently_EachSeeOnlyTheirOwnTenant()
    {
        using var host = await StartAsync();
        var client = host.GetTestClient();
        var tenants = Enumerable.Range(0, 40).Select(_ => Guid.CreateVersion7()).ToArray();

        var bodies = await Task.WhenAll(tenants.Select(async tenant =>
        {
            var response = await client.SendAsync(Get("/needs-tenant", tenant.ToString()));
            return (tenant, body: await response.Content.ReadAsStringAsync());
        }));

        foreach (var (tenant, body) in bodies)
        {
            body.Should().Contain(tenant.ToString());
        }
    }

    [Fact]
    public async Task Endpoint_MarkedAllowWithoutTenant_WorksWithoutOne()
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(Get("/open"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnmatchedRoute_StillReturns404NotATenantError()
    {
        using var host = await StartAsync();

        var response = await host.GetTestClient().SendAsync(Get("/nothing-here"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HeaderResolverDisabled_IgnoresTheHeaderAndRejectsTheRequest()
    {
        using var host = await StartAsync(headerResolver: false);

        var response = await host.GetTestClient().SendAsync(Get("/needs-tenant", Guid.CreateVersion7().ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeOf(response)).Should().Be("tenant.missing");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Startup_WithHeaderResolverOutsideDevelopment_Fails(string environment)
    {
        var act = () => StartAsync(environment, headerResolver: true);

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*Development*");
    }

    [Fact]
    public async Task Startup_WithoutHeaderResolverInProduction_Succeeds()
    {
        using var host = await StartAsync("Production", headerResolver: false);

        var response = await host.GetTestClient().SendAsync(Get("/needs-tenant", Guid.CreateVersion7().ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
