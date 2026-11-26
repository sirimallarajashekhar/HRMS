using System.Net;
using FluentAssertions;
using Hrms.Contracts.Workflow;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Hrms.Core.IntegrationTests.Workflow;

[Collection(MySqlTestGroup.Name)]
public class WorkflowFlowApiTests(EmployeeApiFixture fixture) : WorkflowApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    [Fact]
    public async Task TwoLevels_AreApprovedInOrder_AndTheModuleIsToldOnce()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), "Lead"), Stage(Manager(2), "Head"));
        var line = await LineAsync();
        var (instance, entity) = await StartAsync(line.Worker);

        (await InboxTaskAsync(line.Head.Token, instance)).Should().BeNull("the second stage has not opened yet");
        (await CanDecideAsync(line.Lead.UserId, entity)).Should().BeTrue();
        (await CanDecideAsync(line.Head.UserId, entity)).Should().BeFalse();
        await DecideAsync(line.Lead, instance);
        (await StatusAsync(instance)).Should().Be("pending");
        (await InboxTaskAsync(line.Lead.Token, instance)).Should().BeNull();
        await DecideAsync(line.Head, instance, comment: "Enjoy");

        var view = await InstanceAsync(instance);
        view.GetProperty("instance").GetProperty("status").GetString().Should().Be("approved");
        view.GetProperty("fields").GetProperty("days").GetDecimal().Should().Be(2);
        Actions(view).Should().Equal(
            "started", "stageOpened", "taskAssigned", "approved", "stageOpened", "taskAssigned", "approved", "completed");

        await ProcessAsync();
        await ProcessAsync();
        Decisions.For(entity).Should().ContainSingle().Which.Outcome.Should().Be(WorkflowOutcome.Approved);
        (await InboxAsync(line.Head.Token, "&done=true")).Should().ContainSingle(t => t.GetProperty("instanceId").GetGuid() == instance);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task AConditionalStage_RunsOnlyWhenItsConditionHolds(int days, bool needsHead)
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), "Lead"), Stage(Manager(2), "Long leave", condition: DaysOver(3)));
        var line = await LineAsync();
        var (instance, _) = await StartAsync(line.Worker, days: days);

        await DecideAsync(line.Lead, instance);

        (await StatusAsync(instance)).Should().Be(needsHead ? "pending" : "approved");
        Actions(await InstanceAsync(instance)).Count(a => a == "stageSkipped").Should().Be(needsHead ? 0 : 1);
    }

    [Theory]
    [InlineData("approve", "approved")]
    [InlineData("reject", "rejected")]
    public async Task AnyMode_TheFirstDecisionDecides_AndTheOtherTasksAreSuperseded(string action, string outcome)
    {
        var location = Acme.SecondLocationId;
        var first = await UserAsync(ScopeType.Location, location);
        var role = await RoleCodeOfAsync(first.UserId);
        var second = await UserAsync();
        await Host.GrantRoleAtAsync(Acme.Tenant.TenantId, second.UserId, role, ScopeType.Location, location);
        var tenantWide = await UserAsync();
        await Host.GrantRoleAtAsync(Acme.Tenant.TenantId, tenantWide.UserId, role, ScopeType.Tenant, null);
        await PublishAsync(TestWorkflows.Leave, Stage(new { type = "roleAtLocation", roleCode = role }));
        var worker = await PersonAsync(location: location);
        var (instance, _) = await StartAsync(worker);

        Tasks(await InstanceAsync(instance)).Select(t => t.Assignee).Should()
            .BeEquivalentTo([first.UserId, second.UserId], "only holders scoped to exactly that location are asked");
        using (var response = await ActAsync(first.Token, await RequireTaskAsync(first.Token, instance), action))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        (await StatusAsync(instance)).Should().Be(outcome);
        Tasks(await InstanceAsync(instance)).Should().Contain((second.UserId, "superseded"));
        (await InboxTaskAsync(second.Token, instance)).Should().BeNull();
    }

    [Fact]
    public async Task AllMode_NeedsEveryApproval_AndOneRejectionRejects()
    {
        var location = await NewLocationRoleAsync();
        await PublishAsync(TestWorkflows.Leave, Stage(new { type = "roleAtLocation", roleCode = location.Role }, mode: "all"));
        var worker = await PersonAsync(location: location.Location);
        var (approvedOne, _) = await StartAsync(worker);
        var (rejectedOne, _) = await StartAsync(worker);

        await DecideAsync(location.First, approvedOne);
        (await StatusAsync(approvedOne)).Should().Be("pending");
        await DecideAsync(location.Second, approvedOne);
        await DecideAsync(location.First, rejectedOne, "reject");

        (await StatusAsync(approvedOne)).Should().Be("approved");
        (await StatusAsync(rejectedOne)).Should().Be("rejected");
    }

    [Fact]
    public async Task SendBack_ReturnsToTheRequester_AndResubmissionStartsAgainFromTheFirstStage_OnTheSameVersion()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), "Lead"), Stage(Manager(2), "Head"));
        var line = await LineAsync();
        var (instance, entity) = await StartAsync(line.Worker, days: 9);
        await DecideAsync(line.Lead, instance);
        await DecideAsync(line.Head, instance, "sendBack", "Too long");
        (await StatusAsync(instance)).Should().Be("sentBack");

        // A newer version published meanwhile does not apply to a request already running.
        await PublishAsync(TestWorkflows.Leave, Stage(Manager(), "Only the lead"));
        await using (var scope = Host.TenantScope(Acme.Tenant.TenantId))
        {
            var service = scope.ServiceProvider.GetRequiredService<IWorkflowService>();
            var resubmitted = await service.ResubmitAsync(TestWorkflows.Leave, entity, line.Worker.UserId, Days(4), CancellationToken.None);
            resubmitted.Workflow!.Status.Should().Be(WorkflowStatus.Pending);
            var repeated = await service.ResubmitAsync(TestWorkflows.Leave, entity, line.Worker.UserId, Days(4), CancellationToken.None);
            repeated.IsSuccess.Should().BeTrue("repeating a resubmission is harmless");
            var other = await service.ResubmitAsync(TestWorkflows.Leave, entity, line.Worker.UserId, Days(1), CancellationToken.None);
            other.Failure!.Code.Should().Be("workflow.instance.not_sent_back");
            var someoneElse = await service.ResubmitAsync(TestWorkflows.Leave, entity, line.Lead.UserId, Days(4), CancellationToken.None);
            someoneElse.Failure!.Code.Should().Be("workflow.instance.not_found");
        }

        (await InboxTaskAsync(line.Head.Token, instance)).Should().BeNull("approval starts again with the first stage");
        await DecideAsync(line.Lead, instance);
        await DecideAsync(line.Head, instance);

        var view = await InstanceAsync(instance);
        view.GetProperty("instance").GetProperty("status").GetString().Should().Be("approved");
        view.GetProperty("instance").GetProperty("round").GetInt32().Should().Be(2);
        view.GetProperty("fields").GetProperty("days").GetDecimal().Should().Be(4);
        await ProcessAsync();
        Decisions.For(entity).Select(d => d.Outcome).Should().Equal(WorkflowOutcome.SentBack, WorkflowOutcome.Approved);
    }

    [Fact]
    public async Task ARepeatApprover_IsAskedAgainAtEachStage()
    {
        var line = await LineAsync();
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()), Stage(Named(line.Lead.UserId), "Again"));
        var (instance, _) = await StartAsync(line.Worker);

        await DecideAsync(line.Lead, instance);
        (await StatusAsync(instance)).Should().Be("pending");
        await DecideAsync(line.Lead, instance);

        (await StatusAsync(instance)).Should().Be("approved");
    }

    [Fact]
    public async Task Withdraw_IsForTheRequester_UntilTheFinalDecision()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var stranger = await PersonAsync();
        var (instance, entity) = await StartAsync(line.Worker);
        var task = await RequireTaskAsync(line.Lead.Token, instance);

        await ExpectAsync(await PostAsync($"/workflow/instances/{instance}/withdraw", new { }, stranger.Token), HttpStatusCode.NotFound, "workflow.instance.not_found");
        await ExpectAsync(await PostAsync($"/workflow/instances/{instance}/withdraw", new { }, line.Lead.Token), HttpStatusCode.Forbidden, "workflow.instance.not_requester");
        using (var withdrawn = await PostAsync($"/workflow/instances/{instance}/withdraw", new { comment = "Plans changed" }, line.Worker.Token))
        {
            withdrawn.StatusCode.Should().Be(HttpStatusCode.OK, await withdrawn.Content.ReadAsStringAsync());
        }

        await ExpectAsync(await ActAsync(line.Lead.Token, task, "approve"), HttpStatusCode.Conflict, "workflow.task.not_pending");
        await ExpectAsync(await PostAsync($"/workflow/instances/{instance}/withdraw", new { }, line.Worker.Token), HttpStatusCode.Conflict, "workflow.instance.finished");
        await ProcessAsync();
        Decisions.For(entity).Should().ContainSingle().Which.Outcome.Should().Be(WorkflowOutcome.Withdrawn);
    }

    [Fact]
    public async Task Comment_IsKeptInTheHistory_AndDecidesNothing()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var (instance, _) = await StartAsync(line.Worker);

        await DecideAsync(line.Lead, instance, "comment", "Who covers you?");

        var view = await InstanceAsync(instance, line.Worker.Token);
        view.GetProperty("instance").GetProperty("status").GetString().Should().Be("pending");
        view.GetProperty("history").EnumerateArray().Should().Contain(h =>
            h.GetProperty("action").GetString() == "commented" && h.GetProperty("comment").GetString() == "Who covers you?");
        var task = await RequireTaskAsync(line.Lead.Token, instance);
        await ExpectAsync(await ActAsync(line.Lead.Token, task, "comment", "  "), HttpStatusCode.UnprocessableEntity, "workflow.comment.required");
        await ExpectAsync(await ActAsync(line.Lead.Token, task, "escalate"), HttpStatusCode.UnprocessableEntity, "workflow.action.invalid");
        await ExpectAsync(await ActAsync(line.Lead.Token, task, "approve", new string('x', 1001)), HttpStatusCode.UnprocessableEntity, "workflow.comment.too_long");
    }

    [Fact]
    public async Task UnitHead_IsTheNearestHeadGoingUpTheTree()
    {
        var head = await PersonAsync();
        await SetHeadAsync(Acme.DepartmentAId, head.EmployeeId);
        await PublishAsync(TestWorkflows.Leave, Stage(new { type = "unitHead" }));
        var worker = await PersonAsync(unit: Acme.SubUnitOfAId);

        var (instance, _) = await StartAsync(worker);

        await DecideAsync(head, instance);
        (await StatusAsync(instance)).Should().Be("approved");
    }

    [Fact]
    public async Task TheUserNamedInTheRequest_Approves_OrTheFallbackWhenNobodyIsNamed()
    {
        var line = await LineAsync();
        var cover = await PersonAsync();
        await PublishAsync(TestWorkflows.Leave, Stage(new { type = "payloadUser", field = "coverUserId" }, fallback: Manager()));

        var named = await SubmitAsync(line.Worker, fields: new() { ["days"] = 1m, ["coverUserId"] = cover.UserId });
        var unnamed = await SubmitAsync(line.Worker);

        await DecideAsync(cover, named.Workflow!.InstanceId);
        await DecideAsync(line.Lead, unnamed.Workflow!.InstanceId);
    }

    [Fact]
    public async Task ABlockedRequest_MovesOn_OnceTheDataIsFixedAndRetried()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var managerEmployee = Id(await CreateEmployeeAsync(Acme));
        var worker = await PersonAsync(new Person(managerEmployee, Guid.Empty, string.Empty));
        var (instance, _) = await StartAsync(worker);
        var blocked = (await InstanceAsync(instance)).GetProperty("instance");
        blocked.GetProperty("status").GetString().Should().Be("blocked");
        blocked.GetProperty("blockedReason").GetString().Should().Contain("no active user account");

        var (token, userId) = await Host.TokenWithScopeAsync(Acme.Tenant.TenantId, Acme.Code, EmployeeApiFixture.Password, ScopeType.Self, null, managerEmployee);
        using (var retried = await PostAsync($"/workflow/instances/{instance}/retry", null, await AdminAsync()))
        {
            retried.StatusCode.Should().Be(HttpStatusCode.OK, await retried.Content.ReadAsStringAsync());
        }

        await DecideAsync(new Person(managerEmployee, userId, token), instance);
        (await StatusAsync(instance)).Should().Be("approved");
    }

    [Fact]
    public async Task ABlockedRequest_CanBeGivenToANamedApprover_WithAReason()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var worker = await PersonAsync();
        var stepIn = await PersonAsync();
        var (instance, _) = await StartAsync(worker);
        (await StatusAsync(instance)).Should().Be("blocked", "the worker has no manager");
        var admin = await AdminAsync();

        await ExpectAsync(await PostAsync($"/workflow/instances/{instance}/assign", new { userId = stepIn.UserId }, admin), HttpStatusCode.UnprocessableEntity, "workflow.assign.reason_required");
        using (var assigned = await PostAsync($"/workflow/instances/{instance}/assign", new { userId = stepIn.UserId, reason = "No manager yet" }, admin))
        {
            assigned.StatusCode.Should().Be(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());
        }

        await DecideAsync(stepIn, instance);
        (await StatusAsync(instance)).Should().Be("approved");
    }

    [Fact]
    public async Task AReassignedTask_MovesToTheNewApprover()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var stepIn = await PersonAsync();
        var (instance, _) = await StartAsync(line.Worker);
        var task = await RequireTaskAsync(line.Lead.Token, instance);

        using (var moved = await PostAsync($"/workflow/tasks/{task}/reassign", new { userId = stepIn.UserId, reason = "On leave" }, await AdminAsync()))
        {
            moved.StatusCode.Should().Be(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
        }

        await ExpectAsync(await ActAsync(line.Lead.Token, task, "approve"), HttpStatusCode.Conflict, "workflow.task.not_pending");
        await DecideAsync(stepIn, instance);
        (await StatusAsync(instance)).Should().Be("approved");
    }

    private sealed record LocationRole(Guid Location, string Role, Person First, Person Second);

    private async Task<LocationRole> NewLocationRoleAsync()
    {
        var location = Acme.SecondLocationId;
        var first = await UserAsync(ScopeType.Location, location);
        var role = await RoleCodeOfAsync(first.UserId);
        var second = await UserAsync();
        await Host.GrantRoleAtAsync(Acme.Tenant.TenantId, second.UserId, role, ScopeType.Location, location);
        return new LocationRole(
            location, role, new Person(Guid.Empty, first.UserId, first.Token), new Person(Guid.Empty, second.UserId, second.Token));
    }

    private async Task SetHeadAsync(Guid unit, Guid employee)
    {
        using var history = await GetAsync($"/org-units/{unit}/heads", Acme.OrgAdmin);
        var version = Version(await JsonAsync(history));
        using var response = await PutAsync($"/org-units/{unit}/head", new { employeeId = employee, rowVersion = version }, Acme.OrgAdmin);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
