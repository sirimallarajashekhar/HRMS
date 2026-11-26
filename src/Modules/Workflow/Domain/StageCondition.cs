using System.Text.Json;
using Hrms.Contracts.Workflow;
using Hrms.SharedKernel;

namespace Hrms.Workflow.Domain;

/// <summary>
/// A stage's "only when" rule, written as JSON data and evaluated here. It is not an expression language: the only things it can do are
/// compare a declared field with a constant and combine such comparisons with all, any and not.
/// <code>{"all":[{"field":"days","op":"gt","value":3},{"not":{"field":"paid","op":"eq","value":false}}]}</code>
/// Operators: eq, ne, gt, gte, lt, lte, in. Ordering works on numbers and dates; text compares exactly, including case.
/// </summary>
public sealed class StageCondition
{
    public const int MaxDepth = 3;
    public const int MaxNodes = 20;
    public const int MaxListSize = 50;
    public const int MaxLength = 4000;

    private readonly Node _root;

    private StageCondition(Node root, string json)
    {
        _root = root;
        Json = json;
    }

    /// <summary>The compact JSON that was validated, as stored.</summary>
    public string Json { get; }

    /// <summary>
    /// Parses and type-checks a condition against a key's declaration. Fields used in a condition must be required fields, so that every
    /// request can answer every condition.
    /// </summary>
    public static Result<StageCondition> Parse(string? json, WorkflowKeyDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxLength)
        {
            return Fail($"A condition is 1 to {MaxLength} characters of JSON.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var nodes = 0;
            var parsed = ParseNode(document.RootElement, descriptor, depth: 1, ref nodes);
            return parsed.IsFailure
                ? Result.Failure<StageCondition>(parsed.Error)
                : Result.Success(new StageCondition(parsed.Value, JsonSerializer.Serialize(document.RootElement)));
        }
        catch (JsonException)
        {
            return Fail("The condition is not valid JSON.");
        }
    }

    /// <summary>Fails (never guesses) when a field the condition needs has no value.</summary>
    public Result<bool> Evaluate(IReadOnlyDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return _root.Evaluate(values) is { } answer
            ? Result.Success(answer)
            : Result.Failure<bool>(Error.Validation("workflow.condition.field_missing", "A field the condition needs has no value."));
    }

    private static Result<Node> ParseNode(JsonElement element, WorkflowKeyDescriptor descriptor, int depth, ref int nodes)
    {
        if (++nodes > MaxNodes)
        {
            return NodeFail($"A condition has at most {MaxNodes} parts.");
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return NodeFail("Each part of a condition is a JSON object.");
        }

        var properties = element.EnumerateObject().ToList();
        if (properties.Count == 1 && properties[0].Name is "all" or "any" or "not")
        {
            if (depth > MaxDepth)
            {
                return NodeFail($"A condition nests at most {MaxDepth} levels.");
            }

            var (name, value) = (properties[0].Name, properties[0].Value);
            if (name == "not")
            {
                var inner = ParseNode(value, descriptor, depth + 1, ref nodes);
                return inner.IsFailure ? inner : Result.Success<Node>(new Not(inner.Value));
            }

            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            {
                return NodeFail($"'{name}' takes a non-empty list.");
            }

            var children = new List<Node>();
            foreach (var item in value.EnumerateArray())
            {
                var child = ParseNode(item, descriptor, depth + 1, ref nodes);
                if (child.IsFailure)
                {
                    return child;
                }

                children.Add(child.Value);
            }

            return Result.Success<Node>(new Group(name == "all", children));
        }

        return ParseComparison(element, properties, descriptor);
    }

