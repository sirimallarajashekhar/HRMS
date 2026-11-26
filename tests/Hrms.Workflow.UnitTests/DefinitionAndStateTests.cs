using FluentAssertions;
using Hrms.Contracts.Workflow;
using Hrms.Workflow.Domain;

namespace Hrms.Workflow.UnitTests;

public class WorkflowStageTests
{
    private static string? CodeFor(params StageSpec[] stages) =>
        WorkflowStage.Build(Guid.CreateVersion7(), stages, TestData.Leave()) is { IsFailure: true } failed ? failed.Error.Code : null;

    private const string Long = """{"field":"days","op":"gt","value":3}""";

    [Fact]
    public void Build_ValidStages_AreNumberedInOrder_AndKeepTheirRules()
    {
        var stages = WorkflowStage.Build(
            Guid.CreateVersion7(),
            [TestData.Stage("Manager"), TestData.Stage("Second", new(ApproverType.ManagerLevel, Level: 2), Long, StageMode.All)],
            TestData.Leave()).Value;

        stages.Select(s => s.StageOrder).Should().Equal(1, 2);
        stages[1].Approver.Should().Be(new ApproverRule(ApproverType.ManagerLevel, Level: 2));
        stages[1].ConditionJson.Should().Be(Long);
        stages[1].Mode.Should().Be(StageMode.All);
    }

    [Fact]
    public void Build_WhenEveryStageHasACondition_IsRefused_SoNothingCanPassWithoutAnApprover() =>
        CodeFor(TestData.Stage(condition: Long), TestData.Stage("Other", condition: Long))
            .Should().Be("workflow.definition.no_unconditional_stage");

    [Fact]
    public void Build_NoStagesOrTooMany_IsRefused()
    {
        CodeFor().Should().Be("workflow.definition.stage_count_invalid");
        CodeFor([.. Enumerable.Repeat(TestData.Stage(), WorkflowStage.MaxStages + 1)]).Should().Be("workflow.definition.stage_count_invalid");
    }

