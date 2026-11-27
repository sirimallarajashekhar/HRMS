using FluentAssertions;
using Hrms.Persistence.Auditing;

namespace Hrms.Core.UnitTests;

public class AuditPartitionPlanTests
{
    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2028-01-01", "2026-12-31", 12, 0)] // covered through December 2027, which is exactly twelve months ahead
    [InlineData("2028-01-01", "2027-01-01", 12, 1)] // the first day of a new month needs one more
    [InlineData("2028-01-01", "2027-01-31", 12, 1)] // the day of the month makes no difference
    [InlineData("2029-01-01", "2027-01-15", 12, 0)] // already further ahead than required
    [InlineData("2026-12-01", "2026-11-30", 1, 1)]
    public void Missing_CoversTheCurrentMonthAndTheMonthsAhead(string coveredUpTo, string today, int monthsAhead, int expected) =>
        AuditPartitionPlan.Missing(Date(coveredUpTo), Date(today), monthsAhead).Should().HaveCount(expected);

    [Fact]
    public void Missing_NamesEachPartitionForTheMonthItHolds_AcrossAYearEnd()
    {
        var missing = AuditPartitionPlan.Missing(Date("2027-12-01"), Date("2027-12-10"), 1);

        missing.Should().Equal(
            new AuditPartition("p202712", Date("2028-01-01")),
            new AuditPartition("p202801", Date("2028-02-01")));
    }

    [Fact]
    public void Missing_WhenTheTableFellBehind_FillsEveryMonthWithoutAGap()
    {
        var missing = AuditPartitionPlan.Missing(Date("2028-01-01"), Date("2029-03-05"), 2);

        missing[0].Name.Should().Be("p202801");
        missing[^1].LessThan.Should().Be(Date("2029-06-01"));
        missing.Zip(missing.Skip(1)).Should().AllSatisfy(pair => pair.Second.LessThan.Should().Be(pair.First.LessThan.AddMonths(1)));
    }

    [Fact]
    public void Missing_WhenTheLastBoundIsNotAMonthStart_Refuses() =>
        FluentActions.Invoking(() => AuditPartitionPlan.Missing(Date("2028-01-15"), Date("2027-01-01"), 12))
            .Should().Throw<ArgumentException>();
}
