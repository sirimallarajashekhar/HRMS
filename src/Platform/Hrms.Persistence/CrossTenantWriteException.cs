namespace Hrms.Persistence;

/// <summary>
/// An attempt to write a row that belongs to a different tenant than the current one. Always a bug or an attack,
/// never a business outcome, so it is an exception; the message deliberately carries no tenant ids.
/// </summary>
public sealed class CrossTenantWriteException : InvalidOperationException
{
    public CrossTenantWriteException(string entityTypeName)
        : base($"Refused to write a {entityTypeName} that belongs to another tenant.")
    {
    }
}
