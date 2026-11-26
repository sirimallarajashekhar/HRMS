using System.Text.Json;
using System.Text.Json.Serialization;
using Hrms.Contracts.Workflow;
using Hrms.SharedKernel;

namespace Hrms.Workflow.Domain;

public enum ApproverType
{
    /// <summary>The subject's manager <c>Level</c> steps up the reporting line.</summary>
    ManagerLevel = 1,

    /// <summary>The nearest head from the subject's unit upwards, or the head of the unit of a given type.</summary>
    UnitHead = 2,

    /// <summary>Everyone holding a role scoped to the subject's location.</summary>
    RoleAtLocation = 3,

    NamedUser = 4,

    /// <summary>The user the requesting module named in a field.</summary>
    PayloadUser = 5,
}

/// <summary>Who should approve, as data. Only the properties the type needs may be set.</summary>
public sealed record ApproverRule(
    ApproverType Type,
    int? Level = null,
    Guid? OrgUnitTypeId = null,
    string? RoleCode = null,
    Guid? UserId = null,
    string? Field = null)
{
    public const int MaxLevel = 10;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public Result Validate(WorkflowKeyDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var used = (Level is not null, OrgUnitTypeId is not null, RoleCode is not null, UserId is not null, Field is not null);
        return Type switch
        {
            ApproverType.ManagerLevel when used == (true, false, false, false, false) && Level is >= 1 and <= MaxLevel => Result.Success(),
            ApproverType.ManagerLevel => Fail($"A manager rule takes only a level from 1 to {MaxLevel}."),
            ApproverType.UnitHead when used is (false, _, false, false, false) && OrgUnitTypeId != Guid.Empty => Result.Success(),
            ApproverType.UnitHead => Fail("A unit head rule takes only an optional unit type."),
            ApproverType.RoleAtLocation when used == (false, false, true, false, false) && !string.IsNullOrWhiteSpace(RoleCode) && RoleCode.Length <= 64 => Result.Success(),
            ApproverType.RoleAtLocation => Fail("A role rule takes only a role code."),
            ApproverType.NamedUser when used == (false, false, false, true, false) && UserId != Guid.Empty => Result.Success(),
            ApproverType.NamedUser => Fail("A named user rule takes only a user id."),
            ApproverType.PayloadUser when used == (false, false, false, false, true)
                && descriptor.Fields.Any(f => f.Name == Field && f.Type == WorkflowFieldType.UserId) => Result.Success(),
            ApproverType.PayloadUser => Fail("A request field rule takes only the name of a user id field of this workflow."),
            _ => Fail("That approver type does not exist."),
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ApproverRule FromJson(string json) =>
        JsonSerializer.Deserialize<ApproverRule>(json, Options) ?? throw new InvalidOperationException("A stored approver rule is empty.");

    private static Result Fail(string message) => Result.Failure(Error.Validation("workflow.definition.approver_invalid", message));
}
