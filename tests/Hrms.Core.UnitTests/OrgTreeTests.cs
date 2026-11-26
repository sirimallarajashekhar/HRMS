using FluentAssertions;
using Hrms.Core.Domain.Org;

namespace Hrms.Core.UnitTests;

public class OrgTreeTests
{
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid C = Guid.CreateVersion7();
    private static readonly Guid D = Guid.CreateVersion7();

    private static readonly DateOnly Jan = new(2026, 1, 1);
    private static readonly DateOnly Jun = new(2026, 6, 1);
    private static readonly DateOnly Dec = new(2026, 12, 1);

    private static ParentLink Link(Guid unit, Guid? parent, DateOnly from, DateOnly? to = null) => new(unit, parent, from, to);

    // A is the root, B under A, C under B.
    private static List<ParentLink> Chain() => [Link(A, null, Jan), Link(B, A, Jan), Link(C, B, Jan)];

    [Fact]
    public void CheckMove_UnderAnUnrelatedParent_IsAllowed()
    {
        OrgTree.CheckMove(D, C, Jun, [.. Chain(), Link(D, null, Jan)]).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void CheckMove_ANewUnitUnderAnExistingOne_IsAllowed()
    {
        OrgTree.CheckMove(D, B, Jan, Chain()).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void CheckMove_UnderItself_IsACycle()
    {
        var result = OrgTree.CheckMove(A, A, Jun, Chain());

        result.Error.Code.Should().Be("core.org_unit.cycle");
    }

    [Fact]
    public void CheckMove_UnderADirectChild_IsACycle()
    {
        OrgTree.CheckMove(A, B, Jun, Chain()).Error.Code.Should().Be("core.org_unit.cycle");
    }

    [Fact]
    public void CheckMove_UnderADeepDescendant_IsACycle()
    {
        OrgTree.CheckMove(A, C, Jun, Chain()).Error.Code.Should().Be("core.org_unit.cycle");
    }

    [Fact]
    public void CheckMove_ToBecomeARoot_IsAllowedEvenWhenItHasChildren()
    {
        OrgTree.CheckMove(B, null, Jun, Chain()).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void CheckMove_ThatIsSafeTodayButBreaksOnAFutureChange_IsRefused()
    {
        // From December, C moves under D (a unit nobody has touched yet). Moving D under C from June is fine today
        // but would make D its own ancestor from December.
        List<ParentLink> links =
        [
            Link(A, null, Jan), Link(D, A, Jan),
            Link(B, A, Jan, new DateOnly(2026, 11, 30)), Link(B, D, Dec),
        ];

        // D under B from June: B -> A today, so no cycle on that day, but from December B's parent is D.
        OrgTree.CheckMove(D, B, Jun, links).Error.Code.Should().Be("core.org_unit.cycle");
    }

    [Fact]
    public void CheckMove_WhoseOldPeriodsWouldHaveMadeACycle_IsJudgedOnTheNewArrangement()
    {
        // B sits under A today. Moving A under B is a cycle only while B is under A; if B moves to be a root first,
        // the same move is fine from the later date.
        List<ParentLink> links = [Link(A, null, Jan), Link(B, A, Jan, new DateOnly(2026, 5, 31)), Link(B, null, Jun)];

        OrgTree.CheckMove(A, B, Dec, links).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(2026, 1, 1)]
    [InlineData(2025, 12, 31)]
    public void CheckMove_NotAfterTheLatestPeriod_IsRefused(int year, int month, int day)
    {
        var result = OrgTree.CheckMove(B, null, new DateOnly(year, month, day), Chain());

        result.Error.Code.Should().Be("core.org_unit.parent_change_not_after_latest");
    }

    [Fact]
    public void CheckMove_TheDayAfterTheLatestPeriod_IsAllowed()
    {
        OrgTree.CheckMove(B, null, Jan.AddDays(1), Chain()).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void CheckMove_WithAnExistingLoopInTheData_RefusesInsteadOfLooping()
    {
        // Should be impossible through the API, but a corrupt tree must never hang the check.
        List<ParentLink> links = [Link(B, C, Jan), Link(C, B, Jan), Link(D, null, Jan)];

        OrgTree.CheckMove(D, B, Jun, links).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ParentLink_IsValidOn_IncludesBothEnds()
    {
        var link = Link(B, A, Jan, Jun);

        link.IsValidOn(Jan).Should().BeTrue();
        link.IsValidOn(Jun).Should().BeTrue();
        link.IsValidOn(Jun.AddDays(1)).Should().BeFalse();
        link.IsValidOn(Jan.AddDays(-1)).Should().BeFalse();
    }
}
