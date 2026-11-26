using FluentAssertions;
using Hrms.Contracts.Workflow;
using Hrms.Workflow.Domain;

namespace Hrms.Workflow.UnitTests;

internal static class TestData
{
    public static readonly DateOnly Today = new(2026, 11, 26);
    public static readonly DateTime Now = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    public static WorkflowKeyDescriptor Leave(bool allowAutoApprove = false) => new(
        "leave.apply",
        "leave_request",
        [
            new("days", WorkflowFieldType.Number),
            new("leaveType", WorkflowFieldType.Text),
            new("paid", WorkflowFieldType.Boolean),
            new("startsOn", WorkflowFieldType.Date),
            new("coverUserId", WorkflowFieldType.UserId),
            new("note", WorkflowFieldType.Text, Required: false),
            new("secondUserId", WorkflowFieldType.UserId, Required: false),
        ],
        allowAutoApprove);

    public static readonly Guid Cover = Guid.Parse("01920000-0000-7000-8000-0000000000c1");

    public static Dictionary<string, object?> Fields() => new()
    {
        ["days"] = 5m,
        ["leaveType"] = "annual",
        ["paid"] = true,
        ["startsOn"] = new DateOnly(2027, 1, 10),
        ["coverUserId"] = Cover,
    };

    public static IReadOnlyDictionary<string, object> Values() => WorkflowPayload.Create(Leave(), Fields()).Value.Values;

    public static StageSpec Stage(
        string name = "Manager", ApproverRule? approver = null, string? condition = null, StageMode mode = StageMode.Any) =>
        new(name, approver ?? new ApproverRule(ApproverType.ManagerLevel, Level: 1), Condition: condition, Mode: mode);

    public static WorkflowDefinition PublishedDefinition()
    {
        var definition = WorkflowDefinition.Create("leave.apply", 1, "Leave").Value;
        definition.Publish(Today, Today, null, Now).IsSuccess.Should().BeTrue();
        return definition;
    }
}
