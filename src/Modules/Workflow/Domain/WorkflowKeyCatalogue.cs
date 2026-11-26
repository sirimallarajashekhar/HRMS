using System.Text.RegularExpressions;
using Hrms.Contracts.Workflow;

namespace Hrms.Workflow.Domain;

/// <summary>
/// Every workflow key the modules of this host declared. It fails when built if a declaration is malformed, duplicated or names a field
/// that sounds sensitive, so a bad declaration stops the host instead of surfacing on a customer's request.
/// </summary>
public sealed partial class WorkflowKeyCatalogue
{
    public const int MaxFields = 50;

    // Field values are stored with the workflow and shown to approvers. This is a guard against the obvious mistake, not a classifier:
    // the rule for module authors is that no sensitive value goes into a workflow field, whatever it is called.
    private static readonly string[] ForbiddenNameParts =
    [
        "password", "secret", "token", "accountnumber", "bankaccount", "iban", "nationalid", "taxid", "passport", "aadhaar", "ssn", "salary",
    ];

    private readonly Dictionary<string, WorkflowKeyDescriptor> _byKey = new(StringComparer.Ordinal);

    public WorkflowKeyCatalogue(IEnumerable<WorkflowKeyDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        foreach (var descriptor in descriptors)
        {
            Check(descriptor);
            if (!_byKey.TryAdd(descriptor.Key, descriptor))
            {
                throw new InvalidOperationException($"The workflow key '{descriptor.Key}' is declared twice.");
            }
        }
    }

    public IReadOnlyCollection<WorkflowKeyDescriptor> All => _byKey.Values;

    public WorkflowKeyDescriptor? Find(string? key) => key is null ? null : _byKey.GetValueOrDefault(key);

    private static void Check(WorkflowKeyDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Key is not { Length: <= 100 } || !KeyPattern().IsMatch(descriptor.Key))
        {
            throw new InvalidOperationException($"'{descriptor.Key}' is not a workflow key (lower case words joined by dots, for example leave.apply).");
        }

        if (descriptor.EntityType is not { Length: <= 64 } || !EntityTypePattern().IsMatch(descriptor.EntityType))
        {
            throw new InvalidOperationException($"The workflow key '{descriptor.Key}' needs a lower snake case entity type.");
        }

        if (descriptor.Fields.Count > MaxFields)
        {
            throw new InvalidOperationException($"The workflow key '{descriptor.Key}' declares more than {MaxFields} fields.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptor.Fields)
        {
            if (!FieldPattern().IsMatch(field.Name) || !Enum.IsDefined(field.Type) || !names.Add(field.Name))
            {
                throw new InvalidOperationException($"The workflow key '{descriptor.Key}' has a bad or repeated field '{field.Name}'.");
            }

            if (ForbiddenNameParts.Any(part => field.Name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"The field '{field.Name}' of workflow key '{descriptor.Key}' sounds sensitive. Workflow fields must never hold sensitive values.");
            }
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex EntityTypePattern();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,63}$")]
    private static partial Regex FieldPattern();
}
