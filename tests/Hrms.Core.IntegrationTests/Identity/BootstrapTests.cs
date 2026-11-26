using System.Text.Json;
using FluentAssertions;
using Hrms.Api.Bootstrap;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Infrastructure.Security;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Identity;

[Collection(MySqlTestGroup.Name)]
public class BootstrapTests(MySqlFixture mysql) : IAsyncLifetime
{
    private const string Password = "first-admin-password";

    private string _connectionString = string.Empty;
    private TokenServerTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await TokenServerTestHost.CreateDatabaseAsync(mysql);
        _host = await TokenServerTestHost.StartAsync(_connectionString, new Dictionary<string, string?>
        {
            ["Identity:PasswordHashing:IterationCount"] = PasswordHashingOptions.MinimumIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static BootstrapRequest Request(string code = "ACME") => new(code, "Acme Ltd", "Admin@Acme.example", "Acme Admin");

    private static string[] Args(params string[] pairs) => [BootstrapCommand.Name, .. pairs];

    // --- argument parsing ----------------------------------------------------------------------------------------------

    [Fact]
    public void TryParse_WithAllOptions_BuildsTheRequest()
    {
        var ok = BootstrapCommand.TryParse(
            Args("--code", "ACME", "--name", "Acme Ltd", "--admin-email", "a@acme.example", "--admin-name", "Acme Admin"), out var request, out _);

        ok.Should().BeTrue();
        request.Should().Be(new BootstrapRequest("ACME", "Acme Ltd", "a@acme.example", "Acme Admin"));
    }

    [Theory]
    [InlineData(new[] { "--code", "ACME", "--name", "Acme", "--admin-email", "a@acme.example" }, "--admin-name")]
    [InlineData(new[] { "--name", "Acme", "--admin-email", "a@acme.example", "--admin-name", "A" }, "--code")]
    public void TryParse_WithAMissingOption_NamesIt(string[] pairs, string missing)
    {
        BootstrapCommand.TryParse(Args(pairs), out _, out var error).Should().BeFalse();

        error.Should().Contain(missing);
    }

    [Fact]
    public void TryParse_WithMalformedPairs_Fails()
    {
        string[][] malformed = [["--code", "ACME", "--name"], ["code", "ACME"], ["--code", "--name", "Acme"]];

        foreach (var pairs in malformed)
        {
            BootstrapCommand.TryParse(Args(pairs), out _, out _).Should().BeFalse(string.Join(' ', pairs));
        }
    }

    [Fact]
    public void TryParse_WithAnUnknownOption_Fails_SoAPasswordOptionCannotSneakIn()
    {
        BootstrapCommand.TryParse(
            Args("--code", "A1", "--name", "n", "--admin-email", "a@b.co", "--admin-name", "x", "--password", "secret"), out _, out var error)
            .Should().BeFalse();

        error.Should().Contain("--password").And.NotContain("secret");
    }

    [Fact]
    public void IsRequested_OnlyForTheCommandName()
    {
        BootstrapCommand.IsRequested([BootstrapCommand.Name]).Should().BeTrue();
        BootstrapCommand.IsRequested([]).Should().BeFalse();
        BootstrapCommand.IsRequested(["something-else"]).Should().BeFalse();
    }

    // --- the whole path --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bootstrap_CreatesTheTenantRolesClientsAndAdmin_AndTheAdminCanStartEnrolling()
    {
        var result = await TenantBootstrapper.RunAsync(_host.Services, Request(), Password, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        result.Value.TenantCreated.Should().BeTrue();
        result.Value.AdminCreated.Should().BeTrue();

        await using var context = IdentityTestDb.Context(_connectionString, new TenantId(result.Value.TenantId));
        (await context.Roles.AsNoTracking().Select(r => r.Code).ToListAsync()).Should().BeEquivalentTo(DefaultRoles.All.Select(r => r.Code));
        var admin = await context.Users.AsNoTracking().SingleAsync();
        admin.Email.Should().Be("admin@acme.example");
        admin.MustChangePassword.Should().BeTrue();

        // First sign-in: right password, MFA mandatory for tenant_admin, so an enrolment-only session (no roles, no refresh token).
        var (status, body) = await _host.PasswordAsync("ACME", "admin@acme.example", Password);
        status.Should().Be(200, body.ToString());
        body.TryGetProperty("refresh_token", out _).Should().BeFalse();
        using var me = await _host.GetAsync("/me", body.GetProperty("access_token").GetString());
        JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("roles").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Bootstrap_RunTwice_ChangesNothingTheSecondTime()
    {
        var first = await TenantBootstrapper.RunAsync(_host.Services, Request(), Password, CancellationToken.None);

        var second = await TenantBootstrapper.RunAsync(_host.Services, Request(), Password, CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value.TenantId.Should().Be(first.Value.TenantId);
        second.Value.TenantCreated.Should().BeFalse();
        second.Value.AdminCreated.Should().BeFalse();
        await using var context = IdentityTestDb.Context(_connectionString, new TenantId(first.Value.TenantId));
        (await context.Users.AsNoTracking().CountAsync()).Should().Be(1);
        (await context.Roles.AsNoTracking().CountAsync()).Should().Be(DefaultRoles.All.Count);
    }

    [Fact]
    public async Task Bootstrap_WithAWeakPassword_FailsAndALaterRunWithAGoodOneCompletesTheSetup()
    {
        var weak = await TenantBootstrapper.RunAsync(_host.Services, Request("WEAKCO"), "short", CancellationToken.None);

        weak.IsFailure.Should().BeTrue();
        weak.Error.Code.Should().Be("identity.user.password_policy");
        weak.Error.Message.Should().NotContain("short\"");

        var fixedRun = await TenantBootstrapper.RunAsync(_host.Services, Request("WEAKCO"), Password, CancellationToken.None);

        fixedRun.IsSuccess.Should().BeTrue();
        fixedRun.Value.TenantCreated.Should().BeFalse("the tenant row survived the failed run");
        fixedRun.Value.AdminCreated.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("has space")]
    public async Task Bootstrap_WithAnInvalidTenantCode_FailsValidationAndCreatesNothing(string code)
    {
        var result = await TenantBootstrapper.RunAsync(_host.Services, Request(code), Password, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("core.tenant.code_invalid");
    }

    [Fact]
    public async Task Bootstrap_ForOneTenant_LeavesAnotherTenantUntouched()
    {
        var a = await TenantBootstrapper.RunAsync(_host.Services, Request("ALPHA"), Password, CancellationToken.None);
        var b = await TenantBootstrapper.RunAsync(_host.Services, Request("BRAVO"), Password, CancellationToken.None);

        a.Value.TenantId.Should().NotBe(b.Value.TenantId);
        await using var asA = IdentityTestDb.Context(_connectionString, new TenantId(a.Value.TenantId));
        await using var asB = IdentityTestDb.Context(_connectionString, new TenantId(b.Value.TenantId));
        (await asA.Users.AsNoTracking().CountAsync()).Should().Be(1);
        (await asB.Users.AsNoTracking().CountAsync()).Should().Be(1);
        (await asA.Users.AsNoTracking().Select(u => u.Id).SingleAsync()).Should().NotBe(await asB.Users.AsNoTracking().Select(u => u.Id).SingleAsync());
    }

    // --- the command --------------------------------------------------------------------------------------------------------

    private async Task<(int Code, string Output)> RunCommandAsync(string[] args, string? password)
    {
        var output = new StringWriter();
        var code = await BootstrapCommand.RunAsync(_host.Services, args, output, name => name == BootstrapCommand.PasswordVariable ? password : null);
        return (code, output.ToString());
    }

    private static string[] Full(string code = "CMDCO") =>
        Args("--code", code, "--name", "Cmd Co", "--admin-email", "root@cmd.example", "--admin-name", "Root");

    [Fact]
    public async Task Command_Succeeds_ReportsTheOutcome_AndNeverPrintsThePassword()
    {
        var (code, output) = await RunCommandAsync(Full(), Password);

        code.Should().Be(0);
        output.Should().Contain("CMDCO created").And.Contain("root@cmd.example created").And.NotContain(Password);

        var (again, againOutput) = await RunCommandAsync(Full(), Password);
        again.Should().Be(0);
        againOutput.Should().Contain("already existed").And.Contain("nothing changed");
    }

    [Fact]
    public async Task Command_WithoutThePasswordVariable_ExitsWithCode2AndSaysHow()
    {
        var (code, output) = await RunCommandAsync(Full(), password: null);

        code.Should().Be(2);
        output.Should().Contain(BootstrapCommand.PasswordVariable);
    }

    [Fact]
    public async Task Command_WithBadArguments_ExitsWithCode2()
    {
        var (code, output) = await RunCommandAsync(Args("--code", "X1"), Password);

        code.Should().Be(2);
        output.Should().Contain("Missing");
    }

    [Fact]
    public async Task Command_WithAWeakPassword_ExitsWithCode2AndDoesNotEchoIt()
    {
        var (code, output) = await RunCommandAsync(Full("WEAKCMD"), "tiny");

        code.Should().Be(2);
        output.Should().Contain("password.too_short").And.NotContain("tiny");
    }

    [Fact]
    public async Task Bootstrap_AlsoRegistersTheFirstPartyClients()
    {
        await TenantBootstrapper.RunAsync(_host.Services, Request("CLIENTS"), Password, CancellationToken.None);

        await using var scope = _host.Services.CreateAsyncScope();
        var apps = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
        (await apps.FindByClientIdAsync(OAuthClientSeeder.WebClientId)).Should().NotBeNull();
        (await apps.FindByClientIdAsync(OAuthClientSeeder.MobileClientId)).Should().NotBeNull();
    }
}
