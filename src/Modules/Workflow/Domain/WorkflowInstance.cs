using System.Security.Cryptography;
using System.Text;
using Hrms.Contracts.Workflow;
using Hrms.SharedKernel;

namespace Hrms.Workflow.Domain;

/// <summary>
/// One request going through its stages. Every change to the request or its tasks also records an action on this row
/// (<see cref="Record"/>), which bumps its row_version: two people deciding at the same moment cannot both win, even when they hold
/// different tasks of the same stage.
/// </summary>
public sealed class WorkflowInstance : TenantEntity
{
    public const int ReasonMaxLength = 500;

    private WorkflowInstance(Guid definitionId, int definitionVersion, string workflowKey, string entityType, Guid entityId)
    {
        DefinitionId = definitionId;
        DefinitionVersion = definitionVersion;
        WorkflowKey = workflowKey;
        EntityType = entityType;
        EntityId = entityId;
        PayloadJson = StartFingerprint = string.Empty;
    }

    /// <summary>For the persistence layer only.</summary>
    private WorkflowInstance()
    {
        WorkflowKey = EntityType = PayloadJson = StartFingerprint = string.Empty;
    }

    public Guid DefinitionId { get; private set; }

    public int DefinitionVersion { get; private set; }

    public string WorkflowKey { get; private set; }

    public string EntityType { get; private set; }

    /// <summary>The approval request in the owning module. At most one instance exists for a key and id.</summary>
    public Guid EntityId { get; private set; }

    public Guid SubjectEmployeeId { get; private set; }

    public Guid RequesterUserId { get; private set; }

    public WorkflowStatus Status { get; private set; }

    /// <summary>The stage in progress, or 0 before the first one opens.</summary>
    public int CurrentStage { get; private set; }

    /// <summary>Starts at 1 and goes up on each resubmission; tasks of earlier rounds no longer count.</summary>
    public int Round { get; private set; }

    public string PayloadJson { get; private set; }

    public string? BlockedReason { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    /// <summary>
    /// A hash of what the first start asked for. A repeated start must match it: otherwise changed values could ride on an approval
    /// that was given for the old ones.
    /// </summary>
    public string StartFingerprint { get; private set; }

    /// <summary>The number of events written to the outbox, which numbers them so they are delivered in order.</summary>
    public int EventCount { get; private set; }

    /// <summary>The number of actions recorded so far.</summary>
    public int ActionCount { get; private set; }

    /// <summary>Still going somewhere: pending, blocked or waiting on the requester.</summary>
    public bool IsOpen => Status is WorkflowStatus.Pending or WorkflowStatus.Blocked or WorkflowStatus.SentBack;

    public static WorkflowInstance Start(
        WorkflowDefinition definition, string entityType, Guid entityId, Guid subjectEmployeeId, Guid requesterUserId, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Status != DefinitionStatus.Published)
        {
            throw new ArgumentException("Only a published version can run.", nameof(definition));
        }

        return new WorkflowInstance(definition.Id, definition.Version, definition.WorkflowKey, entityType, entityId)
        {
            SubjectEmployeeId = subjectEmployeeId,
            RequesterUserId = requesterUserId,
            PayloadJson = payloadJson,
            StartFingerprint = Fingerprint(subjectEmployeeId, requesterUserId, payloadJson),
            Status = WorkflowStatus.Pending,
            Round = 1,
        };
    }

    public static string Fingerprint(Guid subjectEmployeeId, Guid requesterUserId, string payloadJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{subjectEmployeeId:N}|{requesterUserId:N}|{payloadJson}")));

    public int NextEventSequence() => ++EventCount;

    /// <summary>Notes that something happened and returns the log entry for it.</summary>
    public WorkflowActionLog Record(
        WorkflowAction action,
        DateTime nowUtc,
        Guid? actorUserId = null,
        Guid? onBehalfOfUserId = null,
        Guid? taskId = null,
        string? comment = null)
    {
        ActionCount++;
        return WorkflowActionLog.Create(Id, ActionCount, CurrentStage, Round, action, nowUtc, actorUserId, onBehalfOfUserId, taskId, comment);
    }

    public void EnterStage(int stageOrder)
    {
        Require(Status is WorkflowStatus.Pending or WorkflowStatus.Blocked);
        Status = WorkflowStatus.Pending;
        CurrentStage = stageOrder;
        BlockedReason = null;
    }

    public void Block(string reason)
    {
        Require(Status is WorkflowStatus.Pending or WorkflowStatus.Blocked);
        Status = WorkflowStatus.Blocked;
        BlockedReason = reason.Length <= ReasonMaxLength ? reason : reason[..ReasonMaxLength];
    }

    public void Approve(DateTime nowUtc) => Complete(WorkflowStatus.Approved, nowUtc);

    public void Reject(DateTime nowUtc) => Complete(WorkflowStatus.Rejected, nowUtc);

    public void SendBack()
    {
        Require(Status == WorkflowStatus.Pending);
        Status = WorkflowStatus.SentBack;
    }

    public void Resubmit(string payloadJson)
    {
        Require(Status == WorkflowStatus.SentBack);
        PayloadJson = payloadJson;
        Status = WorkflowStatus.Pending;
        CurrentStage = 0;
        Round++;
    }

    public void Withdraw(DateTime nowUtc)
    {
        Require(IsOpen);
        Status = WorkflowStatus.Withdrawn;
        BlockedReason = null;
        CompletedAt = nowUtc;
    }

    private void Complete(WorkflowStatus status, DateTime nowUtc)
    {
        Require(Status == WorkflowStatus.Pending);
        Status = status;
        CompletedAt = nowUtc;
    }

    // Callers check the state first and answer with a business error; reaching this with a wrong state is a bug.
    private void Require(bool allowed)
    {
        if (!allowed)
        {
            throw new InvalidOperationException($"That change is not possible while the workflow is {Status}.");
        }
    }
}
