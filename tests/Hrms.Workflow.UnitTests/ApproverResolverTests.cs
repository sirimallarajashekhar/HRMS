using FluentAssertions;
using Hrms.Contracts;
using Hrms.Workflow.Domain;

namespace Hrms.Workflow.UnitTests;

public class ApproverResolverTests
{
    private readonly FakeOrganisation _org = new();

    private readonly Guid _subject = Guid.CreateVersion7();
    private readonly Guid _boss = Guid.CreateVersion7();
    private readonly Guid _bigBoss = Guid.CreateVersion7();
    private readonly Guid _requester = Guid.CreateVersion7();
    private readonly Guid _location = Guid.CreateVersion7();
    private readonly Guid _team = Guid.CreateVersion7();
    private readonly Guid _division = Guid.CreateVersion7();
    private readonly Guid _teamType = Guid.CreateVersion7();
    private readonly Guid _divisionType = Guid.CreateVersion7();

    public ApproverResolverTests()
    {
        _org.Place(_subject, _team, _location, _boss);
        _org.Place(_boss, _team, _location, _bigBoss);
        _org.Place(_bigBoss, _division, _location, null);
    }

    private ApprovalSubject Subject => new(_subject, _requester);

    private Task<ApproverResolution> Resolve(ApproverRule rule, IReadOnlyDictionary<string, object>? payload = null) =>
        new ApproverResolver(_org, _org, _org).ResolveAsync(rule, Subject, payload ?? TestData.Values(), TestData.Today, CancellationToken.None);

    [Fact]
    public async Task Manager_AtEachLevel_IsThatPersonsAccount()
    {
        var boss = _org.Account(_boss);
        var bigBoss = _org.Account(_bigBoss);

        (await Resolve(new(ApproverType.ManagerLevel, Level: 1))).UserIds.Should().Equal(boss);
        (await Resolve(new(ApproverType.ManagerLevel, Level: 2))).UserIds.Should().Equal(bigBoss);
    }

