using System.Text.Json;
using Hrms.SharedKernel;
using Hrms.Workflow.Domain;

namespace Hrms.Workflow.Application;

public sealed record PagedList<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public sealed record PageRequest(int? Page = null, int? PageSize = null)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public int Number => Math.Max(Page ?? 1, 1);

    public int Size => Math.Clamp(PageSize ?? DefaultPageSize, 1, MaxPageSize);

    public int Skip => (Number - 1) * Size;
}

// --- keys and definitions --------------------------------------------------------------------------------------------------

public sealed record WorkflowFieldView(string Name, string Type, bool Required);

public sealed record WorkflowKeyView(string Key, string EntityType, bool AllowAutoApprove, IReadOnlyList<WorkflowFieldView> Fields);

/// <summary>A stage as sent by an administrator. The condition is a JSON object, not a string.</summary>
public sealed record StageRequest(
    string? Name,
    ApproverRuleRequest? Approver,
    ApproverRuleRequest? Fallback = null,
    JsonElement? Condition = null,
    string? Mode = null,
    int? SlaHours = null,
    string? OnTimeout = null,
    ApproverRuleRequest? Escalation = null);

public sealed record ApproverRuleRequest(
    string? Type, int? Level = null, Guid? OrgUnitTypeId = null, string? RoleCode = null, Guid? UserId = null, string? Field = null);

public sealed record CreateDefinitionRequest(string? WorkflowKey, string? Name, IReadOnlyList<StageRequest?>? Stages);

public sealed record UpdateDefinitionRequest(string? Name, IReadOnlyList<StageRequest?>? Stages, long? RowVersion);

/// <summary>Leave the date out to start today (UTC date).</summary>
public sealed record PublishDefinitionRequest(DateOnly? EffectiveFrom, long? RowVersion);

public sealed record StageView(
    int Order,
    string Name,
    ApproverRuleRequest Approver,
    ApproverRuleRequest? Fallback,
    JsonElement? Condition,
    string Mode,
    int? SlaHours,
    string? OnTimeout,
    ApproverRuleRequest? Escalation);

public sealed record DefinitionSummaryView(
    Guid Id, string WorkflowKey, int Version, string Name, string Status, DateOnly? EffectiveFrom, long RowVersion);

public sealed record DefinitionView(
    Guid Id, string WorkflowKey, int Version, string Name, string Status, DateOnly? EffectiveFrom, long RowVersion, IReadOnlyList<StageView> Stages);

public interface IWorkflowDefinitionService
{
    IReadOnlyList<WorkflowKeyView> Keys();

    Task<PagedList<DefinitionSummaryView>> ListAsync(string? workflowKey, PageRequest paging, CancellationToken cancellationToken);

    Task<Result<DefinitionView>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<DefinitionView>> CreateDraftAsync(CreateDefinitionRequest request, CancellationToken cancellationToken);

    Task<Result<DefinitionView>> UpdateDraftAsync(Guid id, UpdateDefinitionRequest request, CancellationToken cancellationToken);

    Task<Result<DefinitionView>> PublishAsync(Guid id, PublishDefinitionRequest request, CancellationToken cancellationToken);
}

// --- tasks and instances ---------------------------------------------------------------------------------------------------

/// <summary><paramref name="OnBehalfOfUserId"/> is set when the task is shown to a delegate.</summary>
public sealed record TaskView(
    Guid Id,
    Guid InstanceId,
    string WorkflowKey,
    string EntityType,
    Guid EntityId,
    Guid SubjectEmployeeId,
    Guid RequesterUserId,
    int Stage,
    string StageName,
    string Status,
    DateTime? DueAt,
    DateTime CreatedAt,
    Guid AssigneeUserId,
    Guid? OnBehalfOfUserId);

public sealed record TaskDetailView(TaskView Task, JsonElement Fields, string InstanceStatus);

/// <summary><paramref name="Action"/> is approve, reject, sendBack or comment.</summary>
public sealed record TaskActionRequest(string? Action, string? Comment = null);

public sealed record InstanceTaskView(Guid Id, int Stage, int Round, Guid AssigneeUserId, string Status, DateTime? DueAt, Guid? DecidedByUserId, DateTime? DecidedAt, bool AssigneeUnavailable);

public sealed record ActionLogView(
    int Sequence, int Stage, int Round, string Action, Guid? ActorUserId, Guid? OnBehalfOfUserId, Guid? TaskId, string? Comment, DateTime OccurredAt);

