namespace Hrms.SharedKernel;

public static class EffectiveDating
{
    /// <summary>The record valid on a business date. Translates to SQL (EF Core) and works in memory.</summary>
    public static IQueryable<T> ValidOn<T>(this IQueryable<T> source, DateOnly date)
        where T : EffectiveDatedEntity
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(e => e.EffectiveFrom <= date && (e.EffectiveTo == null || e.EffectiveTo >= date));
    }

    /// <summary>True when the candidate period overlaps any existing period of the same series.</summary>
    public static bool HasOverlap(IEnumerable<DateRange> existing, DateRange candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return existing.Any(period => period.Overlaps(candidate));
    }
}
