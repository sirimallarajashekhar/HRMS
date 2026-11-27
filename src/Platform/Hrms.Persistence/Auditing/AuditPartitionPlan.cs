using System.Globalization;

namespace Hrms.Persistence.Auditing;

/// <summary>A monthly partition of audit_log, holding rows that occurred before <see cref="LessThan"/> (UTC).</summary>
public sealed record AuditPartition(string Name, DateOnly LessThan);

/// <summary>Which monthly partitions audit_log still needs.</summary>
public static class AuditPartitionPlan
{
    /// <summary>The catch-all after the last month, so a write never fails for want of a partition.</summary>
    public const string CatchAll = "p_future";

    /// <param name="coveredUpTo">The upper bound of the last monthly partition: the first day of the first month without one.</param>
    /// <param name="today">Today in UTC.</param>
    /// <param name="monthsAhead">How many whole months after the current one must have a partition.</param>
    public static IReadOnlyList<AuditPartition> Missing(DateOnly coveredUpTo, DateOnly today, int monthsAhead)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(monthsAhead);
        if (coveredUpTo.Day != 1)
        {
            // Monthly names would no longer say what a partition holds, so someone has to look at the table first.
            throw new ArgumentException("A monthly partition ends on the first day of a month.", nameof(coveredUpTo));
        }

        var required = new DateOnly(today.Year, today.Month, 1).AddMonths(monthsAhead + 1);
        var missing = new List<AuditPartition>();
        for (var bound = coveredUpTo.AddMonths(1); bound <= required; bound = bound.AddMonths(1))
        {
            missing.Add(new AuditPartition("p" + bound.AddMonths(-1).ToString("yyyyMM", CultureInfo.InvariantCulture), bound));
        }

        return missing;
    }
}
