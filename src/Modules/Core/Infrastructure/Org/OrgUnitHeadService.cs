using Hrms.Contracts;
using Hrms.Core.Application.Org;
using Hrms.Core.Domain.Org;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure.Org;

/// <summary>Keeps the head of each org unit over time, and answers other modules' "who heads this part of the organisation".</summary>
internal sealed class OrgUnitHeadService(CoreDbContext db, IEmployeeDirectory employees, TimeProvider time) : IOrgUnitHeadService
{
    public async Task<Result<OrgUnitHeadHistoryView>> HistoryAsync(Guid orgUnitId, CancellationToken cancellationToken)
    {
        var version = await db.OrgUnits.AsNoTracking().Where(u => u.Id == orgUnitId).Select(u => (long?)u.RowVersion)
            .SingleOrDefaultAsync(cancellationToken);
        if (version is not { } rowVersion)
        {
            return Result.Failure<OrgUnitHeadHistoryView>(NotFound());
        }

        var periods = await db.OrgUnitHeads.AsNoTracking()
            .Where(h => h.OrgUnitId == orgUnitId)
            .OrderBy(h => h.EffectiveFrom)
            .Select(h => new OrgUnitHeadView(h.EmployeeId, h.EffectiveFrom, h.EffectiveTo))
            .ToListAsync(cancellationToken);
        return Result.Success(new OrgUnitHeadHistoryView(periods, rowVersion));
    }

    public async Task<Result<OrgUnitHeadHistoryView>> ChangeAsync(Guid orgUnitId, ChangeOrgUnitHeadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RowVersion is not { } basedOn)
        {
            return Result.Failure<OrgUnitHeadHistoryView>(
                Error.Validation("core.row_version_required", "The rowVersion the change is based on is required."));
        }

        var unit = await db.OrgUnits.SingleOrDefaultAsync(u => u.Id == orgUnitId, cancellationToken);
        if (unit is null)
        {
            return Result.Failure<OrgUnitHeadHistoryView>(NotFound());
        }

        if (unit.RowVersion != basedOn)
        {
            return Result.Failure<OrgUnitHeadHistoryView>(Stale());
        }

        if (request.EmployeeId is { } employeeId && !await employees.ExistsAsync(employeeId, cancellationToken))
        {
            return Result.Failure<OrgUnitHeadHistoryView>(Error.Validation("core.org_unit.head_not_found", "The employee does not exist."));
        }

        var from = request.EffectiveFrom ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var latest = await db.OrgUnitHeads.Where(h => h.OrgUnitId == orgUnitId)
            .OrderByDescending(h => h.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);
        var check = OrgUnitHead.CheckChange(latest, request.EmployeeId, from);
        if (check.IsFailure)
        {
            return Result.Failure<OrgUnitHeadHistoryView>(check.Error);
        }

        if (latest is { EffectiveTo: null })
        {
            latest.EndOn(from.AddDays(-1));
        }

        if (request.EmployeeId is { } head)
        {
            db.OrgUnitHeads.Add(OrgUnitHead.Create(orgUnitId, head, from));
        }

        // Touching the unit bumps its row version, so two changes based on the same version cannot both win.
        db.Entry(unit).Property(u => u.Name).IsModified = true;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            return Result.Failure<OrgUnitHeadHistoryView>(Stale());
        }

        return await HistoryAsync(orgUnitId, cancellationToken);
    }

    private static Error NotFound() => Error.NotFound("core.org_unit.not_found", "The record does not exist.");

    private static Error Stale() =>
        Error.Conflict("core.org_unit.stale_version", "The record was changed by someone else. Reload it and apply your change again.");
}

/// <summary>
/// The read side of unit heads for other modules. It is separate from the service above so that asking who heads a unit does not need
/// the employee directory.
/// </summary>
internal sealed class OrgUnitHeadLookup(CoreDbContext db) : IOrgUnitHeadLookup
{
    public async Task<IReadOnlyList<OrgUnitHeadLink>> GetHeadChainAsync(Guid orgUnitId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var types = await db.OrgUnits.AsNoTracking().Select(u => new { u.Id, u.OrgUnitTypeId })
            .ToDictionaryAsync(u => u.Id, u => u.OrgUnitTypeId, cancellationToken);
        if (!types.ContainsKey(orgUnitId))
        {
            return [];
        }

        // The whole arrangement of the date in one query, then a walk up in memory: a tenant has thousands of units at most.
        var parents = await db.OrgUnitParents.AsNoTracking().ValidOn(asOf)
            .Where(l => l.ParentId != null)
            .Select(l => new { l.OrgUnitId, ParentId = l.ParentId!.Value })
            .ToDictionaryAsync(l => l.OrgUnitId, l => l.ParentId, cancellationToken);

        var path = new List<Guid>();
        var seen = new HashSet<Guid>();
        var current = orgUnitId;
        while (types.ContainsKey(current) && seen.Add(current))
        {
            path.Add(current);
            if (!parents.TryGetValue(current, out current))
            {
                break;
            }
        }

        var heads = await db.OrgUnitHeads.AsNoTracking().ValidOn(asOf)
            .Where(h => path.Contains(h.OrgUnitId))
            .Select(h => new { h.OrgUnitId, h.EmployeeId })
            .ToDictionaryAsync(h => h.OrgUnitId, h => h.EmployeeId, cancellationToken);

        return path.ConvertAll(id => new OrgUnitHeadLink(id, types[id], heads.TryGetValue(id, out var head) ? head : null));
    }
}
