using Hrms.Contracts;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure.Org;

/// <summary>
/// Answers other modules' questions about organisation structure from the Core tables, always through the tenant-filtered context,
/// so an id from another tenant is simply reported as not found.
/// </summary>
internal sealed class OrgStructureLookup(CoreDbContext db) : IOrgStructureLookup
{
    public async Task<MasterStatus> GetStatusAsync(string kind, Guid id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        bool? active = kind switch
        {
            MasterKinds.OrgUnitType => await Flag(db.OrgUnitTypes.AsNoTracking().Where(e => e.Id == id).Select(e => (bool?)e.IsActive), cancellationToken),
            MasterKinds.OrgUnit => await Flag(db.OrgUnits.AsNoTracking().Where(e => e.Id == id).Select(e => (bool?)e.IsActive), cancellationToken),
            MasterKinds.Location => await Flag(db.Locations.AsNoTracking().Where(e => e.Id == id).Select(e => (bool?)e.IsActive), cancellationToken),
            MasterKinds.Grade => await Flag(db.Grades.AsNoTracking().Where(e => e.Id == id).Select(e => (bool?)e.IsActive), cancellationToken),
            MasterKinds.Designation => await Flag(db.Designations.AsNoTracking().Where(e => e.Id == id).Select(e => (bool?)e.IsActive), cancellationToken),
            MasterKinds.CostCentre => await Flag(db.CostCentres.AsNoTracking().Where(e => e.Id == id).Select(e => (bool?)e.IsActive), cancellationToken),
            MasterKinds.EmployeeType => await Flag(db.EmployeeTypes.AsNoTracking().Where(e => e.Id == id).Select(e => (bool?)e.IsActive), cancellationToken),
            MasterKinds.LegalEntity => await db.LegalEntities.AsNoTracking().AnyAsync(e => e.Id == id, cancellationToken) ? true : null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown master kind."),
        };

        return active switch
        {
            null => MasterStatus.NotFound,
            false => MasterStatus.Inactive,
            true => MasterStatus.Active,
        };
    }

    public Task<LegalEntitySummary?> GetLegalEntityAsync(Guid id, CancellationToken cancellationToken) =>
        db.LegalEntities.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new LegalEntitySummary(e.Id, e.Code, e.CountryCode, e.CurrencyCode, e.CountryPackCode))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetOrgUnitSubtreeAsync(Guid orgUnitId, DateOnly asOf, CancellationToken cancellationToken)
    {
        if (!await db.OrgUnits.AsNoTracking().AnyAsync(u => u.Id == orgUnitId, cancellationToken))
        {
            return new HashSet<Guid>();
        }

        // The whole parent arrangement of the date in one query, then a walk down in memory: a tenant has thousands of units at most.
        var links = await db.OrgUnitParents.AsNoTracking().ValidOn(asOf)
            .Where(l => l.ParentId != null)
            .Select(l => new { l.OrgUnitId, ParentId = l.ParentId!.Value })
            .ToListAsync(cancellationToken);
        var children = links.ToLookup(l => l.ParentId, l => l.OrgUnitId);

        var found = new HashSet<Guid> { orgUnitId };
        var queue = new Queue<Guid>();
        queue.Enqueue(orgUnitId);
        while (queue.Count > 0)
        {
            foreach (var child in children[queue.Dequeue()])
            {
                if (found.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return found;
    }

    private static Task<bool?> Flag(IQueryable<bool?> query, CancellationToken cancellationToken) => query.SingleOrDefaultAsync(cancellationToken);
}
