using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Security;
using Hrms.Identity.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Identity;

[Collection(MySqlTestGroup.Name)]
public class IdentitySeedingTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await IdentityTestDb.CreateMigratedAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

    private static Hrms.SharedKernel.TenantId NewTenant() => PersistenceTestDb.NewTenant();

    [Fact]
    public async Task SyncPermissionCatalogue_AddsEveryCataloguedCodeOnceAndIsIdempotent()
    {
        var tenant = NewTenant();
        await using var context = IdentityTestDb.Context(_connectionString, tenant);
        var seeder = IdentityTestDb.Seeder(context, tenant);

        var first = await seeder.SyncPermissionCatalogueAsync(CancellationToken.None);
        var second = await seeder.SyncPermissionCatalogueAsync(CancellationToken.None);

        first.Should().Be(Permissions.All.Count);
        second.Should().Be(0);
        (await context.Permissions.AsNoTracking().Select(p => p.Code).ToListAsync()).Should().BeEquivalentTo(Permissions.All);
    }

    [Fact]
    public async Task SeedDefaultRoles_CreatesTheSixRolesWithTheirGrantsAndMfaFlags()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);

        await using var context = IdentityTestDb.Context(_connectionString, tenant);
        var roles = await context.Roles.AsNoTracking().ToListAsync();
        var grants = await context.RoleGrants.AsNoTracking().ToListAsync();

        roles.Select(r => r.Code).Should().BeEquivalentTo(DefaultRoles.All.Select(r => r.Code));
        roles.Should().OnlyContain(r => r.IsSystem);
        foreach (var definition in DefaultRoles.All)
        {
            var role = roles.Single(r => r.Code == definition.Code);
            role.RequiresMfa.Should().Be(definition.RequiresMfa, definition.Code);
            grants.Where(g => g.RoleId == role.Id).Select(g => g.PermissionCode)
                .Should().BeEquivalentTo(definition.PermissionCodes, definition.Code);
        }
    }

    [Fact]
    public async Task SeedDefaultRoles_RunTwice_CreatesNothingTheSecondTimeAndNeverRemovesAGrant()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);

        await using (var context = IdentityTestDb.Context(_connectionString, tenant))
        {
            var manager = await context.Roles.SingleAsync(r => r.Code == DefaultRoles.Manager);
            context.RoleGrants.Add(RoleGrant.Create(manager.Id, Permissions.Identity.UserView).Value);
            await context.SaveChangesAsync();
        }

        await using var again = IdentityTestDb.Context(_connectionString, tenant);
        var created = await IdentityTestDb.Seeder(again, tenant).SeedDefaultRolesAsync(CancellationToken.None);

        created.Should().Be(0);
        var managerGrants = await again.RoleGrants.AsNoTracking()
            .Join(again.Roles.AsNoTracking(), g => g.RoleId, r => r.Id, (g, r) => new { r.Code, g.PermissionCode })
            .Where(x => x.Code == DefaultRoles.Manager).Select(x => x.PermissionCode).ToListAsync();
        managerGrants.Should().Contain([Permissions.Core.LegalEntityView, Permissions.Identity.UserView], "a re-run must keep a grant added by hand");
    }

    [Fact]
    public async Task SeedDefaultRoles_ForOneTenant_DoesNotTouchAnotherTenant()
    {
        var a = NewTenant();
        var b = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, a);

        await using var asB = IdentityTestDb.Context(_connectionString, b);
        (await asB.Roles.AsNoTracking().CountAsync()).Should().Be(0);
        (await asB.RoleGrants.AsNoTracking().CountAsync()).Should().Be(0);

        await using var asA = IdentityTestDb.Context(_connectionString, a);
        (await asA.Roles.AsNoTracking().CountAsync()).Should().Be(DefaultRoles.All.Count);
    }

    [Fact]
    public async Task CreateTenantAdmin_MakesAnAccountThatMustChangeItsPasswordHoldingTenantAdminTenantWide()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        await using var context = IdentityTestDb.Context(_connectionString, tenant);

        var result = await IdentityTestDb.Seeder(context, tenant).CreateTenantAdminAsync(
            "Admin@Acme.example", "Acme Admin", "a-long-first-password", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var verify = IdentityTestDb.Context(_connectionString, tenant);
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == result.Value);
        user.Email.Should().Be("admin@acme.example");
        user.MustChangePassword.Should().BeTrue();
        user.PasswordHash.Should().NotContain("a-long-first-password");
        IdentityTestDb.FastHasher().Verify(user.PasswordHash, "a-long-first-password").Should().NotBe(Hrms.Identity.Application.PasswordCheck.Failed);

        var scope = await verify.UserRoleScopes.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        scope.ScopeType.Should().Be(ScopeType.Tenant);
        scope.ScopeValue.Should().Be(Guid.Empty);
        var role = await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == scope.RoleId);
        role.Code.Should().Be(DefaultRoles.TenantAdmin);
    }

    [Fact]
    public async Task CreateTenantAdmin_WithAPasswordBelowThePolicy_FailsAndCreatesNothing()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        await using var context = IdentityTestDb.Context(_connectionString, tenant);

        var result = await IdentityTestDb.Seeder(context, tenant).CreateTenantAdminAsync(
            "admin@acme.example", "Admin", "short", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("identity.user.password_policy");
        result.Error.Message.Should().Contain(PasswordPolicy.TooShort).And.NotContain("short\"");
        (await context.Users.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateTenantAdmin_HonoursAStricterTenantPolicyInForce()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        await using (var context = IdentityTestDb.Context(_connectionString, tenant))
        {
            var policy = TenantSecurityPolicy.Create(new DateOnly(2026, 1, 1), 16, true, true, true, true, 5, 15).Value;
            context.SecurityPolicies.Add(policy);
            await context.SaveChangesAsync();
        }

        await using var asTenant = IdentityTestDb.Context(_connectionString, tenant);
        var weak = await IdentityTestDb.Seeder(asTenant, tenant).CreateTenantAdminAsync(
            "admin@acme.example", "Admin", "long-lowercase-only", CancellationToken.None);
        var strong = await IdentityTestDb.Seeder(asTenant, tenant).CreateTenantAdminAsync(
            "admin@acme.example", "Admin", "Long-Mixed-Passw0rd!", CancellationToken.None);

        weak.IsFailure.Should().BeTrue();
        weak.Error.Message.Should().Contain(PasswordPolicy.MissingUppercase).And.Contain(PasswordPolicy.MissingDigit);
        strong.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CreateTenantAdmin_ForAnEmailAlreadyInTheTenant_ReturnsConflictIgnoringCase()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        await using var context = IdentityTestDb.Context(_connectionString, tenant);
        var seeder = IdentityTestDb.Seeder(context, tenant);
        await seeder.CreateTenantAdminAsync("admin@acme.example", "Admin", "a-long-first-password", CancellationToken.None);

        var again = await seeder.CreateTenantAdminAsync("ADMIN@acme.example", "Other", "a-long-first-password", CancellationToken.None);

        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be("identity.user.email_taken");
        again.Error.Type.Should().Be(Hrms.SharedKernel.ErrorType.Conflict);
    }

    [Fact]
    public async Task CreateTenantAdmin_BeforeTheDefaultRolesExist_ReturnsConflictInsteadOfAnAdminWithNoRole()
    {
        var tenant = NewTenant();
        await using var context = IdentityTestDb.Context(_connectionString, tenant);

        var result = await IdentityTestDb.Seeder(context, tenant).CreateTenantAdminAsync(
            "admin@acme.example", "Admin", "a-long-first-password", CancellationToken.None);

        result.Error.Code.Should().Be("identity.role.missing");
        (await context.Users.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateTenantAdmin_WithAnInvalidEmail_FailsValidation()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        await using var context = IdentityTestDb.Context(_connectionString, tenant);

        var result = await IdentityTestDb.Seeder(context, tenant).CreateTenantAdminAsync(
            "not-an-email", "Admin", "a-long-first-password", CancellationToken.None);

        result.Error.Code.Should().Be("identity.user.email_invalid");
    }
}
