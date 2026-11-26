using Hrms.Contracts;
using Hrms.Contracts.Events;
using Hrms.Employee.Application;
using Hrms.Employee.Domain;
using Hrms.Employee.Infrastructure.Persistence;
using Hrms.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Hrms.Employee.Infrastructure;

/// <summary>
/// Records and withdraws the end of employment. A separation is refused while the person still has people reporting to them on the last
/// working day (reassign the reports first), or while job changes are scheduled after it. The separated event goes out the day after the
/// last working day, from the event processor.
/// </summary>
internal sealed class SeparationService(EmployeeDbContext db, EmployeeAccess access, TimeProvider time) : ISeparationService
{
    public async Task<Result<SeparationView>> SetAsync(Guid employeeId, SeparationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await access.LoadAsync(employeeId, Permissions.Employee.SeparationManage, tracked: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<SeparationView>(loaded.Error);
        }

        if (!EnumText.TryParse<SeparationType>(request.Type, out var type))
        {
            return Result.Failure<SeparationView>(Error.Validation(
                "employee.separation.type_invalid", "The type is resignation, termination, retirement, endOfContract or other."));
        }

        var employee = loaded.Value;
        var existing = await db.Separations.SingleOrDefaultAsync(s => s.EmployeeId == employeeId, cancellationToken);
        if (existing is not null && request.RowVersion is null)
        {
            return Result.Failure<SeparationView>(Error.Validation("employee.row_version_required", "The rowVersion of the existing separation is required."));
        }

        if (existing is not null && existing.RowVersion != request.RowVersion)
        {
            return Result.Failure<SeparationView>(Stale());
        }

        var lastDay = request.LastWorkingDay;
        var draft = existing is null
            ? Separation.Create(employeeId, type, request.NoticeDate, lastDay, request.Reason)
            : Result.Success(existing);
        if (draft.IsFailure)
        {
            return Result.Failure<SeparationView>(draft.Error);
        }

        var dated = employee.SetLastWorkingDay(lastDay);
        if (dated.IsFailure)
        {
            return Result.Failure<SeparationView>(dated.Error);
        }

        if (await db.Jobs.AsNoTracking().AnyAsync(j => j.EmployeeId == employeeId && j.EffectiveFrom > lastDay, cancellationToken))
        {
            return Result.Failure<SeparationView>(Error.Validation(
                "employee.separation.future_changes_exist", "Job changes are scheduled after the last working day; they must be removed first."));
        }

        var hasReports = await (
                from job in db.Jobs.AsNoTracking().ValidOn(lastDay)
                join report in db.Employees.AsNoTracking() on job.EmployeeId equals report.Id
                where job.ManagerId == employeeId && report.JoiningDate <= lastDay && (report.LastWorkingDay == null || report.LastWorkingDay >= lastDay)
                select job.Id)
            .AnyAsync(cancellationToken);
        if (hasReports)
        {
            return Result.Failure<SeparationView>(Error.Validation(
                "employee.separation.has_reports", "The employee still has people reporting to them on the last working day; reassign them first."));
        }

        Separation separation;
        if (existing is null)
        {
            separation = draft.Value;
            db.Separations.Add(separation);
        }
        else
        {
            separation = existing;
            var updated = separation.Update(type, request.NoticeDate, lastDay, request.Reason);
            if (updated.IsFailure)
            {
                return Result.Failure<SeparationView>(updated.Error);
            }
        }

        db.Entry(employee).Property(e => e.JoiningDate).IsModified = true; // serialises with job changes on the same employee

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            return Result.Failure<SeparationView>(Stale());
        }

