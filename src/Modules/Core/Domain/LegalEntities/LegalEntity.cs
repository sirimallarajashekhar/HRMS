using System.Text.RegularExpressions;
using Hrms.SharedKernel;

namespace Hrms.Core.Domain.LegalEntities;

/// <summary>
/// A company registered in one country that employs people and runs payroll in one currency under one country pack.
/// Country, currency, fiscal year start and country pack are fixed at creation: changing them would silently reinterpret
/// past payroll, so a change of any of them is a new legal entity. Name, default language and time zone can be updated.
/// </summary>
[Audited]
public sealed partial class LegalEntity : TenantEntity
{
    public const int CodeMaxLength = 32;
    public const int NameMaxLength = 200;
    public const int LanguageMaxLength = 35;
    public const int TimeZoneMaxLength = 64;

    private LegalEntity(
        string code,
        string name,
        string countryCode,
        string currencyCode,
        int fiscalYearStartMonth,
        string countryPackCode,
        string defaultLanguage,
        string timeZoneId)
    {
        Code = code;
        Name = name;
        CountryCode = countryCode;
        CurrencyCode = currencyCode;
        FiscalYearStartMonth = fiscalYearStartMonth;
        CountryPackCode = countryPackCode;
        DefaultLanguage = defaultLanguage;
        TimeZoneId = timeZoneId;
    }

    /// <summary>For the persistence layer only.</summary>
    private LegalEntity()
    {
        Code = Name = CountryCode = CurrencyCode = CountryPackCode = DefaultLanguage = TimeZoneId = string.Empty;
    }

    /// <summary>Upper case, unique per tenant.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    /// <summary>ISO 3166-1 alpha-2, for example IN.</summary>
    public string CountryCode { get; private set; }

    /// <summary>ISO 4217 alphabetic, for example INR.</summary>
    public string CurrencyCode { get; private set; }

    /// <summary>Month (1-12) the fiscal year starts on, always on the 1st.</summary>
    public int FiscalYearStartMonth { get; private set; }

    /// <summary>Selects the statutory rules in src/CountryPacks/&lt;code&gt;; the pack itself validates that the code exists.</summary>
    public string CountryPackCode { get; private set; }

    /// <summary>BCP 47 language tag, for example en-IN.</summary>
    public string DefaultLanguage { get; private set; }

    /// <summary>IANA time zone id, for example Asia/Kolkata.</summary>
    public string TimeZoneId { get; private set; }

    public static Result<LegalEntity> Create(
        string? code,
        string? name,
        string? countryCode,
        string? currencyCode,
        int fiscalYearStartMonth,
        string? countryPackCode,
        string? defaultLanguage,
        string? timeZoneId)
    {
        var normalisedCode = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(normalisedCode))
        {
            return Invalid("code_invalid", "A legal entity code is 2 to 32 characters: letters, digits, hyphens and underscores.");
        }

        if (ValidateName(name) is not { } validName)
        {
            return Invalid("name_invalid", "A legal entity name is 1 to 200 characters.");
        }

        var country = countryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!TwoLetters().IsMatch(country))
        {
            return Invalid("country_invalid", "The country is a two-letter ISO 3166-1 code.");
        }

        var currency = currencyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!ThreeLetters().IsMatch(currency))
        {
            return Invalid("currency_invalid", "The currency is a three-letter ISO 4217 code.");
        }

        if (fiscalYearStartMonth is < 1 or > 12)
        {
            return Invalid("fiscal_year_start_invalid", "The fiscal year start month is between 1 and 12.");
        }

        var pack = countryPackCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!TwoLetters().IsMatch(pack))
        {
            return Invalid("country_pack_invalid", "The country pack code is a two-letter ISO code.");
        }

        if (ValidateLanguage(defaultLanguage) is not { } language)
        {
            return Invalid("language_invalid", "The default language is a BCP 47 tag such as en-IN.");
        }

        if (ValidateTimeZone(timeZoneId) is not { } timeZone)
        {
            return Invalid("time_zone_invalid", "The time zone is not a known IANA time zone id.");
        }

        return Result.Success(new LegalEntity(normalisedCode, validName, country, currency, fiscalYearStartMonth, pack, language, timeZone));
    }

    /// <summary>Updates the fields that may change after creation. Nothing is applied unless all three are valid.</summary>
    public Result Update(string? name, string? defaultLanguage, string? timeZoneId)
    {
        if (ValidateName(name) is not { } validName)
        {
            return Result.Failure(Error.Validation("core.legal_entity.name_invalid", "A legal entity name is 1 to 200 characters."));
        }

        if (ValidateLanguage(defaultLanguage) is not { } language)
        {
            return Result.Failure(Error.Validation("core.legal_entity.language_invalid", "The default language is a BCP 47 tag such as en-IN."));
        }

        if (ValidateTimeZone(timeZoneId) is not { } timeZone)
        {
            return Result.Failure(Error.Validation("core.legal_entity.time_zone_invalid", "The time zone is not a known IANA time zone id."));
        }

        Name = validName;
        DefaultLanguage = language;
        TimeZoneId = timeZone;
        return Result.Success();
    }

    private static Result<LegalEntity> Invalid(string reason, string message) =>
        Result.Failure<LegalEntity>(Error.Validation($"core.legal_entity.{reason}", message));

    private static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > NameMaxLength ? null : trimmed;
    }

    private static string? ValidateLanguage(string? language)
    {
        var trimmed = language?.Trim() ?? string.Empty;
        return trimmed.Length <= LanguageMaxLength && LanguageTag().IsMatch(trimmed) ? trimmed : null;
    }

    private static string? ValidateTimeZone(string? timeZoneId)
    {
        var trimmed = timeZoneId?.Trim() ?? string.Empty;
        return trimmed.Length is > 0 and <= TimeZoneMaxLength
               && TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out var zone)
            ? zone.Id
            : null;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,31}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex TwoLetters();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex ThreeLetters();

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageTag();
}
