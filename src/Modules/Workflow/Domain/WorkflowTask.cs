using Hrms.SharedKernel;

namespace Hrms.Workflow.Domain;

public enum WorkflowTaskStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    SentBack = 4,

    /// <summary>Someone else's decision settled the stage first.</summary>
    Superseded = 5,

    /// <summary>Taken away: the request was withdrawn, or the task was reassigned or escalated.</summary>
    Cancelled = 6,
}

public enum TaskOrigin
{
    Rule = 1,
    Fallback = 2,
    Escalation = 3,
    Reassignment = 4,
}

/// <summary>One person's part in one stage. It is decided once.</summary>
public sealed class WorkflowTask : TenantEntity
{
    private WorkflowTask(Guid instanceId, int stageOrder, int round, Guid assigneeUserId, TaskOrigin origin, DateTime? dueAt)
    {
        InstanceId = instanceId;
        StageOrder = stageOrder;
        Round = round;
        AssigneeUserId = assigneeUserId;
        Origin = origin;
        DueAt = dueAt;
        Status = WorkflowTaskStatus.Pending;
    }

    /// <summary>For the persistence layer only.</summary>
    private WorkflowTask()
    {
    }

    public Guid InstanceId { get; private set; }

    public int StageOrder { get; private set; }

    public int Round { get; private set; }

    public Guid AssigneeUserId { get; private set; }

    public TaskOrigin Origin { get; private set; }

    public WorkflowTaskStatus Status { get; private set; }

    public DateTime? DueAt { get; private set; }

    /// <summary>When the deadline action ran. It runs once per task.</summary>
    public DateTime? OverdueHandledAt { get; private set; }

    /// <summary>Set when the deadline passed and the assignee's account could no longer act.</summary>
    public bool AssigneeUnavailable { get; private set; }

    /// <summary>Who actually decided (the assignee or their delegate); null when the system did.</summary>
    public Guid? DecidedByUserId { get; private set; }

    public DateTime? DecidedAt { get; private set; }

    public static WorkflowTask Create(
        Guid instanceId, int stageOrder, int round, Guid assigneeUserId, TaskOrigin origin, int? slaHours, DateTime nowUtc) =>
        new(instanceId, stageOrder, round, assigneeUserId, origin, slaHours is { } hours ? nowUtc.AddHours(hours) : null);

    public void Decide(WorkflowTaskStatus decision, Guid? decidedByUserId, DateTime nowUtc)
    {
        if (decision is not (WorkflowTaskStatus.Approved or WorkflowTaskStatus.Rejected or WorkflowTaskStatus.SentBack))
        {
            throw new ArgumentException("A decision is approve, reject or send back.", nameof(decision));
        }

        Close(decision, nowUtc);
        DecidedByUserId = decidedByUserId;
    }

    public void Supersede(DateTime nowUtc) => Close(WorkflowTaskStatus.Superseded, nowUtc);

    public void Cancel(DateTime nowUtc) => Close(WorkflowTaskStatus.Cancelled, nowUtc);

    public void MarkOverdueHandled(DateTime nowUtc, bool assigneeUnavailable)
    {
        OverdueHandledAt = nowUtc;
        AssigneeUnavailable = assigneeUnavailable;
    }

    private void Close(WorkflowTaskStatus status, DateTime nowUtc)
    {
        if (Status != WorkflowTaskStatus.Pending)
        {
            throw new InvalidOperationException($"The task is already {Status}.");
        }

        Status = status;
        DecidedAt = nowUtc;
    }
}

public enum StageResult
{
    Waiting = 1,
    Approved = 2,
    Rejected = 3,
}

public static class StageRules
{
    /// <summary>
    /// Where a stage stands, from the tasks of that stage in the current round. Superseded and cancelled tasks do not count, and a stage
    /// with nothing that counts is never approved. Sending back is not a stage result: it ends the round at once.
    /// </summary>
    public static StageResult Evaluate(StageMode mode, IEnumerable<WorkflowTask> stageTasks)
    {
        ArgumentNullException.ThrowIfNull(stageTasks);

        var counted = stageTasks.Where(t => t.Status is not (WorkflowTaskStatus.Superseded or WorkflowTaskStatus.Cancelled)).ToList();
        if (counted.Any(t => t.Status == WorkflowTaskStatus.Rejected))
        {
            return StageResult.Rejected;
        }

        var approved = mode == StageMode.Any
            ? counted.Any(t => t.Status == WorkflowTaskStatus.Approved)
            : counted.Count > 0 && counted.All(t => t.Status == WorkflowTaskStatus.Approved);
        return approved ? StageResult.Approved : StageResult.Waiting;
    }
}
