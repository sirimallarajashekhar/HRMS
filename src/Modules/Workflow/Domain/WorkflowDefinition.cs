using Hrms.Contracts.Workflow;
using Hrms.SharedKernel;

namespace Hrms.Workflow.Domain;

public enum DefinitionStatus
{
    Draft = 1,
    Published = 2,
}

public enum StageMode
{
    /// <summary>The first approver to approve or reject decides the stage.</summary>
    Any = 1,

    /// <summary>Everyone must approve; one rejection rejects.</summary>
    All = 2,
}

public enum TimeoutAction
{
    Remind = 1,
    Escalate = 2,
    Reject = 3,

    /// <summary>Only where the owning module allows it.</summary>
    Approve = 4,
}

/// <summary>One stage as an administrator describes it. The order is the position in the list.</summary>
public sealed record StageSpec(
    string? Name,
    ApproverRule? Approver,
    ApproverRule? Fallback = null,
    string? Condition = null,
    StageMode Mode = StageMode.Any,
    int? SlaHours = null,
    TimeoutAction? OnTimeout = null,
    ApproverRule? Escalation = null);

/// <summary>
/// A version of the approval steps for one workflow key. A draft can be changed; a published version never changes, so a running request
/// keeps the steps it started with. A new version takes over from its effective date for requests started from then on.
/// </summary>
[Audited]
public sealed class WorkflowDefinition : TenantEntity
{
    public const int NameMaxLength = 100;

    private WorkflowDefinition(string workflowKey, int version, string name)
    {
        WorkflowKey = workflowKey;
        Version = version;
        Name = name;
        Status = DefinitionStatus.Draft;
    }

    /// <summary>For the persistence layer only.</summary>
    private WorkflowDefinition()
    {
        WorkflowKey = Name = string.Empty;
    }

    public string WorkflowKey { get; private set; }

    public int Version { get; private set; }

    public string Name { get; private set; }

    public DefinitionStatus Status { get; private set; }

    public DateOnly? EffectiveFrom { get; private set; }

    public DateTime? PublishedAt { get; private set; }

    /// <summary>Counts edits to the draft, so that changing only the stages still changes this row (and its row_version).</summary>
    [AuditIgnore]
    public int Revision { get; private set; }

    public static Result<WorkflowDefinition> Create(string workflowKey, int version, string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > NameMaxLength
            ? Result.Failure<WorkflowDefinition>(NameInvalid())
            : Result.Success(new WorkflowDefinition(workflowKey, version, trimmed));
    }

    public Result Edit(string? name)
    {
        if (Status != DefinitionStatus.Draft)
        {
            return Result.Failure(NotDraft());
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            return Result.Failure(NameInvalid());
        }

        Name = trimmed;
        Revision++;
        return Result.Success();
    }

    /// <param name="latestPublishedFrom">The latest start date among the key's published versions, if any.</param>
    public Result Publish(DateOnly effectiveFrom, DateOnly today, DateOnly? latestPublishedFrom, DateTime nowUtc)
    {
        if (Status != DefinitionStatus.Draft)
        {
            return Result.Failure(NotDraft());
        }

        if (effectiveFrom < today)
        {
            return Result.Failure(Error.Validation("workflow.definition.effective_from_past", "A version cannot start in the past."));
        }

        if (latestPublishedFrom is { } latest && effectiveFrom < latest)
        {
            return Result.Failure(Error.Validation(
                "workflow.definition.effective_from_overlaps", "A newer version cannot start before the latest published one."));
        }

        Status = DefinitionStatus.Published;
        EffectiveFrom = effectiveFrom;
        PublishedAt = nowUtc;
        return Result.Success();
    }

    private static Error NameInvalid() => Error.Validation("workflow.definition.name_invalid", $"A name is 1 to {NameMaxLength} characters.");

    private static Error NotDraft() => Error.Conflict("workflow.definition.not_draft", "A published version cannot change; create a new one.");
}

/// <summary>One stage of a definition. Rules and conditions are stored as the validated JSON.</summary>
[Audited(RootType = "workflow_def", RootKey = nameof(DefinitionId))]
public sealed class WorkflowStage : TenantEntity
{
    public const int MaxStages = 10;
    public const int NameMaxLength = 100;
    public const int MaxSlaHours = 2160; // 90 days

    private WorkflowStage(Guid definitionId, int stageOrder, string name)
    {
        DefinitionId = definitionId;
        StageOrder = stageOrder;
        Name = name;
        ApproverJson = string.Empty;
    }

    /// <summary>For the persistence layer only.</summary>
    private WorkflowStage()
    {
        Name = ApproverJson = string.Empty;
    }

    public Guid DefinitionId { get; private set; }

    /// <summary>1 is the first stage.</summary>
    public int StageOrder { get; private set; }

    public string Name { get; private set; }

    public string ApproverJson { get; private set; }

