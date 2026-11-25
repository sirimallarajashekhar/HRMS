using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hrms.Core.IntegrationTests;

public class ApiHostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Ping_V1_ReturnsOkWithServerTime()
    {
        var response = await _client.GetAsync("/api/v1/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PingResponse>();
        body!.Status.Should().Be("ok");
        body.ServerTimeUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_Endpoints_ReturnHealthy(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OpenApi_Document_IsServedAndListsPing()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await response.Content.ReadAsStringAsync();
        document.Should().Contain("\"/api/v1/ping\"").And.NotContain("{version}");
    }

    [Fact]
    public async Task OpenApi_Document_IsNotServedInProduction()
    {
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/api/v2/ping")]
    [InlineData("/api/ping")]
    public async Task Ping_WithUnsupportedOrMissingVersion_ReturnsProblemDetails(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task UnhandledException_StillCarriesCorrelationId()
    {
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new ThrowingTimeProvider()))));
        using var client = failing.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/ping");
        request.Headers.Add("X-Correlation-Id", "trace-500");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle().Which.Should().Be("trace-500");
    }

    [Fact]
    public async Task Request_WithMalformedCorrelationId_ReplacesIt()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "bad id;with<stuff>");

        var response = await _client.SendAsync(request);

        var echoed = response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle().Subject;
        echoed.Should().NotBe("bad id;with<stuff>").And.MatchRegex("^[A-Za-z0-9._-]{1,64}$");
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetailsWithCode()
    {
        var response = await _client.GetAsync("/api/v1/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("code").GetString().Should().Be("http.404");
        problem.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task UnhandledException_ReturnsProblemDetailsWithoutLeakingDetails()
    {
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new ThrowingTimeProvider()))));
        using var client = failing.CreateClient();

        var response = await client.GetAsync("/api/v1/ping");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("secret internal detail").And.NotContain("StackTrace");
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("code").GetString().Should().Be("server.unexpected");
    }

    [Fact]
    public async Task Request_WithCorrelationId_EchoesItBack()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var response = await _client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle().Which.Should().Be("abc-123");
    }

    [Fact]
    public async Task Request_WithoutCorrelationId_GeneratesOne()
    {
        var response = await _client.GetAsync("/health/live");

        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("secret internal detail");
    }
}
