using Hrms.SharedKernel;

namespace Hrms.Employee.Domain;

public enum JobChangeReason
{
    Joining = 1,
    Transfer = 2,
    Promotion = 3,
    Change = 4,
}

/// <summary>What a job period says about the employee's place in the organisation.</summary>
public sealed record JobDetails(
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

/// <summary>
/// One period of an employee's job. Rows are never edited: a change closes the open row the day before and adds the next, so any past
/// date can be answered. Future-dated rows are stored at once and read as of their date; <see cref="EventPublishedAt"/> records that the
/// "job changed" event went out on the day the row took effect.
/// </summary>
[Audited(RootType = "employee", RootKey = nameof(EmployeeId))]
public sealed class EmployeeJob : EffectiveDatedEntity
{
    public const int MaxNoticePeriodDays = 365;

    private EmployeeJob(Guid employeeId, JobDetails details, JobChangeReason reason, DateOnly effectiveFrom)
        : base(effectiveFrom)
    {
        EmployeeId = employeeId;
        LegalEntityId = details.LegalEntityId;
        LocationId = details.LocationId;
        OrgUnitId = details.OrgUnitId;
        DesignationId = details.DesignationId;
        EmployeeTypeId = details.EmployeeTypeId;
        GradeId = details.GradeId;
        CostCentreId = details.CostCentreId;
        ManagerId = details.ManagerId;
        ProbationEnd = details.ProbationEnd;
        NoticePeriodDays = details.NoticePeriodDays;
        Reason = reason;
    }

    /// <summary>For the persistence layer only.</summary>
    private EmployeeJob()
    {
    }

    public Guid EmployeeId { get; private set; }

    public Guid LegalEntityId { get; private set; }

    public Guid LocationId { get; private set; }

    public Guid OrgUnitId { get; private set; }

    public Guid DesignationId { get; private set; }

    public Guid EmployeeTypeId { get; private set; }

    public Guid? GradeId { get; private set; }

    public Guid? CostCentreId { get; private set; }

    public Guid? ManagerId { get; private set; }

    public DateOnly? ProbationEnd { get; private set; }

    public int NoticePeriodDays { get; private set; }

    public JobChangeReason Reason { get; private set; }

    /// <summary>When the "job changed" event for this row was published (null until it took effect and the daily job ran).</summary>
    [AuditIgnore]
    public DateTime? EventPublishedAt { get; private set; }

    public static Result<EmployeeJob> Create(Guid employeeId, JobDetails? details, JobChangeReason reason, DateOnly effectiveFrom)
    {
        if (details is null)
        {
            return Fail("details_required", "The job details are required.");
        }

        if (effectiveFrom == default)
        {
            return Fail("effective_from_required", "The effective date is required.");
        }

        if (details.LegalEntityId == Guid.Empty || details.LocationId == Guid.Empty || details.OrgUnitId == Guid.Empty
            || details.DesignationId == Guid.Empty || details.EmployeeTypeId == Guid.Empty)
        {
            return Fail("reference_required", "Legal entity, location, org unit, designation and employee type are required.");
        }

        if (details.GradeId == Guid.Empty || details.CostCentreId == Guid.Empty || details.ManagerId == Guid.Empty)
        {
            return Fail("reference_invalid", "An empty id is not a reference; leave grade, cost centre and manager out instead.");
        }

        if (details.NoticePeriodDays is < 0 or > MaxNoticePeriodDays)
        {
            return Fail("notice_period_invalid", $"The notice period is 0 to {MaxNoticePeriodDays} days.");
        }

        if (details.ManagerId == employeeId)
        {
            return Fail("manager_self", "An employee cannot be their own manager.");
        }

        if (!Enum.IsDefined(reason))
        {
            return Fail("reason_invalid", "The reason is not recognised.");
        }

        return Result.Success(new EmployeeJob(employeeId, details, reason, effectiveFrom));
    }

    public JobDetails ToDetails() => new(
        LegalEntityId, LocationId, OrgUnitId, DesignationId, EmployeeTypeId, GradeId, CostCentreId, ManagerId, ProbationEnd, NoticePeriodDays);

    public void MarkEventPublished(DateTime at) => EventPublishedAt = at;

    private static Result<EmployeeJob> Fail(string reason, string message) => Result.Failure<EmployeeJob>(Validation.Invalid("job", reason, message));
}
