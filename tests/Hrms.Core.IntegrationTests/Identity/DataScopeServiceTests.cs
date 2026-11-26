using FluentAssertions;
using Hrms.Contracts;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Users;
using Hrms.Identity.Infrastructure.Security;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Identity;

/// <summary>
/// The org graph the fake expander walks (the real one arrives with the employee module in P0.6):
///   ceo -> mgr -> { e1, e2 }, e1 -> e3;   other -> e4
/// </summary>
public sealed class FakeOrg : IEmployeeScopeExpander
{
    public static readonly Guid Ceo = Guid.CreateVersion7();
    public static readonly Guid Mgr = Guid.CreateVersion7();
    public static readonly Guid E1 = Guid.CreateVersion7();
    public static readonly Guid E2 = Guid.CreateVersion7();
    public static readonly Guid E3 = Guid.CreateVersion7();
    public static readonly Guid Other = Guid.CreateVersion7();
    public static readonly Guid E4 = Guid.CreateVersion7();

    public static readonly Guid Hq = Guid.CreateVersion7();
    public static readonly Guid Branch = Guid.CreateVersion7();

    private static readonly Dictionary<Guid, Guid?> ManagerOf = new()
    {
        [Ceo] = null, [Mgr] = Ceo, [E1] = Mgr, [E2] = Mgr, [E3] = E1, [Other] = Ceo, [E4] = Other,
    };

    private static readonly Dictionary<Guid, Guid[]> AtLocation = new() { [Hq] = [Ceo, Mgr, E1], [Branch] = [E2, E3, Other, E4] };

    public static Guid[] Everyone => [.. ManagerOf.Keys];

    public List<IReadOnlyList<DataScopeGrant>> Calls { get; } = [];

