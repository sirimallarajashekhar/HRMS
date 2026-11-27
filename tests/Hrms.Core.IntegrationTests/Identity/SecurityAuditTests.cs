using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Mfa;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Security;
using Hrms.Persistence.Auditing;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Identity;

/// <summary>Sign-ins, wrong credentials, lockouts and credential changes in the audit log.</summary>
[Collection(MySqlTestGroup.Name)]
public class SecurityAuditTests(MySqlFixture mysql) : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string NewPassword = "an-entirely-new-passphrase-77";
    private const string Wrong = "wrong-password-entirely";

    private string _connectionString = string.Empty;
    private TokenServerTestHost _host = null!;
    private Tenant _acme = null!;
    private Tenant _globex = null!;

    private static int Limit => LockoutPolicy.Default.MaxFailedAttempts;

    public async Task InitializeAsync()
    {
        _connectionString = await TokenServerTestHost.CreateDatabaseAsync(mysql);
        _acme = await TokenServerTestHost.SeedTenantAsync(_connectionString, "ACME");
        _globex = await TokenServerTestHost.SeedTenantAsync(_connectionString, "GLOBEX");
        _host = await TokenServerTestHost.StartAsync(_connectionString, new Dictionary<string, string?>
        {
            ["Identity:PasswordHashing:IterationCount"] = PasswordHashingOptions.MinimumIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        await _host.EnsureClientsAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<UserAccount> AddUserAsync(string role = DefaultRoles.Employee) =>
        _host.AddUserAsync(_acme.TenantId, $"user-{Guid.NewGuid():N}@example.com", Password, role);

    private async Task<(int Status, JsonElement Body)> SignInAsync(
        string email, string password = Password, string? totp = null, string? recovery = null, string tenant = "ACME")
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = OAuthClientSeeder.WebClientId,
            ["username"] = email,
            ["password"] = password,
            ["tenant"] = tenant,
        };
        if (totp is not null)
        {
            form["totp_code"] = totp;
        }

        if (recovery is not null)
        {
            form["recovery_code"] = recovery;
        }

        return await _host.PostTokenAsync(form);
    }

    private async Task<string> TokenAsync(string email, string? totp = null)
    {
        var (status, body) = await SignInAsync(email, totp: totp);
        status.Should().Be(200, body.ToString());
        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>Everything logged about one account, apart from its creation by the test set-up.</summary>
    private async Task<List<AuditEntry>> LogAsync(Guid userId)
    {
        await using var context = IdentityTestDb.Context(_connectionString, _acme.TenantId);
        return await context.Set<AuditEntry>().AsNoTracking()
            .Where(e => e.RecordId == userId && e.EntityType == "user_account" && e.Action != AuditAction.Created)
            .ToListAsync();
    }

    private static Dictionary<string, string> Fields(AuditEntry entry) =>
        JsonDocument.Parse(entry.Changes).RootElement.EnumerateArray()
            .ToDictionary(c => c.GetProperty("field").GetString()!, c => c.GetProperty("new").ToString());

    [Fact]
    public async Task SignIn_IsRecorded_WithTheAccountAsActor_AndNoChangeReasonOrPassword()
    {
        var user = await AddUserAsync();
        _host.Client.DefaultRequestHeaders.Add("X-Change-Reason", "text nobody has proved anything about");

        await TokenAsync(user.Email);

        var entry = (await LogAsync(user.Id)).Should().ContainSingle().Subject;
        entry.Action.Should().Be(AuditAction.SignedIn);
        entry.RootType.Should().Be("user_account");
        entry.RootId.Should().Be(user.Id);
        entry.ActorType.Should().Be(AuditActorType.User);
        entry.ActorId.Should().Be(user.Id);
        entry.Reason.Should().BeNull();
        Fields(entry).Should().BeEquivalentTo(new Dictionary<string, string> { ["factor"] = "password" });
        entry.Changes.Should().NotContain(Password);
    }

    [Fact]
    public async Task WrongPassword_IsRecordedAsAnonymous_TheLastAlsoAsALockout_AndNothingMoreWhileLocked()
    {
        var user = await AddUserAsync();

        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await SignInAsync(user.Email, Wrong)).Status.Should().Be(400);
        }

        var log = await LogAsync(user.Id);
        log.Where(e => e.Action == AuditAction.CredentialRefused).Should().HaveCount(Limit).And.AllSatisfy(e =>
        {
            e.ActorType.Should().Be(AuditActorType.Anonymous);
            e.ActorId.Should().BeNull();
            Fields(e).Should().BeEquivalentTo(new Dictionary<string, string> { ["during"] = "sign_in", ["factor"] = "password" });
            e.Changes.Should().NotContain(Wrong);
        });
        Fields(log.Should().ContainSingle(e => e.Action == AuditAction.LockedOut).Subject).Should().ContainKey("until");

        await SignInAsync(user.Email, Wrong);
        await SignInAsync(user.Email, Password);

        (await LogAsync(user.Id)).Should().HaveCount(Limit + 1, "attempts on a locked account must not let anyone fill the log");
    }

    [Fact]
    public async Task ParallelWrongGuesses_AreEachRecordedOnce_DespiteTheRetries()
    {
        var user = await AddUserAsync();

        await Task.WhenAll(Enumerable.Range(0, Limit - 1).Select(_ => SignInAsync(user.Email, Wrong)));

        var log = await LogAsync(user.Id);
        log.Should().HaveCount(Limit - 1).And.OnlyContain(e => e.Action == AuditAction.CredentialRefused);
    }

    [Fact]
    public async Task ABurstOfParallelGuesses_RecordsOneLockout_AndNoMoreRefusalsThanWereCounted()
    {
        var user = await AddUserAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, Limit * 3).Select(_ => SignInAsync(user.Email, Wrong)));

        results.Should().OnlyContain(r => r.Status == 400);
        var log = await LogAsync(user.Id);
        log.Count(e => e.Action == AuditAction.CredentialRefused).Should().Be(Limit);
        log.Count(e => e.Action == AuditAction.LockedOut).Should().Be(1);
    }

    [Fact]
    public async Task UnknownTenant_UnknownAccount_AndDisabledAccount_AreNotRecorded()
    {
        var user = await AddUserAsync();
        await _host.UpdateUserAsync(_acme.TenantId, user.Id, u => u.Disable());
        await using var context = IdentityTestDb.Context(_connectionString, _acme.TenantId);
        var before = await context.Set<AuditEntry>().CountAsync();

        await SignInAsync("nobody@example.com", Wrong);
        await SignInAsync(user.Email, Password, tenant: "NOSUCH");
        await SignInAsync(user.Email, Password);
        await SignInAsync(user.Email, Wrong);

        (await context.Set<AuditEntry>().CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task MfaEnrolmentSignInsAndRemoval_AreRecorded_WithoutTheSecretOrAnyCode()
    {
        var user = await AddUserAsync();
        var token = await TokenAsync(user.Email);
        using var begin = await _host.PostJsonAsync("/api/v1/account/mfa/enrollment", new { }, token);
        var secret = JsonDocument.Parse(await begin.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
        string CodeNow() => Totp.Compute(Base32.TryDecode(secret)!, Totp.StepAt(_host.Clock.GetUtcNow()));

        var enrolCode = CodeNow();
        using var confirm = await _host.PostJsonAsync("/api/v1/account/mfa/enrollment/confirm", new { code = enrolCode }, token);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var recovery = JsonDocument.Parse(await confirm.Content.ReadAsStringAsync()).RootElement
            .GetProperty("recoveryCodes")[0].GetString()!;

        var enabled = (await LogAsync(user.Id)).Should().ContainSingle(e => e.Action == AuditAction.Updated).Subject;
        Fields(enabled)["mfaEnabled"].Should().Be("True");
        enabled.ActorId.Should().Be(user.Id);

        (await SignInAsync(user.Email)).Status.Should().Be(400, "a code is required, which is not a wrong attempt");
        await SignInAsync(user.Email, totp: "000000" == CodeNow() ? "111111" : "000000");
        _host.Clock.Advance(TimeSpan.FromSeconds(60));
        var signInCode = CodeNow();
        var mfaToken = await TokenAsync(user.Email, signInCode);
        (await SignInAsync(user.Email, recovery: recovery)).Status.Should().Be(200);

        _host.Clock.Advance(TimeSpan.FromSeconds(60));
        using var disable = await _host.PostJsonAsync(
            "/api/v1/account/mfa/disable", new { password = Password, code = CodeNow() }, mfaToken);
        disable.IsSuccessStatusCode.Should().BeTrue(await disable.Content.ReadAsStringAsync());

        var log = await LogAsync(user.Id);
        log.Where(e => e.Action == AuditAction.SignedIn).Select(e => Fields(e).GetValueOrDefault("secondFactor"))
            .Should().BeEquivalentTo([null, "totp", "recovery_code"]);
        Fields(log.Should().ContainSingle(e => e.Action == AuditAction.CredentialRefused).Subject)["factor"].Should().Be("totp");
        log.Where(e => e.Action == AuditAction.Updated).Select(e => Fields(e)["mfaEnabled"]).Should().BeEquivalentTo("True", "False");
        var everything = string.Join('\n', log.Select(e => e.Changes));
        everything.Should().NotContainAny(secret, recovery, enrolCode, signInCode);
    }

    [Fact]
    public async Task SignInThatMayOnlySetUpMfa_SaysSo()
    {
        var user = await AddUserAsync(DefaultRoles.HrAdmin);

        await TokenAsync(user.Email);

        Fields((await LogAsync(user.Id)).Should().ContainSingle().Subject)["session"].Should().Be("mfa_setup_only");
    }

    [Fact]
    public async Task PasswordChange_AndAWrongCurrentPassword_AreRecorded()
    {
        var user = await AddUserAsync();
        var token = await TokenAsync(user.Email);

        using var refused = await _host.PostJsonAsync(
            "/api/v1/account/password", new { currentPassword = Wrong, newPassword = NewPassword }, token);
        refused.IsSuccessStatusCode.Should().BeFalse();
        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        using var changed = await _host.PostJsonAsync(
            "/api/v1/account/password", new { currentPassword = Password, newPassword = NewPassword }, token);
        changed.IsSuccessStatusCode.Should().BeTrue(await changed.Content.ReadAsStringAsync());

        var log = await LogAsync(user.Id);
        var wrong = log.Should().ContainSingle(e => e.Action == AuditAction.CredentialRefused).Subject;
        wrong.ActorType.Should().Be(AuditActorType.User);
        wrong.ActorId.Should().Be(user.Id);
        Fields(wrong)["during"].Should().Be("account_change");
        Fields(log.Should().ContainSingle(e => e.Action == AuditAction.Updated).Subject).Should().ContainKey("passwordChangedAt");
        string.Join('\n', log.Select(e => e.Changes)).Should().NotContainAny(Password, Wrong, NewPassword);
    }

    [Fact]
    public async Task ParallelWrongCurrentPasswords_OnTheAccountScreen_AreEachCountedAndRecorded_WithoutServerErrors()
    {
        var user = await AddUserAsync();
        var token = await TokenAsync(user.Email);

        var statuses = await Task.WhenAll(Enumerable.Range(0, Limit - 1).Select(async _ =>
        {
            using var response = await _host.PostJsonAsync(
                "/api/v1/account/password", new { currentPassword = Wrong, newPassword = NewPassword }, token);
            return (int)response.StatusCode;
        }));

        statuses.Should().OnlyContain(status => status >= 400 && status < 500);
        (await LogAsync(user.Id)).Count(e => e.Action == AuditAction.CredentialRefused).Should().Be(Limit - 1);
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).FailedAccessCount.Should().Be(Limit - 1);
    }

    [Fact]
    public async Task History_ShowsSecurityEvents_ToItsOwnTenantOnly()
    {
        var user = await AddUserAsync();
        await TokenAsync(user.Email);
        var (reader, _) = await _host.TokenWithScopeAsync(
            _acme.TenantId, "ACME", Password, ScopeType.Tenant, null, null, Permissions.Audit.LogView, Permissions.Identity.UserView);
        var outsiderEmail = $"user-{Guid.NewGuid():N}@example.com";
        var outsiderRole = new[] { Permissions.Audit.LogView, Permissions.Identity.UserView };
        await _host.AddUserWithScopeAsync(_globex.TenantId, outsiderEmail, Password, ScopeType.Tenant, null, outsiderRole);
        var (status, body) = await SignInAsync(outsiderEmail, tenant: "GLOBEX");
        status.Should().Be(200, body.ToString());

        async Task<List<string>> ActionsAsync(string token)
        {
            using var response = await _host.GetAsync($"/api/v1/audit/user_account/{user.Id}", token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items")
                .EnumerateArray().Select(i => i.GetProperty("action").GetString()!).ToList();
        }

        (await ActionsAsync(reader)).Should().Contain("signedIn");
        (await ActionsAsync(body.GetProperty("access_token").GetString()!)).Should().BeEmpty();
    }

    [Fact]
    public async Task RecordSecurityEvent_RefusesChangeActionsAndUntrackedRecords_AndADroppedChangeDropsItsEvent()
    {
        var user = await AddUserAsync();
        await using var context = IdentityTestDb.Context(_connectionString, _acme.TenantId);
        var tracked = await context.Users.SingleAsync(u => u.Id == user.Id);

        var claimAChange = () => context.RecordSecurityEvent(tracked, AuditAction.Deleted, []);
        claimAChange.Should().Throw<ArgumentOutOfRangeException>();
        var untracked = () => context.RecordSecurityEvent(user, AuditAction.SignedIn, []);
        untracked.Should().Throw<InvalidOperationException>();

        context.RecordSecurityEvent(tracked, AuditAction.SignedIn, []);
        context.ChangeTracker.Clear();
        await context.SaveChangesAsync();
        (await LogAsync(user.Id)).Should().BeEmpty();

        var again = await context.Users.SingleAsync(u => u.Id == user.Id);
        context.RecordSecurityEvent(again, AuditAction.SignedIn, []);
        (await context.SaveChangesAsync()).Should().Be(0, "audit rows are not counted as the caller's changes");
        await context.SaveChangesAsync();
        (await LogAsync(user.Id)).Should().ContainSingle("a saved event is not written again");
    }
}