    private static Result<Node> ParseComparison(JsonElement element, List<JsonProperty> properties, WorkflowKeyDescriptor descriptor)
    {
        // Exactly these three, once each: a misspelt or extra property must not be silently ignored.
        if (properties.Count != 3
            || !element.TryGetProperty("field", out var fieldElement)
            || !element.TryGetProperty("op", out var opElement)
            || !element.TryGetProperty("value", out var valueElement)
            || fieldElement.ValueKind != JsonValueKind.String
            || opElement.ValueKind != JsonValueKind.String)
        {
            return NodeFail("A comparison has a field, an op and a value, and nothing else.");
        }

        var name = fieldElement.GetString()!;
        var field = descriptor.Fields.FirstOrDefault(f => f.Name == name);
        if (field is null)
        {
            return NodeFail($"The workflow '{descriptor.Key}' has no field '{name}'.");
        }

        if (!field.Required)
        {
            return NodeFail($"The field '{name}' is optional; only required fields can be used in a condition.");
        }

        Operator? parsed = opElement.GetString() switch
        {
            "eq" => Operator.Equal,
            "ne" => Operator.NotEqual,
            "gt" => Operator.Greater,
            "gte" => Operator.GreaterOrEqual,
            "lt" => Operator.Less,
            "lte" => Operator.LessOrEqual,
            "in" => Operator.In,
            _ => null,
        };
        if (parsed is not { } op)
        {
            return NodeFail("The operators are eq, ne, gt, gte, lt, lte and in.");
        }

        var ordered = field.Type is WorkflowFieldType.Number or WorkflowFieldType.Date;
        if (!ordered && op is not (Operator.Equal or Operator.NotEqual or Operator.In))
        {
            return NodeFail($"'{name}' is a {field.Type} field; it cannot be ordered.");
        }

        if (op == Operator.In && field.Type == WorkflowFieldType.Boolean)
        {
            return NodeFail($"'in' does not apply to the Boolean field '{name}'.");
        }

        var constants = new List<object>();
        if (op == Operator.In)
        {
            if (valueElement.ValueKind != JsonValueKind.Array || valueElement.GetArrayLength() is 0 or > MaxListSize)
            {
                return NodeFail($"'in' takes a list of 1 to {MaxListSize} values.");
            }

            foreach (var item in valueElement.EnumerateArray())
            {
                if (WorkflowPayload.FromJson(field.Type, item) is not { } constant)
                {
                    return NodeFail($"A value in the list does not match the {field.Type} field '{name}'.");
                }

                constants.Add(constant);
            }
        }
        else if (WorkflowPayload.FromJson(field.Type, valueElement) is { } constant)
        {
            constants.Add(constant);
        }
        else
        {
            return NodeFail($"The value does not match the {field.Type} field '{name}'.");
        }

        return Result.Success<Node>(new Comparison(name, op, constants));
    }

    private static Result<StageCondition> Fail(string message) =>
        Result.Failure<StageCondition>(Error.Validation("workflow.condition.invalid", message));

    private static Result<Node> NodeFail(string message) => Result.Failure<Node>(Error.Validation("workflow.condition.invalid", message));

    private enum Operator
    {
        Equal,
        NotEqual,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
        In,
    }

    private abstract class Node
    {
        /// <summary>Null means "cannot tell".</summary>
        public abstract bool? Evaluate(IReadOnlyDictionary<string, object> values);
    }

    private sealed class Not(Node inner) : Node
    {
        public override bool? Evaluate(IReadOnlyDictionary<string, object> values) => !inner.Evaluate(values);
    }

    private sealed class Group(bool all, IReadOnlyList<Node> children) : Node
    {
        public override bool? Evaluate(IReadOnlyDictionary<string, object> values)
        {
            // Every child is evaluated, and one that cannot tell makes the whole group unable to tell, even if the others would
            // already decide it. A missing field must never be hidden by the order of the parts.
            var answers = children.Select(child => child.Evaluate(values)).ToList();
            if (answers.Any(answer => answer is null))
            {
                return null;
            }

            return all ? answers.All(answer => answer == true) : answers.Any(answer => answer == true);
        }
    }

    private sealed class Comparison(string field, Operator op, IReadOnlyList<object> constants) : Node
    {
        public override bool? Evaluate(IReadOnlyDictionary<string, object> values)
        {
            if (!values.TryGetValue(field, out var actual) || actual.GetType() != constants[0].GetType())
            {
                return null;
            }

            return op switch
            {
                Operator.Equal => actual.Equals(constants[0]),
                Operator.NotEqual => !actual.Equals(constants[0]),
                Operator.In => constants.Contains(actual),
                Operator.Greater => Compare(actual) > 0,
                Operator.GreaterOrEqual => Compare(actual) >= 0,
                Operator.Less => Compare(actual) < 0,
                Operator.LessOrEqual => Compare(actual) <= 0,
                _ => null,
            };
        }

        private int Compare(object actual) => ((IComparable)actual).CompareTo(constants[0]);
    }
}