public sealed record InstanceSummaryView(
    Guid Id,
    string WorkflowKey,
    string EntityType,
    Guid EntityId,
    Guid SubjectEmployeeId,
    Guid RequesterUserId,
    string Status,
    int CurrentStage,
    int Round,
    string? BlockedReason,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    int DefinitionVersion);

public sealed record InstanceView(
    InstanceSummaryView Instance, JsonElement Fields, IReadOnlyList<InstanceTaskView> Tasks, IReadOnlyList<ActionLogView> History);

/// <summary>What a person does with approvals. Every method takes the signed-in user and checks them itself.</summary>
public interface IWorkflowInboxService
{
    /// <param name="done">False: tasks waiting for the user, their own and delegated. True: tasks of theirs, or decided by them, that are closed.</param>
    Task<PagedList<TaskView>> ListTasksAsync(Guid userId, bool done, PageRequest paging, CancellationToken cancellationToken);

    Task<Result<TaskDetailView>> GetTaskAsync(Guid userId, Guid taskId, CancellationToken cancellationToken);

    Task<Result<TaskDetailView>> ActAsync(Guid userId, Guid taskId, TaskActionRequest request, CancellationToken cancellationToken);

    /// <summary>For the requester, anyone who had or has a task, and holders of workflow.instance.view whose scope covers the subject.</summary>
    Task<Result<InstanceView>> GetInstanceAsync(Guid userId, Guid instanceId, CancellationToken cancellationToken);

    Task<Result<InstanceView>> WithdrawAsync(Guid userId, Guid instanceId, string? comment, CancellationToken cancellationToken);
}

// --- delegation ------------------------------------------------------------------------------------------------------------

/// <summary>Leave <paramref name="DelegatorUserId"/> out to delegate your own approvals.</summary>
public sealed record CreateDelegationRequest(
    Guid? DelegatorUserId, Guid DelegateUserId, string? WorkflowKey, DateOnly StartsOn, DateOnly EndsOn);

public sealed record DelegationView(
    Guid Id, Guid DelegatorUserId, Guid DelegateUserId, string? WorkflowKey, DateOnly StartsOn, DateOnly EndsOn, DateTime? RevokedAt);

public interface IDelegationService
{
    /// <summary>The user's delegations, given and received.</summary>
    Task<PagedList<DelegationView>> ListAsync(Guid userId, PageRequest paging, CancellationToken cancellationToken);

    Task<Result<DelegationView>> CreateAsync(Guid userId, CreateDelegationRequest request, CancellationToken cancellationToken);

    Task<Result<DelegationView>> RevokeAsync(Guid userId, Guid delegationId, CancellationToken cancellationToken);
}

// --- administration --------------------------------------------------------------------------------------------------------

/// <summary>Who takes over, and why. The reason is kept in the history.</summary>
public sealed record AssignRequest(Guid UserId, string? Reason);

public sealed record FailedDeliveryView(
    Guid Id, Guid InstanceId, string WorkflowKey, Guid EntityId, string EventType, int Attempts, string? LastError, DateTime OccurredAt);

/// <summary>
/// Repairs, each limited to subjects in the caller's data scope for the permission, and never available on a workflow the caller
/// raised or is the subject of.
/// </summary>
public interface IWorkflowAdminService
{
    Task<Result<PagedList<InstanceSummaryView>>> ListInstancesAsync(
        Guid userId, string? status, string? workflowKey, PageRequest paging, CancellationToken cancellationToken);

    /// <summary>Gives a pending task to someone else (never to the caller).</summary>
    Task<Result<InstanceView>> ReassignTaskAsync(Guid userId, Guid taskId, AssignRequest request, CancellationToken cancellationToken);

    /// <summary>Names an approver for the current stage of a blocked workflow (never the caller).</summary>
    Task<Result<InstanceView>> AssignBlockedAsync(Guid userId, Guid instanceId, AssignRequest request, CancellationToken cancellationToken);

    /// <summary>Looks for approvers again, after the data that blocked the workflow was fixed.</summary>
    Task<Result<InstanceView>> RetryBlockedAsync(Guid userId, Guid instanceId, CancellationToken cancellationToken);

    Task<Result<PagedList<FailedDeliveryView>>> ListFailedDeliveriesAsync(Guid userId, PageRequest paging, CancellationToken cancellationToken);

    Task<Result> RetryDeliveryAsync(Guid userId, Guid deliveryId, CancellationToken cancellationToken);
}
