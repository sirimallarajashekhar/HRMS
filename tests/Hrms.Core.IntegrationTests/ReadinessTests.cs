using Microsoft.AspNetCore.Hosting;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hrms.Core.IntegrationTests;

public class ReadinessTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Ready_WhenReadyDependencyFails_Returns503ButLiveStays200()
    {
        using var degraded = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHealthChecks().AddCheck("fake-dependency", () => HealthCheckResult.Unhealthy("down"), tags: ["ready"])));
        using var client = degraded.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        live.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("ConnectionStrings:Mysql", "Server=127.0.0.1;Port=1;Database=x;User ID=x;Password=x;Connection Timeout=1")]
    [InlineData("ConnectionStrings:Redis", "127.0.0.1:1,connectTimeout=1000,syncTimeout=1000")]
    [InlineData("ConnectionStrings:RabbitMq", "amqp://x:x@127.0.0.1:1")]
    public async Task Ready_WhenConfiguredDependencyIsUnreachable_Returns503WithoutLeakingDetails(string key, string value)
    {
        using var degraded = factory.WithWebHostBuilder(builder => builder.UseSetting(key, value));
        using var client = degraded.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Unhealthy").And.NotContain("127.0.0.1");
    }
}
