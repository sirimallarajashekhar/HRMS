using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Security;
using Hrms.SharedKernel;

namespace Hrms.Core.IntegrationTests.Identity;

[Collection(MySqlTestGroup.Name)]
public class TokenServerTests(MySqlFixture mysql) : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";

    private static readonly Dictionary<string, string?> FastHashing = new()
    {
        ["Identity:PasswordHashing:IterationCount"] = PasswordHashingOptions.MinimumIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private string _connectionString = string.Empty;
    private TokenServerTestHost _host = null!;
    private Tenant _acme = null!;
    private Tenant _globex = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await TokenServerTestHost.CreateDatabaseAsync(mysql);
        _acme = await TokenServerTestHost.SeedTenantAsync(_connectionString, "ACME");
        _globex = await TokenServerTestHost.SeedTenantAsync(_connectionString, "GLOBEX");
        _host = await TokenServerTestHost.StartAsync(_connectionString, FastHashing);
        await _host.EnsureClientsAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static string Email() => $"user-{Guid.NewGuid():N}@example.com";

    private async Task<(UserAccount User, string Email)> NewUserAsync(string role = DefaultRoles.Manager, Tenant? tenant = null)
    {
        var email = Email();
        var user = await _host.AddUserAsync((tenant ?? _acme).TenantId, email, Password, role);
        return (user, email);
    }

    private static string Str(JsonElement body, string name) => body.GetProperty(name).GetString()!;

    private async Task<JsonElement> SignInAsync(string email, string tenant = "ACME")
    {
        var (status, body) = await _host.PasswordAsync(tenant, email, Password);
        status.Should().Be(200, body.ToString());
        return body;
    }

    // --- success ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PasswordGrant_WithValidCredentials_IssuesAnAccessAndARefreshToken_AndTheApiKnowsWhoCalled()
    {
        var (user, email) = await NewUserAsync(DefaultRoles.Manager);

        var token = await SignInAsync(email);
        token.TryGetProperty("access_token", out _).Should().BeTrue();
        token.TryGetProperty("refresh_token", out _).Should().BeTrue();
        Str(token, "token_type").Should().Be("Bearer");

        using var me = await _host.GetAsync("/me", Str(token, "access_token"));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement;
        Str(body, "sub").Should().Be(user.Id.ToString("D"));
        Str(body, "tenant").Should().Be(_acme.TenantId.ToString());
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(DefaultRoles.Manager);
    }

    [Fact]
    public async Task PasswordGrant_WithoutOfflineAccess_IssuesNoRefreshToken()
    {
        var (_, email) = await NewUserAsync();

        var (status, body) = await _host.PasswordAsync("ACME", email, Password, offline: false);

        status.Should().Be(200);
        body.TryGetProperty("refresh_token", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PasswordGrant_IsCaseInsensitiveForTheTenantCodeAndTheEmail()
    {
        var (_, email) = await NewUserAsync();

        var (status, _) = await _host.PasswordAsync("acme", email.ToUpperInvariant(), Password);

        status.Should().Be(200);
    }

    [Fact]
    public async Task Api_AfterSignIn_EnforcesPermissionsFromTheRoles()
    {
        var (_, manager) = await NewUserAsync(DefaultRoles.Manager);
        var (_, employee) = await NewUserAsync(DefaultRoles.Employee);

        using var allowed = await _host.GetAsync("/legal-entities", Str(await SignInAsync(manager), "access_token"));
        using var denied = await _host.GetAsync("/legal-entities", Str(await SignInAsync(employee), "access_token"));

        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Api_WithoutAToken_Returns401()
    {
        using var response = await _host.GetAsync("/me");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "no token and no tenant: the tenant is required before authorization");

        using var withTenant = await _host.GetAsync("/me", tenantHeader: _acme.Id);
        withTenant.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_WithAGarbageToken_Returns401()
    {
        using var response = await _host.GetAsync("/me", "not-a-real-token", _acme.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- uniform refusals ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task PasswordGrant_EveryKindOfFailure_GetsTheIdenticalAnswer()
    {
        var (user, email) = await NewUserAsync();
        var (disabled, disabledEmail) = await NewUserAsync();
        await _host.UpdateUserAsync(_acme.TenantId, disabled.Id, u => u.Disable());
        var (_, globexEmail) = await NewUserAsync(DefaultRoles.Manager, _globex);
        var suspended = await TokenServerTestHost.SeedTenantAsync(_connectionString, "SUSPENDED", suspended: true);
        var (_, suspendedEmail) = await NewUserAsync(DefaultRoles.Manager, suspended);

        var attempts = new[]
        {
            await _host.PasswordAsync("ACME", email, "wrong-password-entirely"),
            await _host.PasswordAsync("ACME", "nobody@example.com", Password),
            await _host.PasswordAsync("NO-SUCH-TENANT", email, Password),
            await _host.PasswordAsync("ACME", disabledEmail, Password),
            await _host.PasswordAsync("ACME", globexEmail, Password),
            await _host.PasswordAsync("SUSPENDED", suspendedEmail, Password),
            await _host.PasswordAsync("ACME", "not-an-email", Password),
        };

        attempts.Select(a => a.Status).Should().OnlyContain(status => status == 400);
        attempts.Select(a => Str(a.Body, "error")).Should().OnlyContain(error => error == "invalid_grant");
        attempts.Select(a => Str(a.Body, "error_description")).Distinct().Should().ContainSingle();
        user.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task PasswordGrant_ForAUserOfAnotherTenant_IsRefusedLikeAnyOtherFailure()
    {
        var (_, acmeEmail) = await NewUserAsync(DefaultRoles.Manager, _acme);

        var (status, body) = await _host.PasswordAsync("GLOBEX", acmeEmail, Password);

        status.Should().Be(400);
        Str(body, "error").Should().Be("invalid_grant");
    }

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    [InlineData("tenant")]
    public async Task PasswordGrant_WithAMissingField_IsAnInvalidRequest(string missing)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = OAuthClientSeeder.WebClientId,
            ["username"] = "a@example.com",
            ["password"] = Password,
            ["tenant"] = "ACME",
        };
        form.Remove(missing);

        var (status, body) = await _host.PostTokenAsync(form);

        status.Should().Be(400);
        Str(body, "error").Should().BeOneOf("invalid_request", "invalid_grant");
    }

    [Fact]
    public async Task PasswordGrant_WithAnUnknownClient_IsRefused()
    {
        var (_, email) = await NewUserAsync();

        var (status, body) = await _host.PasswordAsync("ACME", email, Password, clientId: "not-registered");

        status.Should().Be(401, "invalid_client is a 401 in OAuth");
        Str(body, "error").Should().Be("invalid_client");
    }

    [Fact]
    public async Task PasswordGrant_ForAnAccountWithAnMfaRoleBeforeMfaExists_IsRefusedUniformly()
    {
        var (_, email) = await NewUserAsync(DefaultRoles.HrAdmin);
        var (_, wrongStatusBody) = await _host.PasswordAsync("ACME", email, "wrong-password-entirely");

        var (status, body) = await _host.PasswordAsync("ACME", email, Password);

        status.Should().Be(400);
        Str(body, "error").Should().Be("invalid_grant");
        Str(body, "error_description").Should().Be(Str(wrongStatusBody, "error_description"));
    }

    [Fact]
    public async Task TokenEndpoint_RejectsAGetRequest()
    {
        using var response = await _host.GetAsync("/connect/token");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.BadRequest);
    }

    // --- lockout ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lockout_AfterTooManyWrongPasswords_RefusesEvenTheRightPassword_UntilTheWindowPasses()
    {
        var (user, email) = await NewUserAsync();
        for (var i = 0; i < 5; i++)
        {
            (await _host.PasswordAsync("ACME", email, "wrong-password-entirely")).Status.Should().Be(400);
        }

        var whileLocked = await _host.PasswordAsync("ACME", email, Password);
        whileLocked.Status.Should().Be(400);
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).LockoutUntil.Should().NotBeNull();

        _host.Clock.Advance(TimeSpan.FromMinutes(16));
        var afterWindow = await _host.PasswordAsync("ACME", email, Password);

        afterWindow.Status.Should().Be(200);
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).LockoutUntil.Should().BeNull();
    }

    [Fact]
    public async Task Lockout_CountsFailuresAndAGoodSignInResetsThem()
    {
        var (user, email) = await NewUserAsync();
        await _host.PasswordAsync("ACME", email, "wrong-password-entirely");
        await _host.PasswordAsync("ACME", email, "wrong-password-entirely");
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).FailedAccessCount.Should().Be(2);

        (await _host.PasswordAsync("ACME", email, Password)).Status.Should().Be(200);

        var after = await _host.LoadUserAsync(_acme.TenantId, user.Id);
        after.FailedAccessCount.Should().Be(0);
        after.LastLoginAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Lockout_ParallelWrongGuesses_AreAllCounted()
    {
        var (user, email) = await NewUserAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _host.PasswordAsync("ACME", email, "wrong-password-entirely")));

        var stored = await _host.LoadUserAsync(_acme.TenantId, user.Id);
        stored.FailedAccessCount.Should().Be(4, "parallel guesses must not slip through uncounted");
    }

    // --- refresh ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_WithAValidToken_IssuesNewTokens_AndTheOldRefreshTokenCannotBeReplayed()
    {
        var (_, email) = await NewUserAsync();
        var first = await SignInAsync(email);

        var (status, second) = await _host.RefreshAsync(Str(first, "refresh_token"));

        status.Should().Be(200, second.ToString());
        Str(second, "refresh_token").Should().NotBe(Str(first, "refresh_token"));
        using var me = await _host.GetAsync("/me", Str(second, "access_token"));
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await _host.RefreshAsync(Str(first, "refresh_token"));
        replay.Status.Should().Be(400);
        Str(replay.Body, "error").Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Refresh_AfterTheAccountIsDisabled_IsRefused()
    {
        var (user, email) = await NewUserAsync();
        var token = await SignInAsync(email);
        await _host.UpdateUserAsync(_acme.TenantId, user.Id, u => u.Disable());

        var (status, body) = await _host.RefreshAsync(Str(token, "refresh_token"));

        status.Should().Be(400);
        Str(body, "error").Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Refresh_AfterThePasswordChanged_IsRefusedBecauseTheSecurityStampMoved()
    {
        var (user, email) = await NewUserAsync();
        var token = await SignInAsync(email);
        await _host.UpdateUserAsync(_acme.TenantId, user.Id, u => u.ChangePassword(IdentityTestDb.FastHasher().Hash("a-new-long-password"), DateTime.UtcNow));

        var (status, body) = await _host.RefreshAsync(Str(token, "refresh_token"));

        status.Should().Be(400);
        Str(body, "error").Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Refresh_PicksUpARoleChange()
    {
        var (user, email) = await NewUserAsync(DefaultRoles.Employee);
        var token = await SignInAsync(email);
        await _host.GrantRoleAsync(_acme.TenantId, user.Id, DefaultRoles.Manager);

        var (status, refreshed) = await _host.RefreshAsync(Str(token, "refresh_token"));

        status.Should().Be(200);
        using var me = await _host.GetAsync("/me", Str(refreshed, "access_token"));
        var roles = JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("roles");
        roles.EnumerateArray().Select(r => r.GetString()).Should().BeEquivalentTo(DefaultRoles.Employee, DefaultRoles.Manager);
    }

    [Fact]
    public async Task Refresh_ForAnAccountThatNowHoldsAnMfaRole_IsRefused()
    {
        var (user, email) = await NewUserAsync(DefaultRoles.Employee);
        var token = await SignInAsync(email);
        await _host.GrantRoleAsync(_acme.TenantId, user.Id, DefaultRoles.PayrollAdmin);

        var (status, _) = await _host.RefreshAsync(Str(token, "refresh_token"));

        status.Should().Be(400);
    }

    [Fact]
    public async Task Refresh_WithAGarbageToken_IsRefused()
    {
        var (status, body) = await _host.RefreshAsync("garbage");

        status.Should().Be(400);
        Str(body, "error").Should().Be("invalid_grant");
    }

    // --- tenant isolation through real tokens -------------------------------------------------------------------------

    [Fact]
    public async Task AccessToken_ForTenantA_CannotBeSwitchedToTenantBWithAHeader()
    {
        await SeedLegalEntitiesAsync();
        var (_, email) = await NewUserAsync(DefaultRoles.Manager, _acme);
        var token = Str(await SignInAsync(email), "access_token");

        using var plain = await _host.GetAsync("/legal-entities", token);
        using var withForeignHeader = await _host.GetAsync("/legal-entities", token, _globex.Id);

        (await plain.Content.ReadAsStringAsync()).Should().Contain("ACME-ONE").And.NotContain("GLOBEX-ONE");
        (await withForeignHeader.Content.ReadAsStringAsync()).Should().Contain("ACME-ONE").And.NotContain("GLOBEX-ONE",
            "an authenticated caller's tenant comes from the token, never from a header");
    }

    [Fact]
    public async Task TwoTenantsSignedInAtOnce_EachSeeOnlyTheirOwnData()
    {
        await SeedLegalEntitiesAsync();
        var (_, acme) = await NewUserAsync(DefaultRoles.Manager, _acme);
        var (_, globex) = await NewUserAsync(DefaultRoles.Manager, _globex);
        var acmeToken = Str(await SignInAsync(acme, "ACME"), "access_token");
        var globexToken = Str(await SignInAsync(globex, "GLOBEX"), "access_token");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            var useAcme = i % 2 == 0;
            using var response = await _host.GetAsync("/legal-entities", useAcme ? acmeToken : globexToken);
            return (useAcme, body: await response.Content.ReadAsStringAsync());
        }));

        foreach (var (useAcme, body) in responses)
        {
            body.Should().Contain(useAcme ? "ACME-ONE" : "GLOBEX-ONE").And.NotContain(useAcme ? "GLOBEX-ONE" : "ACME-ONE");
        }
    }

    private async Task SeedLegalEntitiesAsync()
    {
        foreach (var (tenant, code) in new[] { (_acme, "ACME-ONE"), (_globex, "GLOBEX-ONE") })
        {
            await using var context = Core.CoreTestDb.Context(_connectionString, tenant.TenantId);
            if (!await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(context.LegalEntities, e => e.Code == code))
            {
                context.LegalEntities.Add(Core.CoreTestDb.NewLegalEntity(code));
                await context.SaveChangesAsync();
            }
        }
    }

    // --- client credentials -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClientCredentials_ForAnIntegrationBoundToATenant_IssuesATokenForThatTenantAndRolesOnly()
    {
        await SeedLegalEntitiesAsync();
        await _host.RegisterIntegrationAsync("payroll-sync", "integration-secret-1", _acme.Id, DefaultRoles.Auditor);

        var (status, token) = await _host.PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "payroll-sync",
            ["client_secret"] = "integration-secret-1",
        });

        status.Should().Be(200, token.ToString());
        token.TryGetProperty("refresh_token", out _).Should().BeFalse();
        using var me = await _host.GetAsync("/me", Str(token, "access_token"));
        var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement;
        Str(body, "sub").Should().Be("payroll-sync");
        Str(body, "tenant").Should().Be(_acme.TenantId.ToString());
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(DefaultRoles.Auditor);
        using var data = await _host.GetAsync("/legal-entities", Str(token, "access_token"));
        (await data.Content.ReadAsStringAsync()).Should().Contain("ACME-ONE").And.NotContain("GLOBEX-ONE");
    }

    [Fact]
    public async Task ClientCredentials_WithTheWrongSecret_IsRefused()
    {
        await _host.RegisterIntegrationAsync("hr-sync", "integration-secret-2", _acme.Id, DefaultRoles.Auditor);

        var (status, body) = await _host.PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "hr-sync",
            ["client_secret"] = "not-the-secret",
        });

        status.Should().Be(401);
        Str(body, "error").Should().Be("invalid_client");
    }

    [Fact]
    public async Task ClientCredentials_ForAClientWhoseTenantIsSuspended_IsRefused()
    {
        var gone = await TokenServerTestHost.SeedTenantAsync(_connectionString, "GONE");
        await _host.RegisterIntegrationAsync("gone-sync", "integration-secret-3", gone.Id, DefaultRoles.Auditor);
        await using (var context = Core.CoreTestDb.Context(_connectionString, tenant: null))
        {
            var stored = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(context.Tenants, t => t.Id == gone.Id);
            stored.Suspend();
            await context.SaveChangesAsync();
        }

        var (status, _) = await _host.PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "gone-sync",
            ["client_secret"] = "integration-secret-3",
        });

        status.Should().Be(401, "a client of a suspended tenant fails client validation");
    }

    [Fact]
    public async Task FirstPartyClients_CannotUseTheClientCredentialsGrant()
    {
        var (status, body) = await _host.PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = OAuthClientSeeder.WebClientId,
        });

        status.Should().Be(400);
        Str(body, "error").Should().BeOneOf("invalid_client", "unauthorized_client", "invalid_request");
    }

    [Fact]
    public async Task IntegrationClients_CannotUseThePasswordGrant()
    {
        await _host.RegisterIntegrationAsync("no-password", "integration-secret-4", _acme.Id, DefaultRoles.Auditor);
        var (_, email) = await NewUserAsync();

        var (status, body) = await _host.PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "no-password",
            ["client_secret"] = "integration-secret-4",
            ["username"] = email,
            ["password"] = Password,
            ["tenant"] = "ACME",
        });

        status.Should().Be(400);
        Str(body, "error").Should().BeOneOf("unauthorized_client", "invalid_client");
    }

    [Fact]
    public async Task Integration_RegisteredTwice_ReturnsFalseTheSecondTime()
    {
        (await _host.RegisterIntegrationAsync("dup-sync", "integration-secret-5", _acme.Id, DefaultRoles.Auditor)).Should().BeTrue();
        (await _host.RegisterIntegrationAsync("dup-sync", "integration-secret-5", _acme.Id, DefaultRoles.Auditor)).Should().BeFalse();
    }
}
