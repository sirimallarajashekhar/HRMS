using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Security;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Identity;

[Collection(MySqlTestGroup.Name)]
public class IdentityPersistenceTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await IdentityTestDb.CreateMigratedAsync(mysql);

    public Task DisposeAsync() => Task.CompletedTask;

    private static TenantId NewTenant() => PersistenceTestDb.NewTenant();

    // --- uniqueness among live rows ---------------------------------------------------------------------------------

    [Fact]
    public async Task User_WithADuplicateEmailInTheSameTenant_IsRejected()
    {
        var tenant = NewTenant();
        await IdentityTestDb.AddUserAsync(_connectionString, tenant, "same@example.com");

        var act = () => IdentityTestDb.AddUserAsync(_connectionString, tenant, "same@example.com");

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task User_WithTheSameEmailInAnotherTenant_IsAllowed()
    {
        await IdentityTestDb.AddUserAsync(_connectionString, NewTenant(), "shared@example.com");

        var act = () => IdentityTestDb.AddUserAsync(_connectionString, NewTenant(), "shared@example.com");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task User_AfterBeingSoftDeleted_ReleasesItsEmailExactlyOnce()
    {
        var tenant = NewTenant();
        var first = await IdentityTestDb.AddUserAsync(_connectionString, tenant, "reuse@example.com");
        await using (var context = IdentityTestDb.Context(_connectionString, tenant))
        {
            context.Users.Remove(await context.Users.SingleAsync(u => u.Id == first.Id));
            await context.SaveChangesAsync();
        }

        var reuse = () => IdentityTestDb.AddUserAsync(_connectionString, tenant, "reuse@example.com");
        await reuse.Should().NotThrowAsync();

        var duplicateOfTheLiveOne = () => IdentityTestDb.AddUserAsync(_connectionString, tenant, "reuse@example.com");
        await duplicateOfTheLiveOne.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task RoleGrant_Duplicate_IsRejected_ButAGrantRemovedAndAddedAgainIsAllowed()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        var role = await IdentityTestDb.RoleAsync(_connectionString, tenant, DefaultRoles.Employee);

        await using (var context = IdentityTestDb.Context(_connectionString, tenant))
        {
            context.RoleGrants.Add(RoleGrant.Create(role.Id, Permissions.Core.LegalEntityView).Value);
            await context.SaveChangesAsync();
        }

        await using (var duplicate = IdentityTestDb.Context(_connectionString, tenant))
        {
            duplicate.RoleGrants.Add(RoleGrant.Create(role.Id, Permissions.Core.LegalEntityView).Value);
            var act = () => duplicate.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using (var remove = IdentityTestDb.Context(_connectionString, tenant))
        {
            var grant = await remove.RoleGrants.SingleAsync(g => g.RoleId == role.Id);
            remove.RoleGrants.Remove(grant);
            await remove.SaveChangesAsync();
        }

        await using var readd = IdentityTestDb.Context(_connectionString, tenant);
        readd.RoleGrants.Add(RoleGrant.Create(role.Id, Permissions.Core.LegalEntityView).Value);
        var reAdd = () => readd.SaveChangesAsync();
        await reAdd.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UserRoleScope_DuplicatesAreRejected_ButDifferentScopeValuesAreAllowed()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        var user = await IdentityTestDb.AddUserAsync(_connectionString, tenant, "scoped@example.com");
        var role = await IdentityTestDb.RoleAsync(_connectionString, tenant, DefaultRoles.HrAdmin);
        var locationA = Guid.CreateVersion7();
        var locationB = Guid.CreateVersion7();

        await using (var context = IdentityTestDb.Context(_connectionString, tenant))
        {
            context.UserRoleScopes.AddRange(
                UserRoleScope.Create(user.Id, role.Id, ScopeType.Location, locationA).Value,
                UserRoleScope.Create(user.Id, role.Id, ScopeType.Location, locationB).Value,
                UserRoleScope.Create(user.Id, role.Id, ScopeType.Self, null).Value);
            await context.SaveChangesAsync();
        }

        foreach (var duplicate in new[]
                 {
                     UserRoleScope.Create(user.Id, role.Id, ScopeType.Location, locationA).Value,
                     UserRoleScope.Create(user.Id, role.Id, ScopeType.Self, null).Value,
                 })
        {
            await using var context = IdentityTestDb.Context(_connectionString, tenant);
            context.UserRoleScopes.Add(duplicate);
            var act = () => context.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Role_WithADuplicateCodeInTheSameTenant_IsRejected()
    {
        var tenant = NewTenant();
        await using var context = IdentityTestDb.Context(_connectionString, tenant);
        context.Roles.Add(Role.Create("custom", "Custom", null, false, false).Value);
        context.Roles.Add(Role.Create("custom", "Custom again", null, false, false).Value);

        var act = () => context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    // --- tenant isolation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Queries_AsTenantA_NeverSeeTenantBUsersRolesGrantsOrScopes()
    {
        var a = NewTenant();
        var b = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, a);
        await IdentityTestDb.SeedTenantAsync(_connectionString, b);
        await using (var seedB = IdentityTestDb.Context(_connectionString, b))
        {
            await IdentityTestDb.Seeder(seedB, b).CreateTenantAdminAsync("b-admin@example.com", "B Admin", "a-long-first-password", CancellationToken.None);
        }

        await using var asA = IdentityTestDb.Context(_connectionString, a);

        (await asA.Users.AsNoTracking().CountAsync()).Should().Be(0);
        (await asA.UserRoleScopes.AsNoTracking().CountAsync()).Should().Be(0);
        (await asA.Roles.AsNoTracking().CountAsync()).Should().Be(DefaultRoles.All.Count);
        (await asA.RoleGrants.AsNoTracking().Select(g => g.TenantId).Distinct().ToListAsync()).Should().Equal(a);
    }

    [Fact]
    public async Task Update_OfAnotherTenantsUser_IsRefusedAndLeavesTheAccountUntouched()
    {
        var a = NewTenant();
        var b = NewTenant();
        var user = await IdentityTestDb.AddUserAsync(_connectionString, a, "victim@example.com");
        UserAccount detached;
        await using (var asA = IdentityTestDb.Context(_connectionString, a))
        {
            detached = await asA.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        }

        await using (var asB = IdentityTestDb.Context(_connectionString, b))
        {
            asB.Users.Attach(detached);
            detached.Disable();
            asB.Entry(detached).State = EntityState.Modified;
            var act = () => asB.SaveChangesAsync();
            await act.Should().ThrowAsync<CrossTenantWriteException>();
        }

        await using var verify = IdentityTestDb.Context(_connectionString, a);
        (await verify.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task SecurityPolicy_OfOneTenant_IsInvisibleToAnother()
    {
        var a = NewTenant();
        var b = NewTenant();
        await using (var context = IdentityTestDb.Context(_connectionString, a))
        {
            context.SecurityPolicies.Add(Hrms.Identity.Domain.Security.TenantSecurityPolicy.Create(new DateOnly(2026, 1, 1), 20, true, true, true, true, 3, 60).Value);
            await context.SaveChangesAsync();
        }

        await using var asB = IdentityTestDb.Context(_connectionString, b);

        (await asB.SecurityPolicies.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UserState_PersistsLockoutAndAuditColumns()
    {
        var tenant = NewTenant();
        var actor = Guid.CreateVersion7();
        var user = await IdentityTestDb.AddUserAsync(_connectionString, tenant, "lock@example.com");
        var policy = new Hrms.Identity.Domain.Security.LockoutPolicy(2, TimeSpan.FromMinutes(5));

        await using (var context = IdentityTestDb.Context(_connectionString, tenant, actor: actor))
        {
            var stored = await context.Users.SingleAsync(u => u.Id == user.Id);
            stored.RegisterFailedAttempt(IdentityTestDb.Start, policy);
            stored.RegisterFailedAttempt(IdentityTestDb.Start, policy);
            await context.SaveChangesAsync();
        }

        await using var verify = IdentityTestDb.Context(_connectionString, tenant);
        var reloaded = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        reloaded.LockoutUntil.Should().Be(IdentityTestDb.Start.AddMinutes(5));
        reloaded.LockoutUntil!.Value.Kind.Should().Be(DateTimeKind.Utc);
        reloaded.UpdatedBy.Should().Be(actor);
        reloaded.RowVersion.Should().Be(2);
    }

    // --- the permission store ---------------------------------------------------------------------------------------

    private PermissionStore Store(IdentityDbContextHolder holder, TenantId tenant, MovableClock clock, PermissionCache? cache = null)
    {
        holder.Context = IdentityTestDb.Context(_connectionString, tenant);
        var tenantContext = new TenantContext();
        tenantContext.Set(tenant);
        return new PermissionStore(holder.Context, cache ?? new PermissionCache(clock), tenantContext);
    }

    private sealed class IdentityDbContextHolder : IAsyncDisposable
    {
        public Hrms.Identity.Infrastructure.Persistence.IdentityDbContext? Context { get; set; }

        public ValueTask DisposeAsync() => Context?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    [Fact]
    public async Task PermissionStore_ReturnsTheUnionOfTheRolesPermissions_AndNothingForUnknownRoles()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        await using var holder = new IdentityDbContextHolder();
        var store = Store(holder, tenant, new MovableClock(new DateTimeOffset(IdentityTestDb.Start)));

        var auditor = await store.GetPermissionsAsync(tenant.Value, [DefaultRoles.Auditor], CancellationToken.None);
        var both = await store.GetPermissionsAsync(tenant.Value, [DefaultRoles.Auditor, DefaultRoles.HrAdmin], CancellationToken.None);
        var unknown = await store.GetPermissionsAsync(tenant.Value, ["no_such_role"], CancellationToken.None);
        var employee = await store.GetPermissionsAsync(tenant.Value, [DefaultRoles.Employee], CancellationToken.None);

        auditor.Should().BeEquivalentTo(DefaultRoles.All.Single(r => r.Code == DefaultRoles.Auditor).PermissionCodes);
        both.Should().Contain(Permissions.Identity.UserManage).And.Contain(Permissions.Identity.SensitiveView);
        unknown.Should().BeEmpty();
        employee.Should().BeEmpty();
    }

    [Fact]
    public async Task PermissionStore_ForTwoTenantsWithTheSameRoleCode_ReturnsEachTenantsOwnGrants()
    {
        var a = NewTenant();
        var b = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, a);
        await IdentityTestDb.SeedTenantAsync(_connectionString, b);
        var clock = new MovableClock(new DateTimeOffset(IdentityTestDb.Start));

        // Tenant B's manager role also gets user management; tenant A's does not.
        await using (var context = IdentityTestDb.Context(_connectionString, b))
        {
            var manager = await context.Roles.SingleAsync(r => r.Code == DefaultRoles.Manager);
            context.RoleGrants.Add(RoleGrant.Create(manager.Id, Permissions.Identity.UserManage).Value);
            await context.SaveChangesAsync();
        }

        var sharedCache = new PermissionCache(clock);
        await using var holderA = new IdentityDbContextHolder();
        await using var holderB = new IdentityDbContextHolder();
        var forA = await Store(holderA, a, clock, sharedCache).GetPermissionsAsync(a.Value, [DefaultRoles.Manager], CancellationToken.None);
        var forB = await Store(holderB, b, clock, sharedCache).GetPermissionsAsync(b.Value, [DefaultRoles.Manager], CancellationToken.None);

        forA.Should().NotContain(Permissions.Identity.UserManage);
        forB.Should().Contain(Permissions.Identity.UserManage);
    }

    [Fact]
    public async Task PermissionStore_AskedForAnotherTenantThanTheRequests_RefusesInsteadOfLeaking()
    {
        var a = NewTenant();
        var b = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, a);
        await using var holder = new IdentityDbContextHolder();
        var store = Store(holder, a, new MovableClock(new DateTimeOffset(IdentityTestDb.Start)));

        var act = async () => await store.GetPermissionsAsync(b.Value, [DefaultRoles.TenantAdmin], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant of the current request*");
    }

    [Fact]
    public async Task PermissionStore_CachesForAMinute_ThenSeesAChangedRole_AndInvalidateIsImmediate()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        var clock = new MovableClock(new DateTimeOffset(IdentityTestDb.Start));
        var cache = new PermissionCache(clock);
        await using var holder = new IdentityDbContextHolder();
        var store = Store(holder, tenant, clock, cache);
        (await store.GetPermissionsAsync(tenant.Value, [DefaultRoles.Employee], CancellationToken.None)).Should().BeEmpty();

        await using (var context = IdentityTestDb.Context(_connectionString, tenant))
        {
            var employee = await context.Roles.SingleAsync(r => r.Code == DefaultRoles.Employee);
            context.RoleGrants.Add(RoleGrant.Create(employee.Id, Permissions.Core.LegalEntityView).Value);
            await context.SaveChangesAsync();
        }

        clock.Advance(PermissionCache.TimeToLive - TimeSpan.FromSeconds(1));
        (await store.GetPermissionsAsync(tenant.Value, [DefaultRoles.Employee], CancellationToken.None))
            .Should().BeEmpty("the answer is cached until the time to live passes");

        clock.Advance(TimeSpan.FromSeconds(2));
        (await store.GetPermissionsAsync(tenant.Value, [DefaultRoles.Employee], CancellationToken.None))
            .Should().Contain(Permissions.Core.LegalEntityView);

        await using (var context = IdentityTestDb.Context(_connectionString, tenant))
        {
            var grant = await context.RoleGrants.SingleAsync(g => g.PermissionCode == Permissions.Core.LegalEntityView
                                                                  && context.Roles.Any(r => r.Id == g.RoleId && r.Code == DefaultRoles.Employee));
            context.RoleGrants.Remove(grant);
            await context.SaveChangesAsync();
        }

        cache.Invalidate(tenant.Value);
        (await store.GetPermissionsAsync(tenant.Value, [DefaultRoles.Employee], CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task PermissionStore_IgnoresGrantsThatWereSoftDeleted()
    {
        var tenant = NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, tenant);
        await using (var context = IdentityTestDb.Context(_connectionString, tenant))
        {
            var auditor = await context.Roles.SingleAsync(r => r.Code == DefaultRoles.Auditor);
            var grant = await context.RoleGrants.SingleAsync(g => g.RoleId == auditor.Id && g.PermissionCode == Permissions.Identity.UserView);
            context.RoleGrants.Remove(grant);
            await context.SaveChangesAsync();
        }

        await using var holder = new IdentityDbContextHolder();
        var permissions = await Store(holder, tenant, new MovableClock(new DateTimeOffset(IdentityTestDb.Start)))
            .GetPermissionsAsync(tenant.Value, [DefaultRoles.Auditor], CancellationToken.None);

        permissions.Should().NotContain(Permissions.Identity.UserView).And.Contain(Permissions.Core.LegalEntityView);
    }
}
