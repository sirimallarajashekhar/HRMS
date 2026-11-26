using System.Text.Json;
using Hrms.Contracts;
using Hrms.SharedKernel;
using Hrms.Workflow.Application;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Workflow.Infrastructure;

internal sealed class WorkflowDefinitionService(
    WorkflowDbContext db,
    WorkflowKeyCatalogue catalogue,
    IApproverDirectory directory,
    IOrgStructureLookup organisation,
    TimeProvider time) : IWorkflowDefinitionService
{
    public IReadOnlyList<WorkflowKeyView> Keys() => catalogue.All
        .OrderBy(k => k.Key, StringComparer.Ordinal)
        .Select(k => new WorkflowKeyView(
            k.Key, k.EntityType, k.AllowAutoApprove, [.. k.Fields.Select(f => new WorkflowFieldView(f.Name, Views.Camel(f.Type), f.Required))]))
        .ToList();

    public async Task<PagedList<DefinitionSummaryView>> ListAsync(string? workflowKey, PageRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var query = db.Definitions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(workflowKey))
        {
            query = query.Where(d => d.WorkflowKey == workflowKey);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(d => d.WorkflowKey).ThenByDescending(d => d.Version)
            .Skip(paging.Skip).Take(paging.Size)
            .ToListAsync(cancellationToken);
        var items = rows.ConvertAll(d => new DefinitionSummaryView(
            d.Id, d.WorkflowKey, d.Version, d.Name, Views.Camel(d.Status), d.EffectiveFrom, d.RowVersion));
        return new PagedList<DefinitionSummaryView>(items, paging.Number, paging.Size, total);
    }

    public async Task<Result<DefinitionView>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var definition = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        return definition is null
            ? Result.Failure<DefinitionView>(NotFound())
            : Result.Success(await ViewAsync(definition, cancellationToken));
    }

    public async Task<Result<DefinitionView>> CreateDraftAsync(CreateDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (catalogue.Find(request.WorkflowKey?.Trim()) is not { } descriptor)
        {
            return Fail(Error.Validation("workflow.key.unknown", "That workflow does not exist."));
        }

        var existing = await db.Definitions.AsNoTracking()
            .Where(d => d.WorkflowKey == descriptor.Key)
            .Select(d => new { d.Version, d.Status })
            .ToListAsync(cancellationToken);
        if (existing.Any(d => d.Status == DefinitionStatus.Draft))
        {
            return Fail(Error.Conflict("workflow.definition.draft_exists", "This workflow already has a draft. Change or publish it."));
        }

        var definition = WorkflowDefinition.Create(descriptor.Key, existing.Count == 0 ? 1 : existing.Max(d => d.Version) + 1, request.Name);
        if (definition.IsFailure)
        {
            return Fail(definition.Error);
        }

        var stages = await BuildStagesAsync(definition.Value.Id, request.Stages, descriptor.Key, cancellationToken);
        if (stages.IsFailure)
        {
            return Fail(stages.Error);
        }

        db.Definitions.Add(definition.Value);
        db.Stages.AddRange(stages.Value);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            // Someone else created the same version first.
            db.ChangeTracker.Clear();
            return Fail(Error.Conflict("workflow.definition.draft_exists", "This workflow already has a draft. Change or publish it."));
        }

        return Result.Success(await ViewAsync(definition.Value, cancellationToken));
    }

    public async Task<Result<DefinitionView>> UpdateDraftAsync(Guid id, UpdateDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await LoadForChangeAsync(id, request.RowVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return Fail(loaded.Error);
        }

        var definition = loaded.Value;
        var edited = definition.Edit(request.Name);
        if (edited.IsFailure)
        {
            return Fail(edited.Error);
        }

        var stages = await BuildStagesAsync(definition.Id, request.Stages, definition.WorkflowKey, cancellationToken);
        if (stages.IsFailure)
        {
            return Fail(stages.Error);
        }

        // Two saves in one transaction: the old rows must stop counting before the new ones take their places in the unique index.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            db.Stages.RemoveRange(await db.Stages.Where(s => s.DefinitionId == id).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
            db.Stages.AddRange(stages.Value);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return Fail(Stale());
        }

        return Result.Success(await ViewAsync(definition, cancellationToken));
    }

    public async Task<Result<DefinitionView>> PublishAsync(Guid id, PublishDefinitionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await LoadForChangeAsync(id, request.RowVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return Fail(loaded.Error);
        }

        var definition = loaded.Value;
        if (catalogue.Find(definition.WorkflowKey) is not { } descriptor)
        {
            return Fail(Error.Validation("workflow.key.unknown", "That workflow does not exist."));
        }

        // Checked again now: the module's declaration, or the roles and users named, may have changed since the draft was saved.
        var current = await db.Stages.AsNoTracking().Where(s => s.DefinitionId == id).OrderBy(s => s.StageOrder).ToListAsync(cancellationToken);
        var specs = current.ConvertAll(s => (StageSpec?)new StageSpec(
            s.Name, s.Approver, s.Fallback, s.ConditionJson, s.Mode, s.SlaHours, s.OnTimeout, s.Escalation));
        var rebuilt = WorkflowStage.Build(id, specs, descriptor);
        if (rebuilt.IsFailure)
        {
            return Fail(rebuilt.Error);
        }

        var references = await CheckReferencesAsync(specs, cancellationToken);
        if (references.IsFailure)
        {
            return Fail(references.Error);
        }

        var latest = await db.Definitions.AsNoTracking()
            .Where(d => d.WorkflowKey == definition.WorkflowKey && d.Status == DefinitionStatus.Published)
            .MaxAsync(d => d.EffectiveFrom, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var published = definition.Publish(request.EffectiveFrom ?? today, today, latest, now);
        if (published.IsFailure)
        {
            return Fail(published.Error);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            db.ChangeTracker.Clear();
            return Fail(Stale());
        }

        return Result.Success(await ViewAsync(definition, cancellationToken));
    }

    private async Task<Result<IReadOnlyList<WorkflowStage>>> BuildStagesAsync(
        Guid definitionId, IReadOnlyList<StageRequest?>? requests, string workflowKey, CancellationToken cancellationToken)
    {
        var specs = new List<StageSpec?>();
        foreach (var request in requests ?? [])
        {
            var spec = ToSpec(request);
            if (spec.IsFailure)
            {
                return Result.Failure<IReadOnlyList<WorkflowStage>>(spec.Error);
            }

            specs.Add(spec.Value);
        }

        var built = WorkflowStage.Build(definitionId, specs, catalogue.Find(workflowKey)!);
        if (built.IsFailure)
        {
            return built;
        }

        var references = await CheckReferencesAsync(specs, cancellationToken);
        return references.IsFailure ? Result.Failure<IReadOnlyList<WorkflowStage>>(references.Error) : built;
    }

    /// <summary>Roles, users and unit types a rule names must exist now. They can still change later; the engine then blocks.</summary>
    private async Task<Result> CheckReferencesAsync(IReadOnlyList<StageSpec?> specs, CancellationToken cancellationToken)
    {
        foreach (var rule in specs.SelectMany(s => new[] { s?.Approver, s?.Fallback, s?.Escalation }).OfType<ApproverRule>())
        {
            var missing = rule.Type switch
            {
                ApproverType.RoleAtLocation when !await directory.RoleExistsAsync(rule.RoleCode!, cancellationToken) =>
                    $"There is no role '{rule.RoleCode}'.",
                ApproverType.NamedUser when !(await directory.GetAccountsAsync([rule.UserId!.Value], cancellationToken)).Any(a => a.IsActive) =>
                    "The named user does not exist or is not active.",
                ApproverType.UnitHead when rule.OrgUnitTypeId is { } type
                    && await organisation.GetStatusAsync(MasterKinds.OrgUnitType, type, cancellationToken) != MasterStatus.Active =>
                    "The org unit type does not exist or is not active.",
                _ => null,
            };
            if (missing is not null)
            {
                return Result.Failure(Error.Validation("workflow.definition.reference_not_found", missing));
            }
        }

        return Result.Success();
    }

    private static Result<StageSpec> ToSpec(StageRequest? request)
    {
        if (request is null)
        {
            return Invalid("workflow.definition.stage_invalid", "A stage is empty.");
        }

        var rules = new List<ApproverRule?>();
        foreach (var rule in new[] { request.Approver, request.Fallback, request.Escalation })
        {
            if (rule is null)
            {
                rules.Add(null);
            }
            else if (Views.TryParse<ApproverType>(rule.Type, out var type))
            {
                rules.Add(new ApproverRule(type, rule.Level, rule.OrgUnitTypeId, rule.RoleCode?.Trim(), rule.UserId, rule.Field?.Trim()));
            }
            else
            {
                return Invalid("workflow.definition.approver_invalid", "That approver type does not exist.");
            }
        }

        var mode = StageMode.Any;
        if (request.Mode is not null && !Views.TryParse(request.Mode, out mode))
        {
            return Invalid("workflow.definition.mode_invalid", "The mode is any or all.");
        }

        TimeoutAction? onTimeout = null;
        if (request.OnTimeout is not null)
        {
            if (!Views.TryParse<TimeoutAction>(request.OnTimeout, out var parsed))
            {
                return Invalid("workflow.definition.timeout_invalid", "The timeout action is remind, escalate, reject or approve.");
            }

            onTimeout = parsed;
        }

        var condition = request.Condition is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element ? element.GetRawText() : null;
        return Result.Success(new StageSpec(request.Name, rules[0], rules[1], condition, mode, request.SlaHours, onTimeout, rules[2]));

        static Result<StageSpec> Invalid(string code, string message) => Result.Failure<StageSpec>(Error.Validation(code, message));
    }

    private async Task<DefinitionView> ViewAsync(WorkflowDefinition d, CancellationToken cancellationToken)
    {
        var stages = await db.Stages.AsNoTracking().Where(s => s.DefinitionId == d.Id).OrderBy(s => s.StageOrder).ToListAsync(cancellationToken);
        return new DefinitionView(
            d.Id, d.WorkflowKey, d.Version, d.Name, Views.Camel(d.Status), d.EffectiveFrom, d.RowVersion,
            stages.ConvertAll(s => new StageView(
                s.StageOrder, s.Name, Rule(s.Approver)!, Rule(s.Fallback),
                s.ConditionJson is null ? null : JsonSerializer.Deserialize<JsonElement>(s.ConditionJson),
                Views.Camel(s.Mode), s.SlaHours, s.OnTimeout is { } action ? Views.Camel(action) : null, Rule(s.Escalation))));

        static ApproverRuleRequest? Rule(ApproverRule? r) =>
            r is null ? null : new ApproverRuleRequest(Views.Camel(r.Type), r.Level, r.OrgUnitTypeId, r.RoleCode, r.UserId, r.Field);
    }

    private async Task<Result<WorkflowDefinition>> LoadForChangeAsync(Guid id, long? rowVersion, CancellationToken cancellationToken)
    {
        if (rowVersion is not { } version)
        {
            return Result.Failure<WorkflowDefinition>(
                Error.Validation("workflow.row_version_required", "The rowVersion the change is based on is required."));
        }

        var definition = await db.Definitions.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (definition is null)
        {
            return Result.Failure<WorkflowDefinition>(NotFound());
        }

        return definition.RowVersion == version ? Result.Success(definition) : Result.Failure<WorkflowDefinition>(Stale());
    }

    private static Result<DefinitionView> Fail(Error error) => Result.Failure<DefinitionView>(error);

    private static Error NotFound() => Error.NotFound("workflow.definition.not_found", "The workflow definition does not exist.");

    private static Error Stale() => Error.Conflict(
        "workflow.definition.stale_version", "The definition was changed by someone else. Reload it and apply your change again.");
}

/// <summary>Stops Core deleting an org unit type that an approver rule names.</summary>
internal sealed class WorkflowUnitTypeUsageCheck(WorkflowDbContext db) : IMasterUsageCheck
{
    public string Master => MasterKinds.OrgUnitType;

    public Task<bool> IsInUseAsync(Guid id, CancellationToken cancellationToken)
    {
        var text = id.ToString("D");
        return db.Stages.AsNoTracking().AnyAsync(
            s => s.ApproverJson.Contains(text)
                 || (s.FallbackJson != null && s.FallbackJson.Contains(text))
                 || (s.EscalationJson != null && s.EscalationJson.Contains(text)),
            cancellationToken);
    }
}
