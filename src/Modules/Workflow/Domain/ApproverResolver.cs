using Hrms.Contracts;

namespace Hrms.Workflow.Domain;

/// <summary>Who the request is about and who raised it: the two people an approver rule is relative to and must exclude.</summary>
public sealed record ApprovalSubject(Guid SubjectEmployeeId, Guid RequesterUserId)
{
    /// <summary>
    /// The rule that nothing may bypass: neither the requester nor any account of the employee the request is about may approve it,
    /// whether as assignee, delegate or the target of a reassignment.
    /// </summary>
    public bool Bars(ApproverAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.UserId == RequesterUserId || account.EmployeeId == SubjectEmployeeId;
    }
}

/// <summary>The approvers a rule produced, or why it produced none.</summary>
public sealed record ApproverResolution(IReadOnlyList<Guid> UserIds, string? Reason)
{
    public bool IsResolved => UserIds.Count > 0;

    public static ApproverResolution Of(params Guid[] userIds) => new(userIds, null);

    public static ApproverResolution None(string reason) => new([], reason);
}

/// <summary>
/// Turns an approver rule into user accounts, as the organisation stands on the day asked. It never substitutes someone the rule did not
/// ask for: when the named person cannot act, the answer is "nobody", and the stage's fallback or an administrator takes over.
/// </summary>
public sealed class ApproverResolver(IEmployeePositionLookup positions, IOrgUnitHeadLookup unitHeads, IApproverDirectory directory)
{
    /// <summary>A rule that matches more people than this is treated as unusable rather than flooding that many inboxes.</summary>
    public const int MaxApprovers = 50;

    public async Task<ApproverResolution> ResolveAsync(
        ApproverRule rule,
        ApprovalSubject subject,
        IReadOnlyDictionary<string, object> payload,
        DateOnly asOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(payload);

        switch (rule.Type)
        {
            case ApproverType.ManagerLevel:
                return await ManagerAsync(rule.Level!.Value, subject, asOf, cancellationToken);
            case ApproverType.UnitHead:
                return await UnitHeadAsync(rule.OrgUnitTypeId, subject, asOf, cancellationToken);
            case ApproverType.RoleAtLocation:
                return await RoleAtLocationAsync(rule.RoleCode!, subject, asOf, cancellationToken);
            case ApproverType.NamedUser:
                return await UserAsync(rule.UserId!.Value, subject, cancellationToken);
            case ApproverType.PayloadUser:
                return payload.TryGetValue(rule.Field!, out var value) && value is Guid userId
                    ? await UserAsync(userId, subject, cancellationToken)
                    : ApproverResolution.None($"The request names nobody in '{rule.Field}'.");
            default:
                return ApproverResolution.None("The approver rule is of an unknown type.");
        }
    }

    private async Task<ApproverResolution> ManagerAsync(int level, ApprovalSubject subject, DateOnly asOf, CancellationToken cancellationToken)
    {
        var employeeId = subject.SubjectEmployeeId;
        for (var step = 1; step <= level; step++)
        {
            var position = await positions.GetPositionAsync(employeeId, asOf, cancellationToken);
            if (position?.ManagerId is not { } managerId)
            {
                return ApproverResolution.None($"There is no manager at level {level}.");
            }

            employeeId = managerId;
        }

        return await EmployeeAsync(employeeId, $"The manager at level {level}", subject, cancellationToken);
    }

    private async Task<ApproverResolution> UnitHeadAsync(Guid? unitTypeId, ApprovalSubject subject, DateOnly asOf, CancellationToken cancellationToken)
    {
        var position = await positions.GetPositionAsync(subject.SubjectEmployeeId, asOf, cancellationToken);
        if (position is null)
        {
            return ApproverResolution.None("The employee has no position on this date.");
        }

        var chain = await unitHeads.GetHeadChainAsync(position.OrgUnitId, asOf, cancellationToken);
        if (unitTypeId is { } wanted)
        {
            var unit = chain.FirstOrDefault(link => link.OrgUnitTypeId == wanted);
            return unit?.HeadEmployeeId is { } head
                ? await EmployeeAsync(head, "The head of that unit", subject, cancellationToken)
                : ApproverResolution.None("There is no unit of that type with a head above the employee.");
        }

        foreach (var link in chain)
        {
            if (link.HeadEmployeeId is not { } head)
            {
                continue;
            }

            var account = await directory.FindByEmployeeAsync(head, cancellationToken);

            // Only people barred from approving are passed over (someone heading their own unit, or a requester who is the head). A head
            // who simply has no usable account is a gap to fix, not a reason to go over their head.
            if (head == subject.SubjectEmployeeId || (account is not null && subject.Bars(account)))
            {
                continue;
            }

            return account is { IsActive: true }
                ? ApproverResolution.Of(account.UserId)
                : ApproverResolution.None("The unit head has no active user account.");
        }

        return ApproverResolution.None("No unit above the employee has a head who may approve.");
    }

    private async Task<ApproverResolution> RoleAtLocationAsync(string roleCode, ApprovalSubject subject, DateOnly asOf, CancellationToken cancellationToken)
    {
        var position = await positions.GetPositionAsync(subject.SubjectEmployeeId, asOf, cancellationToken);
        if (position is null)
        {
            return ApproverResolution.None("The employee has no position on this date.");
        }

        // Two over the cap: up to two matches may be the barred people.
        var found = await directory.FindWithRoleAtLocationAsync(roleCode, position.LocationId, MaxApprovers + 3, cancellationToken);
        var eligible = found.Where(a => a.IsActive && !subject.Bars(a)).Select(a => a.UserId).Distinct().ToArray();
        return eligible.Length switch
        {
            0 => ApproverResolution.None($"Nobody who may approve holds the role '{roleCode}' at the employee's location."),
            > MaxApprovers => ApproverResolution.None($"More than {MaxApprovers} people hold the role '{roleCode}' at that location."),
            _ => ApproverResolution.Of(eligible),
        };
    }

    private async Task<ApproverResolution> EmployeeAsync(Guid employeeId, string who, ApprovalSubject subject, CancellationToken cancellationToken)
    {
        var account = await directory.FindByEmployeeAsync(employeeId, cancellationToken);
        if (account is not { IsActive: true })
        {
            return ApproverResolution.None($"{who} has no active user account.");
        }

        return subject.Bars(account)
            ? ApproverResolution.None($"{who} may not approve their own request.")
            : ApproverResolution.Of(account.UserId);
    }

    private async Task<ApproverResolution> UserAsync(Guid userId, ApprovalSubject subject, CancellationToken cancellationToken)
    {
        var accounts = await directory.GetAccountsAsync([userId], cancellationToken);
        var account = accounts.Count > 0 ? accounts[0] : null;
        if (account is not { IsActive: true })
        {
            return ApproverResolution.None("The chosen user does not exist or is not active.");
        }

        return subject.Bars(account)
            ? ApproverResolution.None("The chosen user may not approve their own request.")
            : ApproverResolution.Of(account.UserId);
    }
}
