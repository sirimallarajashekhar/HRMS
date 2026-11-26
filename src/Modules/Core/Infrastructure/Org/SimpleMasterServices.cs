using System.Linq.Expressions;
using Hrms.Contracts;
using Hrms.Core.Application.Org;
using Hrms.Core.Domain.Org;
using Hrms.Core.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Core.Infrastructure.Org;

internal sealed class OrgUnitTypeService(CoreDbContext db, IEnumerable<IMasterUsageCheck> usage)
    : MasterService<OrgUnitType, CreateOrgUnitTypeRequest, UpdateOrgUnitTypeRequest, OrgUnitTypeView>(db, usage), IOrgUnitTypeService
{
    protected override string Master => OrgUnitType.Master;

    protected override Expression<Func<OrgUnitType, OrgUnitTypeView>> View =>
        e => new OrgUnitTypeView(e.Id, e.Code, e.Name, e.Level, e.IsActive, e.RowVersion);

    protected override Task<Result<OrgUnitType>> BuildAsync(CreateOrgUnitTypeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(OrgUnitType.Create(request.Code, request.Name, request.Level));

    protected override async Task<Result> ApplyAsync(OrgUnitType entity, UpdateOrgUnitTypeRequest request, CancellationToken cancellationToken)
    {
        // The levels fix the shape of the tree, so a type that has units keeps its level.
        if (request.Level != entity.Level && await IsReferencedInCoreAsync(entity.Id, cancellationToken))
        {
            return Result.Failure(Error.Conflict("core.org_unit_type.in_use", "The level of a type that has units cannot change."));
        }

        return entity.Update(request.Name, request.Level, request.IsActive);
    }

    protected override Task<bool> IsReferencedInCoreAsync(Guid id, CancellationToken cancellationToken) =>
        Db.OrgUnits.AsNoTracking().AnyAsync(u => u.OrgUnitTypeId == id, cancellationToken);
}

internal sealed class GradeService(CoreDbContext db, IEnumerable<IMasterUsageCheck> usage)
    : MasterService<Grade, CreateGradeRequest, UpdateGradeRequest, GradeView>(db, usage), IGradeService
{
    protected override string Master => Grade.Master;

    protected override Expression<Func<Grade, GradeView>> View =>
        e => new GradeView(e.Id, e.Code, e.Name, e.Rank, e.IsActive, e.RowVersion);

    protected override Task<Result<Grade>> BuildAsync(CreateGradeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Grade.Create(request.Code, request.Name, request.Rank));

    protected override Task<Result> ApplyAsync(Grade entity, UpdateGradeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(entity.Update(request.Name, request.Rank, request.IsActive));

    protected override Task<bool> IsReferencedInCoreAsync(Guid id, CancellationToken cancellationToken) =>
        Db.Designations.AsNoTracking().AnyAsync(d => d.GradeId == id, cancellationToken);
}

internal sealed class DesignationService(CoreDbContext db, IEnumerable<IMasterUsageCheck> usage)
    : MasterService<Designation, CreateDesignationRequest, UpdateDesignationRequest, DesignationView>(db, usage), IDesignationService
{
    protected override string Master => Designation.Master;

    protected override Expression<Func<Designation, DesignationView>> View =>
        e => new DesignationView(e.Id, e.Code, e.Name, e.GradeId, e.IsActive, e.RowVersion);

    protected override IQueryable<Designation> Related(IQueryable<Designation> query, Guid relatedId) => query.Where(d => d.GradeId == relatedId);

    protected override async Task<Result<Designation>> BuildAsync(CreateDesignationRequest request, CancellationToken cancellationToken)
    {
        var missing = await GradeMissingAsync(request.GradeId, cancellationToken);
        return missing is not null ? Result.Failure<Designation>(missing) : Designation.Create(request.Code, request.Name, request.GradeId);
    }

    protected override async Task<Result> ApplyAsync(Designation entity, UpdateDesignationRequest request, CancellationToken cancellationToken)
    {
        var missing = await GradeMissingAsync(request.GradeId, cancellationToken);
        return missing is not null ? Result.Failure(missing) : entity.Update(request.Name, request.GradeId, request.IsActive);
    }

    private async Task<Error?> GradeMissingAsync(Guid? gradeId, CancellationToken cancellationToken) =>
        gradeId is { } id && !await Db.Grades.AsNoTracking().AnyAsync(g => g.Id == id, cancellationToken)
            ? Error.Validation("core.designation.grade_not_found", "The grade does not exist.")
            : null;
}

internal sealed class CostCentreService(CoreDbContext db, IEnumerable<IMasterUsageCheck> usage)
    : MasterService<CostCentre, CreateCostCentreRequest, UpdateCostCentreRequest, CostCentreView>(db, usage), ICostCentreService
{
    protected override string Master => CostCentre.Master;

    protected override Expression<Func<CostCentre, CostCentreView>> View =>
        e => new CostCentreView(e.Id, e.Code, e.Name, e.LegalEntityId, e.IsActive, e.RowVersion);

    protected override IQueryable<CostCentre> Related(IQueryable<CostCentre> query, Guid relatedId) => query.Where(c => c.LegalEntityId == relatedId);

    protected override async Task<Result<CostCentre>> BuildAsync(CreateCostCentreRequest request, CancellationToken cancellationToken)
    {
        var missing = await LegalEntityMissingAsync(request.LegalEntityId, cancellationToken);
        return missing is not null ? Result.Failure<CostCentre>(missing) : CostCentre.Create(request.Code, request.Name, request.LegalEntityId);
    }

    protected override async Task<Result> ApplyAsync(CostCentre entity, UpdateCostCentreRequest request, CancellationToken cancellationToken)
    {
        var missing = await LegalEntityMissingAsync(request.LegalEntityId, cancellationToken);
        return missing is not null ? Result.Failure(missing) : entity.Update(request.Name, request.LegalEntityId, request.IsActive);
    }

    private async Task<Error?> LegalEntityMissingAsync(Guid? legalEntityId, CancellationToken cancellationToken) =>
        legalEntityId is { } id && !await Db.LegalEntities.AsNoTracking().AnyAsync(l => l.Id == id, cancellationToken)
            ? Error.Validation("core.cost_centre.legal_entity_not_found", "The legal entity does not exist.")
            : null;
}

internal sealed class EmployeeTypeService(CoreDbContext db, IEnumerable<IMasterUsageCheck> usage)
    : MasterService<EmployeeType, CreateEmployeeTypeRequest, UpdateEmployeeTypeRequest, EmployeeTypeView>(db, usage), IEmployeeTypeService
{
    protected override string Master => EmployeeType.Master;

    protected override Expression<Func<EmployeeType, EmployeeTypeView>> View =>
        e => new EmployeeTypeView(e.Id, e.Code, e.Name, e.IsPayrollEligible, e.IsActive, e.RowVersion);

    protected override Task<Result<EmployeeType>> BuildAsync(CreateEmployeeTypeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(EmployeeType.Create(request.Code, request.Name, request.IsPayrollEligible));

    protected override Task<Result> ApplyAsync(EmployeeType entity, UpdateEmployeeTypeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(entity.Update(request.Name, request.IsPayrollEligible, request.IsActive));
}

internal sealed class LocationService(CoreDbContext db, IEnumerable<IMasterUsageCheck> usage)
    : MasterService<Location, CreateLocationRequest, UpdateLocationRequest, LocationView>(db, usage), ILocationService
{
    protected override string Master => Location.Master;

    protected override Expression<Func<Location, LocationView>> View => e => new LocationView(
        e.Id, e.Code, e.Name, e.AddressLine1, e.AddressLine2, e.City, e.State, e.PostalCode, e.CountryCode, e.TimeZoneId,
        e.WorkWeekDays, e.HolidayCalendarId, e.IsActive, e.RowVersion);

    protected override Task<Result<Location>> BuildAsync(CreateLocationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Location.Create(
            request.Code,
            request.Name,
            new LocationDetails(
                request.AddressLine1, request.AddressLine2, request.City, request.State, request.PostalCode, request.CountryCode,
                request.TimeZoneId, request.WorkWeekDays ?? Location.MondayToFriday, request.HolidayCalendarId)));

    protected override Task<Result> ApplyAsync(Location entity, UpdateLocationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(entity.Update(
            request.Name,
            new LocationDetails(
                request.AddressLine1, request.AddressLine2, request.City, request.State, request.PostalCode, request.CountryCode,
                request.TimeZoneId, request.WorkWeekDays, request.HolidayCalendarId),
            request.IsActive));

    protected override Task<bool> IsReferencedInCoreAsync(Guid id, CancellationToken cancellationToken) =>
        Db.Geofences.AsNoTracking().AnyAsync(g => g.LocationId == id, cancellationToken);
}
