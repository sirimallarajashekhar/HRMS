using System.Text.RegularExpressions;
using Hrms.SharedKernel;

namespace Hrms.Employee.Domain;

public enum Gender
{
    Female = 1,
    Male = 2,
    Other = 3,
    Undisclosed = 4,
}

/// <summary>The editable personal details of an employee.</summary>
public sealed record PersonalDetails(
    string? FirstName,
    string? MiddleName,
    string? LastName,
    DateOnly DateOfBirth,
    Gender Gender,
    string? PersonalEmail,
    string? Phone);

/// <summary>
/// Personal data of one employee (one row per employee). Email and phone are personal contact data: they are masked for callers without
/// the sensitive-view permission when read. Date of birth is validated against the real calendar, not just "not null".
/// </summary>
[Audited(RootType = "employee", RootKey = nameof(EmployeeId))]
public sealed partial class EmployeePersonal : TenantEntity
{
    public const int NameMaxLength = 100;
    public const int EmailMaxLength = 254;
    public const int PhoneMaxLength = 20;

    private EmployeePersonal(Guid employeeId, ValidPersonal details)
    {
        EmployeeId = employeeId;
        Apply(details);
    }

    /// <summary>For the persistence layer only.</summary>
    private EmployeePersonal()
    {
    }

    public Guid EmployeeId { get; private set; }

    public string FirstName { get; private set; } = string.Empty;

    public string? MiddleName { get; private set; }

    public string LastName { get; private set; } = string.Empty;

    [AuditSensitive]
    public DateOnly DateOfBirth { get; private set; }

    public Gender Gender { get; private set; }

    [AuditSensitive]
    public string? PersonalEmail { get; private set; }

    [AuditSensitive]
    public string? Phone { get; private set; }

    /// <summary>First and last name, for lists and search results.</summary>
    public string DisplayName => $"{FirstName} {LastName}";

    public static Result<EmployeePersonal> Create(Guid employeeId, PersonalDetails? details, DateOnly today)
    {
        var valid = Validate(details, today);
        return valid.IsFailure ? Result.Failure<EmployeePersonal>(valid.Error) : Result.Success(new EmployeePersonal(employeeId, valid.Value));
    }

    public Result Update(PersonalDetails? details, DateOnly today)
    {
        var valid = Validate(details, today);
        if (valid.IsFailure)
        {
            return valid;
        }

        Apply(valid.Value);
        return Result.Success();
    }

    private void Apply(ValidPersonal d)
    {
        FirstName = d.FirstName;
        MiddleName = d.MiddleName;
        LastName = d.LastName;
        DateOfBirth = d.DateOfBirth;
        Gender = d.Gender;
        PersonalEmail = d.PersonalEmail;
        Phone = d.Phone;
    }

    private static Result<ValidPersonal> Validate(PersonalDetails? details, DateOnly today)
    {
        if (details is null)
        {
            return Fail("details_required", "The personal details are required.");
        }

        var first = Validation.Required(details.FirstName, NameMaxLength);
        var last = Validation.Required(details.LastName, NameMaxLength);
        if (first is null || last is null)
        {
            return Fail("name_invalid", "A first and a last name of 1 to 100 characters are required.");
        }

        var middle = Validation.Optional(details.MiddleName, NameMaxLength, out var middleTooLong);
        if (middleTooLong)
        {
            return Fail("name_invalid", "A middle name is at most 100 characters.");
        }

        if (details.DateOfBirth == default || details.DateOfBirth > today || details.DateOfBirth < new DateOnly(1900, 1, 1))
        {
            return Fail("date_of_birth_invalid", "The date of birth must be a past date after 1900.");
        }

        if (!Enum.IsDefined(details.Gender))
        {
            return Fail("gender_invalid", "The gender is not recognised.");
        }

        var email = Validation.Optional(details.PersonalEmail, EmailMaxLength, out var emailTooLong);
        if (emailTooLong || (email is not null && !EmailShape().IsMatch(email)))
        {
            return Fail("email_invalid", "The personal email is not a valid address.");
        }

        var phone = Validation.Optional(details.Phone, PhoneMaxLength, out var phoneTooLong);
        if (phoneTooLong || (phone is not null && !PhoneShape().IsMatch(phone)))
        {
            return Fail("phone_invalid", "The phone number is digits with an optional leading + and common separators, at most 20 characters.");
        }

        return Result.Success(new ValidPersonal(first, middle, last, details.DateOfBirth, details.Gender, email?.ToLowerInvariant(), phone));
    }

    private static Result<ValidPersonal> Fail(string reason, string message) => Result.Failure<ValidPersonal>(Validation.Invalid("personal", reason, message));

    private sealed record ValidPersonal(string FirstName, string? MiddleName, string LastName, DateOnly DateOfBirth, Gender Gender, string? PersonalEmail, string? Phone);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailShape();

    [GeneratedRegex(@"^\+?[0-9][0-9 ()\-]{4,19}$")]
    private static partial Regex PhoneShape();
}
