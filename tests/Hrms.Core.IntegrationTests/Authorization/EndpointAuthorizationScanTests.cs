using FluentAssertions;
using Hrms.Contracts;
using Hrms.Http.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Authorization;

/// <summary>
/// CLAUDE.md: every endpoint has an explicit permission check, deny by default. The fallback policy is the safety net; this scan
/// is the rule: an endpoint must state what it needs (a permission, any signed-in user, or anonymous) or the build fails.
/// </summary>
public class EndpointAuthorizationScanTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    public static IReadOnlyList<string> FindUndeclared(IEnumerable<Endpoint> endpoints) => endpoints
        .Where(endpoint => !IsDeclared(endpoint))
        .Select(endpoint => endpoint.DisplayName ?? endpoint.ToString() ?? "(unnamed endpoint)")
        .ToList();

    private static bool IsDeclared(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return true;
        }

        return endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data =>
            data.Policy == PermissionPolicyProvider.AuthenticatedOnlyPolicy
            || (data.Policy?.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public void EveryEndpointInTheRealHost_DeclaresItsAuthorization()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        endpoints.Should().NotBeEmpty();
        FindUndeclared(endpoints).Should().BeEmpty(
            "each endpoint needs RequirePermission, AllowAnyAuthenticatedUser or AllowAnonymous (with a reason in review)");
    }

    [Fact]
    public void EveryPermissionAnEndpointRequires_IsInTheCatalogue()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var required = endpoints
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(data => data.Policy)
            .Where(policy => policy?.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal) ?? false)
            .Select(policy => policy![RequirePermissionAttribute.PolicyPrefix.Length..])
            .ToList();

        required.Where(code => !Permissions.IsKnown(code)).Should().BeEmpty();
    }

    [Fact]
    public void Scan_FlagsAnEndpointThatSaysNothingAboutAuthorization()
    {
        var silent = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "silent");
        var anonymous = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new AllowAnonymousAttribute()), "anonymous");
        var permission = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequirePermissionAttribute(Permissions.Core.LegalEntityView)),
            "permission");
        var authenticated = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute(PermissionPolicyProvider.AuthenticatedOnlyPolicy)),
            "authenticated");
        var plainAuthorize = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new AuthorizeAttribute()), "plain-authorize");

        FindUndeclared([silent, anonymous, permission, authenticated, plainAuthorize]).Should().BeEquivalentTo("silent", "plain-authorize");
    }
}
