using Hrms.SharedKernel;

namespace Hrms.Employee.Domain;

/// <summary>One period of an employee's manager, as plain data so the chain rules can be tested without a database.</summary>
public sealed record ManagerLink(Guid EmployeeId, Guid? ManagerId, DateOnly From, DateOnly? To)
{
    public bool IsValidOn(DateOnly date) => From <= date && (To is null || To >= date);
}

/// <summary>The rules for adding a period to an employee's job history.</summary>
public static class JobTimeline
{
    /// <summary>
    /// Checks a new job period starting on <paramref name="from"/>. The first period starts on the joining date. Later ones must start after
    /// the latest existing period (history is append-only), not before the joining date, and not after the last working day.
    /// </summary>
    public static Result CheckAppend(EmployeeRecord employee, IReadOnlyCollection<DateOnly> existingStarts, DateOnly from)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(existingStarts);

        if (existingStarts.Count == 0)
        {
            return from == employee.JoiningDate
                ? Result.Success()
                : Result.Failure(Validation.Invalid("job", "first_must_start_on_joining", "The first job period starts on the joining date."));
        }

        if (from < employee.JoiningDate)
        {
            return Result.Failure(Validation.Invalid("job", "before_joining", "A job change cannot take effect before the joining date."));
        }

        var latest = existingStarts.Max();
        if (from <= latest)
        {
            return Result.Failure(Validation.Invalid(
                "job",
                "change_not_after_latest",
                $"A job change must take effect after the current arrangement started on {latest:yyyy-MM-dd}; history is never rewritten."));
        }

        if (employee.LastWorkingDay is { } last && from > last)
        {
            return Result.Failure(Validation.Invalid("job", "after_leaving", "A job change cannot take effect after the last working day."));
        }

        return Result.Success();
    }
}

/// <summary>Keeps the reporting line a tree: nobody is, directly or through others, their own manager.</summary>
public static class ManagerChain
{
    /// <summary>
    /// Checks making <paramref name="managerId"/> the manager of <paramref name="employeeId"/> from a date. It walks up from the new manager
    /// and refuses if the walk reaches the employee, on the start date and on every later date where someone in that chain changes manager,
    /// so a change that is safe today cannot become a loop next quarter. <paramref name="loadLinks"/> returns all manager periods of one
    /// employee; each employee on the way up is loaded once.
    /// </summary>
    public static async Task<Result> CheckAsync(
        Guid employeeId,
        Guid? managerId,
        DateOnly from,
        Func<Guid, CancellationToken, Task<IReadOnlyList<ManagerLink>>> loadLinks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loadLinks);

        if (managerId is not { } manager)
        {
            return Result.Success();
        }

        if (manager == employeeId)
        {
            return Cycle("An employee cannot be their own manager.", "manager_self");
        }

        var cache = new Dictionary<Guid, IReadOnlyList<ManagerLink>>();
        var pending = new Queue<DateOnly>();
        var seen = new HashSet<DateOnly>();
        pending.Enqueue(from);
        seen.Add(from);

        while (pending.Count > 0)
        {
            var date = pending.Dequeue();
            var current = (Guid?)manager;
            var steps = 0;

            while (current is { } node)
            {
                if (node == employeeId)
                {
                    return Cycle("That manager would put the employee above themselves in the reporting line.", "manager_cycle");
                }

                if (++steps > 10_000)
                {
                    return Cycle("The reporting line is too deep or already loops.", "manager_cycle");
                }

                if (!cache.TryGetValue(node, out var links))
                {
                    links = await loadLinks(node, cancellationToken);
                    cache[node] = links;
                    foreach (var later in links.Where(l => l.From > from).Select(l => l.From))
                    {
                        if (seen.Add(later))
                        {
                            pending.Enqueue(later);
                        }
                    }
                }

                current = links.FirstOrDefault(l => l.IsValidOn(date))?.ManagerId;
            }
        }

        return Result.Success();
    }

    private static Result Cycle(string message, string reason) => Result.Failure(Validation.Invalid("job", reason, message));
}
