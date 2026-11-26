using System.Collections.Concurrent;
using Hrms.Contracts.Workflow;
using Hrms.SharedKernel;
using Hrms.Workflow.Infrastructure;
using Hrms.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Workflow;

internal static class WorkflowTestDb
{
    public static WorkflowDbContext Context(string connectionString, TenantId? tenant)
    {
        var options = (DbContextOptions<WorkflowDbContext>)WorkflowInfrastructureExtensions
            .UseWorkflowMySql(new DbContextOptionsBuilder<WorkflowDbContext>(), connectionString)
            .Options;
        var tenantContext = new TenantContext();
        if (tenant is { } id)
        {
            tenantContext.Set(id);
        }

        return new WorkflowDbContext(options, tenantContext, new SystemActorContext(), TimeProvider.System);
    }
}

/// <summary>
/// No real module uses the engine yet, so the test host declares two workflows of its own and records what their handlers are told.
/// </summary>
public static class TestWorkflows
{
    public const string Leave = "test.leave";

    /// <summary>The one whose module allows approval by timeout.</summary>
    public const string Auto = "test.auto";

    private static readonly WorkflowField[] Fields =
    [
        new("days", WorkflowFieldType.Number),
        new("note", WorkflowFieldType.Text, Required: false),
        new("coverUserId", WorkflowFieldType.UserId, Required: false),
    ];

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(new WorkflowKeyDescriptor(Leave, "test_leave_request", Fields));
        services.AddSingleton(new WorkflowKeyDescriptor(Auto, "test_auto_request", Fields, AllowAutoApprove: true));
        services.AddSingleton<DecisionRecorder>();
        foreach (var key in new[] { Leave, Auto })
        {
            services.AddSingleton<IWorkflowDecisionHandler>(sp => new RecordingHandler(key, sp.GetRequiredService<DecisionRecorder>()));
        }
    }

    private sealed class RecordingHandler(string key, DecisionRecorder recorder) : IWorkflowDecisionHandler
    {
        public string WorkflowKey => key;

        public Task HandleAsync(WorkflowDecision decision, CancellationToken cancellationToken)
        {
            recorder.Handle(decision);
            return Task.CompletedTask;
        }
    }
}

/// <summary>What the handlers received, and a switch to make them fail for one request.</summary>
public sealed class DecisionRecorder
{
    private readonly ConcurrentQueue<WorkflowDecision> _received = new();
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();

    public void FailFor(Guid entityId) => _failing[entityId] = true;

    public void Recover(Guid entityId) => _failing.TryRemove(entityId, out _);

    public IReadOnlyList<WorkflowDecision> For(Guid entityId) => _received.Where(d => d.EntityId == entityId).ToList();

    public void Handle(WorkflowDecision decision)
    {
        if (_failing.ContainsKey(decision.EntityId))
        {
            throw new InvalidOperationException("The module is not ready.");
        }

        _received.Enqueue(decision);
    }
}
