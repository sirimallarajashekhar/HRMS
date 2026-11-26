using Hrms.Contracts;
using Hrms.SharedKernel;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Workflow.Infrastructure;

/// <summary>The subjects a user may reach for an administrative permission: none, some, or everyone.</summary>
internal sealed record SubjectScope(IReadOnlySet<Guid>? EmployeeIds, bool Everyone)
{
    public static readonly SubjectScope None = new(new HashSet<Guid>(), false);

    public static readonly SubjectScope All = new(null, true);

    public bool Covers(Guid employeeId) => Everyone || EmployeeIds!.Contains(employeeId);
}

/// <summary>The questions every entry point asks before showing or changing anything.</summary>
internal sealed class WorkflowAccess(WorkflowDbContext db, IApproverDirectory directory)
{
    /// <returns>The user's account when it exists in the tenant and is active.</returns>
    public async Task<ApproverAccount?> ActiveAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var accounts = await directory.GetAccountsAsync([userId], cancellationToken);
        return accounts.Count == 1 && accounts[0].IsActive ? accounts[0] : null;
    }

    /// <summary>The delegations that let this user act today, from people whose accounts are still active.</summary>
    public async Task<IReadOnlyList<Delegation>> DelegationsToAsync(Guid userId, DateOnly today, CancellationToken cancellationToken)
    {
        var delegations = await db.Delegations.AsNoTracking()
            .Where(d => d.DelegateUserId == userId && d.RevokedAt == null && d.StartsOn <= today && d.EndsOn >= today)
            .ToListAsync(cancellationToken);
        if (delegations.Count == 0)
        {
            return delegations;
        }

        // One level only: nothing here follows the delegators' own incoming delegations.
        var delegators = await directory.GetAccountsAsync([.. delegations.Select(d => d.DelegatorUserId).Distinct()], cancellationToken);
        var active = delegators.Where(a => a.IsActive).Select(a => a.UserId).ToHashSet();
        return delegations.Where(d => active.Contains(d.DelegatorUserId)).ToList();
    }

    /// <summary>Whether the task is the user's, or one they hold by delegation. Says nothing about whether they are barred.</summary>
    public static bool Holds(Guid userId, IReadOnlyList<Delegation> delegations, WorkflowTask task, string workflowKey) =>
        task.AssigneeUserId == userId || delegations.Any(d => d.DelegatorUserId == task.AssigneeUserId && d.Covers(workflowKey));
}

/// <summary>
/// The data-scope question the administrative endpoints ask. Kept apart so a host without user-facing endpoints (the worker) does not
/// need the data-scope service.
/// </summary>
internal sealed class WorkflowScopes(IDataScopeService dataScopes)
{
    public async Task<Result<SubjectScope>> ForAsync(Guid userId, string permission, CancellationToken cancellationToken)
    {
        var scope = await dataScopes.ResolveAsync(userId, permission, cancellationToken);
        if (scope.IsEmpty)
        {
            return Result.Success(SubjectScope.None);
        }

        if (scope.IsTenantWide)
        {
            return Result.Success(SubjectScope.All);
        }

        try
        {
            var ids = await dataScopes.GetVisibleEmployeeIdsAsync(userId, permission, cancellationToken);
            return Result.Success(new SubjectScope(ids, false));
        }
        catch (ScopeTooLargeException ex)
        {
            return Result.Failure<SubjectScope>(Error.Forbidden("workflow.scope.too_large", ex.Message));
        }
    }
}

/// <summary>The errors more than one service returns.</summary>
internal static class Errors
{
    public static Error TaskNotFound() => Error.NotFound("workflow.task.not_found", "The task does not exist.");

    public static Error InstanceNotFound() => Error.NotFound("workflow.instance.not_found", "The workflow does not exist.");

    public static Error NotPending() => Error.Conflict("workflow.task.not_pending", "The task is no longer waiting for a decision.");

    public static Error OwnRequest() => Error.Forbidden(
        "workflow.task.own_request", "Nobody may approve a request they raised or that is about them.");

    public static Error Changed() => Error.Conflict(
        "workflow.concurrent_change", "The workflow changed while you were acting on it. Reload and try again.");
}
