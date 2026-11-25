namespace Hrms.SharedKernel;

/// <summary>
/// A tenant record with a validity period. History is never overwritten: a change closes the current row
/// with <see cref="EndOn"/> and adds a new row that starts the next day.
/// </summary>
public abstract class EffectiveDatedEntity : TenantEntity
{
    protected EffectiveDatedEntity(DateOnly effectiveFrom, DateOnly? effectiveTo = null)
    {
        _ = new DateRange(effectiveFrom, effectiveTo); // validates the order
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
    }

    /// <summary>For the persistence layer only.</summary>
    protected EffectiveDatedEntity()
    {
    }

    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>Last day the record is valid (inclusive); null while it is open-ended.</summary>
    public DateOnly? EffectiveTo { get; private set; }

    public DateRange Period => new(EffectiveFrom, EffectiveTo);

    public bool IsEffectiveOn(DateOnly date) => Period.Contains(date);

    /// <summary>Closes an open record on the given last valid day.</summary>
    public void EndOn(DateOnly effectiveTo)
    {
        if (EffectiveTo is not null)
        {
            throw new InvalidOperationException("The record is already closed.");
        }

        _ = new DateRange(EffectiveFrom, effectiveTo); // validates the order
        EffectiveTo = effectiveTo;
    }
}
