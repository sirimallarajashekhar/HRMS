using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Employee.Application;
using Hrms.Employee.Domain;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hrms.Employee.Infrastructure;

/// <summary>
/// Employees: list, profile, create (with the first job and personal details and a generated code), confirm, personal data, and job changes.
/// Every read and write goes through <see cref="EmployeeAccess"/>, so the caller's data scope applies; every reference to a Core master is
/// checked through <see cref="IOrgStructureLookup"/>, never by reading Core tables. A change of job or separation touches the employee
/// row, so two racing changes on the same employee cannot both succeed.
/// </summary>
internal sealed class EmployeeService(
    EmployeeDbContext db,
    EmployeeAccess access,
    IOrgStructureLookup org,
    IStatutoryIdProtector protector,
    ITenantContext tenantContext,
    TimeProvider time,
    ILogger<EmployeeService> logger) : IEmployeeService
{
    private const int CreateAttempts = 10;

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // --- list ------------------------------------------------------------------------------------------------------------------

    private sealed class Row
    {
        public Guid Id { get; init; }

        public string Code { get; init; } = string.Empty;

        public DateOnly JoiningDate { get; init; }

        public DateOnly? ConfirmationDate { get; init; }

        public DateOnly? LastWorkingDay { get; init; }

        public long RowVersion { get; init; }

        public string? FirstName { get; init; }

        public string? LastName { get; init; }

        public Guid? JobId { get; init; }

        public DateOnly? JobFrom { get; init; }

        public DateOnly? JobTo { get; init; }

        public Guid? LegalEntityId { get; init; }

        public Guid? LocationId { get; init; }

        public Guid? OrgUnitId { get; init; }

        public Guid? DesignationId { get; init; }

        public Guid? EmployeeTypeId { get; init; }

        public Guid? GradeId { get; init; }

        public Guid? CostCentreId { get; init; }

        public Guid? ManagerId { get; init; }

        public DateOnly? ProbationEnd { get; init; }

        public int? NoticePeriodDays { get; init; }

        public JobChangeReason? Reason { get; init; }
    }

    public async Task<Result<PagedResult<EmployeeSummaryView>>> ListAsync(EmployeeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var visibility = await access.VisibilityAsync(Permissions.Employee.EmployeeView, cancellationToken);
        if (visibility.IsFailure)
        {
            return Result.Failure<PagedResult<EmployeeSummaryView>>(visibility.Error);
        }

        var status = (query.Status?.Trim() ?? "active").ToLowerInvariant();
        if (status is not ("active" or "separated" or "notjoined" or "all"))
        {
            return Result.Failure<PagedResult<EmployeeSummaryView>>(
                Error.Validation("employee.query.invalid_status", "The status is active, separated, notJoined or all."));
        }

        var (page, pageSize) = Paging.Clamp(query.Page, query.PageSize);
        var today = Today;
        var asOf = query.AsOf ?? today;

        IReadOnlySet<Guid>? units = null;
        if (query.OrgUnitId is { } unit)
        {
            units = query.IncludeSubUnits ? await org.GetOrgUnitSubtreeAsync(unit, asOf, cancellationToken) : new HashSet<Guid> { unit };
        }

        // The common case (no search and no filter on job data) needs no join to count or to find the page: the page comes straight from the
        // unique code index, and names and jobs are fetched for just those rows. This is what keeps the default list fast at 20,000 employees.
        var needsJoin = !string.IsNullOrWhiteSpace(query.Search) || query.LegalEntityId is not null || query.LocationId is not null || units is not null
                        || query.DesignationId is not null || query.GradeId is not null || query.EmployeeTypeId is not null || query.ManagerId is not null;
        if (!needsJoin)
        {
            return await ListWithoutJoinsAsync(visibility.Value, status, page, pageSize, today, asOf, cancellationToken);
        }

        var rows =
            from e in EmployeeAccess.Apply(db.Employees.AsNoTracking(), visibility.Value)
            join p in db.Personal.AsNoTracking() on e.Id equals p.EmployeeId into people
            from p in people.DefaultIfEmpty()
            join j in db.Jobs.AsNoTracking().ValidOn(asOf) on e.Id equals j.EmployeeId into jobs
            from j in jobs.DefaultIfEmpty()
            select new Row
            {
                Id = e.Id,
                Code = e.Code,
                JoiningDate = e.JoiningDate,
                ConfirmationDate = e.ConfirmationDate,
                LastWorkingDay = e.LastWorkingDay,
                RowVersion = e.RowVersion,
                FirstName = p == null ? null : p.FirstName,
                LastName = p == null ? null : p.LastName,
                JobId = j == null ? null : j.Id,
                JobFrom = j == null ? null : j.EffectiveFrom,
                JobTo = j == null ? null : j.EffectiveTo,
                LegalEntityId = j == null ? null : j.LegalEntityId,
                LocationId = j == null ? null : j.LocationId,
                OrgUnitId = j == null ? null : j.OrgUnitId,
                DesignationId = j == null ? null : j.DesignationId,
                EmployeeTypeId = j == null ? null : j.EmployeeTypeId,
                GradeId = j == null ? null : j.GradeId,
                CostCentreId = j == null ? null : j.CostCentreId,
                ManagerId = j == null ? null : j.ManagerId,
                ProbationEnd = j == null ? null : j.ProbationEnd,
                NoticePeriodDays = j == null ? null : j.NoticePeriodDays,
                Reason = j == null ? null : j.Reason,
            };

        rows = status switch
        {
            "active" => rows.Where(r => r.JoiningDate <= today && (r.LastWorkingDay == null || r.LastWorkingDay >= today)),
            "separated" => rows.Where(r => r.LastWorkingDay != null && r.LastWorkingDay < today),
            "notjoined" => rows.Where(r => r.JoiningDate > today),
            _ => rows,
        };

        if (query.Search?.Trim() is { Length: > 0 } search)
        {
            rows = rows.Where(r => r.Code.StartsWith(search) || (r.LastName != null && r.LastName.StartsWith(search)) || (r.FirstName != null && r.FirstName.StartsWith(search)));
        }

        if (query.LegalEntityId is { } entity)
        {
            rows = rows.Where(r => r.LegalEntityId == entity);
        }

        if (query.LocationId is { } location)
        {
            rows = rows.Where(r => r.LocationId == location);
        }

        if (units is not null)
        {
            rows = rows.Where(r => r.OrgUnitId != null && units.Contains(r.OrgUnitId.Value));
        }

        if (query.DesignationId is { } designation)
        {
            rows = rows.Where(r => r.DesignationId == designation);
        }

        if (query.GradeId is { } grade)
        {
            rows = rows.Where(r => r.GradeId == grade);
        }

        if (query.EmployeeTypeId is { } type)
        {
            rows = rows.Where(r => r.EmployeeTypeId == type);
        }

        if (query.ManagerId is { } manager)
        {
            rows = rows.Where(r => r.ManagerId == manager);
        }

        var total = await rows.LongCountAsync(cancellationToken);
        var pageRows = await rows.OrderBy(r => r.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        var items = pageRows.Select(r => new EmployeeSummaryView(
            r.Id,
            r.Code,
            DisplayName(r.FirstName, r.LastName),
            r.JoiningDate,
            r.ConfirmationDate,
            r.LastWorkingDay,
            Views.Status(r.JoiningDate, r.LastWorkingDay, today),
            r.JobId is null ? null : new JobView(
                r.JobId.Value, r.JobFrom!.Value, r.JobTo, r.LegalEntityId!.Value, r.LocationId!.Value, r.OrgUnitId!.Value, r.DesignationId!.Value,
                r.EmployeeTypeId!.Value, r.GradeId, r.CostCentreId, r.ManagerId, r.ProbationEnd, r.NoticePeriodDays!.Value, EnumText.Camel(r.Reason!.Value)),
            r.RowVersion)).ToList();

        return Result.Success(new PagedResult<EmployeeSummaryView>(items, page, pageSize, total));
    }

    private async Task<Result<PagedResult<EmployeeSummaryView>>> ListWithoutJoinsAsync(
        Visibility visibility, string status, int page, int pageSize, DateOnly today, DateOnly asOf, CancellationToken cancellationToken)
    {
        var employees = EmployeeAccess.Apply(db.Employees.AsNoTracking(), visibility);
        employees = status switch
        {
            "active" => employees.Where(e => e.JoiningDate <= today && (e.LastWorkingDay == null || e.LastWorkingDay >= today)),
            "separated" => employees.Where(e => e.LastWorkingDay != null && e.LastWorkingDay < today),
            "notjoined" => employees.Where(e => e.JoiningDate > today),
            _ => employees,
        };

        var total = await employees.LongCountAsync(cancellationToken);

        // For live rows the generated active_code equals code, and the unique index on it hands the rows over already sorted.
        var pageEmployees = await employees
            .OrderBy(e => EF.Property<string>(e, "ActiveCode"))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var ids = pageEmployees.Select(e => e.Id).ToList();
        var names = await db.Personal.AsNoTracking().Where(p => ids.Contains(p.EmployeeId))
            .Select(p => new { p.EmployeeId, p.FirstName, p.LastName }).ToDictionaryAsync(p => p.EmployeeId, cancellationToken);
        var jobs = (await db.Jobs.AsNoTracking().ValidOn(asOf).Where(j => ids.Contains(j.EmployeeId)).ToListAsync(cancellationToken))
            .ToDictionary(j => j.EmployeeId);

        var items = pageEmployees.Select(e => Views.Summary(
            e,
            names.TryGetValue(e.Id, out var name) ? DisplayName(name.FirstName, name.LastName) : string.Empty,
            jobs.GetValueOrDefault(e.Id),
            today)).ToList();
        return Result.Success(new PagedResult<EmployeeSummaryView>(items, page, pageSize, total));
    }

    // --- profile ---------------------------------------------------------------------------------------------------------------

    public async Task<Result<EmployeeProfileView>> GetProfileAsync(Guid id, DateOnly? asOf, ProfileAccess profileAccess, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileAccess);

        var loaded = await access.LoadAsync(id, Permissions.Employee.EmployeeView, tracked: false, cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<EmployeeProfileView>(loaded.Error);
        }

        var employee = loaded.Value;
        var today = Today;
        var date = asOf ?? today;

        var jobs = await db.Jobs.AsNoTracking().Where(j => j.EmployeeId == id).OrderBy(j => j.EffectiveFrom).ToListAsync(cancellationToken);
        var personal = await db.Personal.AsNoTracking().SingleOrDefaultAsync(p => p.EmployeeId == id, cancellationToken);
        var current = jobs.LastOrDefault(j => j.IsEffectiveOn(date));
        var summary = Views.Summary(employee, DisplayName(personal?.FirstName, personal?.LastName), current, today);

        var personalView = profileAccess.Personal && personal is not null && await CanSeeAsync(id, Permissions.Employee.PersonalView, cancellationToken)
            ? Views.Personal(personal, profileAccess.RevealSensitive)
            : null;

        SeparationView? separation = null;
        if (profileAccess.Personal && await CanSeeAsync(id, Permissions.Employee.PersonalView, cancellationToken))
        {
            var recorded = await db.Separations.AsNoTracking().SingleOrDefaultAsync(s => s.EmployeeId == id, cancellationToken);
            separation = recorded is null ? null : Views.Separation(recorded);
        }

        IReadOnlyList<StatutoryIdView>? statutory = null;
        if (profileAccess.Statutory && await CanSeeAsync(id, Permissions.Employee.StatutoryIdView, cancellationToken))
        {
            var ids = await db.StatutoryIds.AsNoTracking().Where(s => s.EmployeeId == id).OrderBy(s => s.IdType).ToListAsync(cancellationToken);
            statutory = ids.Select(s => protector.View(tenantContext.Require(), s, profileAccess.RevealSensitive)).ToList();
        }

        IReadOnlyList<DocumentView>? documents = null;
        if (profileAccess.Documents && await CanSeeAsync(id, Permissions.Employee.DocumentView, cancellationToken))
        {
            var docs = await db.Documents.AsNoTracking().Where(d => d.EmployeeId == id).OrderBy(d => d.DocumentType).ThenBy(d => d.Title).ToListAsync(cancellationToken);
            documents = docs.Select(d => Views.Document(d, today)).ToList();
        }

        IReadOnlyList<DeviceView>? devices = null;
        if (profileAccess.Devices && await CanSeeAsync(id, Permissions.Employee.DeviceView, cancellationToken))
        {
            var found = await db.Devices.AsNoTracking().Where(d => d.EmployeeId == id).OrderBy(d => d.RegisteredAt).ToListAsync(cancellationToken);
            devices = found.Select(Views.Device).ToList();
        }

        IReadOnlyList<ConsentView>? consents = null;
        if (profileAccess.Consents && await CanSeeAsync(id, Permissions.Employee.ConsentView, cancellationToken))
        {
            var found = await db.Consents.AsNoTracking().Where(c => c.EmployeeId == id).OrderBy(c => c.GrantedAt).ToListAsync(cancellationToken);
            consents = found.Select(Views.Consent).ToList();
        }

        return Result.Success(new EmployeeProfileView(
            summary, personalView, jobs.Select(Views.Job).ToList(), separation, statutory, documents, devices, consents));
    }

    // --- create ------------------------------------------------------------------------------------------------------------------

    public async Task<Result<EmployeeSummaryView>> CreateAsync(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Personal is null || request.Job is null)
        {
            return Fail<EmployeeSummaryView>(Error.Validation("employee.employee.details_required", "The personal details and the first job are required."));
        }

        var today = Today;
        var tenant = tenantContext.Require();
        var personalDetails = ToPersonal(request.Personal);
        if (personalDetails.IsFailure)
        {
            return Fail<EmployeeSummaryView>(personalDetails.Error);
        }

        var jobDetails = ToDetails(request.Job);

        // Everything that can be wrong with the request is found before a code is spent.
        var rehearsal = Guid.CreateVersion7();
        var checkPersonal = EmployeePersonal.Create(rehearsal, personalDetails.Value, today);
        if (checkPersonal.IsFailure)
        {
            return Fail<EmployeeSummaryView>(checkPersonal.Error);
        }

        var checkJob = EmployeeJob.Create(rehearsal, jobDetails, JobChangeReason.Joining, request.JoiningDate);
        if (checkJob.IsFailure)
        {
            return Fail<EmployeeSummaryView>(checkJob.Error);
        }

        var allowed = await access.CanCreateAsync(Permissions.Employee.EmployeeManage, jobDetails.LegalEntityId, cancellationToken);
        if (allowed.IsFailure)
        {
            return Fail<EmployeeSummaryView>(allowed.Error);
        }

        if (!allowed.Value)
        {
            return Fail<EmployeeSummaryView>(Error.Forbidden("employee.scope.insufficient_for_create", "Creating employees in this legal entity needs a tenant-wide or legal-entity scope."));
        }

        var references = await CheckReferencesAsync(jobDetails, cancellationToken);
        if (references is not null)
        {
            return Fail<EmployeeSummaryView>(references);
        }

        var managerProblem = await CheckManagerAsync(jobDetails.ManagerId, request.JoiningDate, cancellationToken);
        if (managerProblem is not null)
        {
            return Fail<EmployeeSummaryView>(managerProblem);
        }

        for (var attempt = 1; attempt <= CreateAttempts; attempt++)
        {
            var rule = await db.CodeRules.SingleOrDefaultAsync(r => r.LegalEntityId == jobDetails.LegalEntityId, cancellationToken);
            if (rule is null)
            {
                return Fail<EmployeeSummaryView>(Error.Conflict(
                    "employee.code_rule.missing", "Set the employee code rule of this legal entity before creating employees in it."));
            }

            var employee = EmployeeRecord.Create(rule.Allocate(), request.JoiningDate);
            if (employee.IsFailure)
            {
                return Fail<EmployeeSummaryView>(employee.Error);
            }

            var personal = EmployeePersonal.Create(employee.Value.Id, personalDetails.Value, today).Value;
            var job = EmployeeJob.Create(employee.Value.Id, jobDetails, JobChangeReason.Joining, request.JoiningDate).Value;
            job.MarkEventPublished(Now); // the joining period is announced by EmployeeCreated, not by a job-changed event

            db.Employees.Add(employee.Value);
            db.Personal.Add(personal);
            db.Jobs.Add(job);
            Outbox.Add(db, new EmployeeCreated(tenant.Value, employee.Value.Id, jobDetails.LegalEntityId, request.JoiningDate, Now));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success(Views.Summary(employee.Value, personal.DisplayName, job, today));
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
            {
                // Another create took the number (or a code collided with a hand-made one): start over with the rule as it is now.
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Employee create attempt {Attempt} lost a race for a code; retrying", attempt);
                }

                db.ChangeTracker.Clear();
                await Task.Delay(Random.Shared.Next(5, 40), cancellationToken);
            }
        }

        return Fail<EmployeeSummaryView>(Error.Conflict("employee.employee.code_busy", "Could not allocate an employee code because of heavy concurrent creation; try again."));
    }

    // --- confirm, personal ----------------------------------------------------------------------------------------------------------

    public async Task<Result<EmployeeSummaryView>> ConfirmAsync(Guid id, ConfirmEmployeeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await LoadForChangeAsync(id, Permissions.Employee.EmployeeManage, request.RowVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return Fail<EmployeeSummaryView>(loaded.Error);
        }

        var confirmed = loaded.Value.Confirm(request.ConfirmationDate ?? Today);
        if (confirmed.IsFailure)
        {
            return Fail<EmployeeSummaryView>(confirmed.Error);
        }

        var saved = await SaveAsync(cancellationToken);
        return saved.IsFailure ? Fail<EmployeeSummaryView>(saved.Error) : await SummaryAsync(loaded.Value, cancellationToken);
    }

    public async Task<Result<PersonalView>> UpdatePersonalAsync(Guid id, UpdatePersonalRequest request, bool revealSensitive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var employee = await access.LoadAsync(id, Permissions.Employee.PersonalManage, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Fail<PersonalView>(employee.Error);
        }

        if (request.RowVersion is not { } version)
        {
            return Fail<PersonalView>(RowVersionRequired());
        }

        if (request.Personal is null)
        {
            return Fail<PersonalView>(Error.Validation("employee.personal.details_required", "The personal details are required."));
        }

        var details = ToPersonal(request.Personal);
        if (details.IsFailure)
        {
            return Fail<PersonalView>(details.Error);
        }

        var personal = await db.Personal.SingleOrDefaultAsync(p => p.EmployeeId == id, cancellationToken);
        if (personal is null)
        {
            return Fail<PersonalView>(Error.NotFound("employee.personal.not_found", "The employee has no personal record."));
        }

        if (personal.RowVersion != version)
        {
            return Fail<PersonalView>(Stale());
        }

        var applied = personal.Update(details.Value, Today);
        if (applied.IsFailure)
        {
            return Fail<PersonalView>(applied.Error);
        }

        var saved = await SaveAsync(cancellationToken);
        return saved.IsFailure ? Fail<PersonalView>(saved.Error) : Result.Success(Views.Personal(personal, revealSensitive));
    }

    // --- jobs --------------------------------------------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<JobView>>> JobHistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var employee = await access.LoadAsync(id, Permissions.Employee.EmployeeView, tracked: false, cancellationToken);
        if (employee.IsFailure)
        {
            return Fail<IReadOnlyList<JobView>>(employee.Error);
        }

        IReadOnlyList<JobView> history = (await db.Jobs.AsNoTracking().Where(j => j.EmployeeId == id).OrderBy(j => j.EffectiveFrom).ToListAsync(cancellationToken))
            .Select(Views.Job).ToList();
        return Result.Success(history);
    }

    public async Task<Result<EmployeeSummaryView>> ChangeJobAsync(Guid id, ChangeJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await LoadForChangeAsync(id, Permissions.Employee.JobManage, request.RowVersion, cancellationToken);
        if (loaded.IsFailure)
        {
            return Fail<EmployeeSummaryView>(loaded.Error);
        }

        if (request.Job is null)
        {
            return Fail<EmployeeSummaryView>(Error.Validation("employee.job.details_required", "The job details are required."));
        }

        if (!EnumText.TryParse<JobChangeReason>(request.Reason, out var reason) || reason == JobChangeReason.Joining)
        {
            return Fail<EmployeeSummaryView>(Error.Validation("employee.job.reason_invalid", "The reason is transfer, promotion or change."));
        }

        var employee = loaded.Value;
        var details = ToDetails(request.Job);
        var created = EmployeeJob.Create(id, details, reason, request.EffectiveFrom);
        if (created.IsFailure)
        {
            return Fail<EmployeeSummaryView>(created.Error);
        }

        var history = await db.Jobs.Where(j => j.EmployeeId == id).ToListAsync(cancellationToken);
        var timeline = JobTimeline.CheckAppend(employee, history.Select(j => j.EffectiveFrom).ToList(), request.EffectiveFrom);
        if (timeline.IsFailure)
        {
            return Fail<EmployeeSummaryView>(timeline.Error);
        }

        var open = history.SingleOrDefault(j => j.EffectiveTo is null);
        if (open is not null && open.LegalEntityId != details.LegalEntityId)
        {
            var allowed = await CheckLegalEntityChangeAsync(open.LegalEntityId, details.LegalEntityId, cancellationToken);
            if (allowed is not null)
            {
                return Fail<EmployeeSummaryView>(allowed);
            }
        }

        var references = await CheckReferencesAsync(details, cancellationToken);
        if (references is not null)
        {
            return Fail<EmployeeSummaryView>(references);
        }

        var managerProblem = await CheckManagerAsync(details.ManagerId, request.EffectiveFrom, cancellationToken);
        if (managerProblem is not null)
        {
            return Fail<EmployeeSummaryView>(managerProblem);
        }

        var cycle = await ManagerChain.CheckAsync(id, details.ManagerId, request.EffectiveFrom, LoadManagerLinksAsync, cancellationToken);
        if (cycle.IsFailure)
        {
            return Fail<EmployeeSummaryView>(cycle.Error);
        }

        open?.EndOn(request.EffectiveFrom.AddDays(-1));
        var job = created.Value;
        db.Jobs.Add(job);

        // A change that is already in force is announced now; a future one by the event processor on the day it takes effect.
        if (request.EffectiveFrom <= Today)
        {
            job.MarkEventPublished(Now);
            Outbox.Add(db, new EmployeeJobChanged(tenantContext.Require().Value, id, job.Id, request.EffectiveFrom, EnumText.Camel(reason), Now));
        }

        db.Entry(employee).Property(e => e.JoiningDate).IsModified = true; // bumps the employee row version: racing changes cannot both win

        var saved = await SaveAsync(cancellationToken);
        return saved.IsFailure ? Fail<EmployeeSummaryView>(saved.Error) : await SummaryAsync(employee, cancellationToken);
    }

    // --- shared helpers -----------------------------------------------------------------------------------------------------------------

    private async Task<bool> CanSeeAsync(Guid employeeId, string permission, CancellationToken cancellationToken)
    {
        var visibility = await access.VisibilityAsync(permission, cancellationToken);
        return visibility.IsSuccess && visibility.Value.Includes(employeeId);
    }

    private async Task<Result<EmployeeRecord>> LoadForChangeAsync(Guid id, string permission, long? rowVersion, CancellationToken cancellationToken)
    {
        var loaded = await access.LoadAsync(id, permission, tracked: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        if (rowVersion is not { } version)
        {
            return Fail<EmployeeRecord>(RowVersionRequired());
        }

        return loaded.Value.RowVersion == version ? loaded : Fail<EmployeeRecord>(Stale());
    }

    private async Task<Result> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            return Result.Failure(Stale());
        }
    }

    private async Task<Result<EmployeeSummaryView>> SummaryAsync(EmployeeRecord employee, CancellationToken cancellationToken)
    {
        var today = Today;
        var job = await db.Jobs.AsNoTracking().ValidOn(today).SingleOrDefaultAsync(j => j.EmployeeId == employee.Id, cancellationToken);
        var name = await db.Personal.AsNoTracking().Where(p => p.EmployeeId == employee.Id).Select(p => p.FirstName + " " + p.LastName).SingleOrDefaultAsync(cancellationToken);
        return Result.Success(Views.Summary(employee, name ?? employee.Code, job, today));
    }

    private async Task<IReadOnlyList<ManagerLink>> LoadManagerLinksAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await db.Jobs.AsNoTracking().Where(j => j.EmployeeId == employeeId)
            .Select(j => new ManagerLink(j.EmployeeId, j.ManagerId, j.EffectiveFrom, j.EffectiveTo))
            .ToListAsync(cancellationToken);

    /// <summary>Every reference must exist for the tenant and be active; the first problem is returned.</summary>
    private async Task<Error?> CheckReferencesAsync(JobDetails details, CancellationToken cancellationToken)
    {
        (string Name, string Kind, Guid? Id)[] references =
        [
            ("legal_entity", MasterKinds.LegalEntity, details.LegalEntityId),
            ("location", MasterKinds.Location, details.LocationId),
            ("org_unit", MasterKinds.OrgUnit, details.OrgUnitId),
            ("designation", MasterKinds.Designation, details.DesignationId),
            ("employee_type", MasterKinds.EmployeeType, details.EmployeeTypeId),
            ("grade", MasterKinds.Grade, details.GradeId),
            ("cost_centre", MasterKinds.CostCentre, details.CostCentreId),
        ];

        foreach (var (name, kind, id) in references)
        {
            if (id is not { } value)
            {
                continue;
            }

            var status = await org.GetStatusAsync(kind, value, cancellationToken);
            if (status == MasterStatus.NotFound)
            {
                return Error.Validation($"employee.job.{name}_not_found", $"The {name.Replace('_', ' ')} does not exist.");
            }

            if (status == MasterStatus.Inactive)
            {
                return Error.Validation($"employee.job.{name}_inactive", $"The {name.Replace('_', ' ')} is inactive and cannot be assigned.");
            }
        }

        return null;
    }

    private async Task<Error?> CheckLegalEntityChangeAsync(Guid fromId, Guid toId, CancellationToken cancellationToken)
    {
        var from = await org.GetLegalEntityAsync(fromId, cancellationToken);
        var to = await org.GetLegalEntityAsync(toId, cancellationToken);
        return from is not null && to is not null && from.CountryCode == to.CountryCode && from.CurrencyCode == to.CurrencyCode && from.CountryPackCode == to.CountryPackCode
            ? null
            : Error.Validation("employee.job.legal_entity_change_not_allowed",
                "An employee can move only between legal entities with the same country, currency and country pack, because payroll rules differ otherwise.");
    }

    private async Task<Error?> CheckManagerAsync(Guid? managerId, DateOnly on, CancellationToken cancellationToken)
    {
        if (managerId is not { } id)
        {
            return null;
        }

        var manager = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (manager is null)
        {
            return Error.Validation("employee.job.manager_not_found", "The manager does not exist.");
        }

        return manager.IsEmployedOn(on) ? null : Error.Validation("employee.job.manager_not_employed", "The manager is not employed on that date.");
    }

    internal static Result<PersonalDetails> ToPersonal(PersonalRequest request)
    {
        if (!EnumText.TryParse<Gender>(request.Gender, out var gender))
        {
            return Fail<PersonalDetails>(Error.Validation("employee.personal.gender_invalid", "The gender is female, male, other or undisclosed."));
        }

        return Result.Success(new PersonalDetails(request.FirstName, request.MiddleName, request.LastName, request.DateOfBirth, gender, request.PersonalEmail, request.Phone));
    }

    internal static JobDetails ToDetails(JobRequest r) => new(
        r.LegalEntityId, r.LocationId, r.OrgUnitId, r.DesignationId, r.EmployeeTypeId, r.GradeId, r.CostCentreId, r.ManagerId, r.ProbationEnd, r.NoticePeriodDays);

    private static string DisplayName(string? first, string? last) =>
        first is null && last is null ? string.Empty : $"{first} {last}".Trim();

    private static Result<T> Fail<T>(Error error) => Result.Failure<T>(error);

    private static Error RowVersionRequired() => Error.Validation("employee.row_version_required", "The rowVersion the change is based on is required.");

    private static Error Stale() => Error.Conflict("employee.employee.stale_version", "The record was changed by someone else. Reload it and apply your change again.");
}
