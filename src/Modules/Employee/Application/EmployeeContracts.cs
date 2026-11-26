using Hrms.SharedKernel;

namespace Hrms.Employee.Application;

// --- paging --------------------------------------------------------------------------------------------------------------------

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public static class Paging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Clamp(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}

/// <summary>An update names the version it was based on; a stale one is refused instead of overwriting someone else's change.</summary>
public interface IUpdateRequest
{
    long? RowVersion { get; }
}

// --- requests ------------------------------------------------------------------------------------------------------------------

/// <summary>Gender is one of female, male, other, undisclosed (any case).</summary>
public sealed record PersonalRequest(
    string? FirstName,
    string? MiddleName,
    string? LastName,
    DateOnly DateOfBirth,
    string? Gender,
    string? PersonalEmail,
    string? Phone);

public sealed record JobRequest(
    Guid LegalEntityId,
    Guid LocationId,
    Guid OrgUnitId,
    Guid DesignationId,
    Guid EmployeeTypeId,
    Guid? GradeId,
    Guid? CostCentreId,
    Guid? ManagerId,
    DateOnly? ProbationEnd,
    int NoticePeriodDays);

/// <summary>Creates the employee with the personal details and the first job period (which starts on the joining date). The code is generated.</summary>
public sealed record CreateEmployeeRequest(DateOnly JoiningDate, PersonalRequest? Personal, JobRequest? Job);

/// <summary>A job change from a date. Reason is transfer, promotion or change. Send the employee rowVersion you read.</summary>
public sealed record ChangeJobRequest(DateOnly EffectiveFrom, string? Reason, JobRequest? Job, long? RowVersion) : IUpdateRequest;

/// <summary>Confirms probation on the given date (default today).</summary>
public sealed record ConfirmEmployeeRequest(DateOnly? ConfirmationDate, long? RowVersion) : IUpdateRequest;

/// <summary>Replaces the personal details. Send the personal rowVersion from the profile.</summary>
public sealed record UpdatePersonalRequest(PersonalRequest? Personal, long? RowVersion) : IUpdateRequest;

/// <summary>Type is resignation, termination, retirement, endOfContract or other. RowVersion is required when a separation already exists.</summary>
public sealed record SeparationRequest(string? Type, DateOnly? NoticeDate, DateOnly LastWorkingDay, string? Reason, long? RowVersion) : IUpdateRequest;

public sealed record SetStatutoryIdRequest(string? Value);

public sealed record CreateDocumentRequest(string? DocumentType, string? Title, string? FileKey, DateOnly? IssuedOn, DateOnly? ExpiresOn);

public sealed record UpdateDocumentRequest(string? DocumentType, string? Title, string? FileKey, DateOnly? IssuedOn, DateOnly? ExpiresOn, long? RowVersion) : IUpdateRequest;

/// <summary>Platform is android, ios or web.</summary>
public sealed record RegisterDeviceRequest(string? DeviceId, string? Platform, string? Name);

public sealed record GrantConsentRequest(string? ConsentType, string? Version, string? Source);

public sealed record CodeRuleRequest(string? Prefix, int Width, long NextNumber, long? RowVersion) : IUpdateRequest;

// --- views ---------------------------------------------------------------------------------------------------------------------

public sealed record JobView(
    Guid Id,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    Guid LegalEntityId,
    Guid LocationId,
    Guid OrgUnitId,
    Guid DesignationId,
    Guid EmployeeTypeId,
    Guid? GradeId,
    Guid? CostCentreId,
    Guid? ManagerId,
    DateOnly? ProbationEnd,
    int NoticePeriodDays,
    string Reason);

/// <summary>Status is "active" (employed today), "separated" (last working day passed) or "notJoined" (joining date still ahead).</summary>
public sealed record EmployeeSummaryView(
    Guid Id,
    string Code,
    string DisplayName,
    DateOnly JoiningDate,
    DateOnly? ConfirmationDate,
    DateOnly? LastWorkingDay,
    string Status,
    JobView? Job,
    long RowVersion);

/// <summary>Email and phone are masked unless the caller may see sensitive values.</summary>
public sealed record PersonalView(
    string FirstName,
    string? MiddleName,
    string LastName,
    DateOnly DateOfBirth,
    string Gender,
    string? PersonalEmail,
    string? Phone,
    long RowVersion);

public sealed record SeparationView(string Type, DateOnly? NoticeDate, DateOnly LastWorkingDay, string? Reason, long RowVersion);

/// <summary>The value is masked (last characters only) unless the caller may see sensitive values.</summary>
public sealed record StatutoryIdView(Guid Id, string IdType, string Value, long RowVersion);

public sealed record DocumentView(Guid Id, string DocumentType, string Title, string? FileKey, DateOnly? IssuedOn, DateOnly? ExpiresOn, bool IsExpired, long RowVersion);

public sealed record DeviceView(Guid Id, string DeviceId, string Platform, string? Name, string Status, DateTime RegisteredAt, DateTime? RevokedAt, long RowVersion);

public sealed record ConsentView(Guid Id, string ConsentType, string Version, string? Source, DateTime GrantedAt, DateTime? WithdrawnAt, long RowVersion);

