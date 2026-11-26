using System.Security.Claims;
using System.Text.Encodings.Web;
using Hrms.Contracts;
using Hrms.Http.Authorization;
using Hrms.Http.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Hosting;

namespace Hrms.Core.IntegrationTests.Authorization;

/// <summary>Builds a principal from X-Test-* headers so authorization can be tested without the token server.</summary>
public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string TenantHeader = "X-Test-Tenant";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(HrmsClaims.Subject, user.ToString()) };
        if (Request.Headers.TryGetValue(TenantHeader, out var tenant))
        {
            claims.Add(new Claim(HrmsClaims.TenantId, tenant.ToString()));
        }

        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(role => new Claim(HrmsClaims.Role, role)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class FakePermissionStore(Dictionary<string, string[]> permissionsByRole) : IPermissionStore
{
    public List<(Guid Tenant, string[] Roles)> Calls { get; } = [];

    public ValueTask<IReadOnlySet<string>> GetPermissionsAsync(
        Guid tenantId, IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((tenantId, [.. roleCodes]));
        }

        IReadOnlySet<string> granted = roleCodes
            .SelectMany(role => permissionsByRole.GetValueOrDefault(role, []))
            .ToHashSet(StringComparer.Ordinal);
        return ValueTask.FromResult(granted);
    }
}

public sealed class ThrowingPermissionStore : IPermissionStore
{
    public ValueTask<IReadOnlySet<string>> GetPermissionsAsync(
        Guid tenantId, IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The permission store is down.");
}

public sealed class AlwaysActiveDirectory : ITenantDirectory
{
    public ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

public static class AuthorizationTestHost
{
    public static async Task<IHost> StartAsync(IPermissionStore store)
    {
        var host = new HostBuilder()
            .UseEnvironment("Development")
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tenancy:HeaderResolver:Enabled"] = "true",
                }))
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddProblemDetails();
                    services.AddSingleton<IDiagnosticContext>(new DiagnosticContext(Log.Logger));
                    services.AddHrmsTenancy();
                    services.AddScoped<ITenantDirectory, AlwaysActiveDirectory>();
                    // Header-only on purpose: these tests exercise the handler own tenant check, which the claim-first resolver would hide.
                    services.AddSingleton<ITenantResolver, HeaderTenantResolver>();
                    services.AddAuthentication(TestAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
                    services.AddHrmsAuthorization();
                    services.AddScoped(_ => store);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseHrmsTenancy();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/view", () => "view").RequirePermission(Permissions.Core.LegalEntityView);
                        endpoints.MapGet("/manage", () => "manage").RequirePermission(Permissions.Core.LegalEntityManage);
                        endpoints.MapGet("/authed", () => "authed").AllowAnyAuthenticatedUser();
                        endpoints.MapGet("/anon", () => "anon").AllowAnonymous().AllowWithoutTenant();
                        endpoints.MapGet("/forgotten", () => "forgotten"); // no authorization metadata: the fallback policy must protect it
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    public static HttpRequestMessage Get(
        string path,
        Guid? requestTenant = null,
        Guid? user = null,
        Guid? tokenTenant = null,
        string? roles = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (requestTenant is { } rt)
        {
            request.Headers.Add(HeaderTenantResolver.HeaderName, rt.ToString());
        }

        if (user is { } u)
        {
            request.Headers.Add(TestAuthenticationHandler.UserHeader, u.ToString());
        }

        if (tokenTenant is { } tt)
        {
            request.Headers.Add(TestAuthenticationHandler.TenantHeader, tt.ToString());
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthenticationHandler.RolesHeader, roles);
        }

        return request;
    }
}
