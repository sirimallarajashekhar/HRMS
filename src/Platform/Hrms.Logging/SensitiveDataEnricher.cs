using Serilog.Core;
using Serilog.Events;

namespace Hrms.Logging;

/// <summary>
/// Last line of defence for "never log secrets or personal data": replaces the value of any log property whose NAME marks it
/// as sensitive (password, token, bank account, national id, salary, email, phone...) with <see cref="Mask"/>, including inside
/// destructured objects, dictionaries and lists. Add it after Enrich.FromLogContext() so pushed properties are covered.
/// <para>
/// It matches names, not content: a secret formatted into the message text or an exception message is not caught. The rule
/// stays "do not log it"; this catches the mistake of logging an object that happens to carry it.
/// </para>
/// </summary>
public sealed class SensitiveDataEnricher : ILogEventEnricher
{
    public const string Mask = "***";

    // Compared against the property name lower-cased with '_', '-' and '.' removed, so Bank_Account and bankAccount both match.
    private static readonly string[] SensitiveFragments =
    [
        // secrets
        "password", "passwd", "secret", "token", "apikey", "authorization", "cookie", "connectionstring", "privatekey",
        // financial and identity
        "bankaccount", "accountnumber", "iban", "ifsc", "routingnumber", "nationalid", "ssn", "aadhaar", "passport", "taxid",
        // compensation
        "salary", "ctc", "wage", "netpay", "grosspay", "bonus",
        // contact
        "email", "phone", "mobile",
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var property in logEvent.Properties.ToArray())
        {
            var masked = IsSensitive(property.Key) ? new ScalarValue(Mask) : MaskValue(property.Value);
            if (!ReferenceEquals(masked, property.Value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, masked));
            }
        }
    }

    public static bool IsSensitive(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var normalised = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);

        foreach (var fragment in SensitiveFragments)
        {
            if (normalised.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the same instance when nothing inside needed masking, so unchanged events cost no allocations.</summary>
    private static LogEventPropertyValue MaskValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case StructureValue structure:
            {
                var changed = false;
                var properties = new List<LogEventProperty>(structure.Properties.Count);
                foreach (var inner in structure.Properties)
                {
                    var masked = IsSensitive(inner.Name) ? new ScalarValue(Mask) : MaskValue(inner.Value);
                    changed |= !ReferenceEquals(masked, inner.Value);
                    properties.Add(ReferenceEquals(masked, inner.Value) ? inner : new LogEventProperty(inner.Name, masked));
                }

                return changed ? new StructureValue(properties, structure.TypeTag) : value;
            }

            case DictionaryValue dictionary:
            {
                var changed = false;
                var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                foreach (var (key, inner) in dictionary.Elements)
                {
                    var masked = key.Value is string name && IsSensitive(name) ? new ScalarValue(Mask) : MaskValue(inner);
                    changed |= !ReferenceEquals(masked, inner);
                    elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(key, masked));
                }

                return changed ? new DictionaryValue(elements) : value;
            }

            case SequenceValue sequence:
            {
                var changed = false;
                var items = new List<LogEventPropertyValue>(sequence.Elements.Count);
                foreach (var inner in sequence.Elements)
                {
                    var masked = MaskValue(inner);
                    changed |= !ReferenceEquals(masked, inner);
                    items.Add(masked);
                }

                return changed ? new SequenceValue(items) : value;
            }

            default:
                return value;
        }
    }
}