    public static TheoryData<ApproverRule, bool> Rules => new()
    {
        { new(ApproverType.ManagerLevel, Level: 1), true },
        { new(ApproverType.ManagerLevel, Level: 10), true },
        { new(ApproverType.ManagerLevel), false },
        { new(ApproverType.ManagerLevel, Level: 0), false },
        { new(ApproverType.ManagerLevel, Level: 11), false },
        { new(ApproverType.ManagerLevel, Level: 1, RoleCode: "hr_admin"), false },
        { new(ApproverType.UnitHead), true },
        { new(ApproverType.UnitHead, OrgUnitTypeId: Guid.CreateVersion7()), true },
        { new(ApproverType.UnitHead, OrgUnitTypeId: Guid.Empty), false },
        { new(ApproverType.UnitHead, Level: 1), false },
        { new(ApproverType.RoleAtLocation, RoleCode: "hr_admin"), true },
        { new(ApproverType.RoleAtLocation, RoleCode: " "), false },
        { new(ApproverType.RoleAtLocation), false },
        { new(ApproverType.NamedUser, UserId: Guid.CreateVersion7()), true },
        { new(ApproverType.NamedUser, UserId: Guid.Empty), false },
        { new(ApproverType.NamedUser), false },
        { new(ApproverType.PayloadUser, Field: "coverUserId"), true },
        { new(ApproverType.PayloadUser, Field: "secondUserId"), true },
        { new(ApproverType.PayloadUser, Field: "leaveType"), false },
        { new(ApproverType.PayloadUser, Field: "nobody"), false },
        { new((ApproverType)99), false },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Build_EachApproverRule_TakesOnlyWhatItsTypeNeeds(ApproverRule rule, bool valid) =>
        CodeFor(TestData.Stage(approver: rule)).Should().Be(valid ? null : "workflow.definition.approver_invalid");

    [Fact]
    public void Build_BadFallbackOrEscalationRules_AreRefusedToo()
    {
        var bad = new ApproverRule(ApproverType.ManagerLevel);

        CodeFor(TestData.Stage() with { Fallback = bad }).Should().Be("workflow.definition.approver_invalid");
        CodeFor(TestData.Stage() with { SlaHours = 1, OnTimeout = TimeoutAction.Escalate, Escalation = bad })
            .Should().Be("workflow.definition.approver_invalid");
    }

    [Theory]
    [InlineData(24, null, false, "workflow.definition.timeout_invalid")]
    [InlineData(null, TimeoutAction.Remind, false, "workflow.definition.timeout_invalid")]
    [InlineData(0, TimeoutAction.Remind, false, "workflow.definition.sla_invalid")]
    [InlineData(2161, TimeoutAction.Remind, false, "workflow.definition.sla_invalid")]
    [InlineData(24, TimeoutAction.Escalate, false, "workflow.definition.escalation_invalid")]
    [InlineData(24, TimeoutAction.Remind, true, "workflow.definition.escalation_invalid")]
    [InlineData(24, TimeoutAction.Approve, false, "workflow.definition.auto_approve_not_allowed")]
    [InlineData(24, TimeoutAction.Escalate, true, null)]
    [InlineData(24, TimeoutAction.Reject, false, null)]
    [InlineData(2160, TimeoutAction.Remind, false, null)]
    public void Build_DeadlineSettings_AreChecked(int? hours, TimeoutAction? action, bool escalation, string? expected) =>
        CodeFor(TestData.Stage() with
        {
            SlaHours = hours,
            OnTimeout = action,
            Escalation = escalation ? new ApproverRule(ApproverType.ManagerLevel, Level: 2) : null,
        }).Should().Be(expected);

    [Fact]
    public void Build_ApprovalByTimeout_IsAllowedOnlyWhenTheOwningModuleSaysSo() =>
        WorkflowStage.Build(Guid.CreateVersion7(), [TestData.Stage() with { SlaHours = 1, OnTimeout = TimeoutAction.Approve }], TestData.Leave(true))
            .IsSuccess.Should().BeTrue();

    [Fact]
    public void Build_ABadCondition_NamesTheStage()
    {
        var result = WorkflowStage.Build(Guid.CreateVersion7(), [TestData.Stage(), TestData.Stage("B", condition: "{}")], TestData.Leave());

        result.Error.Code.Should().Be("workflow.condition.invalid");
        result.Error.Message.Should().StartWith("Stage 2:");
    }
}

public class WorkflowDefinitionTests
{
    [Fact]
    public void Publish_FreezesTheVersion()
    {
        var definition = TestData.PublishedDefinition();

        definition.Edit("New name").Error.Code.Should().Be("workflow.definition.not_draft");
        definition.Publish(TestData.Today, TestData.Today, null, TestData.Now).Error.Code.Should().Be("workflow.definition.not_draft");
    }

    [Theory]
    [InlineData(-1, null, "workflow.definition.effective_from_past")]
    [InlineData(1, 2, "workflow.definition.effective_from_overlaps")]
    [InlineData(2, 2, null)]
    [InlineData(0, null, null)]
    [InlineData(5, -30, null)]
    public void Publish_StartsTodayOrLater_AndNotBeforeTheLatestVersion(int fromOffset, int? latestOffset, string? expected)
    {
        var definition = WorkflowDefinition.Create("leave.apply", 2, "Leave").Value;

        var result = definition.Publish(
            TestData.Today.AddDays(fromOffset),
            TestData.Today,
            latestOffset is { } days ? TestData.Today.AddDays(days) : null,
            TestData.Now);

        (result.IsFailure ? result.Error.Code : null).Should().Be(expected);
    }

