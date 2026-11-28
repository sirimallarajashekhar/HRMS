using System.Text.Json;
using Hrms.Audit.Persistence;
using Hrms.Contracts;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Audit;

public sealed record AuditEntryView(
    Guid Id,
    DateTime OccurredAt,
    string EntityType,
    Guid RecordId,
    string Action,
    JsonElement Changes,
    string ActorType,
    Guid? ActorId,
    string? ClientId,
    string? IpAddress,
    string? Reason,
    string? CorrelationId);

public sealed record AuditHistoryPage(IReadOnlyList<AuditEntryView> Items, int Page, int PageSize, int Total);

/// <summary><paramref name="From"/> is inclusive and <paramref name="To"/> exclusive, both in UTC.</summary>
public sealed record AuditHistoryQuery(DateTime? From, DateTime? To, int? Page, int? PageSize);

/// <summary>
/// Reads the history of one record, with everything filed under it, limited to what the caller may see. A record type nobody
/// registered, a missing permission for the record and a record out of scope all look the same: not found.
/// </summary>
public sealed class AuditHistoryService(AuditDbContext db, IEnumerable<IAuditRecordAccess> modules)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    private const int MaxPage = 100_000;

    /// <param name="holdsPermission">Whether the caller's token grants a permission.</param>
    public async Task<Result<AuditHistoryPage>> GetAsync(
        string entityType,
        Guid recordId,
        AuditHistoryQuery query,
        Func<string, Task<bool>> holdsPermission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(holdsPermission);

        if (query.From is { } start && query.To is { } end && start >= end)
        {
            return Result.Failure<AuditHistoryPage>(Error.Validation("audit.history.range_invalid", "'from' must be earlier than 'to'."));
        }

        var found = modules
            .SelectMany(module => module.RecordTypes.Select(type => (Module: module, Type: type)))
            .SingleOrDefault(candidate => string.Equals(candidate.Type.RootType, entityType, StringComparison.Ordinal));
        if (found.Module is null)
        {
            return NotFound();
        }

        var allowed = await AllowedPermissionsAsync(found.Module, found.Type, recordId, holdsPermission, cancellationToken);
        var visible = found.Type.Rules.Where(rule => allowed.Contains(rule.Permission)).Select(rule => rule.EntityType).ToList();
        var showReason = found.Type.ReasonPermission is not { } reasonPermission || allowed.Contains(reasonPermission);
        var rootType = found.Type.RootType;
        if (!visible.Contains(rootType))
        {
            return NotFound();
        }

        var page = Math.Clamp(query.Page ?? 1, 1, MaxPage);
        var size = Math.Clamp(query.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var entries = db.Entries.AsNoTracking()
            .Where(e => e.RootType == rootType && e.RootId == recordId && visible.Contains(e.EntityType));
        if (query.From is { } from)
        {
            entries = entries.Where(e => e.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(e => e.OccurredAt < to);
        }

        var total = await entries.CountAsync(cancellationToken);
        var rows = await entries
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var items = rows.Select(e => new AuditEntryView(
            e.Id, e.OccurredAt, e.EntityType, e.RecordId, Name(e.Action), Parse(e.Changes), Name(e.ActorType),
            e.ActorId, e.ClientId, e.IpAddress, showReason ? e.Reason : null, e.CorrelationId)).ToList();

        return Result.Success(new AuditHistoryPage(items, page, size, total));
    }

    /// <summary>Which of the record type's permissions the caller holds with this record in their data scope for it.</summary>
    private static async Task<HashSet<string>> AllowedPermissionsAsync(
        IAuditRecordAccess module,
        AuditRecordType type,
        Guid recordId,
        Func<string, Task<bool>> holdsPermission,
        CancellationToken cancellationToken)
    {
        // One scope check per permission, however many entity types share it.
        var permissions = type.Rules.Select(rule => rule.Permission);
        if (type.ReasonPermission is { } reasonPermission)
        {
            permissions = permissions.Append(reasonPermission);
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in permissions.Distinct(StringComparer.Ordinal))
        {
            if (await holdsPermission(permission)
                && await module.IsInScopeAsync(type.RootType, recordId, permission, cancellationToken))
            {
                allowed.Add(permission);
            }
        }

        return allowed;
    }

    private static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Result<AuditHistoryPage> NotFound() =>
        Result.Failure<AuditHistoryPage>(Error.NotFound("audit.record.not_found", "The record does not exist."));
}