        return Result.Success(Views.Separation(separation));
    }

    public async Task<Result> WithdrawAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var loaded = await access.LoadAsync(employeeId, Permissions.Employee.SeparationManage, tracked: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.Error);
        }

        var separation = await db.Separations.SingleOrDefaultAsync(s => s.EmployeeId == employeeId, cancellationToken);
        if (separation is null)
        {
            return Result.Failure(Error.NotFound("employee.separation.not_found", "The employee has no separation recorded."));
        }

        if (separation.EventPublishedAt is not null || separation.LastWorkingDay < DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime))
        {
            return Result.Failure(Error.Conflict("employee.separation.already_effective", "The last working day has passed; the separation can no longer be withdrawn."));
        }

        loaded.Value.ClearLastWorkingDay();
        db.Separations.Remove(separation);
        db.Entry(loaded.Value).Property(e => e.JoiningDate).IsModified = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            return Result.Failure(Stale());
        }

        return Result.Success();
    }

    private static Error Stale() => Error.Conflict("employee.separation.stale_version", "The record was changed by someone else. Reload it and apply your change again.");
}

/// <summary>The employee code rules, one per legal entity. They are configuration, so the permission alone decides (no per-employee scope).</summary>
internal sealed class CodeRuleService(EmployeeDbContext db, IOrgStructureLookup org) : ICodeRuleService
{
    public async Task<IReadOnlyList<CodeRuleView>> ListAsync(CancellationToken cancellationToken) =>
        (await db.CodeRules.AsNoTracking().OrderBy(r => r.Prefix).ThenBy(r => r.Id).ToListAsync(cancellationToken)).Select(ToView).ToList();

    public async Task<Result<CodeRuleView>> GetAsync(Guid legalEntityId, CancellationToken cancellationToken)
    {
        var rule = await db.CodeRules.AsNoTracking().SingleOrDefaultAsync(r => r.LegalEntityId == legalEntityId, cancellationToken);
        return rule is null ? Result.Failure<CodeRuleView>(NotFound()) : Result.Success(ToView(rule));
    }

    public async Task<Result<CodeRuleView>> SetAsync(Guid legalEntityId, CodeRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await org.GetLegalEntityAsync(legalEntityId, cancellationToken) is null)
        {
            return Result.Failure<CodeRuleView>(Error.NotFound("employee.code_rule.legal_entity_not_found", "The legal entity does not exist."));
        }

        var rule = await db.CodeRules.SingleOrDefaultAsync(r => r.LegalEntityId == legalEntityId, cancellationToken);
        if (rule is null)
        {
            var created = EmployeeCodeRule.Create(legalEntityId, request.Prefix, request.Width, request.NextNumber);
            if (created.IsFailure)
            {
                return Result.Failure<CodeRuleView>(created.Error);
            }

            db.CodeRules.Add(created.Value);
            return await SaveAsync(created.Value, cancellationToken);
        }

        if (request.RowVersion is not { } version)
        {
            return Result.Failure<CodeRuleView>(Error.Validation("employee.row_version_required", "The rowVersion of the existing rule is required."));
        }

        if (rule.RowVersion != version)
        {
            return Result.Failure<CodeRuleView>(Stale());
        }

        var updated = rule.Update(request.Prefix, request.Width, request.NextNumber);
        return updated.IsFailure ? Result.Failure<CodeRuleView>(updated.Error) : await SaveAsync(rule, cancellationToken);
    }

    private async Task<Result<CodeRuleView>> SaveAsync(EmployeeCodeRule rule, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            return Result.Failure<CodeRuleView>(Stale());
        }

        return Result.Success(ToView(rule));
    }

    private static CodeRuleView ToView(EmployeeCodeRule r) =>
        new(r.Id, r.LegalEntityId, r.Prefix, r.Width, r.NextNumber, EmployeeCodeRule.Format(r.Prefix, r.NextNumber, r.Width), r.RowVersion);

    private static Error NotFound() => Error.NotFound("employee.code_rule.not_found", "The legal entity has no code rule.");

    private static Error Stale() => Error.Conflict("employee.code_rule.stale_version", "The record was changed by someone else. Reload it and apply your change again.");
}
