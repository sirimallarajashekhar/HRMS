using System.Globalization;
using System.Text;
using System.Text.Json;
using Hrms.Contracts.Workflow;
using Hrms.SharedKernel;

namespace Hrms.Workflow.Domain;

/// <summary>
/// The typed routing facts of one request, checked against the key's declaration. Nothing undeclared gets in. The JSON form is canonical
/// (fields in name order), so two submissions of the same values give the same text.
/// </summary>
public sealed class WorkflowPayload
{
    public const int MaxBytes = 16 * 1024;
    public const int MaxTextLength = 500;
    private const string DateFormat = "yyyy-MM-dd";

    private WorkflowPayload(IReadOnlyDictionary<string, object> values, string json)
    {
        Values = values;
        Json = json;
    }

    /// <summary>decimal, string, bool, DateOnly or Guid, by field name.</summary>
    public IReadOnlyDictionary<string, object> Values { get; }

    public string Json { get; }

    public static Result<WorkflowPayload> Create(WorkflowKeyDescriptor descriptor, IReadOnlyDictionary<string, object?>? fields)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        fields ??= new Dictionary<string, object?>();

        var declared = descriptor.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var unknown = fields.Keys.Where(name => !declared.ContainsKey(name)).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
        {
            return Fail("workflow.payload.unknown_field", $"The workflow '{descriptor.Key}' has no field '{unknown}'.");
        }

        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in descriptor.Fields)
        {
            if (!fields.TryGetValue(field.Name, out var raw) || raw is null)
            {
                if (field.Required)
                {
                    return Fail("workflow.payload.field_required", $"The field '{field.Name}' is required.");
                }

                continue;
            }

            var value = Normalise(field.Type, raw);
            if (value is null)
            {
                return Fail("workflow.payload.field_invalid", $"The field '{field.Name}' is not a valid {field.Type} value.");
            }

            values[field.Name] = value;
        }

        var json = Serialise(values);
        return Encoding.UTF8.GetByteCount(json) > MaxBytes
            ? Fail("workflow.payload.too_large", $"The fields take more than {MaxBytes} bytes.")
            : Result.Success(new WorkflowPayload(values, json));
    }

    /// <summary>
    /// Reads stored JSON back using the declaration. A stored field the declaration no longer has, or whose type changed, is left out; a
    /// condition that needs it then fails to evaluate, which blocks the request instead of guessing.
    /// </summary>
    public static IReadOnlyDictionary<string, object> Read(WorkflowKeyDescriptor descriptor, string json)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        foreach (var field in descriptor.Fields)
        {
            if (document.RootElement.TryGetProperty(field.Name, out var element) && FromJson(field.Type, element) is { } value)
            {
                values[field.Name] = value;
            }
        }

        return values;
    }

    /// <summary>Converts a JSON value to the CLR form of a field type, or null when it does not fit.</summary>
    internal static object? FromJson(WorkflowFieldType type, JsonElement element) => type switch
    {
        WorkflowFieldType.Number => element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number) ? number : null,
        WorkflowFieldType.Text => element.ValueKind == JsonValueKind.String && element.GetString() is { Length: <= MaxTextLength } text ? text : null,
        WorkflowFieldType.Boolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : null,
        WorkflowFieldType.Date => element.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(element.GetString(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
        WorkflowFieldType.UserId => element.ValueKind == JsonValueKind.String && element.TryGetGuid(out var id) && id != Guid.Empty ? id : null,
        _ => null,
    };

    // Floating point is refused on purpose: amounts must arrive as decimal so a threshold compares exactly.
    private static object? Normalise(WorkflowFieldType type, object raw) => (type, raw) switch
    {
        (WorkflowFieldType.Number, decimal number) => number,
        (WorkflowFieldType.Number, int number) => (decimal)number,
        (WorkflowFieldType.Number, long number) => (decimal)number,
        (WorkflowFieldType.Text, string { Length: <= MaxTextLength } text) => text,
        (WorkflowFieldType.Boolean, bool flag) => flag,
        (WorkflowFieldType.Date, DateOnly date) => date,
        (WorkflowFieldType.UserId, Guid id) when id != Guid.Empty => id,
        _ => null,
    };

    private static string Serialise(SortedDictionary<string, object> values)
    {
        var json = values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is DateOnly date ? date.ToString(DateFormat, CultureInfo.InvariantCulture) : pair.Value,
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(json);
    }

    private static Result<WorkflowPayload> Fail(string code, string message) => Result.Failure<WorkflowPayload>(Error.Validation(code, message));
}
