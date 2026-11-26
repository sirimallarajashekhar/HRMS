using FluentAssertions;
using Hrms.Contracts.Workflow;
using Hrms.Workflow.Domain;

namespace Hrms.Workflow.UnitTests;

public class StageConditionTests
{
    private static string Compare(string field, string op, string value) => $$"""{"field":"{{field}}","op":"{{op}}","value":{{value}}}""";

    [Theory]
    // The request has days 5, leaveType "annual", paid true, startsOn 2027-01-10 and the cover user.
    [InlineData("days", "eq", "5", true)]
    [InlineData("days", "eq", "5.00", true)]
    [InlineData("days", "ne", "5", false)]
    [InlineData("days", "gt", "5", false)]
    [InlineData("days", "gt", "4.99", true)]
    [InlineData("days", "gte", "5", true)]
    [InlineData("days", "lt", "5", false)]
    [InlineData("days", "lte", "5", true)]
    [InlineData("days", "in", "[1,5,9]", true)]
    [InlineData("days", "in", "[1,9]", false)]
    [InlineData("leaveType", "eq", "\"annual\"", true)]
    [InlineData("leaveType", "eq", "\"Annual\"", false)]
    [InlineData("leaveType", "ne", "\"sick\"", true)]
    [InlineData("leaveType", "in", "[\"sick\",\"annual\"]", true)]
    [InlineData("paid", "eq", "true", true)]
    [InlineData("paid", "ne", "true", false)]
    [InlineData("startsOn", "gt", "\"2027-01-09\"", true)]
    [InlineData("startsOn", "lt", "\"2027-01-10\"", false)]
    [InlineData("startsOn", "lte", "\"2027-01-10\"", true)]
    [InlineData("coverUserId", "eq", "\"01920000-0000-7000-8000-0000000000c1\"", true)]
    [InlineData("coverUserId", "in", "[\"01920000-0000-7000-8000-0000000000c2\"]", false)]
    public void Evaluate_EachOperator_ComparesTheTypedValue(string field, string op, string value, bool expected)
    {
        var condition = StageCondition.Parse(Compare(field, op, value), TestData.Leave());

        condition.IsSuccess.Should().BeTrue(condition.IsFailure ? condition.Error.Message : string.Empty);
        condition.Value.Evaluate(TestData.Values()).Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"all":[{"field":"days","op":"gt","value":3},{"field":"paid","op":"eq","value":true}]}""", true)]
    [InlineData("""{"all":[{"field":"days","op":"gt","value":3},{"field":"paid","op":"eq","value":false}]}""", false)]
    [InlineData("""{"any":[{"field":"days","op":"gt","value":30},{"field":"paid","op":"eq","value":true}]}""", true)]
    [InlineData("""{"any":[{"field":"days","op":"gt","value":30},{"field":"paid","op":"eq","value":false}]}""", false)]
    [InlineData("""{"not":{"field":"days","op":"gt","value":3}}""", false)]
    [InlineData("""{"not":{"any":[{"all":[{"field":"days","op":"gt","value":9}]}]}}""", true)]
    public void Evaluate_Groups_CombineTheirParts(string json, bool expected) =>
        StageCondition.Parse(json, TestData.Leave()).Value.Evaluate(TestData.Values()).Value.Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"field":"days","op":"gt"}""")]
    [InlineData("""{"field":"days","op":"gt","value":1,"extra":true}""")]
    [InlineData("""{"field":"days","op":"gt","value":1,"value":2}""")]
    [InlineData("""{"field":"missing","op":"eq","value":1}""")]
    [InlineData("""{"field":"note","op":"eq","value":"x"}""")]
    [InlineData("""{"field":"days","op":"like","value":1}""")]
    [InlineData("""{"field":"days","op":"eq","value":"5"}""")]
    [InlineData("""{"field":"leaveType","op":"gt","value":"a"}""")]
    [InlineData("""{"field":"paid","op":"gt","value":true}""")]
    [InlineData("""{"field":"paid","op":"in","value":[true]}""")]
    [InlineData("""{"field":"startsOn","op":"eq","value":"10/01/2027"}""")]
    [InlineData("""{"field":"days","op":"in","value":[]}""")]
    [InlineData("""{"field":"days","op":"in","value":5}""")]
    [InlineData("""{"field":"days","op":"in","value":[1,"2"]}""")]
    [InlineData("""{"all":[]}""")]
    [InlineData("""{"all":{"field":"days","op":"eq","value":1}}""")]
    [InlineData("""{"not":{"not":{"not":{"not":{"field":"days","op":"eq","value":1}}}}}""")]
    public void Parse_AnythingMalformedOrMistyped_IsRefused(string json)
    {
        var result = StageCondition.Parse(json, TestData.Leave());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("workflow.condition.invalid");
    }

    [Fact]
    public void Parse_TooManyParts_IsRefused()
    {
        var parts = string.Join(',', Enumerable.Repeat(Compare("days", "eq", "1"), StageCondition.MaxNodes));

        StageCondition.Parse($$"""{"any":[{{parts}}]}""", TestData.Leave()).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_WhenAFieldHasNoValue_CannotTell_EvenIfAnotherPartWouldDecide()
    {
        var condition = StageCondition.Parse(
            """{"any":[{"field":"paid","op":"eq","value":true},{"field":"days","op":"gt","value":1}]}""", TestData.Leave()).Value;

        var result = condition.Evaluate(new Dictionary<string, object> { ["paid"] = true });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("workflow.condition.field_missing");
    }
}

