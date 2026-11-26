using FluentAssertions;

namespace Hrms.SharedKernel.UnitTests;

public class EffectiveDatingTests
{
    private sealed class Rate(DateOnly from, DateOnly? to) : EffectiveDatedEntity(from, to);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public void ValidOn_PicksTheSingleRecordInForceOnTheDate()
    {
        var jan = new Rate(D(1, 1), D(3, 31));
        var apr = new Rate(D(4, 1), D(6, 30));
        var jul = new Rate(D(7, 1), null);
        var all = new[] { jan, apr, jul }.AsQueryable();

        all.ValidOn(D(3, 31)).Should().ContainSingle().Which.Should().BeSameAs(jan);
        all.ValidOn(D(4, 1)).Should().ContainSingle().Which.Should().BeSameAs(apr);
        all.ValidOn(D(12, 31)).Should().ContainSingle().Which.Should().BeSameAs(jul);
        all.ValidOn(D(1, 1).AddDays(-1)).Should().BeEmpty();
    }

    [Fact]
    public void Ctor_EndBeforeStart_Throws() =>
        FluentActions.Invoking(() => new Rate(D(5, 2), D(5, 1))).Should().Throw<ArgumentException>();

    [Fact]
    public void EndOn_OpenRecord_ClosesItAndKeepsTheStart()
    {
        var rate = new Rate(D(1, 1), null);

        rate.EndOn(D(6, 30));

        rate.EffectiveFrom.Should().Be(D(1, 1));
        rate.EffectiveTo.Should().Be(D(6, 30));
        rate.IsEffectiveOn(D(6, 30)).Should().BeTrue();
        rate.IsEffectiveOn(D(7, 1)).Should().BeFalse();
    }

    [Fact]
    public void EndOn_AlreadyClosedRecord_Throws() =>
        FluentActions.Invoking(() => new Rate(D(1, 1), D(2, 1)).EndOn(D(3, 1))).Should().Throw<InvalidOperationException>();

    [Fact]
    public void EndOn_BeforeTheStart_Throws() =>
        FluentActions.Invoking(() => new Rate(D(5, 1), null).EndOn(D(4, 30))).Should().Throw<ArgumentException>();

    [Fact]
    public void HasOverlap_CandidateTouchingAnExistingPeriod_IsDetected()
    {
        var existing = new[] { new DateRange(D(1, 1), D(3, 31)), new DateRange(D(4, 1), null) };

        EffectiveDating.HasOverlap(existing, new DateRange(D(3, 31), D(4, 1))).Should().BeTrue();
        EffectiveDating.HasOverlap(existing.Take(1), new DateRange(D(4, 1), D(4, 30))).Should().BeFalse();
    }
}
