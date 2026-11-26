using System.Text.Json;
using Hrms.SharedKernel;

namespace Hrms.Core.Application.Org;

// --- paging and shared shapes ----------------------------------------------------------------------------------------------

/// <summary>A page of a list. Offset paging: these masters are small per tenant, so a total count is cheap and useful to a UI.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

/// <summary>
/// Paging and filtering for a master list. <see cref="Search"/> matches code or name; <see cref="RelatedId"/> filters by the
/// master's own parent reference (type for org units, grade for designations, legal entity for cost centres).
/// </summary>
public sealed record MasterQuery(int Page = 1, int PageSize = MasterQuery.DefaultPageSize, string? Search = null, bool? Active = null, Guid? RelatedId = null)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>Clamps a caller's paging to sane bounds instead of failing: page 1 or later, 1 to 200 per page.</summary>
    public MasterQuery Normalised() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
    };
}

/// <summary>Lets the API build a Location header for whatever a create returns.</summary>
public interface IIdentified
{
    Guid Id { get; }
}

/// <summary>An update names the version it was based on; a stale one is refused instead of overwriting someone else's change.</summary>
public interface IUpdateRequest
{
    long? RowVersion { get; }
}

public interface IMasterService<in TCreate, in TUpdate, TView>
{
    Task<PagedResult<TView>> ListAsync(MasterQuery query, CancellationToken cancellationToken);

    Task<Result<TView>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<TView>> CreateAsync(TCreate request, CancellationToken cancellationToken);

    Task<Result<TView>> UpdateAsync(Guid id, TUpdate request, CancellationToken cancellationToken);

    /// <summary>Soft delete. Refused with <c>core.&lt;master&gt;.in_use</c> while anything still references the row.</summary>
    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

// --- org unit types, grades, designations, cost centres, employee types ---------------------------------------------------------

public sealed record OrgUnitTypeView(Guid Id, string Code, string Name, int Level, bool IsActive, long RowVersion) : IIdentified;

public sealed record CreateOrgUnitTypeRequest(string? Code, string? Name, int Level);

public sealed record UpdateOrgUnitTypeRequest(string? Name, int Level, bool IsActive, long? RowVersion) : IUpdateRequest;

public interface IOrgUnitTypeService : IMasterService<CreateOrgUnitTypeRequest, UpdateOrgUnitTypeRequest, OrgUnitTypeView>;

public sealed record GradeView(Guid Id, string Code, string Name, int Rank, bool IsActive, long RowVersion) : IIdentified;

public sealed record CreateGradeRequest(string? Code, string? Name, int Rank);

public sealed record UpdateGradeRequest(string? Name, int Rank, bool IsActive, long? RowVersion) : IUpdateRequest;

public interface IGradeService : IMasterService<CreateGradeRequest, UpdateGradeRequest, GradeView>;

public sealed record DesignationView(Guid Id, string Code, string Name, Guid? GradeId, bool IsActive, long RowVersion) : IIdentified;

public sealed record CreateDesignationRequest(string? Code, string? Name, Guid? GradeId);

public sealed record UpdateDesignationRequest(string? Name, Guid? GradeId, bool IsActive, long? RowVersion) : IUpdateRequest;

public interface IDesignationService : IMasterService<CreateDesignationRequest, UpdateDesignationRequest, DesignationView>;

public sealed record CostCentreView(Guid Id, string Code, string Name, Guid? LegalEntityId, bool IsActive, long RowVersion) : IIdentified;

public sealed record CreateCostCentreRequest(string? Code, string? Name, Guid? LegalEntityId);

public sealed record UpdateCostCentreRequest(string? Name, Guid? LegalEntityId, bool IsActive, long? RowVersion) : IUpdateRequest;

public interface ICostCentreService : IMasterService<CreateCostCentreRequest, UpdateCostCentreRequest, CostCentreView>;

public sealed record EmployeeTypeView(Guid Id, string Code, string Name, bool IsPayrollEligible, bool IsActive, long RowVersion) : IIdentified;

public sealed record CreateEmployeeTypeRequest(string? Code, string? Name, bool IsPayrollEligible);

public sealed record UpdateEmployeeTypeRequest(string? Name, bool IsPayrollEligible, bool IsActive, long? RowVersion) : IUpdateRequest;

public interface IEmployeeTypeService : IMasterService<CreateEmployeeTypeRequest, UpdateEmployeeTypeRequest, EmployeeTypeView>;

// --- locations and geofences -----------------------------------------------------------------------------------------------------

public sealed record LocationView(
    Guid Id,
    string Code,
    string Name,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string? State,
    string? PostalCode,
    string CountryCode,
    string TimeZoneId,
    int WorkWeekDays,
    Guid? HolidayCalendarId,
    bool IsActive,
    long RowVersion) : IIdentified;

/// <summary><paramref name="WorkWeekDays"/> is a mask (Monday = 1 ... Sunday = 64); leave it out for Monday to Friday.</summary>
public sealed record CreateLocationRequest(
    string? Code,
    string? Name,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryCode,
    string? TimeZoneId,
    int? WorkWeekDays,
    Guid? HolidayCalendarId);

public sealed record UpdateLocationRequest(
    string? Name,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryCode,
    string? TimeZoneId,
    int WorkWeekDays,
    Guid? HolidayCalendarId,
    bool IsActive,
    long? RowVersion) : IUpdateRequest;

public interface ILocationService : IMasterService<CreateLocationRequest, UpdateLocationRequest, LocationView>;

/// <summary>A geofence. <c>Kind</c> is "circle" or "polygon"; the polygon is a GeoJSON Polygon geometry (one ring, [longitude, latitude]).</summary>
public sealed record GeofenceView(
    Guid Id,
    Guid LocationId,
    string Name,
    string Kind,
    decimal? CenterLatitude,
    decimal? CenterLongitude,
    int? RadiusMetres,
    JsonElement? Polygon,
    bool IsActive,
    long RowVersion) : IIdentified;

public sealed record CreateGeofenceRequest(
    string? Name,
    string? Kind,
    decimal? CenterLatitude,
    decimal? CenterLongitude,
    int? RadiusMetres,
    JsonElement? Polygon);

public sealed record UpdateGeofenceRequest(
    string? Name,
    string? Kind,
    decimal? CenterLatitude,
    decimal? CenterLongitude,
    int? RadiusMetres,
    JsonElement? Polygon,
    bool IsActive,
    long? RowVersion) : IUpdateRequest;

public interface IGeofenceService
{
    Task<Result<PagedResult<GeofenceView>>> ListAsync(Guid locationId, MasterQuery query, CancellationToken cancellationToken);