public class WorkflowPayloadTests
{
    [Fact]
    public void Create_ValidFields_GivesCanonicalJson_WhateverTheOrder_AndReadsBack()
    {
        var reversed = TestData.Fields().Reverse().ToDictionary();

        var first = WorkflowPayload.Create(TestData.Leave(), TestData.Fields()).Value;
        var second = WorkflowPayload.Create(TestData.Leave(), reversed).Value;

        second.Json.Should().Be(first.Json);
        WorkflowPayload.Read(TestData.Leave(), first.Json).Should().BeEquivalentTo(first.Values);
    }

    [Fact]
    public void Create_AnUndeclaredField_IsRefused()
    {
        var fields = TestData.Fields();
        fields["bankAccount"] = "123456789";

        WorkflowPayload.Create(TestData.Leave(), fields).Error.Code.Should().Be("workflow.payload.unknown_field");
    }

    [Theory]
    [InlineData("days")]
    [InlineData("leaveType")]
    [InlineData("coverUserId")]
    public void Create_AMissingRequiredField_IsRefused(string name)
    {
        var fields = TestData.Fields();
        fields.Remove(name);

        WorkflowPayload.Create(TestData.Leave(), fields).Error.Code.Should().Be("workflow.payload.field_required");
    }

    public static TheoryData<string, object> WrongValues => new()
    {
        { "days", "5" },
        { "days", 5.5d },
        { "leaveType", 1 },
        { "leaveType", new string('x', WorkflowPayload.MaxTextLength + 1) },
        { "paid", "true" },
        { "startsOn", "2027-01-10" },
        { "coverUserId", Guid.Empty },
    };

    [Theory]
    [MemberData(nameof(WrongValues))]
    public void Create_AValueOfTheWrongType_IsRefused(string name, object value)
    {
        var fields = TestData.Fields();
        fields[name] = value;

        WorkflowPayload.Create(TestData.Leave(), fields).Error.Code.Should().Be("workflow.payload.field_invalid");
    }

    [Fact]
    public void Create_MoreThanTheSizeLimit_IsRefused()
    {
        var fields = Enumerable.Range(0, WorkflowKeyCatalogue.MaxFields).Select(i => new WorkflowField($"f{i}", WorkflowFieldType.Text)).ToList();
        var values = fields.ToDictionary(f => f.Name, _ => (object?)new string('x', WorkflowPayload.MaxTextLength));

        WorkflowPayload.Create(new("big.one", "big", fields), values).Error.Code.Should().Be("workflow.payload.too_large");
    }
}

public class WorkflowKeyCatalogueTests
{
    private static WorkflowKeyDescriptor With(string key = "leave.apply", string entity = "leave_request", params WorkflowField[] fields) =>
        new(key, entity, fields);

    [Theory]
    [InlineData("leave")]
    [InlineData("Leave.Apply")]
    [InlineData("leave..apply")]
    [InlineData("leave.apply ")]
    public void ABadKey_StopsTheHost(string key) =>
        FluentActions.Invoking(() => new WorkflowKeyCatalogue([With(key)])).Should().Throw<InvalidOperationException>();

    [Theory]
    [InlineData("bankAccountNumber")]
    [InlineData("basicSalary")]
    [InlineData("nationalId")]
    [InlineData("apiToken")]
    public void AFieldThatSoundsSensitive_StopsTheHost(string name) =>
        FluentActions.Invoking(() => new WorkflowKeyCatalogue([With(fields: new WorkflowField(name, WorkflowFieldType.Text))]))
            .Should().Throw<InvalidOperationException>().WithMessage("*sensitive*");

    [Fact]
    public void ADuplicateKeyOrField_StopsTheHost()
    {
        FluentActions.Invoking(() => new WorkflowKeyCatalogue([With(), With()])).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => new WorkflowKeyCatalogue(
                [With(fields: [new("days", WorkflowFieldType.Number), new("Days", WorkflowFieldType.Text)])]))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ADeclaredKey_IsFound()
    {
        var catalogue = new WorkflowKeyCatalogue([TestData.Leave()]);

        catalogue.Find("leave.apply").Should().NotBeNull();
        catalogue.Find("leave.unknown").Should().BeNull();
        catalogue.Find(null).Should().BeNull();
    }
}
