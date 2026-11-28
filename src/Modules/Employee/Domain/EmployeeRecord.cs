using System.Text.RegularExpressions;
using Hrms.SharedKernel;

namespace Hrms.Employee.Domain;

/// <summary>
/// The employee: a stable identity (code, joining date) around which the effective-dated job history, personal data and everything else
/// hangs. The record holds no job fields and no status column: whether someone is employed on a date follows from the joining date and
/// the last working day, so nothing has to flip at midnight. Persisted as the <c>employee</c> table (the type is named EmployeeRecord
/// because a type called Employee would be shadowed by the Hrms.Employee namespace).
/// </summary>
[Audited]
public sealed partial class EmployeeRecord : TenantEntity
{
    public const int CodeMaxLength = 32;

    private EmployeeRecord(string code, DateOnly joiningDate)
    {
        Code = code;
        JoiningDate = joiningDate;
    }

    /// <summary>For the persistence layer only.</summary>
    private EmployeeRecord() => Code = string.Empty;

    /// <summary>Unique per tenant among live rows. Generated from the legal entity code rule; never edited.</summary>
    public string Code { get; private set; }

    public DateOnly JoiningDate { get; private set; }

    /// <summary>The date probation was confirmed, if it has been.</summary>
    public DateOnly? ConfirmationDate { get; private set; }

    /// <summary>The last day of employment, once a separation is recorded.</summary>
    public DateOnly? LastWorkingDay { get; private set; }

    public static Result<EmployeeRecord> Create(string? code, DateOnly joiningDate)
    {
        var normalised = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(normalised))
        {
            return Result.Failure<EmployeeRecord>(Validation.Invalid("employee", "code_invalid", "An employee code is 1 to 32 characters: letters, digits, hyphens and underscores."));
        }

        return joiningDate == default
            ? Result.Failure<EmployeeRecord>(Validation.Invalid("employee", "joining_date_required", "The joining date is required."))
            : Result.Success(new EmployeeRecord(normalised, joiningDate));
    }

    /// <summary>True from the joining date until the last working day inclusive (open-ended while no separation is recorded).</summary>
    public bool IsEmployedOn(DateOnly date) => JoiningDate <= date && (LastWorkingDay is null || LastWorkingDay >= date);

    public Result Confirm(DateOnly date)
    {
        if (ConfirmationDate is not null)
        {
            return Result.Failure(Error.Conflict("employee.employee.already_confirmed", "The employee is already confirmed."));
        }

        if (date < JoiningDate)
        {
            return Result.Failure(Validation.Invalid("employee", "confirmation_before_joining", "The confirmation date cannot be before the joining date."));
        }

        if (LastWorkingDay is { } last && date > last)
        {
            return Result.Failure(Validation.Invalid("employee", "confirmation_after_leaving", "The confirmation date cannot be after the last working day."));
        }

        ConfirmationDate = date;
        return Result.Success();
    }

    /// <summary>Sets or changes the last working day; it cannot precede the joining date or an existing confirmation.</summary>
    public Result SetLastWorkingDay(DateOnly lastWorkingDay)
    {
        if (lastWorkingDay < JoiningDate)
        {
            return Result.Failure(Validation.Invalid("separation", "before_joining", "The last working day cannot be before the joining date."));
        }

        if (ConfirmationDate is { } confirmed && lastWorkingDay < confirmed)
        {
            return Result.Failure(Validation.Invalid("separation", "before_confirmation", "The last working day cannot be before the confirmation date."));
        }

        LastWorkingDay = lastWorkingDay;
        return Result.Success();
    }

    /// <summary>Withdraws a separation, so the employee is employed again without an end date.</summary>
    public void ClearLastWorkingDay() => LastWorkingDay = null;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,31}$")]
    private static partial Regex CodePattern();
}
