using System.Net;
using FluentAssertions;
using Hrms.Contracts.Workflow;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Persistence;
using Hrms.Workflow.Domain;
using Hrms.Workflow.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.IntegrationTests.Workflow;

/// <summary>Deadlines and result delivery: what the worker does, run one pass at a time.</summary>
[Collection(MySqlTestGroup.Name)]
public class WorkflowProcessingTests(EmployeeApiFixture fixture) : WorkflowApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    private async Task<IReadOnlyList<WorkflowOutboxMessage>> OutboxAsync(Guid instance)
    {
        await using var db = WorkflowTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        return await db.Outbox.AsNoTracking().Where(m => m.InstanceId == instance).OrderBy(m => m.Sequence).ToListAsync();
    }

    /// <summary>Makes waiting retries due, as if their delay had passed.</summary>
    private async Task SkipBackOffAsync(Guid instance)
    {
        await using var db = WorkflowTestDb.Context(ConnectionString, Acme.Tenant.TenantId);
        foreach (var message in await db.Outbox.Where(m => m.InstanceId == instance && m.ProcessedAt == null).ToListAsync())
        {
            db.Entry(message).Property(m => m.NextAttemptAt).CurrentValue = HostNow.AddSeconds(-1);
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ADeadline_Reminds_OnlyOnce()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), slaHours: 24, onTimeout: "remind"));
        var line = await LineAsync();
        var (instance, _) = await StartAsync(line.Worker);
        await ProcessAsync();
        Actions(await InstanceAsync(instance)).Should().NotContain("reminded", "the deadline has not passed");

        await MakeOverdueAsync(instance);
        await ProcessAsync();
        await ProcessAsync();

        Actions(await InstanceAsync(instance)).Count(a => a == "reminded").Should().Be(1);
        (await OutboxAsync(instance)).Count(m => m.EventType == "WorkflowTaskReminderDue").Should().Be(1);
        await DecideAsync(line.Lead, instance);
    }

    [Fact]
    public async Task Escalation_HandsTheTaskOn_AndDoesNotChain()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), slaHours: 24, onTimeout: "escalate", escalation: Manager(2)));
        var line = await LineAsync();
        var (instance, _) = await StartAsync(line.Worker);
        await MakeOverdueAsync(instance);

        await ProcessAsync();

        Tasks(await InstanceAsync(instance)).Should().BeEquivalentTo([(line.Lead.UserId, "cancelled"), (line.Head.UserId, "pending")]);
        (await InboxTaskAsync(line.Lead.Token, instance)).Should().BeNull();
        (await OutboxAsync(instance)).Should().ContainSingle(m => m.EventType == "WorkflowTaskEscalated");

        await MakeOverdueAsync(instance);
        await ProcessAsync();
        Tasks(await InstanceAsync(instance)).Should().HaveCount(2, "an escalated task is only reminded");
        await DecideAsync(line.Head, instance);
        (await StatusAsync(instance)).Should().Be("approved");
    }

    [Fact]
    public async Task WhenEscalationFindsNobody_TheApproverKeepsTheTask()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), slaHours: 24, onTimeout: "escalate", escalation: Manager(5)));
        var line = await LineAsync();
        var (instance, _) = await StartAsync(line.Worker);
        await MakeOverdueAsync(instance);

        await ProcessAsync();

        Actions(await InstanceAsync(instance)).Should().Contain("escalationFailed");
        await DecideAsync(line.Lead, instance);
    }

    [Fact]
    public async Task ATimeout_CanReject()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), slaHours: 1, onTimeout: "reject"));
        var line = await LineAsync();
        var (instance, entity) = await StartAsync(line.Worker);
        await MakeOverdueAsync(instance);

        await ProcessAsync();

        (await StatusAsync(instance)).Should().Be("rejected");
        Actions(await InstanceAsync(instance)).Should().Contain("timedOutRejected");
        Decisions.For(entity).Should().ContainSingle().Which.Outcome.Should().Be(WorkflowOutcome.Rejected);
    }

    [Fact]
    public async Task ATimeout_CanApprove_WhereTheModuleAllowsIt_AndMovesToTheNextStage()
    {
        await PublishAsync(TestWorkflows.Auto, Stage(Manager(), slaHours: 1, onTimeout: "approve"), Stage(Manager(2), "Head"));
        var line = await LineAsync();
        var (instance, _) = await StartAsync(line.Worker, key: TestWorkflows.Auto);
        await MakeOverdueAsync(instance);

        await ProcessAsync();

        Actions(await InstanceAsync(instance)).Should().Contain("timedOutApproved");
        await DecideAsync(line.Head, instance);
        (await StatusAsync(instance)).Should().Be("approved");
    }

    [Fact]
    public async Task AnOverdueTaskOfADisabledAccount_IsFlagged()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), slaHours: 1, onTimeout: "remind"));
        var line = await LineAsync();
        var (instance, _) = await StartAsync(line.Worker);
        await DisableAsync(line.Lead.UserId);
        await MakeOverdueAsync(instance);

        await ProcessAsync();

        var view = await InstanceAsync(instance);
        view.GetProperty("tasks")[0].GetProperty("assigneeUnavailable").GetBoolean().Should().BeTrue();
        Actions(view).Should().Contain("assigneeUnavailable");
    }

    [Fact]
    public async Task AFailingHandler_IsRetried_HoldsBackOnlyItsOwnWorkflow_AndResultsStayInOrder()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var (stuck, stuckEntity) = await StartAsync(line.Worker);
        var (healthy, healthyEntity) = await StartAsync(line.Worker);
        Decisions.FailFor(stuckEntity);
        await DecideAsync(line.Lead, stuck, "sendBack");
        await DecideAsync(line.Lead, healthy);
        (await PostAsync($"/workflow/instances/{stuck}/withdraw", new { }, line.Worker.Token)).Dispose();

        await ProcessAsync();

        Decisions.For(healthyEntity).Should().ContainSingle();
        var waiting = (await OutboxAsync(stuck)).Where(m => m.ProcessedAt == null).ToList();
        waiting.Select(m => m.EventType).Should().Equal("WorkflowSentBack", "WorkflowWithdrawn");
        waiting[0].Attempts.Should().Be(1);
        waiting[0].LastError.Should().Be("InvalidOperationException", "only the type is kept, never the message");
        waiting[1].Attempts.Should().Be(0, "a later result waits for the earlier one");

        Decisions.Recover(stuckEntity);
        await SkipBackOffAsync(stuck);
        await ProcessAsync();
        Decisions.For(stuckEntity).Select(d => d.Outcome).Should().Equal(WorkflowOutcome.SentBack, WorkflowOutcome.Withdrawn);
    }

    [Fact]
    public async Task ADeliveryThatIsGivenUp_IsListed_AndCanBeRetriedByAnAdministrator()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var (instance, entity) = await StartAsync(line.Worker);
        Decisions.FailFor(entity);
        await DecideAsync(line.Lead, instance);
        for (var attempt = 0; attempt < WorkflowOutboxMessage.MaxAttempts; attempt++)
        {
            await SkipBackOffAsync(instance);
            await ProcessAsync();
        }

        var admin = await AdminAsync();
        using var listed = await GetAsync("/workflow/deliveries?pageSize=200", admin);
        var failed = (await JsonAsync(listed)).GetProperty("items").EnumerateArray().Single(d => d.GetProperty("instanceId").GetGuid() == instance);
        failed.GetProperty("attempts").GetInt32().Should().Be(WorkflowOutboxMessage.MaxAttempts);

        Decisions.Recover(entity);
        await ProcessAsync();
        Decisions.For(entity).Should().BeEmpty("a given-up delivery is not retried by itself");
        using (var retried = await PostAsync($"/workflow/deliveries/{Id(failed)}/retry", null, admin))
        {
            retried.StatusCode.Should().Be(HttpStatusCode.NoContent, await retried.Content.ReadAsStringAsync());
        }

        await ProcessAsync();
        Decisions.For(entity).Should().ContainSingle();
        await ExpectAsync(await PostAsync($"/workflow/deliveries/{Id(failed)}/retry", null, admin), HttpStatusCode.Conflict, "workflow.delivery.not_failed");
    }

    [Fact]
    public async Task TheBackgroundPass_VisitsEveryTenant_AndOnlyOneInstanceRunsIt()
    {
        var service = Host.Service<WorkflowBackgroundService>();
        (await service.TickAsync(CancellationToken.None)).Should().BeTrue();

        await using var held = await Host.Service<IJobLock>().TryAcquireAsync(WorkflowBackgroundService.LockName, CancellationToken.None);

        held.Should().NotBeNull();
        (await service.TickAsync(CancellationToken.None)).Should().BeFalse();
    }
}
