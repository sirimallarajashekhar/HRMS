using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>One period of one unit's parent, as plain data so the tree rules can be tested without a database.</summary>
public sealed record ParentLink(Guid UnitId, Guid? ParentId, DateOnly From, DateOnly? To)
{
    public bool IsValidOn(DateOnly date) => From <= date && (To is null || To >= date);
}

/// <summary>The rules that keep the organisation tree a tree.</summary>
public static class OrgTree
{
    /// <summary>
    /// Checks a move of <paramref name="unitId"/> under <paramref name="newParentId"/> (null = make it a root) from a date.
    /// History is append-only: the move must start after the unit's latest existing period. The tree must stay free of cycles on the
    /// start date and on every later date where any unit's parent changes, so a move cannot be made safe today and break next quarter.
    /// </summary>
    public static Result CheckMove(Guid unitId, Guid? newParentId, DateOnly from, IReadOnlyCollection<ParentLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        var latest = links.Where(l => l.UnitId == unitId).Select(l => (DateOnly?)l.From).Max();
        if (latest is { } start && from <= start)
        {
            return Result.Failure(Error.Validation(
                "core.org_unit.parent_change_not_after_latest",
                $"A move must take effect after the current arrangement started on {start:yyyy-MM-dd}; history is never rewritten."));
        }

        if (newParentId is not { } parent)
        {
            return Result.Success();
        }

        if (parent == unitId)
        {
            return Cycle();
        }

        var checkDates = links.Where(l => l.From > from).Select(l => l.From).Append(from).Distinct();
        foreach (var date in checkDates)
        {
            if (ReachesUnit(unitId, parent, date, links))
            {
                return Cycle();
            }
        }

        return Result.Success();
    }

    private static bool ReachesUnit(Guid unitId, Guid start, DateOnly date, IReadOnlyCollection<ParentLink> links)
    {
        // The moved unit's own earlier periods end the day before the move, so on or after it only the proposed parent counts.
        var parentOf = links.Where(l => l.UnitId != unitId && l.IsValidOn(date)).ToDictionary(l => l.UnitId, l => l.ParentId);
        var current = (Guid?)start;
        var steps = 0;

        while (current is { } node)
        {
            if (node == unitId)
            {
                return true;
            }

            if (++steps > parentOf.Count + 1)
            {
                return true; // an existing loop was found on the way up: refuse rather than walk forever
            }

            current = parentOf.GetValueOrDefault(node);
        }

        return false;
    }

    private static Result Cycle() =>
        Result.Failure(Error.Validation("core.org_unit.cycle", "That move would make the unit its own ancestor."));
}
