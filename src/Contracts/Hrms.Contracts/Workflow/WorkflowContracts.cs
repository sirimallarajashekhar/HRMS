namespace Hrms.Contracts.Workflow;

/// <summary>The type of one routing fact a module hands to the workflow engine.</summary>
public enum WorkflowFieldType
{
    Number = 1,
    Text = 2,
    Boolean = 3,

    /// <summary>A business date.</summary>
    Date = 4,

    /// <summary>A user account id; the only type the "user named in the request" approver rule accepts.</summary>
    UserId = 5,
}

/// <summary>
/// One field a workflow key may carry. Fields exist to route a request and to evaluate stage conditions (an amount, a number of days).
/// They are stored with the workflow and shown to approvers, so they must never hold sensitive values.
/// </summary>
public sealed record WorkflowField(string Name, WorkflowFieldType Type, bool Required = true);

/// <summary>
/// What a module declares about a workflow it owns. Register one singleton per key in every host that runs the engine. A tenant can only
/// define workflows for declared keys, and a start may only carry declared fields.
/// </summary>
/// <param name="Key">Lower-case dotted name, for example <c>leave.apply</c>.</param>
/// <param name="EntityType">The kind of record being approved, for example <c>leave_request</c>.</param>
/// <param name="AllowAutoApprove">Whether a tenant may let an overdue stage approve itself. Leave false for money and sensitive changes.</param>
public sealed record WorkflowKeyDescriptor(string Key, string EntityType, IReadOnlyList<WorkflowField> Fields, bool AllowAutoApprove = false);

public enum WorkflowStatus
{
    Pending = 1,

    /// <summary>A stage has nobody who may approve it; it waits for an administrator.</summary>
    Blocked = 2,

    /// <summary>Returned to the requester, who may change it and submit it again.</summary>
    SentBack = 3,

    Approved = 4,
    Rejected = 5,
    Withdrawn = 6,
}

/// <summary>
/// A request for approval. <paramref name="EntityId"/> identifies one approval request in the owning module: the engine runs at most one
/// workflow for a key and id, ever, so repeating a start is always safe. A fresh attempt after a rejection or withdrawal needs a new id.
/// </summary>
/// <param name="SubjectEmployeeId">The employee the request is about; approver rules are relative to this person, who can never approve it.</param>
/// <param name="RequesterUserId">The user who raised it, who can never approve it either.</param>
/// <param name="Fields">Values for the key's declared fields: numbers, strings, booleans, <see cref="DateOnly"/> or <see cref="Guid"/>.</param>
public sealed record StartWorkflow(
    string WorkflowKey, Guid EntityId, Guid SubjectEmployeeId, Guid RequesterUserId, IReadOnlyDictionary<string, object?> Fields);

public sealed record WorkflowHandle(Guid InstanceId, WorkflowStatus Status);

public enum WorkflowFailureKind
{
    Invalid = 1,
    NotFound = 2,
    Conflict = 3,
}

/// <summary>Why a start or resubmission was refused, with a stable error code.</summary>
public sealed record WorkflowFailure(string Code, string Message, WorkflowFailureKind Kind);

/// <summary>
/// The answer to a start or resubmission: the workflow, or why there is none. This assembly is shared with the mobile app and depends
/// on nothing, so it has its own result type.
/// </summary>
public sealed record WorkflowSubmission(WorkflowHandle? Workflow, WorkflowFailure? Failure)
{
    public bool IsSuccess => Workflow is not null;
}

/// <summary>
/// The only way a module asks for approval (CLAUDE.md rule 7). Call it from server code, with values the module has validated itself;
/// there is no public endpoint that starts a workflow. The module saves its own request as "awaiting submission" first, then starts, and
/// repeats the start if it did not hear back.
/// </summary>
public interface IWorkflowService
{
    Task<WorkflowSubmission> StartAsync(StartWorkflow request, CancellationToken cancellationToken);

    /// <summary>Submits a sent-back request again with new field values; approval restarts at the first stage.</summary>
    Task<WorkflowSubmission> ResubmitAsync(
        string workflowKey, Guid entityId, Guid requesterUserId, IReadOnlyDictionary<string, object?> fields, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this user may decide on the record right now, directly or as a delegate. A module asks before showing an approver the
    /// details of what they are approving.
    /// </summary>
    Task<bool> CanDecideAsync(Guid userId, string workflowKey, Guid entityId, CancellationToken cancellationToken);
}

public enum WorkflowOutcome
{
    Approved = 1,
    Rejected = 2,
    SentBack = 3,
    Withdrawn = 4,
}

/// <summary>What happened to a request, for the module that owns it.</summary>
public sealed record WorkflowDecision(
    Guid TenantId, Guid InstanceId, string WorkflowKey, string EntityType, Guid EntityId, WorkflowOutcome Outcome, DateTime DecidedAt);

/// <summary>
/// The owning module's reaction to a decision. The worker calls it inside the tenant's scope, at least once and in order for each
/// workflow, so it must be safe to run twice. Throwing makes the engine try again later. Register it in the worker host.
/// </summary>
public interface IWorkflowDecisionHandler
{
    string WorkflowKey { get; }

    Task HandleAsync(WorkflowDecision decision, CancellationToken cancellationToken);
}
