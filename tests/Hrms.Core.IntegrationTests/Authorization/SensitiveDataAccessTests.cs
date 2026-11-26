using System.Security.Claims;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Http.Authorization;
using Hrms.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Authorization;

public class SensitiveDataAccessTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static FakePermissionStore Store() => new(new Dictionary<string, string[]>
    {
        ["hr_admin"] = [Permissions.Identity.SensitiveView],
        ["manager"] = [Permissions.Core.LegalEntityView],
    });

    private static HttpSensitiveDataAccess Access(FakePermissionStore store, ClaimsPrincipal? user, Guid? requestTenant = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = user is null ? null : new DefaultHttpContext { User = user } };
        var tenantContext = new TenantContext();
        tenantContext.Set(new TenantId(requestTenant ?? Tenant));
        var services = new ServiceCollection().AddSingleton<IPermissionStore>(store).BuildServiceProvider();
        return new HttpSensitiveDataAccess(accessor, tenantContext, services);
    }

    private static ClaimsPrincipal Caller(Guid? tenant, params string[] roles)
    {
        var claims = roles.Select(role => new Claim(HrmsClaims.Role, role)).ToList();
        if (tenant is { } id)
        {
            claims.Add(new Claim(HrmsClaims.TenantId, id.ToString("D")));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task ARoleWithTheSensitiveViewPermission_MaySeeSensitiveFields() =>
        (await Access(Store(), Caller(Tenant, "hr_admin")).CanViewSensitiveAsync(CancellationToken.None)).Should().BeTrue();

    [Fact]
    public async Task ARoleWithoutIt_MayNot() =>
        (await Access(Store(), Caller(Tenant, "manager")).CanViewSensitiveAsync(CancellationToken.None)).Should().BeFalse();

    [Fact]
    public async Task OneRoleOfSeveralGrantingIt_IsEnough() =>
        (await Access(Store(), Caller(Tenant, "manager", "hr_admin")).CanViewSensitiveAsync(CancellationToken.None)).Should().BeTrue();

    [Fact]
    public async Task AnUnauthenticatedCaller_MayNot()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        (await Access(Store(), anonymous).CanViewSensitiveAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task WithNoHttpRequestAtAll_TheAnswerIsNo() =>
        (await Access(Store(), user: null).CanViewSensitiveAsync(CancellationToken.None)).Should().BeFalse();

    [Fact]
    public async Task ATokenForAnotherTenant_MayNotEvenWithTheRightRole() =>
        (await Access(Store(), Caller(Guid.CreateVersion7(), "hr_admin")).CanViewSensitiveAsync(CancellationToken.None)).Should().BeFalse();

    [Fact]
    public async Task ATokenWithoutATenantClaim_MayNot() =>
        (await Access(Store(), Caller(tenant: null, "hr_admin")).CanViewSensitiveAsync(CancellationToken.None)).Should().BeFalse();

    [Fact]
    public async Task ACallerWithNoRoles_MayNot() =>
        (await Access(Store(), Caller(Tenant)).CanViewSensitiveAsync(CancellationToken.None)).Should().BeFalse();

    [Fact]
    public async Task TheAnswerIsComputedOncePerRequest()
    {
        var store = Store();
        var access = Access(store, Caller(Tenant, "hr_admin"));

        await access.CanViewSensitiveAsync(CancellationToken.None);
        await access.CanViewSensitiveAsync(CancellationToken.None);
        await access.CanViewSensitiveAsync(CancellationToken.None);

        store.Calls.Should().ContainSingle();
    }
}