    [Fact]
    public void Edit_ChangesTheRow_EvenWhenOnlyTheStagesChanged()
    {
        var definition = WorkflowDefinition.Create("leave.apply", 1, "Leave").Value;

        definition.Edit("Leave").IsSuccess.Should().BeTrue();

        definition.Revision.Should().Be(1);
    }
}

public class StageRulesTests
{
    private static WorkflowTask Task(char status)
    {
        var task = WorkflowTask.Create(Guid.CreateVersion7(), 1, 1, Guid.CreateVersion7(), TaskOrigin.Rule, null, TestData.Now);
        switch (status)
        {
            case 'A': task.Decide(WorkflowTaskStatus.Approved, task.AssigneeUserId, TestData.Now); break;
            case 'R': task.Decide(WorkflowTaskStatus.Rejected, task.AssigneeUserId, TestData.Now); break;
            case 'S': task.Supersede(TestData.Now); break;
            case 'C': task.Cancel(TestData.Now); break;
        }

        return task;
    }

    [Theory]
    // P pending, A approved, R rejected, S superseded, C cancelled.
    [InlineData(StageMode.Any, "", StageResult.Waiting)]
    [InlineData(StageMode.Any, "PP", StageResult.Waiting)]
    [InlineData(StageMode.Any, "AP", StageResult.Approved)]
    [InlineData(StageMode.Any, "RP", StageResult.Rejected)]
    [InlineData(StageMode.Any, "CS", StageResult.Waiting)]
    [InlineData(StageMode.All, "", StageResult.Waiting)]
    [InlineData(StageMode.All, "AP", StageResult.Waiting)]
    [InlineData(StageMode.All, "AA", StageResult.Approved)]
    [InlineData(StageMode.All, "AR", StageResult.Rejected)]
    [InlineData(StageMode.All, "RP", StageResult.Rejected)]
    [InlineData(StageMode.All, "AC", StageResult.Approved)]
    [InlineData(StageMode.All, "CC", StageResult.Waiting)]
    public void Evaluate_FollowsTheStageMode_AndNeverApprovesAnEmptyStage(StageMode mode, string tasks, StageResult expected) =>
        StageRules.Evaluate(mode, tasks.Select(Task).ToList()).Should().Be(expected);

