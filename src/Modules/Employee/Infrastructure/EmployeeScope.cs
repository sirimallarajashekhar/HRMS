using Hrms.Contracts;
using Hrms.Employee.Domain;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.Persistence.Sql;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Employee.Infrastructure;

/// <summary>
/// Turns data-scope grants into employee ids, completing the data-scope service of P0.4 (this replaces its self-only default). What each
/// scope means, judged on today's job rows: Self is the user's own record; DirectReports and AllReports follow the manager link and count
/// only people employed today (a leaver leaves their manager's scope); Department, Location and LegalEntity match the current job (a
/// department includes the units below it) and keep people who have left, so HR can still find them. A scope wider than
/// <see cref="MaxScopeSize"/> is refused loudly instead of silently truncated.
/// </summary>
internal sealed class EmployeeScopeExpander(
    EmployeeDbContext db,
    IOrgStructureLookup org,
    TenantDbConnectionFactory connections,
    TimeProvider time) : IEmployeeScopeExpander
{
    public const int MaxScopeSize = 5_000;

    private const string ReportsOfSql = """
        WITH RECURSIVE chain (id) AS (
            SELECT j.employee_id
            FROM employee_job j
            WHERE j.tenant_id = @TenantId AND j.is_deleted = 0 AND j.manager_id = @Manager
              AND j.effective_from <= @Today AND (j.effective_to IS NULL OR j.effective_to >= @Today)
            UNION
            SELECT j.employee_id
            FROM employee_job j
            JOIN chain c ON j.manager_id = c.id
            WHERE j.tenant_id = @TenantId AND j.is_deleted = 0
              AND j.effective_from <= @Today AND (j.effective_to IS NULL OR j.effective_to >= @Today)
        )
        SELECT e.id
        FROM chain c
        JOIN employee e ON e.id = c.id AND e.tenant_id = @TenantId AND e.is_deleted = 0
          AND e.joining_date <= @Today AND (e.last_working_day IS NULL OR e.last_working_day >= @Today)
        LIMIT @Limit
        """;

    public async Task<IReadOnlySet<Guid>> ExpandAsync(Guid? employeeId, IReadOnlyList<DataScopeGrant> grants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var ids = new HashSet<Guid>();

        foreach (var grant in grants)
        {
            switch (grant.Kind)
            {
                case ScopeKind.Tenant:
                    return (await db.Employees.AsNoTracking().Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();

                case ScopeKind.Self:
                    if (employeeId is { } own)
                    {
                        ids.Add(own);
                    }

                    break;

                case ScopeKind.DirectReports when employeeId is { } manager:
                    ids.UnionWith(await DirectReportsAsync(manager, today, cancellationToken));
                    break;

                case ScopeKind.AllReports when employeeId is { } top:
                    await using (var connection = await connections.OpenAsync(cancellationToken))
                    {
                        ids.UnionWith(await connection.QueryAsync<Guid>(
                            ReportsOfSql, new { Manager = top, Today = today, Limit = MaxScopeSize + 1 }, cancellationToken));
                    }

                    break;

                case ScopeKind.Department when grant.Value is { } unit:
                    var subtree = await org.GetOrgUnitSubtreeAsync(unit, today, cancellationToken);
                    ids.UnionWith(await CurrentJobMatchAsync(j => subtree.Contains(j.OrgUnitId), today, cancellationToken));
                    break;

                case ScopeKind.Location when grant.Value is { } location:
                    ids.UnionWith(await CurrentJobMatchAsync(j => j.LocationId == location, today, cancellationToken));
                    break;

                case ScopeKind.LegalEntity when grant.Value is { } entity:
                    ids.UnionWith(await CurrentJobMatchAsync(j => j.LegalEntityId == entity, today, cancellationToken));
                    break;

                case ScopeKind.DirectReports or ScopeKind.AllReports:
                    break; // the account is not linked to an employee, so there is no one to report to them

                default:
                    break; // a scope that needs a value but has none matches nobody (deny by default)
            }

            if (ids.Count > MaxScopeSize)
            {
                throw new ScopeTooLargeException(MaxScopeSize);
            }
        }

        return ids;
    }

    private async Task<List<Guid>> DirectReportsAsync(Guid manager, DateOnly today, CancellationToken cancellationToken) =>
        await (
                from job in db.Jobs.AsNoTracking().ValidOn(today)
                join employee in db.Employees.AsNoTracking() on job.EmployeeId equals employee.Id
                where job.ManagerId == manager && employee.JoiningDate <= today && (employee.LastWorkingDay == null || employee.LastWorkingDay >= today)
                select employee.Id)
            .Take(MaxScopeSize + 1)
            .ToListAsync(cancellationToken);

    private async Task<List<Guid>> CurrentJobMatchAsync(System.Linq.Expressions.Expression<Func<EmployeeJob, bool>> match, DateOnly today, CancellationToken cancellationToken) =>
        await db.Jobs.AsNoTracking().ValidOn(today).Where(match).Select(j => j.EmployeeId).Distinct().Take(MaxScopeSize + 1).ToListAsync(cancellationToken);
}

/// <summary>Who the caller may reach for one permission: everyone, or a set of employees.</summary>
internal sealed record Visibility(bool All, IReadOnlySet<Guid> Ids)
{
    public bool Includes(Guid employeeId) => All || Ids.Contains(employeeId);
}

/// <summary>
/// The one place the employee services ask "may the caller see this employee for this permission". An employee outside the scope is
/// reported as not found, never as forbidden, so the answer does not reveal who exists.
/// </summary>
internal sealed class EmployeeAccess(IDataScopeService scopes, ICurrentUser user, EmployeeDbContext db)
{
    public async Task<Result<Visibility>> VisibilityAsync(string permission, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Result.Failure<Visibility>(Error.Forbidden("employee.access.no_user", "Employee data is only available to signed-in users."));
        }

        var scope = await scopes.ResolveAsync(userId, permission, cancellationToken);
        if (scope.IsEmpty)
        {
            return Result.Success(new Visibility(false, new HashSet<Guid>()));
        }

        if (scope.IsTenantWide)
        {
            return Result.Success(new Visibility(true, new HashSet<Guid>()));
        }

        try
        {
            return Result.Success(new Visibility(false, await scopes.GetVisibleEmployeeIdsAsync(userId, permission, cancellationToken)));
        }
        catch (ScopeTooLargeException)
        {
            return Result.Failure<Visibility>(Error.Forbidden("employee.scope.too_large", "The role's data scope is too wide to apply; ask for a tenant-wide scope for this role."));
        }
    }

    /// <summary>True when the caller may create employees in that legal entity: a tenant-wide scope, or a legal-entity scope for it.</summary>
    public async Task<Result<bool>> CanCreateAsync(string permission, Guid legalEntityId, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return Result.Failure<bool>(Error.Forbidden("employee.access.no_user", "Employee data is only available to signed-in users."));
        }

        var scope = await scopes.ResolveAsync(userId, permission, cancellationToken);
        return Result.Success(scope.IsTenantWide || scope.Grants.Any(g => g.Kind == ScopeKind.LegalEntity && g.Value == legalEntityId));
    }

    public static IQueryable<EmployeeRecord> Apply(IQueryable<EmployeeRecord> employees, Visibility visibility)
    {
        if (visibility.All)
        {
            return employees;
        }

        var ids = visibility.Ids;
        return employees.Where(e => ids.Contains(e.Id));
    }

    /// <summary>Loads one employee the caller may reach for the permission (tracked when it will be changed), or NotFound.</summary>
    public async Task<Result<EmployeeRecord>> LoadAsync(Guid employeeId, string permission, bool tracked, CancellationToken cancellationToken)
    {
        var visibility = await VisibilityAsync(permission, cancellationToken);
        if (visibility.IsFailure)
        {
            return Result.Failure<EmployeeRecord>(visibility.Error);
        }

        if (!visibility.Value.Includes(employeeId))
        {
            return Result.Failure<EmployeeRecord>(NotFound());
        }

        var query = tracked ? db.Employees.AsQueryable() : db.Employees.AsNoTracking();
        var employee = await query.SingleOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        return employee is null ? Result.Failure<EmployeeRecord>(NotFound()) : Result.Success(employee);
    }

    public static Error NotFound() => Error.NotFound("employee.employee.not_found", "The employee does not exist.");
}
