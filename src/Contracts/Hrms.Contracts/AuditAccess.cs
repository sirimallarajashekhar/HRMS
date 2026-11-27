namespace Hrms.Contracts;

/// <summary>History rows of one entity type, and the permission that normally shows that data.</summary>
public sealed record AuditViewRule(string EntityType, string Permission);

/// <summary>
/// A kind of record whose history can be asked for. <see cref="Rules"/> covers the record itself (the rule whose entity type equals
/// <see cref="RootType"/>) and every record filed under it. An entity type with no rule is never shown.
/// </summary>
/// <param name="ReasonPermission">
/// Set when the reason a caller gave for a change may say something sensitive about the record. A request's reason is stored on
/// every entry it caused, so without this it would reach people who may not see the part it is about. Reasons are then returned only
/// to callers who hold this permission with the record in its data scope. Null shows them with the entries.
/// </param>
public sealed record AuditRecordType(string RootType, IReadOnlyList<AuditViewRule> Rules, string? ReasonPermission = null);

/// <summary>
/// How a module says who may see the history of its records, so history never shows more than the module's own screens do. The audit
/// API checks that the caller holds the rule's permission and then asks the module whether the record is within the caller's data scope
/// for it. Register one per module in the API host; implementations read only their own tables.
/// </summary>
public interface IAuditRecordAccess
{
    IReadOnlyList<AuditRecordType> RecordTypes { get; }

    /// <summary>
    /// Whether the record is within the signed-in caller's data scope for the permission. False for a record that does not exist,
    /// where the module can tell, so callers learn nothing about records outside their reach.
    /// </summary>
    Task<bool> IsInScopeAsync(string rootType, Guid recordId, string permission, CancellationToken cancellationToken);
}