    public string? FallbackJson { get; private set; }

    public string? ConditionJson { get; private set; }

    public StageMode Mode { get; private set; }

    public int? SlaHours { get; private set; }

    public TimeoutAction? OnTimeout { get; private set; }

    public string? EscalationJson { get; private set; }

    public ApproverRule Approver => ApproverRule.FromJson(ApproverJson);

    public ApproverRule? Fallback => FallbackJson is null ? null : ApproverRule.FromJson(FallbackJson);

    public ApproverRule? Escalation => EscalationJson is null ? null : ApproverRule.FromJson(EscalationJson);

    /// <summary>
    /// Validates a whole list of stages against the key's declaration and builds them. At least one stage must have no condition: that is
    /// what guarantees no request can ever be approved without a stage running.
    /// </summary>
    public static Result<IReadOnlyList<WorkflowStage>> Build(Guid definitionId, IReadOnlyList<StageSpec?>? specs, WorkflowKeyDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (specs is not { Count: > 0 and <= MaxStages })
        {
            return Fail("workflow.definition.stage_count_invalid", $"A workflow has 1 to {MaxStages} stages.");
        }

        var stages = new List<WorkflowStage>(specs.Count);
        for (var index = 0; index < specs.Count; index++)
        {
            var order = index + 1;
            var built = BuildOne(definitionId, order, specs[index], descriptor);
            if (built.IsFailure)
            {
                return Result.Failure<IReadOnlyList<WorkflowStage>>(built.Error with { Message = $"Stage {order}: {built.Error.Message}" });
            }

            stages.Add(built.Value);
        }

        return stages.All(s => s.ConditionJson is not null)
            ? Fail("workflow.definition.no_unconditional_stage", "At least one stage must apply to every request (have no condition).")
            : Result.Success<IReadOnlyList<WorkflowStage>>(stages);
    }

    private static Result<WorkflowStage> BuildOne(Guid definitionId, int order, StageSpec? spec, WorkflowKeyDescriptor descriptor)
    {
        if (spec is null)
        {
            return StageFail("workflow.definition.stage_invalid", "The stage is empty.");
        }

        var name = spec.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > NameMaxLength)
        {
            return StageFail("workflow.definition.stage_name_invalid", $"A stage name is 1 to {NameMaxLength} characters.");
        }

        if (spec.Approver is null)
        {
            return StageFail("workflow.definition.approver_invalid", "The stage needs an approver rule.");
        }

        foreach (var rule in new[] { spec.Approver, spec.Fallback, spec.Escalation })
        {
            if (rule?.Validate(descriptor) is { IsFailure: true } invalid)
            {
                return Result.Failure<WorkflowStage>(invalid.Error);
            }
        }

        if (!Enum.IsDefined(spec.Mode))
        {
            return StageFail("workflow.definition.mode_invalid", "The mode is any or all.");
        }

        if (spec.SlaHours is < 1 or > MaxSlaHours)
        {
            return StageFail("workflow.definition.sla_invalid", $"A deadline is 1 to {MaxSlaHours} hours.");
        }

        if (spec.SlaHours is null != spec.OnTimeout is null || (spec.OnTimeout is { } action && !Enum.IsDefined(action)))
        {
            return StageFail("workflow.definition.timeout_invalid", "A deadline and its action come together.");
        }

        if (spec.OnTimeout == TimeoutAction.Approve && !descriptor.AllowAutoApprove)
        {
            return StageFail("workflow.definition.auto_approve_not_allowed", $"The workflow '{descriptor.Key}' does not allow approval by timeout.");
        }

        if ((spec.OnTimeout == TimeoutAction.Escalate) != (spec.Escalation is not null))
        {
            return StageFail("workflow.definition.escalation_invalid", "An escalation rule is given exactly when the action is escalate.");
        }

        string? condition = null;
        if (!string.IsNullOrWhiteSpace(spec.Condition))
        {
            var parsed = StageCondition.Parse(spec.Condition, descriptor);
            if (parsed.IsFailure)
            {
                return Result.Failure<WorkflowStage>(parsed.Error);
            }

            condition = parsed.Value.Json;
        }

        return Result.Success(new WorkflowStage(definitionId, order, name)
        {
            ApproverJson = spec.Approver.ToJson(),
            FallbackJson = spec.Fallback?.ToJson(),
            ConditionJson = condition,
            Mode = spec.Mode,
            SlaHours = spec.SlaHours,
            OnTimeout = spec.OnTimeout,
            EscalationJson = spec.Escalation?.ToJson(),
        });
    }

    private static Result<IReadOnlyList<WorkflowStage>> Fail(string code, string message) =>
        Result.Failure<IReadOnlyList<WorkflowStage>>(Error.Validation(code, message));

    private static Result<WorkflowStage> StageFail(string code, string message) =>
        Result.Failure<WorkflowStage>(Error.Validation(code, message));
}
