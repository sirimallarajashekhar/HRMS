using Hrms.Contracts;
using Hrms.SharedKernel;
using Hrms.Workflow.Application;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Workflow.Infrastructure;

internal sealed class DelegationService(
    WorkflowDbContext db,
    WorkflowAccess access,
    WorkflowScopes scopes,
    WorkflowKeyCatalogue catalogue,
    IApproverDirectory directory,
    TimeProvider time) : IDelegationService
{
    public async Task<PagedList<DelegationView>> ListAsync(Guid userId, PageRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = db.Delegations.AsNoTracking().Where(d => d.DelegatorUserId == userId || d.DelegateUserId == userId);
        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(d => d.EndsOn).ThenByDescending(d => d.Id)
            .Skip(paging.Skip).Take(paging.Size)
            .ToListAsync(cancellationToken);
        return new PagedList<DelegationView>(rows.ConvertAll(Views.Delegation), paging.Number, paging.Size, total);
    }

    public async Task<Result<DelegationView>> CreateAsync(Guid userId, CreateDelegationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await access.ActiveAccountAsync(userId, cancellationToken) is null)
        {
            return Fail(Error.Forbidden("workflow.user_required", "This needs an active user."));
        }

        var delegator = request.DelegatorUserId ?? userId;
        if (delegator != userId)
        {
            var allowed = await MayManageAsync(userId, delegator, cancellationToken);
            if (allowed.IsFailure)
            {
                return Fail(allowed.Error);
            }

            // Otherwise an administrator could hand themselves anyone's approvals.
            if (request.DelegateUserId == userId)
            {
                return Fail(Error.Forbidden("workflow.delegation.self_grant", "You cannot delegate someone else's approvals to yourself."));
            }
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var delegation = Delegation.Create(delegator, request.DelegateUserId, request.WorkflowKey?.Trim(), request.StartsOn, request.EndsOn, today);
        if (delegation.IsFailure)
        {
            return Fail(delegation.Error);
        }

        if (delegation.Value.WorkflowKey.Length > 0 && catalogue.Find(delegation.Value.WorkflowKey) is null)
        {
            return Fail(Error.Validation("workflow.key.unknown", "That workflow does not exist."));
        }

        if (await access.ActiveAccountAsync(request.DelegateUserId, cancellationToken) is null)
        {
            return Fail(Error.Validation("workflow.delegation.delegate_not_found", "The delegate does not exist or is not active."));
        }

        db.Delegations.Add(delegation.Value);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(Views.Delegation(delegation.Value));
    }

    public async Task<Result<DelegationView>> RevokeAsync(Guid userId, Guid delegationId, CancellationToken cancellationToken)
    {
        var delegation = await db.Delegations.SingleOrDefaultAsync(d => d.Id == delegationId, cancellationToken);
        if (delegation is null || await access.ActiveAccountAsync(userId, cancellationToken) is null)
        {
            return Fail(NotFound());
        }

        // Either side may end it; anyone else needs the permission, for a delegator in their scope.
        if (delegation.DelegatorUserId != userId && delegation.DelegateUserId != userId
            && (await MayManageAsync(userId, delegation.DelegatorUserId, cancellationToken)).IsFailure)
        {
            return Fail(NotFound());
        }

        var revoked = delegation.Revoke(time.GetUtcNow().UtcDateTime);
        if (revoked.IsFailure)
        {
            return Fail(revoked.Error);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            return Fail(Errors.Changed());
        }

        return Result.Success(Views.Delegation(delegation));

        static Error NotFound() => Error.NotFound("workflow.delegation.not_found", "The delegation does not exist.");
    }

    private async Task<Result> MayManageAsync(Guid userId, Guid delegatorUserId, CancellationToken cancellationToken)
    {
        var scope = await scopes.ForAsync(userId, Permissions.Workflow.DelegationManage, cancellationToken);
        if (scope.IsFailure)
        {
            return scope;
        }

        var denied = Result.Failure(Error.Forbidden("workflow.delegation.not_allowed", "You cannot manage that person's delegations."));
        if (scope.Value.Everyone)
        {
            var known = await directory.GetAccountsAsync([delegatorUserId], cancellationToken);
            return known.Count == 1 ? Result.Success() : denied;
        }

        var accounts = await directory.GetAccountsAsync([delegatorUserId], cancellationToken);
        return accounts.Count == 1 && accounts[0].EmployeeId is { } employee && scope.Value.Covers(employee) ? Result.Success() : denied;
    }

    private static Result<DelegationView> Fail(Error error) => Result.Failure<DelegationView>(error);
}
