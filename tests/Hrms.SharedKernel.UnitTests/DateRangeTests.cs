using FluentAssertions;

namespace Hrms.SharedKernel.UnitTests;

public class DateRangeTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public void Ctor_EndBeforeStart_Throws() =>
        FluentActions.Invoking(() => new DateRange(D(5, 10), D(5, 9))).Should().Throw<ArgumentException>();

    [Fact]
    public void Ctor_SingleDay_IsValid() => new DateRange(D(5, 10), D(5, 10)).Contains(D(5, 10)).Should().BeTrue();

    [Theory]
    [InlineData(5, 9, false)]
    [InlineData(5, 10, true)]
    [InlineData(5, 20, true)]
    [InlineData(5, 31, true)]
    [InlineData(6, 1, false)]
    public void Contains_ClosedRange_IsInclusiveAtBothEnds(int month, int day, bool expected) =>
        new DateRange(D(5, 10), D(5, 31)).Contains(D(month, day)).Should().Be(expected);

    [Fact]
    public void Contains_OpenEndedRange_ContainsEveryLaterDate()
    {
        var range = new DateRange(D(5, 10), null);

        range.Contains(D(5, 9)).Should().BeFalse();
        range.Contains(DateOnly.MaxValue).Should().BeTrue();
    }

    [Theory]
    [InlineData(1, 1, 1, 31, 2, 1, 2, 28, false)]
    [InlineData(1, 1, 1, 31, 1, 31, 2, 28, true)]
    [InlineData(1, 1, 3, 1, 2, 1, 2, 28, true)]
    [InlineData(2, 1, 2, 28, 1, 1, 3, 1, true)]
    [InlineData(1, 1, 1, 31, 1, 15, 1, 20, true)]
    public void Overlaps_ClosedRanges_DetectsSharedDays(int m1, int d1, int m2, int d2, int m3, int d3, int m4, int d4, bool expected) =>
        new DateRange(D(m1, d1), D(m2, d2)).Overlaps(new DateRange(D(m3, d3), D(m4, d4))).Should().Be(expected);

    [Fact]
    public void Overlaps_OpenEndedAgainstLaterRange_Overlaps() =>
        new DateRange(D(1, 1), null).Overlaps(new DateRange(D(12, 1), D(12, 31))).Should().BeTrue();

    [Fact]
    public void Overlaps_ClosedRangeEndingTheDayBeforeAnOpenRange_DoesNotOverlap() =>
        new DateRange(D(1, 1), D(1, 31)).Overlaps(new DateRange(D(2, 1), null)).Should().BeFalse();
}
