using Hrms.Contracts;
using Hrms.Contracts.Workflow;
using Hrms.Persistence;
using Hrms.SharedKernel;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Workflow.Infrastructure;

/// <summary>The contract other modules use. See <see cref="IWorkflowService"/> for the promises it keeps.</summary>
internal sealed class WorkflowService(
    WorkflowDbContext db,
    WorkflowEngine engine,
    WorkflowAccess access,
    WorkflowKeyCatalogue catalogue,
    IEmployeeDirectory employees) : IWorkflowService
{
    public async Task<WorkflowSubmission> StartAsync(StartWorkflow request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (catalogue.Find(request.WorkflowKey) is not { } descriptor)
        {
            return UnknownKey(request.WorkflowKey);
        }

        if (request.EntityId == Guid.Empty || request.SubjectEmployeeId == Guid.Empty || request.RequesterUserId == Guid.Empty)
        {
            return Invalid("workflow.start.ids_required", "The request id, the employee and the requester are required.");
        }

        var payload = WorkflowPayload.Create(descriptor, request.Fields);
        if (payload.IsFailure)
        {
            return Invalid(payload.Error.Code, payload.Error.Message);
        }

        var fingerprint = WorkflowInstance.Fingerprint(request.SubjectEmployeeId, request.RequesterUserId, payload.Value.Json);
        if (await FindAsync(request.WorkflowKey, request.EntityId, cancellationToken) is { } existing)
        {
            return Repeat(existing, fingerprint);
        }

        if (!await employees.ExistsAsync(request.SubjectEmployeeId, cancellationToken))
        {
            return Invalid("workflow.start.subject_not_found", "The employee does not exist.");
        }

        if (await access.ActiveAccountAsync(request.RequesterUserId, cancellationToken) is null)
        {
            return Invalid("workflow.start.requester_not_found", "The requester has no active user account.");
        }

        var today = engine.Today;
        var definition = await db.Definitions.AsNoTracking()
            .Where(d => d.WorkflowKey == request.WorkflowKey && d.Status == DefinitionStatus.Published && d.EffectiveFrom <= today)
            .OrderByDescending(d => d.EffectiveFrom).ThenByDescending(d => d.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (definition is null)
        {
            // Never treat "nothing configured" as "nothing to approve".
            return new WorkflowSubmission(null, new WorkflowFailure(
                "workflow.definition.missing", $"No workflow is published for '{request.WorkflowKey}'.", WorkflowFailureKind.NotFound));
        }

        var instance = WorkflowInstance.Start(
            definition, descriptor.EntityType, request.EntityId, request.SubjectEmployeeId, request.RequesterUserId, payload.Value.Json);
        db.Instances.Add(instance);
        engine.Log(instance, WorkflowAction.Started, actor: request.RequesterUserId);
        var running = new RunningWorkflow(instance, await engine.StagesAsync(definition.Id, cancellationToken), descriptor);
        await engine.AdvanceAsync(running, 1, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            // Another start for the same request won the race; the unique index let only one through.
            db.ChangeTracker.Clear();
            return await FindAsync(request.WorkflowKey, request.EntityId, cancellationToken) is { } winner
                ? Repeat(winner, fingerprint)
                : Conflict(Errors.Changed().Code, Errors.Changed().Message);
        }

        return Success(instance);
    }

    public async Task<WorkflowSubmission> ResubmitAsync(
        string workflowKey, Guid entityId, Guid requesterUserId, IReadOnlyDictionary<string, object?> fields, CancellationToken cancellationToken)
    {
        if (catalogue.Find(workflowKey) is not { } descriptor)
        {
            return UnknownKey(workflowKey);
        }

        var payload = WorkflowPayload.Create(descriptor, fields);
        if (payload.IsFailure)
        {
            return Invalid(payload.Error.Code, payload.Error.Message);
        }

        var instance = await db.Instances.SingleOrDefaultAsync(i => i.WorkflowKey == workflowKey && i.EntityId == entityId, cancellationToken);
        if (instance is null || instance.RequesterUserId != requesterUserId)
        {
            return new WorkflowSubmission(null, new WorkflowFailure(
                "workflow.instance.not_found", "There is no such request from this user.", WorkflowFailureKind.NotFound));
        }

        if (instance.Status != WorkflowStatus.SentBack)
        {
            // A repeat of a resubmission that already happened carries the values now on the instance.
            return instance.Round > 1 && instance.PayloadJson == payload.Value.Json
                ? Success(instance)
                : Conflict("workflow.instance.not_sent_back", "Only a request that was sent back can be submitted again.");
        }

        instance.Resubmit(payload.Value.Json);
        engine.Log(instance, WorkflowAction.Resubmitted, actor: requesterUserId);
        var running = new RunningWorkflow(instance, await engine.StagesAsync(instance.DefinitionId, cancellationToken), descriptor);
        await engine.AdvanceAsync(running, 1, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (WriteConflicts.Is(ex))
        {
            db.ChangeTracker.Clear();
            return Conflict(Errors.Changed().Code, Errors.Changed().Message);
        }

        return Success(instance);
    }

    public async Task<bool> CanDecideAsync(Guid userId, string workflowKey, Guid entityId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking()
            .SingleOrDefaultAsync(i => i.WorkflowKey == workflowKey && i.EntityId == entityId, cancellationToken);
        if (instance is not { Status: WorkflowStatus.Pending })
        {
            return false;
        }

        var account = await access.ActiveAccountAsync(userId, cancellationToken);
        if (account is null || new ApprovalSubject(instance.SubjectEmployeeId, instance.RequesterUserId).Bars(account))
        {
            return false;
        }

        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.InstanceId == instance.Id && t.Round == instance.Round && t.StageOrder == instance.CurrentStage)
            .ToListAsync(cancellationToken);
        if (tasks.Any(t => t.DecidedByUserId == userId))
        {
            return false;
        }

        var delegations = await access.HonouredForAsync(
            await access.DelegationsToAsync(userId, engine.Today, cancellationToken),
            new ApprovalSubject(instance.SubjectEmployeeId, instance.RequesterUserId),
            cancellationToken);
        return tasks.Any(t => t.Status == WorkflowTaskStatus.Pending && WorkflowAccess.Holds(userId, delegations, t, workflowKey));
    }

    private Task<WorkflowInstance?> FindAsync(string workflowKey, Guid entityId, CancellationToken cancellationToken) =>
        db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.WorkflowKey == workflowKey && i.EntityId == entityId, cancellationToken);

    private static WorkflowSubmission Repeat(WorkflowInstance existing, string fingerprint) =>
        existing.StartFingerprint == fingerprint
            ? Success(existing)
            : Conflict(
                "workflow.instance.start_mismatch",
                "A workflow for this request already exists with different details. Use a new request id for a new request.");

    private static WorkflowSubmission Success(WorkflowInstance instance) => new(new WorkflowHandle(instance.Id, instance.Status), null);

    private static WorkflowSubmission UnknownKey(string? key) => Invalid("workflow.key.unknown", $"'{key}' is not a declared workflow.");

    private static WorkflowSubmission Invalid(string code, string message) => new(null, new WorkflowFailure(code, message, WorkflowFailureKind.Invalid));

    private static WorkflowSubmission Conflict(string code, string message) => new(null, new WorkflowFailure(code, message, WorkflowFailureKind.Conflict));
}
