using System.Net.Http.Headers;
using System.Text.Json;
using Hrms.Contracts;
using Hrms.Core.Infrastructure;
using Hrms.Core.IntegrationTests.Core;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Http.Authorization;
using Hrms.Http.Tenancy;
using Hrms.Identity.Api;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure;
using Hrms.Identity.Infrastructure.Persistence;
using Hrms.Identity.Infrastructure.Security;
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
using MySqlConnector;
using Serilog;
using Serilog.Extensions.Hosting;

namespace Hrms.Core.IntegrationTests.Identity;

/// <summary>
/// The real chain with nothing faked: the OpenIddict token server and validation, the sign-in service, the tenant resolver, the tenant
/// directory, permission authorization and MySQL built from the real migrations of both modules. Only the endpoints /me and
/// /legal-entities exist for the tests to call.
/// </summary>
public sealed class TokenServerTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private TokenServerTestHost(IHost host, string connectionString, MovableClock clock)
    {
        _host = host;
        ConnectionString = connectionString;
        Clock = clock;
        Client = host.GetTestClient();
    }

    public string ConnectionString { get; }

    public MovableClock Clock { get; }

    public IServiceProvider Services => _host.Services;

    public HttpClient Client { get; }

    public static async Task<string> CreateDatabaseAsync(MySqlFixture mysql)
    {
        var database = $"auth_{Guid.NewGuid():N}";
        var admin = new MySqlConnectionStringBuilder(mysql.ConnectionString) { UserID = "root", Database = string.Empty };
        await using (var connection = new MySqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new MySqlCommand($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4", connection);
            await create.ExecuteNonQueryAsync();
        }

        admin.Database = database;
        await using (var core = CoreTestDb.Context(admin.ConnectionString, tenant: null))
        {
            await core.Database.MigrateAsync();
        }

        await using (var identity = IdentityTestDb.Context(admin.ConnectionString, tenant: null))
        {
            await identity.Database.MigrateAsync();
        }

        return admin.ConnectionString;
    }

    public static async Task<TokenServerTestHost> StartAsync(
        string connectionString,
        IDictionary<string, string?>? settings = null,
        string environment = "Development")
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var configuration = new Dictionary<string, string?> { ["Tenancy:HeaderResolver:Enabled"] = "true" };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        var host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(config => config.AddInMemoryCollection(configuration))
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.AddProblemDetails();
                    services.AddSingleton<TimeProvider>(clock);
                    services.AddSingleton<IDiagnosticContext>(new DiagnosticContext(Log.Logger));
                    services.AddHrmsTenancy();
                    services.AddHrmsAuthorization();
                    services.AddHrmsPersistence(connectionString);
                    services.AddCoreInfrastructure(connectionString);
                    services.AddIdentityInfrastructure(connectionString);
                    services.AddIdentityServer(context.Configuration, context.HostingEnvironment);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseAuthentication();
                    app.UseHrmsTenancy();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapTokenEndpoint();
                        endpoints.MapGet("/me", (HttpContext http, ITenantContext tenant) => Results.Ok(new
                        {
                            sub = http.User.FindFirst(HrmsClaims.Subject)?.Value,
                            tenant = tenant.TenantId?.ToString(),
                            roles = http.User.FindAll(HrmsClaims.Role).Select(c => c.Value).Order().ToArray(),
                        })).AllowAnyAuthenticatedUser();
                        endpoints.MapGet("/legal-entities", async (Hrms.Core.Infrastructure.Persistence.CoreDbContext db, CancellationToken ct) =>
                            Results.Ok(await db.LegalEntities.AsNoTracking().OrderBy(e => e.Code).Select(e => e.Code).ToListAsync(ct)))
                            .RequirePermission(Permissions.Core.LegalEntityView);
                    });
                }))
            .Build();

        await host.StartAsync();
        return new TokenServerTestHost(host, connectionString, clock);
    }

    public async Task<(int Status, JsonElement Body)> PostTokenAsync(Dictionary<string, string> form)
    {
        using var response = await Client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public Task<(int Status, JsonElement Body)> PasswordAsync(
        string tenantCode, string email, string password, string clientId = OAuthClientSeeder.WebClientId, bool offline = true) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = email,
            ["password"] = password,
            ["tenant"] = tenantCode,
            ["scope"] = offline ? "offline_access" : string.Empty,
        });

    public Task<(int Status, JsonElement Body)> RefreshAsync(string refreshToken, string clientId = OAuthClientSeeder.WebClientId) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        });

    public async Task<HttpResponseMessage> GetAsync(string path, string? bearer = null, Guid? tenantHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (tenantHeader is { } tenant)
        {
            request.Headers.Add(HeaderTenantResolver.HeaderName, tenant.ToString());
        }

        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // --- seeding ----------------------------------------------------------------------------------------------------

    /// <summary>A tenant with the default roles and the two first-party OAuth clients.</summary>
    public static async Task<Hrms.Core.Domain.Tenants.Tenant> SeedTenantAsync(string connectionString, string code, bool suspended = false)
    {
        var tenant = await CoreTestDb.SeedTenantAsync(connectionString, code, suspended);
        await IdentityTestDb.SeedTenantAsync(connectionString, tenant.TenantId);
        return tenant;
    }

    public async Task EnsureClientsAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OAuthClientSeeder>().EnsureFirstPartyClientsAsync(CancellationToken.None);
    }

    public async Task<bool> RegisterIntegrationAsync(string clientId, string secret, Guid tenantId, params string[] roles)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OAuthClientSeeder>()
            .RegisterIntegrationAsync(clientId, secret, tenantId, roles, CancellationToken.None);
    }

    /// <summary>Adds an account holding one default role with the Self scope (or tenant-wide for administrators).</summary>
    public async Task<UserAccount> AddUserAsync(TenantId tenant, string email, string password, string roleCode, IPasswordHasher? hasher = null)
    {
        var user = UserAccount.Create(email, "Test User", (hasher ?? IdentityTestDb.FastHasher()).Hash(password), DateTime.UtcNow).Value;
        await using var context = IdentityTestDb.Context(ConnectionString, tenant);
        var role = await context.Roles.AsNoTracking().SingleAsync(r => r.Code == roleCode);
        context.Users.Add(user);
        context.UserRoleScopes.Add(UserRoleScope.Create(user.Id, role.Id, ScopeType.Self, null).Value);
        await context.SaveChangesAsync();
        return user;
    }

    public async Task<UserAccount> LoadUserAsync(TenantId tenant, Guid userId)
    {
        await using var context = IdentityTestDb.Context(ConnectionString, tenant);
        return await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    public async Task UpdateUserAsync(TenantId tenant, Guid userId, Action<UserAccount> change)
    {
        await using var context = IdentityTestDb.Context(ConnectionString, tenant);
        var user = await context.Users.SingleAsync(u => u.Id == userId);
        change(user);
        await context.SaveChangesAsync();
    }

    public async Task GrantRoleAsync(TenantId tenant, Guid userId, string roleCode)
    {
        await using var context = IdentityTestDb.Context(ConnectionString, tenant);
        var role = await context.Roles.AsNoTracking().SingleAsync(r => r.Code == roleCode);
        context.UserRoleScopes.Add(UserRoleScope.Create(userId, role.Id, ScopeType.Self, null).Value);
        await context.SaveChangesAsync();
    }
}
