using Hrms.SharedKernel;

namespace Hrms.Workflow.Domain;

public enum WorkflowAction
{
    Started = 1,
    StageOpened = 2,
    StageSkipped = 3,
    TaskAssigned = 4,
    Approved = 5,
    Rejected = 6,
    SentBack = 7,
    Commented = 8,
    Withdrawn = 9,
    Resubmitted = 10,
    Blocked = 11,
    Reminded = 12,
    Escalated = 13,
    EscalationFailed = 14,
    TimedOutApproved = 15,
    TimedOutRejected = 16,
    Reassigned = 17,
    Retried = 18,
    Completed = 19,
    AssigneeUnavailable = 20,
}

/// <summary>The history of a request. Rows are only ever added: the type has no way to change one.</summary>
public sealed class WorkflowActionLog : TenantEntity
{
    public const int CommentMaxLength = 1000;

    private WorkflowActionLog()
    {
    }

    public Guid InstanceId { get; private set; }

    /// <summary>1, 2, 3... within the instance: the order things happened in.</summary>
    public int Sequence { get; private set; }

    public int StageOrder { get; private set; }

    public int Round { get; private set; }

    public WorkflowAction Action { get; private set; }

    /// <summary>The person who acted; null for the system.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>Whose task it was, when a delegate acted.</summary>
    public Guid? OnBehalfOfUserId { get; private set; }

    public Guid? TaskId { get; private set; }

    /// <summary>Free text from a person. It is shown to participants and never written to the application log.</summary>
    public string? Comment { get; private set; }

    public DateTime OccurredAt { get; private set; }

    internal static WorkflowActionLog Create(
        Guid instanceId, int sequence, int stageOrder, int round, WorkflowAction action, DateTime nowUtc,
        Guid? actorUserId, Guid? onBehalfOfUserId, Guid? taskId, string? comment) => new()
        {
            InstanceId = instanceId,
            Sequence = sequence,
            StageOrder = stageOrder,
            Round = round,
            Action = action,
            OccurredAt = nowUtc,
            ActorUserId = actorUserId,
            OnBehalfOfUserId = onBehalfOfUserId,
            TaskId = taskId,
            Comment = comment,
        };

    /// <summary>Trims a comment; empty becomes null.</summary>
    public static Result<string> CleanComment(string? comment)
    {
        var trimmed = comment?.Trim() ?? string.Empty;
        return trimmed.Length > CommentMaxLength
            ? Result.Failure<string>(Error.Validation("workflow.comment.too_long", $"A comment is at most {CommentMaxLength} characters."))
            : Result.Success(trimmed);
    }
}

/// <summary>
/// Lets one user act on another's approval tasks for a while. It is looked up when a task is shown or acted on, so it covers tasks that
/// already exist and stops by itself. It is not passed on: a delegate's own delegate gets nothing.
/// </summary>
public sealed class Delegation : TenantEntity
{
    public const int MaxDays = 90;

    private Delegation(Guid delegatorUserId, Guid delegateUserId, string workflowKey, DateOnly startsOn, DateOnly endsOn)
    {
        DelegatorUserId = delegatorUserId;
        DelegateUserId = delegateUserId;
        WorkflowKey = workflowKey;
        StartsOn = startsOn;
        EndsOn = endsOn;
    }

    /// <summary>For the persistence layer only.</summary>
    private Delegation()
    {
        WorkflowKey = string.Empty;
    }

    public Guid DelegatorUserId { get; private set; }

    public Guid DelegateUserId { get; private set; }

    /// <summary>One workflow key, or empty for all of them.</summary>
    public string WorkflowKey { get; private set; }

    public DateOnly StartsOn { get; private set; }

    /// <summary>The last day it works (inclusive).</summary>
    public DateOnly EndsOn { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public static Result<Delegation> Create(
        Guid delegatorUserId, Guid delegateUserId, string? workflowKey, DateOnly startsOn, DateOnly endsOn, DateOnly today)
    {
        if (delegatorUserId == Guid.Empty || delegateUserId == Guid.Empty || delegatorUserId == delegateUserId)
        {
            return Fail("workflow.delegation.people_invalid", "A delegation goes from one user to another.");
        }

        if (endsOn < startsOn || endsOn < today)
        {
            return Fail("workflow.delegation.dates_invalid", "A delegation ends on or after the day it starts, and not in the past.");
        }

        return endsOn.DayNumber - startsOn.DayNumber + 1 > MaxDays
            ? Fail("workflow.delegation.too_long", $"A delegation lasts at most {MaxDays} days.")
            : Result.Success(new Delegation(delegatorUserId, delegateUserId, workflowKey ?? string.Empty, startsOn, endsOn));
    }

    public bool IsActive(DateOnly date) => RevokedAt is null && StartsOn <= date && EndsOn >= date;

    public bool Covers(string workflowKey) => WorkflowKey.Length == 0 || WorkflowKey == workflowKey;

    public Result Revoke(DateTime nowUtc)
    {
        if (RevokedAt is not null)
        {
            return Result.Failure(Error.Conflict("workflow.delegation.already_revoked", "The delegation is already revoked."));
        }

        RevokedAt = nowUtc;
        return Result.Success();
    }

    private static Result<Delegation> Fail(string code, string message) => Result.Failure<Delegation>(Error.Validation(code, message));
}

/// <summary>
/// An event waiting to be delivered, written in the same transaction as the change. Messages of one instance are delivered in order:
/// a later one waits while an earlier one is still undelivered.
/// </summary>
public sealed class WorkflowOutboxMessage : TenantEntity
{
    public const int MaxAttempts = 10;
    public const int ErrorMaxLength = 200;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    private WorkflowOutboxMessage()
    {
        EventType = Payload = string.Empty;
    }

    public Guid InstanceId { get; private set; }

    /// <summary>The instance's action count when this was written; it orders the messages of one instance.</summary>
    public int Sequence { get; private set; }

    public string EventType { get; private set; }

    public string Payload { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Given up on until an administrator retries it.</summary>
    public bool IsExhausted => ProcessedAt is null && Attempts >= MaxAttempts;

    public static WorkflowOutboxMessage Create(Guid instanceId, int sequence, string eventType, string payload, DateTime occurredAt) => new()
    {
        InstanceId = instanceId,
        Sequence = sequence,
        EventType = eventType,
        Payload = payload,
        OccurredAt = occurredAt,
        NextAttemptAt = occurredAt,
    };

    public void MarkProcessed(DateTime nowUtc) => ProcessedAt = nowUtc;

    /// <summary>Waits twice as long after each failure, from one minute up to an hour.</summary>
    public void MarkFailed(string error, DateTime nowUtc)
    {
        Attempts++;
        LastError = error.Length <= ErrorMaxLength ? error : error[..ErrorMaxLength];
        var delay = TimeSpan.FromMinutes(Math.Pow(2, Attempts - 1));
        NextAttemptAt = nowUtc + (delay < MaxDelay ? delay : MaxDelay);
    }

    public Result Retry(DateTime nowUtc)
    {
        if (!IsExhausted)
        {
            return Result.Failure(Error.Conflict("workflow.delivery.not_failed", "Only a delivery that was given up on can be retried."));
        }

        Attempts = 0;
        NextAttemptAt = nowUtc;
        return Result.Success();
    }
}
