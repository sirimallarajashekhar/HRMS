using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Api;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Infrastructure.Security;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using OpenIddict.Validation;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Identity;

[Collection(MySqlTestGroup.Name)]
public class TokenServerConfigurationTests(MySqlFixture mysql) : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";

    private static readonly Dictionary<string, string?> FastHashing = new()
    {
        ["Identity:PasswordHashing:IterationCount"] = PasswordHashingOptions.MinimumIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await TokenServerTestHost.CreateDatabaseAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?>(FastHashing);
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    // --- the OpenIddict advisories and token lifetimes ---------------------------------------------------------------

    [Fact]
    public async Task Validation_EnablesBothAuthorizationAndTokenEntryChecks_SoTheOpenIddictAdvisoryCannotApply()
    {
        await using var host = await TokenServerTestHost.StartAsync(_connectionString, FastHashing);

        var options = host.Services.GetRequiredService<IOptionsMonitor<OpenIddictValidationOptions>>().CurrentValue;

        // GHSA-cxgj-hwwh-855g: authorization-entry validation without token-entry validation skips the database check.
        options.EnableAuthorizationEntryValidation.Should().BeTrue();
        options.EnableTokenEntryValidation.Should().BeTrue();
    }

    [Fact]
    public async Task Server_UsesShortAccessTokens_AndRefreshTokensThatDoNotSlide()
    {
        await using var host = await TokenServerTestHost.StartAsync(_connectionString, FastHashing);

        var options = host.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

        options.AccessTokenLifetime.Should().Be(TimeSpan.FromMinutes(10));
        options.RefreshTokenLifetime.Should().Be(TimeSpan.FromDays(14));
        options.DisableSlidingRefreshTokenExpiration.Should().BeTrue();
        options.DisableRollingRefreshTokens.Should().BeFalse("each refresh must hand out a new token and retire the old one");
    }

    [Fact]
    public async Task Server_AllowsOnlyThePasswordRefreshAndClientCredentialsGrants()
    {
        await using var host = await TokenServerTestHost.StartAsync(_connectionString, FastHashing);

        var options = host.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;

        options.GrantTypes.Should().BeEquivalentTo(["password", "refresh_token", "client_credentials"]);
    }

    [Fact]
    public async Task Clients_AreNeverRegisteredWithKeySets_SoClientAssertionsCannotBeUsed()
    {
        var tenant = await TokenServerTestHost.SeedTenantAsync(_connectionString, "KEYS");
        await using var host = await TokenServerTestHost.StartAsync(_connectionString, FastHashing);
        await host.EnsureClientsAsync();
        await host.RegisterIntegrationAsync("keys-sync", "integration-secret-9", tenant.Id, DefaultRoles.Auditor);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT COUNT(*) FROM oauth_application WHERE json_web_key_set IS NOT NULL", connection);

        // GHSA-925x-4h4v-2792 concerns private_key_jwt client assertions, which need a JSON Web Key Set on the client.
        Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(0);
    }


    // --- certificates outside Development -------------------------------------------------------------------------------

    private static string NewPfx(string password, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=hrms-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        var path = Path.Combine(Path.GetTempPath(), $"hrms-test-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static (string Key, string? Value)[] Production(string? signing, string? encryption, string password = "pfx-password") =>
    [
        ("Tenancy:HeaderResolver:Enabled", "false"),
        ("Identity:Server:SigningCertificate:Path", signing),
        ("Identity:Server:SigningCertificate:Password", password),
        ("Identity:Server:EncryptionCertificate:Path", encryption),
        ("Identity:Server:EncryptionCertificate:Password", password),
    ];

    [Fact]
    public async Task Production_WithoutCertificates_RefusesToStart()
    {
        var act = () => TokenServerTestHost.StartAsync(_connectionString, With(Production(null, null)), "Production");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Path must be set outside Development*");
    }

    [Fact]
    public async Task Production_WithACertificateFileThatDoesNotExist_RefusesToStart()
    {
        var real = NewPfx("pfx-password", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        try
        {
            var act = () => TokenServerTestHost.StartAsync(
                _connectionString, With(Production(real, Path.Combine(Path.GetTempPath(), "missing.pfx"))), "Production");

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*does not exist*");
        }
        finally
        {
            File.Delete(real);
        }
    }

    [Fact]
    public async Task Production_WithAnExpiredCertificate_RefusesToStart()
    {
        var valid = NewPfx("pfx-password", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var expired = NewPfx("pfx-password", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        try
        {
            var act = () => TokenServerTestHost.StartAsync(_connectionString, With(Production(valid, expired)), "Production");

            (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*expired*");
        }
        finally
        {
            File.Delete(valid);
            File.Delete(expired);
        }
    }

    [Fact]
    public async Task Production_WithValidCertificates_StartsAndServesTokens()
    {
        var signing = NewPfx("pfx-password", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var encryption = NewPfx("pfx-password", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        try
        {
            await TokenServerTestHost.SeedTenantAsync(_connectionString, "PRODCO");
            await using var host = await TokenServerTestHost.StartAsync(_connectionString, With(Production(signing, encryption)), "Production");
            await host.EnsureClientsAsync();

            var (status, _) = await host.PasswordAsync("PRODCO", "nobody@example.com", Password);

            status.Should().Be(400, "the server is up and answers with a proper OAuth refusal");
        }
        finally
        {
            File.Delete(signing);
            File.Delete(encryption);
        }
    }

    [Theory]
    [InlineData("Identity:Server:AccessTokenMinutes", "0")]
    [InlineData("Identity:Server:AccessTokenMinutes", "61")]
    [InlineData("Identity:Server:RefreshTokenDays", "91")]
    [InlineData("Identity:Server:TokenRequestsPerMinute", "0")]
    [InlineData("Identity:Server:RefreshReuseLeewaySeconds", "61")]
    public async Task Options_OutsideTheirRange_RefuseToStart(string key, string value)
    {
        var act = () => TokenServerTestHost.StartAsync(_connectionString, With((key, value)));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*must be*");
    }

    // --- rate limiting ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TokenEndpoint_IsRateLimitedPerClientAddress()
    {
        await TokenServerTestHost.SeedTenantAsync(_connectionString, "LIMITED");
        await using var host = await TokenServerTestHost.StartAsync(_connectionString, With(("Identity:Server:TokenRequestsPerMinute", "3")));
        await host.EnsureClientsAsync();

        var statuses = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await host.PasswordAsync("LIMITED", "nobody@example.com", Password)).Status);
        }

        statuses.Take(3).Should().OnlyContain(status => status == 400);
        statuses.Skip(3).Should().OnlyContain(status => status == 429);
    }

    // --- hashing ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SignIn_WithAnOlderHash_UpgradesItWithoutSigningTheUserOut()
    {
        var tenant = await TokenServerTestHost.SeedTenantAsync(_connectionString, "REHASH");
        await using var host = await TokenServerTestHost.StartAsync(
            _connectionString, With(("Identity:PasswordHashing:IterationCount", "150000")));
        await host.EnsureClientsAsync();
        var user = await host.AddUserAsync(tenant.TenantId, "old@example.com", Password, DefaultRoles.Employee, IdentityTestDb.FastHasher());
        var oldHash = user.PasswordHash;
        var oldStamp = user.SecurityStamp;

        var (status, _) = await host.PasswordAsync("REHASH", "old@example.com", Password);

        status.Should().Be(200);
        var after = await host.LoadUserAsync(tenant.TenantId, user.Id);
        after.PasswordHash.Should().NotBe(oldHash);
        after.SecurityStamp.Should().Be(oldStamp, "a rehash must not invalidate sessions");
        new IdentityPasswordHasher(Options.Create(new PasswordHashingOptions { IterationCount = 150_000 }))
            .Verify(after.PasswordHash, Password).Should().Be(PasswordCheck.Success);
    }

    // --- equal work for every refusal -------------------------------------------------------------------------------------

    private sealed class SpyHasher(IPasswordHasher inner) : IPasswordHasher
    {
        public int Verifications { get; private set; }

        public string DummyHash => inner.DummyHash;

        public string Hash(string password) => inner.Hash(password);

        public PasswordCheck Verify(string passwordHash, string password)
        {
            Verifications++;
            return inner.Verify(passwordHash, password);
        }
    }

    private sealed class FixedLookup(Guid? tenant) : ITenantLookup
    {
        public ValueTask<Guid?> FindActiveTenantIdByCodeAsync(string tenantCode, CancellationToken cancellationToken) => ValueTask.FromResult(tenant);
    }

    private async Task<int> VerificationsForAsync(Guid? lookedUpTenant, TenantId dataTenant, string email, string password, Action<TenantContext>? prepare = null)
    {
        await using var context = IdentityTestDb.Context(_connectionString, tenant: null);
        var spy = new SpyHasher(IdentityTestDb.FastHasher());
        var tenantContext = new TenantContext();
        prepare?.Invoke(tenantContext);
        var service = new PasswordSignInService(
            context, new FixedLookup(lookedUpTenant), tenantContext, spy, TimeProvider.System, NullLogger<PasswordSignInService>.Instance);

        await service.SignInAsync("ANY", email, password, CancellationToken.None);
        return spy.Verifications;
    }

    [Fact]
    public async Task SignIn_EveryRefusal_DoesTheSameExpensiveWork_SoTimingRevealsNothing()
    {
        var tenant = await TokenServerTestHost.SeedTenantAsync(_connectionString, "TIMING");
        await using var host = await TokenServerTestHost.StartAsync(_connectionString, FastHashing);
        var real = await host.AddUserAsync(tenant.TenantId, "real@example.com", Password, DefaultRoles.Employee);
        var disabled = await host.AddUserAsync(tenant.TenantId, "off@example.com", Password, DefaultRoles.Employee);
        await host.UpdateUserAsync(tenant.TenantId, disabled.Id, u => u.Disable());

        var wrongPassword = await VerificationsForAsync(tenant.Id, tenant.TenantId, "real@example.com", "wrong-password-entirely");
        var unknownUser = await VerificationsForAsync(tenant.Id, tenant.TenantId, "ghost@example.com", Password);
        var unknownTenant = await VerificationsForAsync(null, tenant.TenantId, "real@example.com", Password);
        var badEmail = await VerificationsForAsync(tenant.Id, tenant.TenantId, "not-an-email", Password);
        var disabledAccount = await VerificationsForAsync(tenant.Id, tenant.TenantId, "off@example.com", Password);

        new[] { wrongPassword, unknownUser, unknownTenant, badEmail, disabledAccount }.Should().OnlyContain(count => count == 1);
        real.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task SignIn_ThatTriesToSwitchTheTenantOfARequestThatHasOne_Throws()
    {
        var tenant = await TokenServerTestHost.SeedTenantAsync(_connectionString, "SWITCH");
        var other = PersistenceTestDb.NewTenant();

        var act = () => VerificationsForAsync(tenant.Id, tenant.TenantId, "x@example.com", Password, tc => tc.Set(other));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot switch the tenant*");
    }

    [Fact]
    public async Task IdentityServerOptions_Validate_AcceptsTheDefaults()
    {
        var act = () => new IdentityServerOptions().Validate();

        await Task.CompletedTask;
        act.Should().NotThrow();
    }

    [Fact]
    public async Task OAuthClientBinding_RejectsAClientWithoutAValidTenant()
    {
        await using var host = await TokenServerTestHost.StartAsync(_connectionString, FastHashing);

        var empty = System.Collections.Immutable.ImmutableDictionary<string, System.Text.Json.JsonElement>.Empty;
        var emptyGuid = empty.Add("tenant_id", System.Text.Json.JsonSerializer.SerializeToElement(Guid.Empty));
        var notAGuid = empty.Add("tenant_id", System.Text.Json.JsonSerializer.SerializeToElement("not-a-guid"));
        var number = empty.Add("tenant_id", System.Text.Json.JsonSerializer.SerializeToElement(42));
        string[] roles = ["auditor", "manager"];
        var valid = empty.Add("tenant_id", System.Text.Json.JsonSerializer.SerializeToElement(Guid.CreateVersion7()))
            .Add("roles", System.Text.Json.JsonSerializer.SerializeToElement(roles));

        OAuthClientBinding.Read(empty).Should().BeNull();
        OAuthClientBinding.Read(emptyGuid).Should().BeNull();
        OAuthClientBinding.Read(notAGuid).Should().BeNull();
        OAuthClientBinding.Read(number).Should().BeNull();
        OAuthClientBinding.Read(valid)!.Value.Roles.Should().Equal("auditor", "manager");
    }
}