    [Fact]
    public void Task_IsDecidedOnlyOnce()
    {
        var task = Task('A');

        FluentActions.Invoking(() => task.Decide(WorkflowTaskStatus.Rejected, null, TestData.Now)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => task.Cancel(TestData.Now)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Task_WithADeadline_IsDueThatManyHoursLater() =>
        WorkflowTask.Create(Guid.CreateVersion7(), 1, 1, Guid.CreateVersion7(), TaskOrigin.Rule, 48, TestData.Now)
            .DueAt.Should().Be(TestData.Now.AddHours(48));
}

public class WorkflowInstanceTests
{
    private static WorkflowInstance Start() => WorkflowInstance.Start(
        TestData.PublishedDefinition(), "leave_request", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "{}");

    [Fact]
    public void Record_NumbersTheHistory_AndChangesTheRow()
    {
        var instance = Start();

        var first = instance.Record(WorkflowAction.Started, TestData.Now);
        var second = instance.Record(WorkflowAction.Commented, TestData.Now, comment: "hello");

        (first.Sequence, second.Sequence, instance.ActionCount).Should().Be((1, 2, 2));
    }

    [Fact]
    public void SendBackThenResubmit_StartsANewRound_FromTheBeginning()
    {
        var instance = Start();
        instance.EnterStage(2);
        instance.SendBack();

        instance.Resubmit("""{"days":2}""");

        (instance.Status, instance.Round, instance.CurrentStage).Should().Be((WorkflowStatus.Pending, 2, 0));
    }

    [Fact]
    public void ABlockedWorkflow_CannotBeApproved_UntilAStageOpensAgain()
    {
        var instance = Start();
        instance.EnterStage(1);
        instance.Block("nobody");

        FluentActions.Invoking(() => instance.Approve(TestData.Now)).Should().Throw<InvalidOperationException>();
        instance.EnterStage(1);
        instance.Approve(TestData.Now);
        instance.Status.Should().Be(WorkflowStatus.Approved);
        instance.BlockedReason.Should().BeNull();
    }

    [Fact]
    public void AFinishedWorkflow_NeverChangesStateAgain()
    {
        var instance = Start();
        instance.Reject(TestData.Now);

        instance.IsOpen.Should().BeFalse();
        FluentActions.Invoking(() => instance.Withdraw(TestData.Now)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => instance.Approve(TestData.Now)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => instance.EnterStage(1)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Start_FromADraft_IsABug() =>
        FluentActions.Invoking(() => WorkflowInstance.Start(
                WorkflowDefinition.Create("leave.apply", 1, "Leave").Value, "x", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "{}"))
            .Should().Throw<ArgumentException>();
}

public class DelegationTests
{
    private static readonly Guid From = Guid.CreateVersion7();
    private static readonly Guid To = Guid.CreateVersion7();

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(0, 89, null)]
    [InlineData(0, 90, "workflow.delegation.too_long")]
    [InlineData(5, 4, "workflow.delegation.dates_invalid")]
    [InlineData(-10, -1, "workflow.delegation.dates_invalid")]
    [InlineData(-10, 0, null)]
    public void Create_ChecksTheDates(int startOffset, int endOffset, string? expected)
    {
        var result = Delegation.Create(From, To, null, TestData.Today.AddDays(startOffset), TestData.Today.AddDays(endOffset), TestData.Today);

        (result.IsFailure ? result.Error.Code : null).Should().Be(expected);
    }

    [Fact]
    public void Create_ToOneself_IsRefused() =>
        Delegation.Create(From, From, null, TestData.Today, TestData.Today, TestData.Today).Error.Code
            .Should().Be("workflow.delegation.people_invalid");

    [Fact]
    public void ADelegation_WorksOnlyInItsDates_ForItsKey_UntilRevoked()
    {
        var one = Delegation.Create(From, To, "leave.apply", TestData.Today, TestData.Today.AddDays(2), TestData.Today).Value;
        var all = Delegation.Create(From, To, null, TestData.Today, TestData.Today, TestData.Today).Value;

        one.IsActive(TestData.Today.AddDays(-1)).Should().BeFalse();
        one.IsActive(TestData.Today.AddDays(2)).Should().BeTrue();
        one.IsActive(TestData.Today.AddDays(3)).Should().BeFalse();
        (one.Covers("leave.apply"), one.Covers("loan.request"), all.Covers("loan.request")).Should().Be((true, false, true));

        one.Revoke(TestData.Now).IsSuccess.Should().BeTrue();
        one.IsActive(TestData.Today).Should().BeFalse();
        one.Revoke(TestData.Now).Error.Code.Should().Be("workflow.delegation.already_revoked");
    }
}

public class OutboxMessageTests
{
    [Fact]
    public void Failures_BackOff_AndAreGivenUpAfterTheLimit_UntilRetried()
    {
        var message = WorkflowOutboxMessage.Create(Guid.CreateVersion7(), 1, "WorkflowApproved", "{}", TestData.Now);
        message.NextAttemptAt.Should().Be(TestData.Now);
        message.Retry(TestData.Now).Error.Code.Should().Be("workflow.delivery.not_failed");

        var delays = new List<double>();
        for (var attempt = 1; attempt <= WorkflowOutboxMessage.MaxAttempts; attempt++)
        {
            message.IsExhausted.Should().BeFalse();
            message.MarkFailed(new string('e', 500), TestData.Now);
            delays.Add((message.NextAttemptAt - TestData.Now).TotalMinutes);
        }

        delays.Should().Equal(1, 2, 4, 8, 16, 32, 60, 60, 60, 60);
        message.IsExhausted.Should().BeTrue();
        message.LastError!.Length.Should().Be(WorkflowOutboxMessage.ErrorMaxLength);
        message.Retry(TestData.Now).IsSuccess.Should().BeTrue();
        message.IsExhausted.Should().BeFalse();
    }
}
