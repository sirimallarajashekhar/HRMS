using Hrms.SharedKernel;

namespace Hrms.Core.Domain.Org;

/// <summary>The editable details of a location.</summary>
public sealed record LocationDetails(
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryCode,
    string? TimeZoneId,
    int WorkWeekDays,
    Guid? HolidayCalendarId);

/// <summary>
/// A place people work: an address, an IANA time zone, a work-week definition and a holiday calendar to be wired up later.
/// The work week is a bit mask of the days that are working days (Monday = 1 ... Sunday = 64); alternate-Saturday style rules
/// belong to the policy engine, not here. The holiday calendar id is a placeholder until the calendar exists, so it has no
/// foreign key.
/// </summary>
public sealed class Location : CodedMaster
{
    public const string Master = "location";
    public const int AddressMaxLength = 200;
    public const int CityMaxLength = 100;
    public const int PostalCodeMaxLength = 20;
    public const int TimeZoneMaxLength = 64;

    /// <summary>Monday to Friday.</summary>
    public const int MondayToFriday = 0b0011111;

    private const int AllDays = 0b1111111;

    private Location(string code, string name, ValidDetails details)
        : base(code, name) => Apply(details);

    /// <summary>For the persistence layer only.</summary>
    private Location() => AddressLine1 = City = CountryCode = TimeZoneId = string.Empty;

    public string AddressLine1 { get; private set; } = string.Empty;

    public string? AddressLine2 { get; private set; }

    public string City { get; private set; } = string.Empty;

    public string? State { get; private set; }

    public string? PostalCode { get; private set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string CountryCode { get; private set; } = string.Empty;

    /// <summary>IANA time zone id, for example Asia/Kolkata.</summary>
    public string TimeZoneId { get; private set; } = string.Empty;

    /// <summary>Bit mask of working days: Monday = 1, Tuesday = 2 ... Sunday = 64.</summary>
    public int WorkWeekDays { get; private set; }

    /// <summary>Placeholder for the holiday calendar of a later step; no foreign key yet.</summary>
    public Guid? HolidayCalendarId { get; private set; }

    public static bool IsWorkingDay(int workWeekDays, DayOfWeek day) => (workWeekDays & (1 << (((int)day + 6) % 7))) != 0;

    public static Result<Location> Create(string? code, string? name, LocationDetails? details)
    {
        var identity = ValidateIdentity(Master, code, name);
        if (identity.IsFailure)
        {
            return Result.Failure<Location>(identity.Error);
        }

        var valid = Validate(details);
        return valid.IsFailure
            ? Result.Failure<Location>(valid.Error)
            : Result.Success(new Location(identity.Value.Code, identity.Value.Name, valid.Value));
    }

    public Result Update(string? name, LocationDetails? details, bool isActive)
    {
        var valid = Validate(details);
        if (valid.IsFailure)
        {
            return valid;
        }

        var applied = ApplyIdentity(Master, name, isActive);
        if (applied.IsSuccess)
        {
            Apply(valid.Value);
        }

        return applied;
    }

    private void Apply(ValidDetails details)
    {
        AddressLine1 = details.AddressLine1;
        AddressLine2 = details.AddressLine2;
        City = details.City;
        State = details.State;
        PostalCode = details.PostalCode;
        CountryCode = details.CountryCode;
        TimeZoneId = details.TimeZoneId;
        WorkWeekDays = details.WorkWeekDays;
        HolidayCalendarId = details.HolidayCalendarId;
    }

    private static Result<ValidDetails> Validate(LocationDetails? details)
    {
        if (details is null)
        {
            return Fail<ValidDetails>("details_required", "The location details are required.");
        }

        var line1 = Trim(details.AddressLine1);
        if (line1 is null or { Length: 0 } or { Length: > AddressMaxLength })
        {
            return Fail<ValidDetails>("address_invalid", "The first address line is 1 to 200 characters.");
        }

        var line2 = Trim(details.AddressLine2);
        if (line2 is { Length: > AddressMaxLength })
        {
            return Fail<ValidDetails>("address_invalid", "The second address line is at most 200 characters.");
        }

        var city = Trim(details.City);
        if (city is null or { Length: 0 } or { Length: > CityMaxLength })
        {
            return Fail<ValidDetails>("city_invalid", "The city is 1 to 100 characters.");
        }

        var state = Trim(details.State);
        if (state is { Length: > CityMaxLength })
        {
            return Fail<ValidDetails>("state_invalid", "The state is at most 100 characters.");
        }

        var postal = Trim(details.PostalCode);
        if (postal is { Length: > PostalCodeMaxLength })
        {
            return Fail<ValidDetails>("postal_code_invalid", "The postal code is at most 20 characters.");
        }

        var country = details.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
        {
            return Fail<ValidDetails>("country_invalid", "The country is a two-letter ISO 3166-1 code.");
        }

        var zone = details.TimeZoneId?.Trim() ?? string.Empty;
        if (zone.Length is 0 or > TimeZoneMaxLength || !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var found))
        {
            return Fail<ValidDetails>("time_zone_invalid", "The time zone is not a known IANA time zone id.");
        }

        if (details.WorkWeekDays is < 1 or > AllDays)
        {
            return Fail<ValidDetails>("work_week_invalid", "The work week is a mask of 1 to 127 with at least one working day (Monday = 1 ... Sunday = 64).");
        }

        return Result.Success(new ValidDetails(
            line1, line2, city, state, postal, country, found.Id, details.WorkWeekDays, details.HolidayCalendarId));
    }

    private static string? Trim(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static Result<T> Fail<T>(string reason, string message) => Result.Failure<T>(Error.Validation($"core.location.{reason}", message));

    private sealed record ValidDetails(
        string AddressLine1,
        string? AddressLine2,
        string City,
        string? State,
        string? PostalCode,
        string CountryCode,
        string TimeZoneId,
        int WorkWeekDays,
        Guid? HolidayCalendarId);
}
