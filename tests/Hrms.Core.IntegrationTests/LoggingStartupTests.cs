using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hrms.Core.IntegrationTests;

public class LoggingStartupTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    // Regression: WriteTo.Conditional built the Seq sink eagerly, so a missing URL crashed host startup.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://127.0.0.1:1")]
    public async Task Startup_WithSeqServerUrl_StartsAndServesLiveHealth(string seqServerUrl)
    {
        using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("Seq:ServerUrl", seqServerUrl));
        using var client = configured.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