    [Fact]
    public async Task Manager_AboveTheTopOfTheLine_IsNobody_NotTheNearestOne()
    {
        _org.Account(_boss);
        _org.Account(_bigBoss);

        (await Resolve(new(ApproverType.ManagerLevel, Level: 3))).IsResolved.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manager_WithoutAnActiveAccount_IsNobody_AndNobodyElseIsSubstituted(bool hasInactiveAccount)
    {
        if (hasInactiveAccount)
        {
            _org.Account(_boss, active: false);
        }

        _org.Account(_bigBoss);

        var result = await Resolve(new(ApproverType.ManagerLevel, Level: 1));

        result.IsResolved.Should().BeFalse();
        result.Reason.Should().Contain("no active user account");
    }

    [Fact]
    public async Task Manager_WhoRaisedTheRequest_MayNotApproveIt()
    {
        _org.Account(_boss, userId: _requester);

        (await Resolve(new(ApproverType.ManagerLevel, Level: 1))).Reason.Should().Contain("their own request");
    }

    [Fact]
    public async Task UnitHead_IsTheNearestOneGoingUp()
    {
        var bigBoss = _org.Account(_bigBoss);
        _org.Chain(_team, new(_team, _teamType, null), new(_division, _divisionType, _bigBoss));

        (await Resolve(new(ApproverType.UnitHead))).UserIds.Should().Equal(bigBoss);
    }

    [Fact]
    public async Task UnitHead_SomeoneHeadingTheirOwnUnit_IsPassedOver()
    {
        _org.Account(_subject);
        var bigBoss = _org.Account(_bigBoss);
        _org.Chain(_team, new(_team, _teamType, _subject), new(_division, _divisionType, _bigBoss));

        (await Resolve(new(ApproverType.UnitHead))).UserIds.Should().Equal(bigBoss);
    }

    [Fact]
    public async Task UnitHead_WhoRaisedTheRequest_IsPassedOver()
    {
        _org.Account(_boss, userId: _requester);
        var bigBoss = _org.Account(_bigBoss);
        _org.Chain(_team, new(_team, _teamType, _boss), new(_division, _divisionType, _bigBoss));

        (await Resolve(new(ApproverType.UnitHead))).UserIds.Should().Equal(bigBoss);
    }

    [Fact]
    public async Task UnitHead_WithoutAnAccount_IsNotJumpedOver()
    {
        _org.Account(_bigBoss);
        _org.Chain(_team, new(_team, _teamType, _boss), new(_division, _divisionType, _bigBoss));

        (await Resolve(new(ApproverType.UnitHead))).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task UnitHead_OfAGivenType_IsThatUnitsHead_AndNobodyIfItHasNone()
    {
        _org.Account(_boss);
        var bigBoss = _org.Account(_bigBoss);
        _org.Chain(_team, new(_team, _teamType, _boss), new(_division, _divisionType, _bigBoss));

        (await Resolve(new(ApproverType.UnitHead, OrgUnitTypeId: _divisionType))).UserIds.Should().Equal(bigBoss);
        (await Resolve(new(ApproverType.UnitHead, OrgUnitTypeId: Guid.CreateVersion7()))).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task RoleAtLocation_IsEveryEligibleHolder_WithoutTheBarredOrInactive()
    {
        var first = _org.Holder("hr_officer", _location);
        var second = _org.Holder("hr_officer", _location);
        _org.Holder("hr_officer", _location, userId: _requester);
        _org.Holder("hr_officer", _location, employeeId: _subject);
        _org.Holder("hr_officer", _location, active: false);
        _org.Holder("hr_officer", Guid.CreateVersion7());
        _org.Holder("finance", _location);

        (await Resolve(new(ApproverType.RoleAtLocation, RoleCode: "hr_officer"))).UserIds.Should().BeEquivalentTo([first, second]);
    }

    [Theory]
    [InlineData(ApproverResolver.MaxApprovers, true)]
    [InlineData(ApproverResolver.MaxApprovers + 1, false)]
    public async Task RoleAtLocation_WithTooManyHolders_IsUnusable(int holders, bool resolved)
    {
        // Two barred holders as well, to show that the cap is on the people who could be asked.
        _org.Holder("hr_officer", _location, userId: _requester);
        _org.Holder("hr_officer", _location, employeeId: _subject);
        for (var i = 0; i < holders; i++)
        {
            _org.Holder("hr_officer", _location);
        }

        (await Resolve(new(ApproverType.RoleAtLocation, RoleCode: "hr_officer"))).IsResolved.Should().Be(resolved);
    }

    [Fact]
    public async Task NamedAndRequestUsers_MustBeActive_AndNotBarred()
    {
        var fine = _org.Account(Guid.CreateVersion7(), userId: TestData.Cover);
        var inactive = _org.Account(Guid.CreateVersion7(), active: false);
        _org.Account(null, userId: _requester);
        var subjectAccount = _org.Account(_subject);

        (await Resolve(new(ApproverType.NamedUser, UserId: fine))).UserIds.Should().Equal(fine);
        (await Resolve(new(ApproverType.PayloadUser, Field: "coverUserId"))).UserIds.Should().Equal(fine);
        foreach (var refused in new[] { inactive, _requester, subjectAccount, Guid.CreateVersion7() })
        {
            (await Resolve(new(ApproverType.NamedUser, UserId: refused))).IsResolved.Should().BeFalse();
        }
    }

    [Fact]
    public async Task RequestUser_WhenTheOptionalFieldIsEmpty_IsNobody() =>
        (await Resolve(new(ApproverType.PayloadUser, Field: "secondUserId"))).IsResolved.Should().BeFalse();

    private sealed class FakeOrganisation : IEmployeePositionLookup, IOrgUnitHeadLookup, IApproverDirectory
    {
        private readonly Dictionary<Guid, EmployeePosition> _positions = [];
        private readonly Dictionary<Guid, IReadOnlyList<OrgUnitHeadLink>> _chains = [];
        private readonly List<ApproverAccount> _accounts = [];
        private readonly List<(string Role, Guid Location, Guid User)> _holders = [];

        public void Place(Guid employee, Guid unit, Guid location, Guid? manager) =>
            _positions[employee] = new(employee, Guid.CreateVersion7(), location, unit, manager);

        public void Chain(Guid unit, params OrgUnitHeadLink[] links) => _chains[unit] = links;

        public Guid Account(Guid? employee, bool active = true, Guid? userId = null)
        {
            var id = userId ?? Guid.CreateVersion7();
            _accounts.Add(new(id, active, employee));
            return id;
        }

        public Guid Holder(string role, Guid location, bool active = true, Guid? userId = null, Guid? employeeId = null)
        {
            var id = Account(employeeId, active, userId);
            _holders.Add((role, location, id));
            return id;
        }

        public Task<EmployeePosition?> GetPositionAsync(Guid employeeId, DateOnly asOf, CancellationToken cancellationToken) =>
            Task.FromResult(_positions.GetValueOrDefault(employeeId));

        public Task<IReadOnlyList<OrgUnitHeadLink>> GetHeadChainAsync(Guid orgUnitId, DateOnly asOf, CancellationToken cancellationToken) =>
            Task.FromResult(_chains.GetValueOrDefault(orgUnitId, []));

        public Task<IReadOnlyList<ApproverAccount>> GetAccountsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ApproverAccount>>(_accounts.Where(a => userIds.Contains(a.UserId)).ToList());

        public Task<ApproverAccount?> FindByEmployeeAsync(Guid employeeId, CancellationToken cancellationToken) =>
            Task.FromResult(_accounts.FirstOrDefault(a => a.EmployeeId == employeeId));

        public Task<bool> RoleExistsAsync(string roleCode, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlyList<ApproverAccount>> FindWithRoleAtLocationAsync(
            string roleCode, Guid locationId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ApproverAccount>>(_holders.Where(h => h.Role == roleCode && h.Location == locationId)
                .Select(h => _accounts.First(a => a.UserId == h.User)).Take(limit).ToList());
    }
}
