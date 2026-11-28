using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Application;
using Hrms.Identity.Domain.Mfa;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Identity;

/// <summary>A locked account cannot change its password or turn MFA off, and its answers do not tell a right guess from a wrong one.</summary>
[Collection(MySqlTestGroup.Name)]
public class AccountLockoutTests(MySqlFixture mysql) : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string NewPassword = "an-entirely-new-passphrase-77";
    private const string Wrong = "wrong-password-entirely";
    private const string Enrollment = "/api/v1/account/mfa/enrollment";
    private const string Confirm = "/api/v1/account/mfa/enrollment/confirm";
    private const string Disable = "/api/v1/account/mfa/disable";
    private const string ChangePassword = "/api/v1/account/password";

    private readonly InterruptingPasswordHasher _hasher = new();
    private string _connectionString = string.Empty;
    private TokenServerTestHost _host = null!;
    private Tenant _acme = null!;

    private static int Limit => LockoutPolicy.Default.MaxFailedAttempts;

    public async Task InitializeAsync()
    {
        _connectionString = await TokenServerTestHost.CreateDatabaseAsync(mysql);
        _acme = await TokenServerTestHost.SeedTenantAsync(_connectionString, "ACME");
        _host = await TokenServerTestHost.StartAsync(
            _connectionString,
            new Dictionary<string, string?>
            {
                ["Identity:PasswordHashing:IterationCount"] = PasswordHashingOptions.MinimumIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            configureServices: services => services.AddSingleton<IPasswordHasher>(_hasher));
        await _host.EnsureClientsAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(UserAccount User, string Token)> SignedInUserAsync()
    {
        var user = await _host.AddUserAsync(_acme.TenantId, $"user-{Guid.NewGuid():N}@example.com", Password, DefaultRoles.Employee);
        var (status, body) = await _host.PasswordAsync("ACME", user.Email, Password);
        status.Should().Be(200, body.ToString());
        return (user, body.GetProperty("access_token").GetString()!);
    }

    private async Task LockBySignInAsync(string email)
    {
        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await _host.PasswordAsync("ACME", email, Wrong)).Status.Should().Be(400);
        }
    }

    private async Task<(HttpStatusCode Status, string? Code)> ChangeAsync(string token, string currentPassword)
    {
        using var response = await _host.PostJsonAsync(ChangePassword, new { currentPassword, newPassword = NewPassword }, token);
        return (response.StatusCode, await CodeAsync(response));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.GetProperty("code").GetString();
    }

    [Theory]
    [InlineData(Password)]
    [InlineData(Wrong)]
    public async Task ChangePassword_WhileLocked_IsRefusedAlikeForARightAndAWrongPassword_AndChangesNothing(string currentPassword)
    {
        var (user, token) = await SignedInUserAsync();
        await LockBySignInAsync(user.Email);
        var locked = await _host.LoadUserAsync(_acme.TenantId, user.Id);

        var (status, code) = await ChangeAsync(token, currentPassword);

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("identity.account.locked");
        var after = await _host.LoadUserAsync(_acme.TenantId, user.Id);
        after.PasswordHash.Should().Be(locked.PasswordHash);
        after.LockoutUntil.Should().Be(locked.LockoutUntil, "an attempt during the lock neither lifts nor extends it");
        after.FailedAccessCount.Should().Be(0);
    }

    [Fact]
    public async Task ChangePassword_AfterGuessesOnTheAccountEndpointLockIt_IsRefused_UntilTheLockExpires()
    {
        var (user, token) = await SignedInUserAsync();
        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await ChangeAsync(token, Wrong)).Code.Should().Be("identity.account.wrong_password", "each of these was counted");
        }

        (await ChangeAsync(token, Password)).Code.Should().Be("identity.account.locked");

        // The lock is moved into the past, not the clock forward: the access token lives for less time than the lock lasts.
        ChangeRow(user.Id, _host.Clock.GetUtcNow().UtcDateTime.AddSeconds(-1));
        (await ChangeAsync(token, Password)).Status.Should().Be(HttpStatusCode.NoContent);
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).LockoutUntil.Should().BeNull();
    }

    [Fact]
    public async Task DisableMfa_WhileLocked_IsRefused_EvenWithTheRightPasswordAndCode_AndMfaStaysOn()
    {
        var (user, token) = await SignedInUserAsync();
        using var begin = await _host.PostJsonAsync(Enrollment, new { }, token);
        var secret = JsonDocument.Parse(await begin.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
        string CodeNow() => Totp.Compute(Base32.TryDecode(secret)!, Totp.StepAt(_host.Clock.GetUtcNow()));
        using var confirm = await _host.PostJsonAsync(Confirm, new { code = CodeNow() }, token);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        await LockBySignInAsync(user.Email);
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 2));

        using var response = await _host.PostJsonAsync(Disable, new { password = Password, code = CodeNow() }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeAsync(response)).Should().Be("identity.account.locked");
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).MfaEnabled.Should().BeTrue();
    }

    /// <summary>The lock lands after the request has read the account and while it is checking the password.</summary>
    [Theory]
    [InlineData(Password)]
    [InlineData(Wrong)]
    public async Task ChangePassword_WhenAParallelRequestLocksTheAccountMidway_AnswersLockedForRightAndWrongAlike(string currentPassword)
    {
        var (user, token) = await SignedInUserAsync();
        var until = _host.Clock.GetUtcNow().UtcDateTime.AddMinutes(15);
        _hasher.DuringNextCheck(() => ChangeRow(user.Id, until));

        var (status, code) = await ChangeAsync(token, currentPassword);

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("identity.account.locked");
        var after = await _host.LoadUserAsync(_acme.TenantId, user.Id);
        after.PasswordHash.Should().Be(user.PasswordHash);
        after.LockoutUntil.Should().NotBeNull("the change must not lift the lock it lost the race to");
    }

    [Fact]
    public async Task ChangePassword_WhenTheAccountChangesMidwayWithoutALock_IsARetryableConflict()
    {
        var (user, token) = await SignedInUserAsync();
        _hasher.DuringNextCheck(() => ChangeRow(user.Id, lockoutUntil: null));

        var (status, code) = await ChangeAsync(token, Password);

        status.Should().Be(HttpStatusCode.Conflict);
        code.Should().Be("identity.account.concurrent_change");
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).PasswordHash.Should().Be(user.PasswordHash);
        (await ChangeAsync(token, Password)).Status.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>Stands in for another request saving the account: sets the lock (or none) and bumps the row version.</summary>
    private void ChangeRow(Guid userId, DateTime? lockoutUntil)
    {
        using var connection = new MySqlConnection(_connectionString);
        connection.Open();
        using var command = new MySqlCommand(
            "UPDATE user_account SET lockout_until = @until, row_version = row_version + 1 WHERE id = @id", connection);
        command.Parameters.AddWithValue("@until", lockoutUntil);
        command.Parameters.AddWithValue("@id", userId.ToByteArray(bigEndian: true));
        command.ExecuteNonQuery().Should().Be(1);
    }

    /// <summary>The real hasher, able to run one action in the middle of the next password check.</summary>
    private sealed class InterruptingPasswordHasher : IPasswordHasher
    {
        private readonly IPasswordHasher _inner = IdentityTestDb.FastHasher();
        private Action? _next;

        public string DummyHash => _inner.DummyHash;

        public void DuringNextCheck(Action action) => _next = action;

        public string Hash(string password) => _inner.Hash(password);

        public PasswordCheck Verify(string passwordHash, string password)
        {
            Interlocked.Exchange(ref _next, null)?.Invoke();
            return _inner.Verify(passwordHash, password);
        }
    }
}
