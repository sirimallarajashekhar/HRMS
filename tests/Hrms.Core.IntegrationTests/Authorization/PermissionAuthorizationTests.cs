using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Http.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using static Hrms.Core.IntegrationTests.Authorization.AuthorizationTestHost;

namespace Hrms.Core.IntegrationTests.Authorization;

public class PermissionAuthorizationTests
{
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();

    private static FakePermissionStore Store() => new(new Dictionary<string, string[]>
    {
        ["hr_admin"] = [Permissions.Core.LegalEntityView, Permissions.Core.LegalEntityManage],
        ["viewer"] = [Permissions.Core.LegalEntityView],
        ["nobody"] = [],
    });

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    [Fact]
    public async Task Anonymous_EndpointMarkedAllowAnonymous_WorksWithNoTokenAndNoTenant()
    {
        using var host = await StartAsync(Store());

        var response = await host.GetTestClient().SendAsync(Get("/anon"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Protected_WithoutAToken_Returns401WithAStableCode()
    {
        using var host = await StartAsync(Store());

        var response = await host.GetTestClient().SendAsync(Get("/view", requestTenant: TenantA));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CodeOf(response)).Should().Be(ProblemAuthorizationResultHandler.UnauthenticatedCode);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer");
    }

    [Fact]
    public async Task Protected_WithARoleThatGrantsThePermission_Returns200()
    {
        using var host = await StartAsync(Store());

        var response = await host.GetTestClient().SendAsync(Get("/view", TenantA, User, TenantA, "viewer"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Protected_WithARoleThatLacksThePermission_Returns403()
    {
        using var host = await StartAsync(Store());

        var response = await host.GetTestClient().SendAsync(Get("/manage", TenantA, User, TenantA, "viewer"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(response)).Should().Be(ProblemAuthorizationResultHandler.ForbiddenCode);
    }

    [Fact]
    public async Task Protected_WithOneRoleOfSeveralGrantingThePermission_Returns200()
    {
        using var host = await StartAsync(Store());

        var response = await host.GetTestClient().SendAsync(Get("/manage", TenantA, User, TenantA, "nobody,hr_admin"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Protected_WithNoRoles_Returns403()
    {
        using var host = await StartAsync(Store());

        var response = await host.GetTestClient().SendAsync(Get("/view", TenantA, User, TenantA));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Protected_WithATokenForAnotherTenant_Returns403EvenWithTheRightRole()
    {
        using var host = await StartAsync(Store());

        // The token says tenant A, the request is for tenant B: a replayed token must never work across tenants.
        var response = await host.GetTestClient().SendAsync(Get("/view", requestTenant: TenantB, user: User, tokenTenant: TenantA, roles: "hr_admin"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Protected_WithATokenThatHasNoTenantClaim_Returns403()
    {
        using var host = await StartAsync(Store());

        var response = await host.GetTestClient().SendAsync(Get("/view", requestTenant: TenantA, user: User, tokenTenant: null, roles: "hr_admin"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Protected_WithAMalformedTenantClaim_Returns403()
    {
        using var host = await StartAsync(Store());
        var request = Get("/view", TenantA, User, roles: "hr_admin");
        request.Headers.Add(TestAuthenticationHandler.TenantHeader, "not-a-guid");

        var response = await host.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AuthenticatedOnly_NeedsNoRoleButNeedsTheRightTenantAndAToken()
    {
        using var host = await StartAsync(Store());
        var client = host.GetTestClient();

        (await client.SendAsync(Get("/authed", TenantA, User, TenantA))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get("/authed", requestTenant: TenantA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Get("/authed", requestTenant: TenantB, user: User, tokenTenant: TenantA))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EndpointWithNoAuthorizationMetadata_IsProtectedByTheFallbackPolicy()
    {
        using var host = await StartAsync(Store());
        var client = host.GetTestClient();

        (await client.SendAsync(Get("/forgotten", requestTenant: TenantA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Get("/forgotten", TenantA, User, TenantA))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Store_IsAskedForTheTokenTenantAndDistinctRoles()
    {
        var store = Store();
        using var host = await StartAsync(store);

        await host.GetTestClient().SendAsync(Get("/view", TenantA, User, TenantA, "viewer,viewer,nobody"));

        var call = store.Calls.Should().ContainSingle().Subject;
        call.Tenant.Should().Be(TenantA);
        call.Roles.Should().BeEquivalentTo("viewer", "nobody");
    }

    [Fact]
    public async Task Store_WhenItFails_TheRequestFailsClosed()
    {
        using var host = await StartAsync(new ThrowingPermissionStore());

        var act = () => host.GetTestClient().SendAsync(Get("/view", TenantA, User, TenantA, "hr_admin"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*permission store is down*");
    }

    [Fact]
    public async Task DefaultStore_WhenNoIdentityModuleIsRegistered_DeniesEveryPermission()
    {
        using var host = await StartAsync(new DenyAllPermissionStore());

        var response = await host.GetTestClient().SendAsync(Get("/view", TenantA, User, TenantA, "hr_admin"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PolicyProvider_ForACodeNotInTheCatalogue_FailsInsteadOfDenyingOrAllowing()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var act = () => provider.GetPolicyAsync("perm:core.legal_entity.vieww");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not in the permission catalogue*");
    }

    [Fact]
    public async Task PolicyProvider_BuildsEachPolicyOnce()
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var first = await provider.GetPolicyAsync("perm:" + Permissions.Core.LegalEntityView);
        var second = await provider.GetPolicyAsync("perm:" + Permissions.Core.LegalEntityView);

        first.Should().BeSameAs(second);
        first!.Requirements.OfType<PermissionRequirement>().Single().Permission.Should().Be(Permissions.Core.LegalEntityView);
    }
}
