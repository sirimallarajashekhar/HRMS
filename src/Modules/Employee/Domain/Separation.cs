using Hrms.SharedKernel;

namespace Hrms.Employee.Domain;

public enum SeparationType
{
    Resignation = 1,
    Termination = 2,
    Retirement = 3,
    EndOfContract = 4,
    Other = 5,
}

/// <summary>
/// The recorded end of an employee's employment (at most one per employee). It carries the last working day, which the employee record
/// mirrors so "employed on a date" needs no join. The separated event goes out the day after the last working day.
/// </summary>
public sealed class Separation : TenantEntity
{
    public const int ReasonMaxLength = 500;

    private Separation(Guid employeeId, SeparationType type, DateOnly? noticeDate, DateOnly lastWorkingDay, string? reason)
    {
        EmployeeId = employeeId;
        Type = type;
        NoticeDate = noticeDate;
        LastWorkingDay = lastWorkingDay;
        Reason = reason;
    }

    /// <summary>For the persistence layer only.</summary>
    private Separation()
    {
    }

    public Guid EmployeeId { get; private set; }

    public SeparationType Type { get; private set; }

    /// <summary>The day notice was given (resignation) or served (termination), if known.</summary>
    public DateOnly? NoticeDate { get; private set; }

    public DateOnly LastWorkingDay { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>When the separated event was published (null until the day after the last working day, then set by the daily job).</summary>
    public DateTime? EventPublishedAt { get; private set; }

    public static Result<Separation> Create(Guid employeeId, SeparationType type, DateOnly? noticeDate, DateOnly lastWorkingDay, string? reason)
    {
        var valid = Validate(type, noticeDate, lastWorkingDay, reason);
        return valid.IsFailure
            ? Result.Failure<Separation>(valid.Error)
            : Result.Success(new Separation(employeeId, type, noticeDate, lastWorkingDay, valid.Value.Reason));
    }

    /// <summary>Changes the details; the event is re-armed so it goes out for the corrected last working day.</summary>
    public Result Update(SeparationType type, DateOnly? noticeDate, DateOnly lastWorkingDay, string? reason)
    {
        var valid = Validate(type, noticeDate, lastWorkingDay, reason);
        if (valid.IsFailure)
        {
            return valid;
        }

        Type = type;
        NoticeDate = noticeDate;
        LastWorkingDay = lastWorkingDay;
        Reason = valid.Value.Reason;
        EventPublishedAt = null;
        return Result.Success();
    }

    public void MarkEventPublished(DateTime at) => EventPublishedAt = at;

    private sealed record Checked(string? Reason);

    private static Result<Checked> Validate(SeparationType type, DateOnly? noticeDate, DateOnly lastWorkingDay, string? reason)
    {
        if (!Enum.IsDefined(type))
        {
            return Fail("type_invalid", "The separation type is not recognised.");
        }

        if (lastWorkingDay == default)
        {
            return Fail("last_working_day_required", "The last working day is required.");
        }

        if (noticeDate is { } notice && notice > lastWorkingDay)
        {
            return Fail("notice_after_last_day", "The notice date cannot be after the last working day.");
        }

        var text = Validation.Optional(reason, ReasonMaxLength, out var tooLong);
        return tooLong ? Fail("reason_invalid", "The reason is at most 500 characters.") : Result.Success(new Checked(text));
    }

    private static Result<Checked> Fail(string reason, string message) => Result.Failure<Checked>(Validation.Invalid("separation", reason, message));
}
