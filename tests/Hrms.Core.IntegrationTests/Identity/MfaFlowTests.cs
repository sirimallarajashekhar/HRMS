using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hrms.Core.Domain.Tenants;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Mfa;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Infrastructure.Security;
using MySqlConnector;

namespace Hrms.Core.IntegrationTests.Identity;

[Collection(MySqlTestGroup.Name)]
public class MfaFlowTests(MySqlFixture mysql) : IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string Enrollment = "/api/v1/account/mfa/enrollment";
    private const string Confirm = "/api/v1/account/mfa/enrollment/confirm";
    private const string Disable = "/api/v1/account/mfa/disable";
    private const string ChangePassword = "/api/v1/account/password";

    private string _connectionString = string.Empty;
    private TokenServerTestHost _host = null!;
    private Tenant _acme = null!;
    private Tenant _globex = null!;

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

    private static string Email() => $"user-{Guid.NewGuid():N}@example.com";

    private static string Str(JsonElement body, string name) => body.GetProperty(name).GetString()!;

    private string CodeFor(string base32Secret, int stepOffset = 0) =>
        Totp.Compute(Base32.TryDecode(base32Secret)!, Totp.StepAt(_host.Clock.GetUtcNow()) + stepOffset);

    private async Task<string> SessionAsync(string email, string tenant = "ACME", string? totp = null, string? recovery = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = OAuthClientSeeder.WebClientId,
            ["username"] = email,
            ["password"] = Password,
            ["tenant"] = tenant,
            ["scope"] = "offline_access",
        };
        if (totp is not null)
        {
            form["totp_code"] = totp;
        }

        if (recovery is not null)
        {
            form["recovery_code"] = recovery;
        }

        var (status, body) = await _host.PostTokenAsync(form);
        status.Should().Be(200, body.ToString());
        return Str(body, "access_token");
    }

    private async Task<(int Status, JsonElement Body)> TryPasswordSignInAsync(string email, string? totp = null, string? recovery = null, string tenant = "ACME")
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = OAuthClientSeeder.WebClientId,
            ["username"] = email,
            ["password"] = Password,
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

    /// <summary>Signs in, starts and confirms enrolment, and returns the secret and recovery codes.</summary>
    private async Task<(string Secret, IReadOnlyList<string> Codes)> EnrolAsync(string email, string tenant = "ACME")
    {
        var token = await SessionAsync(email, tenant);
        using var begin = await _host.PostJsonAsync(Enrollment, new { }, token);
        begin.StatusCode.Should().Be(HttpStatusCode.OK);
        var secret = Str(JsonDocument.Parse(await begin.Content.ReadAsStringAsync()).RootElement, "secret");

        using var confirm = await _host.PostJsonAsync(Confirm, new { code = CodeFor(secret) }, token);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());
        var codes = JsonDocument.Parse(await confirm.Content.ReadAsStringAsync()).RootElement.GetProperty("recoveryCodes")
            .EnumerateArray().Select(c => c.GetString()!).ToList();
        return (secret, codes);
    }

    private static async Task<string?> ProblemCode(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();

    // --- the enrolment-only session ------------------------------------------------------------------------------------

    [Fact]
    public async Task MfaRole_WithoutEnrolment_GetsAnEnrolmentOnlySession_NoRolesAndNoRefreshToken()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.HrAdmin);

        var (status, body) = await TryPasswordSignInAsync(email);
        status.Should().Be(200);
        body.TryGetProperty("refresh_token", out _).Should().BeFalse("an enrolment-only session cannot be extended");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = OAuthClientSeeder.WebClientId, ["username"] = email,
            ["password"] = Password, ["tenant"] = "ACME", ["scope"] = "offline_access",
        };
        var withOffline = await _host.PostTokenAsync(form);
        withOffline.Body.TryGetProperty("refresh_token", out _).Should().BeFalse("asking for offline_access does not unlock a refresh token");

        var token = Str(body, "access_token");
        using var me = await _host.GetAsync("/me", token);
        JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("roles").GetArrayLength().Should().Be(0);
        using var data = await _host.GetAsync("/legal-entities", token);
        data.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the session carries no permissions");
    }

    // --- full enrolment and sign-in with a second factor -------------------------------------------------------------------

    [Fact]
    public async Task Enrolment_StoresTheSecretEncrypted_ReturnsEightRecoveryCodes_AndThenSignInNeedsACode()
    {
        var email = Email();
        var user = await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.HrAdmin);

        var (secret, codes) = await EnrolAsync(email);

        codes.Should().HaveCount(8).And.OnlyHaveUniqueItems();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT mfa_secret_protected, mfa_enabled FROM user_account WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", user.Id.ToByteArray(bigEndian: true));
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().StartWith("v1.test.").And.NotContain(secret);
        reader.GetBoolean(1).Should().BeTrue();

        var (status, body) = await TryPasswordSignInAsync(email);
        status.Should().Be(400);
        Str(body, "error").Should().Be("mfa_required");
    }

    [Fact]
    public async Task SignIn_WithTheRightCode_GivesAFullSessionWithRoles_AndTheSameCodeCannotBeReplayed()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.HrAdmin);
        var (secret, _) = await EnrolAsync(email);

        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 2)); // a later step than the one enrolment used
        var code = CodeFor(secret);
        var token = await SessionAsync(email, totp: code);

        using var me = await _host.GetAsync("/me", token);
        JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetString()).Should().Equal(DefaultRoles.HrAdmin);

        var replay = await TryPasswordSignInAsync(email, totp: code);
        replay.Status.Should().Be(400);
        Str(replay.Body, "error").Should().Be("invalid_grant", "a code that was already used is a wrong code");
    }

    [Fact]
    public async Task SignIn_WithAWrongCode_IsRefusedLikeAWrongPassword_AndCountsTowardLockout()
    {
        var email = Email();
        var user = await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.HrAdmin);
        var (secret, _) = await EnrolAsync(email);
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 2));
        var wrongPassword = await _host.PasswordAsync("ACME", email, "wrong-password-entirely");

        var wrong = await TryPasswordSignInAsync(email, totp: CodeFor(secret) == "000000" ? "111111" : "000000");

        wrong.Status.Should().Be(400);
        Str(wrong.Body, "error").Should().Be("invalid_grant");
        Str(wrong.Body, "error_description").Should().Be(Str(wrongPassword.Body, "error_description"));
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).FailedAccessCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SignIn_WithMalformedCodes_IsRefusedWithoutAnError()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.HrAdmin);
        await EnrolAsync(email);

        foreach (var junk in new[] { "abc", "12345", "1234567", "12 34 56", "' OR 1=1 --" })
        {
            var (status, body) = await TryPasswordSignInAsync(email, totp: junk);
            status.Should().Be(400);
            Str(body, "error").Should().Be("invalid_grant");
        }
    }

    [Fact]
    public async Task RecoveryCode_SignsInOnce_AndNeverAgain()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.HrAdmin);
        var (_, codes) = await EnrolAsync(email);

        var first = await TryPasswordSignInAsync(email, recovery: codes[0].ToLowerInvariant().Replace('-', ' '));
        var second = await TryPasswordSignInAsync(email, recovery: codes[0]);
        var other = await TryPasswordSignInAsync(email, recovery: codes[1]);
        var bogus = await TryPasswordSignInAsync(email, recovery: "AAAA-AAAA-AAAA-AAAA");

        first.Status.Should().Be(200, "case and separators do not matter");
        second.Status.Should().Be(400);
        other.Status.Should().Be(200, "each code is independent");
        bogus.Status.Should().Be(400);
    }

    [Fact]
    public async Task Mfa_CanBeOptedIntoByAnAccountWhoseRolesDoNotRequireIt()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        await EnrolAsync(email);

        var (status, body) = await TryPasswordSignInAsync(email);

        status.Should().Be(400);
        Str(body, "error").Should().Be("mfa_required");
    }

    [Fact]
    public async Task Refresh_AfterEnrolment_IsRefused_BecauseTheSessionPredatesMfa()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var (_, signedIn) = await _host.PasswordAsync("ACME", email, Password);
        var refresh = Str(signedIn, "refresh_token");
        var accessToken = Str(signedIn, "access_token");

        using var begin = await _host.PostJsonAsync(Enrollment, new { }, accessToken);
        var secret = Str(JsonDocument.Parse(await begin.Content.ReadAsStringAsync()).RootElement, "secret");
        (await _host.PostJsonAsync(Confirm, new { code = CodeFor(secret) }, accessToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var (status, _) = await _host.RefreshAsync(refresh);

        status.Should().Be(400, "turning MFA on rotates the security stamp, ending older sessions");
    }

    // --- the enrolment endpoints --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AccountEndpoints_WithoutAToken_Return401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Enrollment);
        request.Headers.Add("X-Tenant-Id", _acme.Id.ToString());

        using var response = await _host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccountEndpoints_ForAnIntegrationToken_AreRefusedBecauseItIsNotAUser()
    {
        await _host.RegisterIntegrationAsync("acct-sync", "integration-secret-7", _acme.Id, DefaultRoles.Auditor);
        var (_, token) = await _host.PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "acct-sync", ["client_secret"] = "integration-secret-7",
        });

        using var response = await _host.PostJsonAsync(Enrollment, new { }, Str(token, "access_token"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCode(response)).Should().Be("identity.account.not_a_user");
    }

    [Fact]
    public async Task Enrolment_Responses_AreNotCacheable()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var token = await SessionAsync(email);

        using var response = await _host.PostJsonAsync(Enrollment, new { }, token);

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Confirm_WithoutStartingEnrolment_IsAConflict_AndAWrongOrBlankCodeIsInvalid()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var token = await SessionAsync(email);

        using var noStart = await _host.PostJsonAsync(Confirm, new { code = "123456" }, token);
        noStart.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _host.PostJsonAsync(Enrollment, new { }, token)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var wrong = await _host.PostJsonAsync(Confirm, new { code = "000000" }, token);
        using var blank = await _host.PostJsonAsync(Confirm, new { code = "  " }, token);
        wrong.StatusCode.Should().BeOneOf(HttpStatusCode.UnprocessableEntity, HttpStatusCode.OK);
        blank.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Begin_AfterMfaIsOn_IsAConflict_AndStartingTwiceBeforeConfirmingReplacesTheSecret()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var token = await SessionAsync(email);

        using var first = await _host.PostJsonAsync(Enrollment, new { }, token);
        using var second = await _host.PostJsonAsync(Enrollment, new { }, token);
        var firstSecret = Str(JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement, "secret");
        var secondSecret = Str(JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement, "secret");
        secondSecret.Should().NotBe(firstSecret);

        using var stale = await _host.PostJsonAsync(Confirm, new { code = CodeFor(firstSecret) }, token);
        stale.StatusCode.Should().NotBe(HttpStatusCode.OK, "the first secret was replaced");
        using var current = await _host.PostJsonAsync(Confirm, new { code = CodeFor(secondSecret) }, token);
        current.StatusCode.Should().Be(HttpStatusCode.OK);

        using var again = await _host.PostJsonAsync(Enrollment, new { }, token);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- turning MFA off -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disable_IsRefusedWhenARoleRequiresMfa()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.PayrollAdmin);
        var (secret, _) = await EnrolAsync(email);
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 2));
        var token = await SessionAsync(email, totp: CodeFor(secret));
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 2));

        using var response = await _host.PostJsonAsync(Disable, new { password = Password, code = CodeFor(secret) }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCode(response)).Should().Be("identity.mfa.required_by_role");
    }

    [Fact]
    public async Task Disable_NeedsThePasswordAndACode_AndThenSignInIsPasswordOnlyAgain()
    {
        var email = Email();
        var user = await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var (secret, _) = await EnrolAsync(email);
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 2));
        var token = await SessionAsync(email, totp: CodeFor(secret));
        _host.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 2));

        using var wrongPassword = await _host.PostJsonAsync(Disable, new { password = "not-the-password", code = CodeFor(secret) }, token);
        using var wrongCode = await _host.PostJsonAsync(Disable, new { password = Password, code = "000000" }, token);
        wrongPassword.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        wrongCode.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await _host.LoadUserAsync(_acme.TenantId, user.Id)).FailedAccessCount.Should().Be(2, "guessing here counts toward lockout");

        using var ok = await _host.PostJsonAsync(Disable, new { password = Password, code = CodeFor(secret) }, token);
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await TryPasswordSignInAsync(email)).Status.Should().Be(200);
    }

    [Fact]
    public async Task Disable_WhenMfaIsNotOn_IsAConflict()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var token = await SessionAsync(email);

        using var response = await _host.PostJsonAsync(Disable, new { password = Password, code = "123456" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- changing the password -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangePassword_ValidatesAgainstThePolicy_AndEndsOtherSessions()
    {
        var email = Email();
        await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var (_, signedIn) = await _host.PasswordAsync("ACME", email, Password);
        var token = Str(signedIn, "access_token");
        var refresh = Str(signedIn, "refresh_token");

        using var wrongCurrent = await _host.PostJsonAsync(ChangePassword, new { currentPassword = "nope-nope-nope-nope", newPassword = "a-brand-new-long-password" }, token);
        using var tooShort = await _host.PostJsonAsync(ChangePassword, new { currentPassword = Password, newPassword = "Tiny-pw1" }, token);
        using var same = await _host.PostJsonAsync(ChangePassword, new { currentPassword = Password, newPassword = Password }, token);
        using var missing = await _host.PostJsonAsync(ChangePassword, new { currentPassword = Password }, token);

        wrongCurrent.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        tooShort.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCode(tooShort)).Should().Be("identity.account.password_policy");
        (await tooShort.Content.ReadAsStringAsync()).Should().Contain("password.too_short").And.NotContain("Tiny-pw1", "the submitted password is never echoed back");
        same.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        missing.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        using var changed = await _host.PostJsonAsync(ChangePassword, new { currentPassword = Password, newPassword = "a-brand-new-long-password" }, token);
        changed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _host.PasswordAsync("ACME", email, Password)).Status.Should().Be(400);
        (await _host.PasswordAsync("ACME", email, "a-brand-new-long-password")).Status.Should().Be(200);
        (await _host.RefreshAsync(refresh)).Status.Should().Be(400, "the old refresh token died with the old password");
    }

    // --- tenant isolation of the account endpoints --------------------------------------------------------------------------

    [Fact]
    public async Task TwoTenantsWithTheSameEmail_EnrolIndependently_AndNeitherCanReachTheOther()
    {
        var email = Email();
        var acmeUser = await _host.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var globexUser = await _host.AddUserAsync(_globex.TenantId, email, Password, DefaultRoles.Employee);
        var acmeToken = await SessionAsync(email, "ACME");
        var globexToken = await SessionAsync(email, "GLOBEX");

        using var begin = await _host.PostJsonAsync(Enrollment, new { }, acmeToken);
        var acmeSecret = Str(JsonDocument.Parse(await begin.Content.ReadAsStringAsync()).RootElement, "secret");

        (await _host.LoadUserAsync(_acme.TenantId, acmeUser.Id)).HasPendingMfaEnrollment.Should().BeTrue();
        (await _host.LoadUserAsync(_globex.TenantId, globexUser.Id)).HasPendingMfaEnrollment.Should().BeFalse("another tenant's account is untouched");

        using var globexConfirm = await _host.PostJsonAsync(Confirm, new { code = CodeFor(acmeSecret) }, globexToken);
        globexConfirm.StatusCode.Should().Be(HttpStatusCode.Conflict, "tenant GLOBEX has no pending enrolment to confirm");
        (await _host.LoadUserAsync(_globex.TenantId, globexUser.Id)).MfaEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task AccountEndpoints_AreRateLimitedPerUser()
    {
        await using var limited = await TokenServerTestHost.StartAsync(_connectionString, new Dictionary<string, string?>
        {
            ["Identity:PasswordHashing:IterationCount"] = PasswordHashingOptions.MinimumIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Identity:Server:AccountRequestsPerMinute"] = "3",
        });
        await limited.EnsureClientsAsync();
        var email = Email();
        await limited.AddUserAsync(_acme.TenantId, email, Password, DefaultRoles.Employee);
        var (_, signedIn) = await limited.PasswordAsync("ACME", email, Password);
        var token = Str(signedIn, "access_token");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await limited.PostJsonAsync(Confirm, new { code = "000000" }, token);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(3).Should().NotContain(HttpStatusCode.TooManyRequests);
        statuses.Skip(3).Should().OnlyContain(status => status == HttpStatusCode.TooManyRequests);
    }
}