    Task<Result<GeofenceView>> GetAsync(Guid locationId, Guid id, CancellationToken cancellationToken);

    Task<Result<GeofenceView>> CreateAsync(Guid locationId, CreateGeofenceRequest request, CancellationToken cancellationToken);

    Task<Result<GeofenceView>> UpdateAsync(Guid locationId, Guid id, UpdateGeofenceRequest request, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(Guid locationId, Guid id, CancellationToken cancellationToken);
}

// --- org units -------------------------------------------------------------------------------------------------------------------

/// <summary>An org unit with its parent as of a date (null parent: a root, or not yet placed on that date).</summary>
public sealed record OrgUnitView(
    Guid Id,
    string Code,
    string Name,
    Guid OrgUnitTypeId,
    Guid? ParentId,
    DateOnly? ParentEffectiveFrom,
    bool IsActive,
    long RowVersion) : IIdentified;

/// <summary>One node of the whole tree on a date; the client assembles the tree from <see cref="ParentId"/>.</summary>
public sealed record OrgUnitTreeNode(Guid Id, string Code, string Name, Guid OrgUnitTypeId, int Level, Guid? ParentId);

public sealed record OrgUnitParentView(Guid? ParentId, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary><paramref name="EffectiveFrom"/> defaults to today (UTC date) when left out.</summary>
public sealed record CreateOrgUnitRequest(string? Code, string? Name, Guid OrgUnitTypeId, Guid? ParentId, DateOnly? EffectiveFrom);

public sealed record UpdateOrgUnitRequest(string? Name, bool IsActive, long? RowVersion) : IUpdateRequest;

public sealed record MoveOrgUnitRequest(Guid? ParentId, DateOnly EffectiveFrom, long? RowVersion) : IUpdateRequest;

public sealed record OrgUnitQuery(MasterQuery Paging, Guid? OrgUnitTypeId, Guid? ParentId, DateOnly? AsOf);

public interface IOrgUnitService
{
    Task<PagedResult<OrgUnitView>> ListAsync(OrgUnitQuery query, CancellationToken cancellationToken);

    /// <summary>Every unit placed in the tree on the date (default today), in one query, for rendering the whole tree.</summary>
    Task<IReadOnlyList<OrgUnitTreeNode>> TreeAsync(DateOnly? asOf, CancellationToken cancellationToken);

    Task<Result<OrgUnitView>> GetAsync(Guid id, DateOnly? asOf, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OrgUnitParentView>>> ParentHistoryAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<OrgUnitView>> CreateAsync(CreateOrgUnitRequest request, CancellationToken cancellationToken);

    Task<Result<OrgUnitView>> UpdateAsync(Guid id, UpdateOrgUnitRequest request, CancellationToken cancellationToken);

    /// <summary>Re-parents the unit from a date, keeping the old arrangement as history. Refuses cycles and level violations.</summary>
    Task<Result<OrgUnitView>> MoveAsync(Guid id, MoveOrgUnitRequest request, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
