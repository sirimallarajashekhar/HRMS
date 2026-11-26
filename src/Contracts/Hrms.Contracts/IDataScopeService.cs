namespace Hrms.Contracts;

/// <summary>
/// Answers "whose data may this user see for this permission". The identity module resolves the user role scopes; turning a scope
/// into employees needs the employee table, which arrives in P0.6, so that part sits behind <see cref="IEmployeeScopeExpander"/>.
/// </summary>
public interface IDataScopeService
{
    /// <summary>The scopes the user holds through roles that grant <paramref name="permission"/>; empty for a disabled or unknown user.</summary>
    Task<DataScope> ResolveAsync(Guid userId, string permission, CancellationToken cancellationToken);

    /// <summary>The employee ids the user may see for the permission. Empty when the user holds no scope for it.</summary>
    Task<IReadOnlySet<Guid>> GetVisibleEmployeeIdsAsync(Guid userId, string permission, CancellationToken cancellationToken);
}

/// <summary>
/// Turns scope grants into employee ids. The employee module implements it in P0.6 (the reporting chain is a recursive query over
/// employee_job). Until then the default handles only the Self scope and refuses the rest loudly instead of guessing.
/// </summary>
public interface IEmployeeScopeExpander
{
    /// <param name="employeeId">The employee record linked to the user account, if there is one.</param>
    /// <param name="grants">The scopes to expand; the result is the union of all of them.</param>
    Task<IReadOnlySet<Guid>> ExpandAsync(Guid? employeeId, IReadOnlyList<DataScopeGrant> grants, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when a data scope covers more employees than will be listed in memory. Roles that need that reach use a tenant-wide scope.
/// </summary>
public sealed class ScopeTooLargeException(int limit)
    : InvalidOperationException($"The data scope covers more than {limit} employees; use a tenant-wide scope for roles that need that reach.")
{
    public int Limit { get; } = limit;
}
