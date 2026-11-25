using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Hrms.Core.IntegrationTests.Tenancy;

public class RealHostTenancyTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void RealHost_InProductionWithTheHeaderResolverEnabled_RefusesToStart()
    {
        using var production = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Tenancy:HeaderResolver:Enabled", "true"));

        var act = () => production.CreateClient();

        act.Should().Throw<OptionsValidationException>().WithMessage("*Development*");
    }

    [Theory]
    [InlineData("/api/v1/ping")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/openapi/v1.json")]
    public async Task RealHost_ProbeAndDescriptionEndpoints_WorkWithoutATenant(string path)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
