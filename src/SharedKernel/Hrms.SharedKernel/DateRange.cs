namespace Hrms.SharedKernel;

/// <summary>
/// A business-date interval. Both ends are inclusive; an open <see cref="To"/> means "until further notice".
/// This is the shape of effective_from / effective_to on every effective-dated record.
/// </summary>
public readonly record struct DateRange
{
    public DateRange(DateOnly from, DateOnly? to)
    {
        if (to < from)
        {
            throw new ArgumentException("The end of a date range cannot be before its start.", nameof(to));
        }

        From = from;
        To = to;
    }

    public DateOnly From { get; }

    public DateOnly? To { get; }

    public bool Contains(DateOnly date) => date >= From && (To is null || date <= To);

    public bool Overlaps(DateRange other) =>
        From <= (other.To ?? DateOnly.MaxValue) && other.From <= (To ?? DateOnly.MaxValue);
}