    public Task<IReadOnlySet<Guid>> ExpandAsync(Guid? employeeId, IReadOnlyList<DataScopeGrant> grants, CancellationToken cancellationToken)
    {
        Calls.Add(grants);
        var ids = new HashSet<Guid>();
        foreach (var grant in grants)
        {
            switch (grant.Kind)
            {
                case ScopeKind.Self when employeeId is { } own:
                    ids.Add(own);
                    break;
                case ScopeKind.DirectReports when employeeId is { } boss:
                    ids.UnionWith(ManagerOf.Where(pair => pair.Value == boss).Select(pair => pair.Key));
                    break;
                case ScopeKind.AllReports when employeeId is { } top:
                    ids.UnionWith(ReportsOf(top));
                    break;
                case ScopeKind.Location when grant.Value is { } location && AtLocation.TryGetValue(location, out var members):
                    ids.UnionWith(members);
                    break;
                case ScopeKind.Tenant:
                    ids.UnionWith(Everyone);
                    break;
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(ids);
    }

    private static IEnumerable<Guid> ReportsOf(Guid boss)
    {
        var direct = ManagerOf.Where(pair => pair.Value == boss).Select(pair => pair.Key).ToList();
        return direct.Concat(direct.SelectMany(ReportsOf));
    }
}

[Collection(MySqlTestGroup.Name)]
public class DataScopeServiceTests(MySqlFixture mysql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _connectionString = await IdentityTestDb.CreateMigratedAsync(mysql);
        _tenant = PersistenceTestDb.NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, _tenant);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<UserAccount> UserAsync(TenantId tenant, Guid? employeeId, string role, params (ScopeType Type, Guid? Value)[] scopes)
    {
        var user = UserAccount.Create($"u-{Guid.NewGuid():N}@example.com", "User", IdentityTestDb.FastHasher().Hash("a-very-long-password"), IdentityTestDb.Start, employeeId).Value;
        await using var context = IdentityTestDb.Context(_connectionString, tenant);
        var roleRow = await context.Roles.AsNoTracking().SingleAsync(r => r.Code == role);
        context.Users.Add(user);
        foreach (var (type, value) in scopes)
        {
            context.UserRoleScopes.Add(UserRoleScope.Create(user.Id, roleRow.Id, type, value).Value);
        }

        await context.SaveChangesAsync();
        return user;
    }

    private static DataScopeService Service(Hrms.Identity.Infrastructure.Persistence.IdentityDbContext context, IEmployeeScopeExpander? expander = null) =>
        new(context, expander ?? new FakeOrg());

    private async Task<IReadOnlySet<Guid>> VisibleAsync(UserAccount user, string permission = Permissions.Core.LegalEntityView, TenantId? tenant = null)
    {
        await using var context = IdentityTestDb.Context(_connectionString, tenant ?? _tenant);
        return await Service(context).GetVisibleEmployeeIdsAsync(user.Id, permission, CancellationToken.None);
    }

    // The manager role holds core.legal_entity.view in the default roles, so it is the role used to carry scopes here.

    [Fact]
    public async Task Manager_WithAllReports_SeesTheWholeReportingChainAndNobodyElse()
    {
        var manager = await UserAsync(_tenant, FakeOrg.Mgr, DefaultRoles.Manager, (ScopeType.AllReports, null));

        var visible = await VisibleAsync(manager);

        visible.Should().BeEquivalentTo([FakeOrg.E1, FakeOrg.E2, FakeOrg.E3]);
        visible.Should().NotContain([FakeOrg.Ceo, FakeOrg.Mgr, FakeOrg.Other, FakeOrg.E4], "not above them, not themselves, not another branch");
    }

    [Fact]
    public async Task Manager_WithDirectReports_SeesOnlyThePeopleReportingToThem()
    {
        var manager = await UserAsync(_tenant, FakeOrg.Mgr, DefaultRoles.Manager, (ScopeType.DirectReports, null));

        (await VisibleAsync(manager)).Should().BeEquivalentTo([FakeOrg.E1, FakeOrg.E2]);
    }

    [Fact]
    public async Task Employee_WithSelf_SeesOnlyThemselves()
    {
        var employee = await UserAsync(_tenant, FakeOrg.E3, DefaultRoles.Manager, (ScopeType.Self, null));

        (await VisibleAsync(employee)).Should().BeEquivalentTo([FakeOrg.E3]);
    }

    [Fact]
    public async Task SeveralScopes_AreUnioned()
    {
        var manager = await UserAsync(
            _tenant, FakeOrg.Mgr, DefaultRoles.Manager, (ScopeType.Self, null), (ScopeType.DirectReports, null), (ScopeType.Location, FakeOrg.Branch));

        (await VisibleAsync(manager)).Should().BeEquivalentTo([FakeOrg.Mgr, FakeOrg.E1, FakeOrg.E2, FakeOrg.E3, FakeOrg.Other, FakeOrg.E4]);
    }

    [Fact]
    public async Task TenantScope_SeesEveryoneInTheTenant()
    {
        var admin = await UserAsync(_tenant, null, DefaultRoles.Manager, (ScopeType.Tenant, null));

        (await VisibleAsync(admin)).Should().BeEquivalentTo(FakeOrg.Everyone);
    }

    [Fact]
    public async Task ARoleThatDoesNotGrantThePermission_ContributesNoScope()
    {
        // The employee role has no permissions at all, so its scopes are worthless for any permission.
        var user = await UserAsync(_tenant, FakeOrg.Mgr, DefaultRoles.Employee, (ScopeType.Tenant, null));

        (await VisibleAsync(user)).Should().BeEmpty();
        await using var context = IdentityTestDb.Context(_connectionString, _tenant);
        (await Service(context).ResolveAsync(user.Id, Permissions.Core.LegalEntityView, CancellationToken.None)).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task APermissionTheRoleLacks_GivesNoScopeEvenThoughTheRoleHasOthers()
    {
        var manager = await UserAsync(_tenant, FakeOrg.Mgr, DefaultRoles.Manager, (ScopeType.Tenant, null));

        (await VisibleAsync(manager, Permissions.Identity.UserManage)).Should().BeEmpty("the manager role does not grant user management");
    }

    [Fact]
    public async Task ADisabledUser_HasNoScopeAtAll()
    {
        var manager = await UserAsync(_tenant, FakeOrg.Mgr, DefaultRoles.Manager, (ScopeType.Tenant, null));
        await using (var context = IdentityTestDb.Context(_connectionString, _tenant))
        {
            (await context.Users.SingleAsync(u => u.Id == manager.Id)).Disable();
            await context.SaveChangesAsync();
        }

        (await VisibleAsync(manager)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownUser_HasNoScope()
    {
        await using var context = IdentityTestDb.Context(_connectionString, _tenant);

        var scope = await Service(context).ResolveAsync(Guid.CreateVersion7(), Permissions.Core.LegalEntityView, CancellationToken.None);

        scope.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task ARoleThatWasRemovedFromTheUser_NoLongerGivesScope()
    {
        var manager = await UserAsync(_tenant, FakeOrg.Mgr, DefaultRoles.Manager, (ScopeType.AllReports, null));
        await using (var context = IdentityTestDb.Context(_connectionString, _tenant))
        {
            context.UserRoleScopes.RemoveRange(await context.UserRoleScopes.Where(s => s.UserId == manager.Id).ToListAsync());
            await context.SaveChangesAsync();
        }

        (await VisibleAsync(manager)).Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_DescribesTheScopesWithoutExpandingThem()
    {
        var manager = await UserAsync(_tenant, FakeOrg.Mgr, DefaultRoles.Manager, (ScopeType.Location, FakeOrg.Hq), (ScopeType.Tenant, null));
        var expander = new FakeOrg();
        await using var context = IdentityTestDb.Context(_connectionString, _tenant);

        var scope = await Service(context, expander).ResolveAsync(manager.Id, Permissions.Core.LegalEntityView, CancellationToken.None);

        scope.IsTenantWide.Should().BeTrue();
        scope.Grants.Should().BeEquivalentTo([new DataScopeGrant(ScopeKind.Location, FakeOrg.Hq), new DataScopeGrant(ScopeKind.Tenant, null)]);
        expander.Calls.Should().BeEmpty("resolving describes the scope; only GetVisibleEmployeeIdsAsync expands it");
    }

    [Fact]
    public async Task AUserWithNoScopeForThePermission_NeverReachesTheExpander()
    {
        var nobody = await UserAsync(_tenant, FakeOrg.Mgr, DefaultRoles.Employee);
        var expander = new FakeOrg();
        await using var context = IdentityTestDb.Context(_connectionString, _tenant);

        var ids = await Service(context, expander).GetVisibleEmployeeIdsAsync(nobody.Id, Permissions.Core.LegalEntityView, CancellationToken.None);

        ids.Should().BeEmpty();
        expander.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherTenantsUser_HasNoScopeInThisTenant()
    {
        var other = PersistenceTestDb.NewTenant();
        await IdentityTestDb.SeedTenantAsync(_connectionString, other);
        var foreign = await UserAsync(other, FakeOrg.Mgr, DefaultRoles.Manager, (ScopeType.Tenant, null));

        (await VisibleAsync(foreign, tenant: _tenant)).Should().BeEmpty("the user does not exist in this tenant");
        (await VisibleAsync(foreign, tenant: other)).Should().BeEquivalentTo(FakeOrg.Everyone);
    }

    [Fact]
    public async Task TheDefaultExpander_AnswersSelfAndRefusesWiderScopesUntilTheEmployeeModuleExists()
    {
        var expander = new SelfOnlyScopeExpander();
        var me = Guid.CreateVersion7();

        (await expander.ExpandAsync(me, [new DataScopeGrant(ScopeKind.Self)], CancellationToken.None)).Should().BeEquivalentTo([me]);
        (await expander.ExpandAsync(null, [new DataScopeGrant(ScopeKind.Self)], CancellationToken.None)).Should().BeEmpty();

        var act = () => expander.ExpandAsync(me, [new DataScopeGrant(ScopeKind.AllReports)], CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*employee module*");
    }
}
