using Hrms.Contracts;
using Hrms.Core.Application.Org;
using Hrms.Core.Domain.Org;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure.Org;

/// <summary>
/// Org units and their effective-dated parent. A move closes the unit's open parent period the day before and opens the next, in one
/// transaction; the unit's row version is bumped so two racing moves cannot both succeed, and the unique (unit, start date) index is the
/// backstop. The tree rules (no cycles, parent on a higher level) are checked before anything is written.
/// </summary>
internal sealed class OrgUnitService(CoreDbContext db, IEnumerable<IMasterUsageCheck> usageChecks, TimeProvider time) : IOrgUnitService
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    public async Task<PagedResult<OrgUnitView>> ListAsync(OrgUnitQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var paging = query.Paging.Normalised();
        var asOf = query.AsOf ?? Today;

        var rows = Placed(asOf);
        if (paging.Search is { } search)
        {
            rows = rows.Where(r => r.Code.Contains(search) || r.Name.Contains(search));
        }

        if (paging.Active is { } active)
        {
            rows = rows.Where(r => r.IsActive == active);
        }

        if (query.OrgUnitTypeId is { } typeId)
        {
            rows = rows.Where(r => r.OrgUnitTypeId == typeId);
        }

        if (query.ParentId is { } parentId)
        {
            rows = rows.Where(r => r.ParentId == parentId);
        }

        var total = await rows.LongCountAsync(cancellationToken);
        var items = await rows
            .OrderBy(r => r.Code)
            .Skip((paging.Page - 1) * paging.PageSize)
            .Take(paging.PageSize)
            .Select(r => new OrgUnitView(
                r.Id, r.Code, r.Name, r.OrgUnitTypeId, r.ParentId, r.ParentFrom, r.IsActive, r.RowVersion))
            .ToListAsync(cancellationToken);

        return new PagedResult<OrgUnitView>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<IReadOnlyList<OrgUnitTreeNode>> TreeAsync(DateOnly? asOf, CancellationToken cancellationToken)
    {
        var date = asOf ?? Today;

        // Inner join: a unit with no parent period on the date was not part of the structure yet.
        return await (
                from unit in db.OrgUnits.AsNoTracking()
                join link in db.OrgUnitParents.AsNoTracking().ValidOn(date) on unit.Id equals link.OrgUnitId
                join type in db.OrgUnitTypes.AsNoTracking() on unit.OrgUnitTypeId equals type.Id
                orderby type.Level, unit.Code
                select new OrgUnitTreeNode(unit.Id, unit.Code, unit.Name, unit.OrgUnitTypeId, type.Level, link.ParentId))
            .ToListAsync(cancellationToken);
    }

    public async Task<Result<OrgUnitView>> GetAsync(Guid id, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var view = await Placed(asOf ?? Today)
            .Where(r => r.Id == id)
            .Select(r => new OrgUnitView(
                r.Id, r.Code, r.Name, r.OrgUnitTypeId, r.ParentId, r.ParentFrom, r.IsActive, r.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

        return view is null ? Result.Failure<OrgUnitView>(NotFound()) : Result.Success(view);
    }

    public async Task<Result<IReadOnlyList<OrgUnitParentView>>> ParentHistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.OrgUnits.AsNoTracking().AnyAsync(u => u.Id == id, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<OrgUnitParentView>>(NotFound());
        }

        IReadOnlyList<OrgUnitParentView> history = await db.OrgUnitParents.AsNoTracking()
            .Where(l => l.OrgUnitId == id)
            .OrderBy(l => l.EffectiveFrom)
            .Select(l => new OrgUnitParentView(l.ParentId, l.EffectiveFrom, l.EffectiveTo))
            .ToListAsync(cancellationToken);
        return Result.Success(history);
    }

    public async Task<Result<OrgUnitView>> CreateAsync(CreateOrgUnitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var type = await db.OrgUnitTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.OrgUnitTypeId, cancellationToken);
        if (type is null)
        {
            return Result.Failure<OrgUnitView>(Error.Validation("core.org_unit.type_not_found", "The org unit type does not exist."));
        }

        var unit = OrgUnit.Create(request.Code, request.Name, request.OrgUnitTypeId);
        if (unit.IsFailure)
        {
            return Result.Failure<OrgUnitView>(unit.Error);
        }

        var parentCheck = await CheckParentAsync(request.ParentId, type.Level, cancellationToken);
        if (parentCheck is not null)
        {
            return Result.Failure<OrgUnitView>(parentCheck);
        }

        if (await db.OrgUnits.AsNoTracking().AnyAsync(u => u.Code == unit.Value.Code, cancellationToken))
        {
            return Result.Failure<OrgUnitView>(CodeTaken());
        }

        var from = request.EffectiveFrom ?? Today;
        db.OrgUnits.Add(unit.Value);
        db.OrgUnitParents.Add(OrgUnitParent.Create(unit.Value.Id, request.ParentId, from));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.OrgUnits.AsNoTracking().AnyAsync(u => u.Code == unit.Value.Code, cancellationToken))
            {
                return Result.Failure<OrgUnitView>(CodeTaken());
            }

            throw;
        }

        return Result.Success(new OrgUnitView(
            unit.Value.Id, unit.Value.Code, unit.Value.Name, unit.Value.OrgUnitTypeId, request.ParentId, from, unit.Value.IsActive, unit.Value.RowVersion));
    }

    public async Task<Result<OrgUnitView>> UpdateAsync(Guid id, UpdateOrgUnitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await LoadForChangeAsync(id, request.RowVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<OrgUnitView>(loaded.Error);
        }

        var applied = loaded.Value.Update(request.Name, request.IsActive);
        if (applied.IsFailure)
        {
            return Result.Failure<OrgUnitView>(applied.Error);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<OrgUnitView>(Stale());
        }

        return await GetAsync(id, asOf: null, cancellationToken);
    }

    public async Task<Result<OrgUnitView>> MoveAsync(Guid id, MoveOrgUnitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await LoadForChangeAsync(id, request.RowVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<OrgUnitView>(loaded.Error);
        }

        var unit = loaded.Value;
        var type = await db.OrgUnitTypes.AsNoTracking().SingleAsync(t => t.Id == unit.OrgUnitTypeId, cancellationToken);
        var parentCheck = await CheckParentAsync(request.ParentId, type.Level, cancellationToken);
        if (parentCheck is not null)
        {
            return Result.Failure<OrgUnitView>(parentCheck);
        }

        var links = await db.OrgUnitParents.AsNoTracking()
            .Select(l => new ParentLink(l.OrgUnitId, l.ParentId, l.EffectiveFrom, l.EffectiveTo))
            .ToListAsync(cancellationToken);
        var treeCheck = OrgTree.CheckMove(id, request.ParentId, request.EffectiveFrom, links);
        if (treeCheck.IsFailure)
        {
            return Result.Failure<OrgUnitView>(treeCheck.Error);
        }

        var open = await db.OrgUnitParents.SingleOrDefaultAsync(l => l.OrgUnitId == id && l.EffectiveTo == null, cancellationToken);
        open?.EndOn(request.EffectiveFrom.AddDays(-1));
        db.OrgUnitParents.Add(OrgUnitParent.Create(id, request.ParentId, request.EffectiveFrom));

        // Touching the unit bumps its row version, so a second move based on the same version loses.
        db.Entry(unit).Property(u => u.Name).IsModified = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            return Result.Failure<OrgUnitView>(Stale());
        }

        return await GetAsync(id, request.EffectiveFrom, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var unit = await db.OrgUnits.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (unit is null)
        {
            return Result.Failure(NotFound());
        }

        var today = Today;
        var hasChildren = await db.OrgUnitParents.AsNoTracking()
            .Where(l => l.ParentId == id && (l.EffectiveTo == null || l.EffectiveTo >= today))
            .AnyAsync(l => db.OrgUnits.Any(child => child.Id == l.OrgUnitId), cancellationToken);

        if (hasChildren || await UsedElsewhereAsync(id, cancellationToken))
        {
            return Result.Failure(Error.Conflict("core.org_unit.in_use", "This unit still has child units or is in use, so it cannot be deleted. Deactivate it instead."));
        }

        db.OrgUnits.Remove(unit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Stale());
        }

        return Result.Success();
    }

    // --- helpers -------------------------------------------------------------------------------------------------------------

    // Member-initialised (not constructor-built) so EF can keep filtering and ordering on it after the projection.
    private sealed class Placement
    {
        public Guid Id { get; init; }

        public string Code { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public Guid OrgUnitTypeId { get; init; }

        public bool IsActive { get; init; }

        public long RowVersion { get; init; }

        public Guid? ParentId { get; init; }

        public DateOnly? ParentFrom { get; init; }
    }

    /// <summary>Every live unit with its parent period valid on the date (left join: a unit not yet placed has no parent).</summary>
    private IQueryable<Placement> Placed(DateOnly date) =>
        from unit in db.OrgUnits.AsNoTracking()
        join link in db.OrgUnitParents.AsNoTracking().ValidOn(date) on unit.Id equals link.OrgUnitId into links
        from link in links.DefaultIfEmpty()
        select new Placement
        {
            Id = unit.Id,
            Code = unit.Code,
            Name = unit.Name,
            OrgUnitTypeId = unit.OrgUnitTypeId,
            IsActive = unit.IsActive,
            RowVersion = unit.RowVersion,
            ParentId = link == null ? null : link.ParentId,
            ParentFrom = link == null ? null : link.EffectiveFrom,
        };

    private async Task<Result<OrgUnit>> LoadForChangeAsync(Guid id, long? rowVersion, CancellationToken cancellationToken)
    {
        if (rowVersion is not { } version)
        {
            return Result.Failure<OrgUnit>(Error.Validation("core.row_version_required", "The rowVersion the change is based on is required."));
        }

        var unit = await db.OrgUnits.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (unit is null)
        {
            return Result.Failure<OrgUnit>(NotFound());
        }

        return unit.RowVersion == version ? Result.Success(unit) : Result.Failure<OrgUnit>(Stale());
    }

    /// <summary>The parent must exist, be active, and sit on a higher level (a lower number) than the unit.</summary>
    private async Task<Error?> CheckParentAsync(Guid? parentId, int unitLevel, CancellationToken cancellationToken)
    {
        if (parentId is not { } id)
        {
            return null;
        }

        var parent = await (
                from unit in db.OrgUnits.AsNoTracking()
                join type in db.OrgUnitTypes.AsNoTracking() on unit.OrgUnitTypeId equals type.Id
                where unit.Id == id
                select new { unit.IsActive, type.Level })
            .SingleOrDefaultAsync(cancellationToken);

        if (parent is null)
        {
            return Error.Validation("core.org_unit.parent_not_found", "The parent unit does not exist.");
        }

        if (!parent.IsActive)
        {
            return Error.Validation("core.org_unit.parent_inactive", "The parent unit is inactive.");
        }

        return parent.Level < unitLevel
            ? null
            : Error.Validation("core.org_unit.parent_level_invalid", "A unit's parent must be on a higher level (a lower level number) than the unit.");
    }

    private async Task<bool> UsedElsewhereAsync(Guid id, CancellationToken cancellationToken)
    {
        foreach (var check in usageChecks.Where(c => c.Master == MasterKinds.OrgUnit))
        {
            if (await check.IsInUseAsync(id, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static Error NotFound() => Error.NotFound("core.org_unit.not_found", "The record does not exist.");

    private static Error Stale() =>
        Error.Conflict("core.org_unit.stale_version", "The record was changed by someone else. Reload it and apply your change again.");

    private static Error CodeTaken() => Error.Conflict("core.org_unit.code_taken", "That code is already used.");
}