public sealed record CodeRuleView(Guid Id, Guid LegalEntityId, string Prefix, int Width, long NextNumber, string Example, long RowVersion);

/// <summary>
/// The employee with the tabs the caller may see. A tab the caller has no permission for is null (not an empty list), so "hidden" and
/// "nothing there" cannot be confused.
/// </summary>
public sealed record EmployeeProfileView(
    EmployeeSummaryView Summary,
    PersonalView? Personal,
    IReadOnlyList<JobView>? JobHistory,
    SeparationView? Separation,
    IReadOnlyList<StatutoryIdView>? StatutoryIds,
    IReadOnlyList<DocumentView>? Documents,
    IReadOnlyList<DeviceView>? Devices,
    IReadOnlyList<ConsentView>? Consents);

/// <summary>Which tabs of the profile the caller holds the permission for, and whether sensitive values may be shown unmasked.</summary>
public sealed record ProfileAccess(bool Personal, bool Statutory, bool Documents, bool Devices, bool Consents, bool RevealSensitive);

// --- queries -------------------------------------------------------------------------------------------------------------------

/// <summary>Search matches the start of the code or of a name. Status is active (default), separated, notJoined or all.</summary>
public sealed record EmployeeQuery(
    int? Page,
    int? PageSize,
    string? Search,
    string? Status,
    Guid? LegalEntityId,
    Guid? LocationId,
    Guid? OrgUnitId,
    bool IncludeSubUnits,
    Guid? DesignationId,
    Guid? GradeId,
    Guid? EmployeeTypeId,
    Guid? ManagerId,
    DateOnly? AsOf);

// --- services ------------------------------------------------------------------------------------------------------------------

public interface IEmployeeService
{
    Task<Result<PagedResult<EmployeeSummaryView>>> ListAsync(EmployeeQuery query, CancellationToken cancellationToken);

    Task<Result<EmployeeProfileView>> GetProfileAsync(Guid id, DateOnly? asOf, ProfileAccess access, CancellationToken cancellationToken);

    Task<Result<EmployeeSummaryView>> CreateAsync(CreateEmployeeRequest request, CancellationToken cancellationToken);

    Task<Result<EmployeeSummaryView>> ConfirmAsync(Guid id, ConfirmEmployeeRequest request, CancellationToken cancellationToken);

    Task<Result<PersonalView>> UpdatePersonalAsync(Guid id, UpdatePersonalRequest request, bool revealSensitive, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<JobView>>> JobHistoryAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<EmployeeSummaryView>> ChangeJobAsync(Guid id, ChangeJobRequest request, CancellationToken cancellationToken);
}

public interface ISeparationService
{
    Task<Result<SeparationView>> SetAsync(Guid employeeId, SeparationRequest request, CancellationToken cancellationToken);

    Task<Result> WithdrawAsync(Guid employeeId, CancellationToken cancellationToken);
}

public interface IStatutoryIdService
{
    Task<Result<IReadOnlyList<StatutoryIdView>>> ListAsync(Guid employeeId, bool revealSensitive, CancellationToken cancellationToken);

    /// <summary>Creates the id of that type or replaces its value.</summary>
    Task<Result<StatutoryIdView>> SetAsync(Guid employeeId, string idType, SetStatutoryIdRequest request, bool revealSensitive, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(Guid employeeId, string idType, CancellationToken cancellationToken);
}

public interface IDocumentService
{
    Task<Result<IReadOnlyList<DocumentView>>> ListAsync(Guid employeeId, CancellationToken cancellationToken);

    Task<Result<DocumentView>> CreateAsync(Guid employeeId, CreateDocumentRequest request, CancellationToken cancellationToken);

    Task<Result<DocumentView>> UpdateAsync(Guid employeeId, Guid id, UpdateDocumentRequest request, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(Guid employeeId, Guid id, CancellationToken cancellationToken);
}

public interface IDeviceService
{
    Task<Result<IReadOnlyList<DeviceView>>> ListAsync(Guid employeeId, CancellationToken cancellationToken);

    Task<Result<DeviceView>> RegisterAsync(Guid employeeId, RegisterDeviceRequest request, CancellationToken cancellationToken);

    Task<Result<DeviceView>> RevokeAsync(Guid employeeId, Guid id, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(Guid employeeId, Guid id, CancellationToken cancellationToken);
}

public interface IConsentService
{
    Task<Result<IReadOnlyList<ConsentView>>> ListAsync(Guid employeeId, CancellationToken cancellationToken);

    Task<Result<ConsentView>> GrantAsync(Guid employeeId, GrantConsentRequest request, CancellationToken cancellationToken);

    Task<Result<ConsentView>> WithdrawAsync(Guid employeeId, Guid id, CancellationToken cancellationToken);
}

public interface ICodeRuleService
{
    Task<IReadOnlyList<CodeRuleView>> ListAsync(CancellationToken cancellationToken);

    Task<Result<CodeRuleView>> GetAsync(Guid legalEntityId, CancellationToken cancellationToken);

    /// <summary>Creates the rule of the legal entity or changes it (send the rowVersion when it exists).</summary>
    Task<Result<CodeRuleView>> SetAsync(Guid legalEntityId, CodeRuleRequest request, CancellationToken cancellationToken);
}
