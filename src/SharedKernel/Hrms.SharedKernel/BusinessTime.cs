namespace Hrms.SharedKernel;

/// <summary>Time zone conversion lives at the edges: storage and logic use UTC instants and DateOnly business dates.</summary>
public static class BusinessTime
{
    /// <summary>The calendar date an instant falls on in the given (location) time zone.</summary>
    public static DateOnly ToBusinessDate(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }
}
