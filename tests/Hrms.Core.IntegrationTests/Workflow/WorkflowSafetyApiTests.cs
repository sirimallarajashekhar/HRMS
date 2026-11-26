using System.Net;
using FluentAssertions;
using Hrms.Contracts.Workflow;
using Hrms.Core.IntegrationTests.Employee;
using Hrms.Core.IntegrationTests.Persistence;
using Hrms.Identity.Domain.Roles;
using Hrms.Identity.Domain.Users;

namespace Hrms.Core.IntegrationTests.Workflow;

/// <summary>The rules nothing may bypass: no self-approval by any route, no approval without an approver, one winner per decision.</summary>
[Collection(MySqlTestGroup.Name)]
public class WorkflowSafetyApiTests(EmployeeApiFixture fixture) : WorkflowApiTestBase(fixture), IClassFixture<EmployeeApiFixture>
{
    // --- nobody approves their own request -------------------------------------------------------------------------------

    [Fact]
    public async Task AManagerWhoRaisesARequestForAReport_IsNotAskedToApproveIt()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();

        var (instance, _) = await StartAsync(line.Worker, requester: line.Lead.UserId);

        (await StatusAsync(instance)).Should().Be("blocked");
        (await InboxTaskAsync(line.Lead.Token, instance)).Should().BeNull();
    }

    [Fact]
    public async Task TheEmployeeARequestIsAbout_CannotApproveIt_EvenWhenNamed()
    {
        var subject = await PersonAsync();
        var hr = await PersonAsync();
        await PublishAsync(TestWorkflows.Leave, Stage(Named(subject.UserId)));

        var (instance, _) = await StartAsync(subject, requester: hr.UserId);

        (await StatusAsync(instance)).Should().Be("blocked");
    }

    [Fact]
    public async Task ADelegateWhoIsTheRequesterOrTheSubject_NeitherSeesNorDecidesTheTask()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var requester = await PersonAsync();
        var (instance, entity) = await StartAsync(line.Worker, requester: requester.UserId);
        var task = await RequireTaskAsync(line.Lead.Token, instance);
        foreach (var barred in new[] { requester, line.Worker })
        {
            using var delegated = await DelegateAsync(line.Lead.Token, barred.UserId);
            delegated.StatusCode.Should().Be(HttpStatusCode.Created, await delegated.Content.ReadAsStringAsync());

            (await InboxTaskAsync(barred.Token, instance)).Should().BeNull();
            (await CanDecideAsync(barred.UserId, entity)).Should().BeFalse();
            using var detail = await GetAsync($"/workflow/tasks/{task}", barred.Token);
            detail.StatusCode.Should().Be(HttpStatusCode.NotFound);
            await ExpectAsync(await ActAsync(barred.Token, task, "approve"), HttpStatusCode.Forbidden, "workflow.task.own_request");
        }

        (await StatusAsync(instance)).Should().Be("pending");
    }

    [Fact]
    public async Task AnAdministrator_CannotMoveATaskToThemselves_TheRequester_OrTheSubject()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var requester = await PersonAsync();
        var (instance, _) = await StartAsync(line.Worker, requester: requester.UserId);
        var task = await RequireTaskAsync(line.Lead.Token, instance);
        var admin = await AdminAsync();
        Task<HttpResponseMessage> Move(Guid to) => PostAsync($"/workflow/tasks/{task}/reassign", new { userId = to, reason = "x" }, admin);

        await ExpectAsync(await Move(await AdminUserIdAsync()), HttpStatusCode.Forbidden, "workflow.assign.to_self");
        await ExpectAsync(await Move(requester.UserId), HttpStatusCode.Forbidden, "workflow.task.own_request");
        await ExpectAsync(await Move(line.Worker.UserId), HttpStatusCode.Forbidden, "workflow.task.own_request");
        await ExpectAsync(await Move(line.Lead.UserId), HttpStatusCode.UnprocessableEntity, "workflow.assign.already_approver");
        await ExpectAsync(await Move(Guid.NewGuid()), HttpStatusCode.UnprocessableEntity, "workflow.assign.user_not_found");
    }

    [Fact]
    public async Task AnAdministratorWhoRaisedTheRequest_CannotRepairIt()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var admin = await AdminAsync();
        var (instance, _) = await StartAsync(line.Worker, requester: await AdminUserIdAsync());
        var task = await RequireTaskAsync(line.Lead.Token, instance);

        await ExpectAsync(
            await PostAsync($"/workflow/tasks/{task}/reassign", new { userId = line.Head.UserId, reason = "friend" }, admin),
            HttpStatusCode.Forbidden, "workflow.admin.own_request");
    }

    [Fact]
    public async Task AnAdministrator_CannotDelegateSomeoneElsesApprovalsToThemselves()
    {
        var boss = await PersonAsync();

        await ExpectAsync(
            await DelegateAsync(await AdminAsync(), await AdminUserIdAsync(), delegator: boss.UserId),
            HttpStatusCode.Forbidden, "workflow.delegation.self_grant");
    }

    [Fact]
    public async Task OnePerson_CannotSupplyTwoApprovalsOfAnEveryoneStage_ThroughADelegation()
    {
        var location = Acme.SecondLocationId;
        var first = await UserAsync(ScopeType.Location, location);
        var role = await RoleCodeOfAsync(first.UserId);
        var second = await UserAsync();
        await Host.GrantRoleAtAsync(Acme.Tenant.TenantId, second.UserId, role, ScopeType.Location, location);
        await PublishAsync(TestWorkflows.Leave, Stage(new { type = "roleAtLocation", roleCode = role }, mode: "all"));
        var (instance, entity) = await StartAsync(await PersonAsync(location: location));
        using (var delegated = await DelegateAsync(second.Token, first.UserId))
        {
            delegated.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var both = (await InboxAsync(first.Token)).Where(t => t.GetProperty("instanceId").GetGuid() == instance).ToList();
        both.Should().HaveCount(2);
        var own = both.Single(t => t.GetProperty("assigneeUserId").GetGuid() == first.UserId);
        var delegatedTask = both.Single(t => t.GetProperty("assigneeUserId").GetGuid() == second.UserId);
        using (var approved = await ActAsync(first.Token, Id(own), "approve"))
        {
            approved.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await CanDecideAsync(first.UserId, entity)).Should().BeFalse();
        await ExpectAsync(await ActAsync(first.Token, Id(delegatedTask), "approve"), HttpStatusCode.Conflict, "workflow.task.already_decided_by_you");
        (await StatusAsync(instance)).Should().Be("pending");
    }

    // --- delegation ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADelegate_ActsOnExistingTasks_IsRecordedAsActingForTheAssignee_AndStopsWhenRevoked()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var deputy = await PersonAsync();
        var (first, firstEntity) = await StartAsync(line.Worker);
        var (second, secondEntity) = await StartAsync(line.Worker);
        using var delegated = await DelegateAsync(line.Lead.Token, deputy.UserId);
        var delegationId = Id(await JsonAsync(delegated));

        var inbox = await InboxAsync(deputy.Token);
        inbox.Single(t => t.GetProperty("instanceId").GetGuid() == first).GetProperty("onBehalfOfUserId").GetGuid().Should().Be(line.Lead.UserId);
        await DecideAsync(deputy, first);

        var history = (await InstanceAsync(first)).GetProperty("history").EnumerateArray().Single(h => h.GetProperty("action").GetString() == "approved");
        history.GetProperty("actorUserId").GetGuid().Should().Be(deputy.UserId);
        history.GetProperty("onBehalfOfUserId").GetGuid().Should().Be(line.Lead.UserId);

        var task = await RequireTaskAsync(deputy.Token, second);
        using (var revoked = await DeleteAsync($"/workflow/delegations/{delegationId}", line.Lead.Token))
        {
            revoked.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await InboxTaskAsync(deputy.Token, second)).Should().BeNull();
        (await CanDecideAsync(deputy.UserId, secondEntity)).Should().BeFalse();
        await ExpectAsync(await ActAsync(deputy.Token, task, "approve"), HttpStatusCode.NotFound, "workflow.task.not_found");
        firstEntity.Should().NotBe(secondEntity);
    }

    [Fact]
    public async Task Delegation_IsNotPassedOn_AndIsLimitedToItsWorkflow_AndEndsWhenTheDelegatorIsDisabled()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var deputy = await PersonAsync();
        var deputysDeputy = await PersonAsync();
        var otherKeyOnly = await PersonAsync();
        var (instance, _) = await StartAsync(line.Worker);
        (await DelegateAsync(line.Lead.Token, deputy.UserId)).Dispose();
        (await DelegateAsync(deputy.Token, deputysDeputy.UserId)).Dispose();
        (await DelegateAsync(line.Lead.Token, otherKeyOnly.UserId, key: TestWorkflows.Auto)).Dispose();

        (await InboxTaskAsync(deputy.Token, instance)).Should().NotBeNull();
        (await InboxTaskAsync(deputysDeputy.Token, instance)).Should().BeNull("a delegation is not passed on");
        (await InboxTaskAsync(otherKeyOnly.Token, instance)).Should().BeNull("it covers another workflow only");

        await DisableAsync(line.Lead.UserId);
        (await InboxTaskAsync(deputy.Token, instance)).Should().BeNull();
    }

    [Fact]
    public async Task ADisabledAccount_CannotDecide_EvenWithATokenIssuedBefore()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var (instance, _) = await StartAsync(line.Worker);
        var task = await RequireTaskAsync(line.Lead.Token, instance);

        await DisableAsync(line.Lead.UserId);

        await ExpectAsync(await ActAsync(line.Lead.Token, task, "approve"), HttpStatusCode.NotFound, "workflow.task.not_found");
        (await InboxAsync(line.Lead.Token)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheSessionThatStillOwesMfa_CannotReachTheInboxOrAct()
    {
        var email = $"mfa-{Guid.NewGuid():N}@example.com";
        await Host.AddUserAsync(Acme.Tenant.TenantId, email, EmployeeApiFixture.Password, DefaultRoles.HrAdmin);
        var (status, body) = await Host.PasswordAsync(Acme.Code, email, EmployeeApiFixture.Password);
        status.Should().Be(200);
        var limited = body.GetProperty("access_token").GetString();

        foreach (var response in new[]
                 {
                     await GetAsync("/workflow/tasks", limited),
                     await ActAsync(limited!, Guid.NewGuid(), "approve"),
                     await DelegateAsync(limited!, Guid.NewGuid()),
                 })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            response.Dispose();
        }
    }

    // --- concurrency and repeats -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ApprovalsAtTheSameMoment_OfAnEveryoneStage_NeverLeaveItStuck()
    {
        var location = Acme.SecondLocationId;
        var first = await UserAsync(ScopeType.Location, location);
        var role = await RoleCodeOfAsync(first.UserId);
        var second = await UserAsync();
        await Host.GrantRoleAtAsync(Acme.Tenant.TenantId, second.UserId, role, ScopeType.Location, location);
        await PublishAsync(TestWorkflows.Leave, Stage(new { type = "roleAtLocation", roleCode = role }, mode: "all"));
        var worker = await PersonAsync(location: location);

        for (var round = 0; round < 5; round++)
        {
            var (instance, _) = await StartAsync(worker);
            var approvers = new[] { first, second };
            var tasks = new[] { await RequireTaskAsync(first.Token, instance), await RequireTaskAsync(second.Token, instance) };

            var responses = await Task.WhenAll(approvers.Select((a, i) => ActAsync(a.Token, tasks[i], "approve")));

            for (var i = 0; i < responses.Length; i++)
            {
                responses[i].StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
                if (responses[i].StatusCode == HttpStatusCode.Conflict)
                {
                    using var again = await ActAsync(approvers[i].Token, tasks[i], "approve");
                    again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
                }

                responses[i].Dispose();
            }

            (await StatusAsync(instance)).Should().Be("approved");
        }
    }

    [Fact]
    public async Task ApproveAndRejectAtTheSameMoment_HaveOneWinner_AndOneResult()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();

        for (var round = 0; round < 5; round++)
        {
            var (instance, entity) = await StartAsync(line.Worker);
            var task = await RequireTaskAsync(line.Lead.Token, instance);

            var responses = await Task.WhenAll(ActAsync(line.Lead.Token, task, "approve"), ActAsync(line.Lead.Token, task, "reject"));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
            var expected = responses[0].StatusCode == HttpStatusCode.OK ? "approved" : "rejected";
            (await StatusAsync(instance)).Should().Be(expected);
            await ProcessAsync();
            Decisions.For(entity).Should().ContainSingle();
            Array.ForEach(responses, r => r.Dispose());
        }
    }

    [Fact]
    public async Task ARepeatedStart_ReturnsTheSameWorkflow_EvenAfterTheDecision()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var (instance, entity) = await StartAsync(line.Worker);
        await DecideAsync(line.Lead, instance);

        var repeated = await SubmitAsync(line.Worker, entityId: entity);

        repeated.Workflow.Should().Be(new WorkflowHandle(instance, WorkflowStatus.Approved));
    }

    [Fact]
    public async Task AStartWithDifferentDetails_IsRefused_SoAnOldApprovalCannotCoverNewValues()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var (instance, entity) = await StartAsync(line.Worker, days: 1);
        await DecideAsync(line.Lead, instance);

        var moreDays = await SubmitAsync(line.Worker, fields: Days(30), entityId: entity);
        var otherSubject = await SubmitAsync(line.Lead, fields: Days(1), entityId: entity);

        moreDays.Workflow.Should().BeNull();
        moreDays.Failure!.Code.Should().Be("workflow.instance.start_mismatch");
        otherSubject.Failure!.Code.Should().Be("workflow.instance.start_mismatch");
    }

    [Fact]
    public async Task StartsAtTheSameMoment_MakeOneWorkflow()
    {
        await PublishAsync(TestWorkflows.Leave, Stage(Manager()));
        var line = await LineAsync();
        var entity = Guid.CreateVersion7();

        var submissions = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SubmitAsync(line.Worker, entityId: entity)));

        submissions.Should().OnlyContain(s => s.IsSuccess);
        submissions.Select(s => s.Workflow!.InstanceId).Distinct().Should().ContainSingle();
        Tasks(await InstanceAsync(submissions[0].Workflow!.InstanceId)).Should().ContainSingle();
    }

    [Fact]
    public async Task AStart_ThatCannotBeTrusted_IsRefused()
    {
        var line = await LineAsync();

        var undeclared = await SubmitAsync(line.Worker, fields: new() { ["days"] = 1m, ["bankAccount"] = "123" });
        var missing = await SubmitAsync(line.Worker, fields: []);
        var unknownKey = await SubmitAsync(line.Worker, key: "test.nothing");
        var unknownRequester = await SubmitAsync(line.Worker, requester: Guid.NewGuid());
        var unknownSubject = await SubmitAsync(new Person(Guid.NewGuid(), line.Worker.UserId, string.Empty));

        undeclared.Failure!.Code.Should().Be("workflow.payload.unknown_field");
        missing.Failure!.Code.Should().Be("workflow.payload.field_required");
        unknownKey.Failure!.Code.Should().Be("workflow.key.unknown");
        unknownRequester.Failure!.Code.Should().Be("workflow.start.requester_not_found");
        unknownSubject.Failure!.Code.Should().Be("workflow.start.subject_not_found");
    }

    [Fact]
    public async Task WithNoPublishedDefinition_NothingIsApproved()
    {
        // The Auto workflow is never published in this class.
        var line = await LineAsync();

        var submission = await SubmitAsync(line.Worker, key: TestWorkflows.Auto);

        submission.Workflow.Should().BeNull();
        submission.Failure!.Code.Should().Be("workflow.definition.missing");
    }
}
