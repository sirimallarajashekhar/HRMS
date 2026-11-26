using FluentAssertions;
using Hrms.Core.Domain.LegalEntities;
using Hrms.SharedKernel;

namespace Hrms.Core.UnitTests;

public class LegalEntityTests
{
    private static Result<LegalEntity> Create(
        string? code = "acme-in",
        string? name = "Acme India Pvt Ltd",
        string? country = "in",
        string? currency = "inr",
        int fiscalMonth = 4,
        string? pack = "in",
        string? language = "en-IN",
        string? timeZone = "Asia/Kolkata") =>
        LegalEntity.Create(code, name, country, currency, fiscalMonth, pack, language, timeZone);

    [Fact]
    public void Create_WithValidData_NormalisesCodesAndKeepsTheRest()
    {
        var entity = Create().Value;

        entity.Code.Should().Be("ACME-IN");
        entity.Name.Should().Be("Acme India Pvt Ltd");
        entity.CountryCode.Should().Be("IN");
        entity.CurrencyCode.Should().Be("INR");
        entity.FiscalYearStartMonth.Should().Be(4);
        entity.CountryPackCode.Should().Be("IN");
        entity.DefaultLanguage.Should().Be("en-IN");
        entity.TimeZoneId.Should().Be("Asia/Kolkata");
    }

    [Theory]
    [InlineData("code", null, "code_invalid")]
    [InlineData("code", "", "code_invalid")]
    [InlineData("code", "a", "code_invalid")]
    [InlineData("code", "has space", "code_invalid")]
    [InlineData("name", null, "name_invalid")]
    [InlineData("name", "   ", "name_invalid")]
    [InlineData("country", null, "country_invalid")]
    [InlineData("country", "IND", "country_invalid")]
    [InlineData("country", "1N", "country_invalid")]
    [InlineData("currency", null, "currency_invalid")]
    [InlineData("currency", "IN", "currency_invalid")]
    [InlineData("currency", "INRR", "currency_invalid")]
    [InlineData("pack", null, "country_pack_invalid")]
    [InlineData("pack", "IND", "country_pack_invalid")]
    [InlineData("language", null, "language_invalid")]
    [InlineData("language", "english", "language_invalid")]
    [InlineData("language", "en_IN", "language_invalid")]
    [InlineData("timezone", null, "time_zone_invalid")]
    [InlineData("timezone", "Mars/Olympus_Mons", "time_zone_invalid")]
    [InlineData("timezone", "", "time_zone_invalid")]
    public void Create_WithAnInvalidField_FailsWithThatFieldsStableCode(string field, string? value, string expectedCode)
    {
        var result = field switch
        {
            "code" => Create(code: value),
            "name" => Create(name: value),
            "country" => Create(country: value),
            "currency" => Create(currency: value),
            "pack" => Create(pack: value),
            "language" => Create(language: value),
            _ => Create(timeZone: value),
        };

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be($"core.legal_entity.{expectedCode}");
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    public void Create_WithAFiscalMonthOutsideOneToTwelve_Fails(int month) =>
        Create(fiscalMonth: month).Error.Code.Should().Be("core.legal_entity.fiscal_year_start_invalid");

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void Create_WithTheFirstOrLastMonth_Succeeds(int month) => Create(fiscalMonth: month).IsSuccess.Should().BeTrue();

    [Fact]
    public void Update_WithValidData_ChangesOnlyNameLanguageAndTimeZone()
    {
        var entity = Create().Value;

        var result = entity.Update("  Acme India Limited ", "hi-IN", "Asia/Kolkata");

        result.IsSuccess.Should().BeTrue();
        entity.Name.Should().Be("Acme India Limited");
        entity.DefaultLanguage.Should().Be("hi-IN");
        entity.CountryCode.Should().Be("IN");
        entity.CurrencyCode.Should().Be("INR");
        entity.FiscalYearStartMonth.Should().Be(4);
        entity.CountryPackCode.Should().Be("IN");
        entity.Code.Should().Be("ACME-IN");
    }

    [Theory]
    [InlineData(null, "en-IN", "Asia/Kolkata", "name_invalid")]
    [InlineData("Acme", "x", "Asia/Kolkata", "language_invalid")]
    [InlineData("Acme", "en-IN", "Nowhere/Land", "time_zone_invalid")]
    public void Update_WithAnInvalidField_FailsAndChangesNothing(string? name, string language, string timeZone, string expectedCode)
    {
        var entity = Create().Value;

        var result = entity.Update(name, language, timeZone);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be($"core.legal_entity.{expectedCode}");
        entity.Name.Should().Be("Acme India Pvt Ltd");
        entity.DefaultLanguage.Should().Be("en-IN");
        entity.TimeZoneId.Should().Be("Asia/Kolkata");
    }

    [Fact]
    public void Update_WithAValidNameButInvalidTimeZone_DoesNotApplyTheNameEither()
    {
        var entity = Create().Value;

        entity.Update("Renamed", "en-IN", "Nowhere/Land").IsFailure.Should().BeTrue();

        entity.Name.Should().Be("Acme India Pvt Ltd");
    }

    [Fact]
    public void Entity_ExposesNoSettersForTheFieldsThatAreFixedAtCreation()
    {
        var fixedAtCreation = new[]
        {
            nameof(LegalEntity.Code), nameof(LegalEntity.CountryCode), nameof(LegalEntity.CurrencyCode),
            nameof(LegalEntity.FiscalYearStartMonth), nameof(LegalEntity.CountryPackCode),
        };

        foreach (var name in fixedAtCreation)
        {
            var setter = typeof(LegalEntity).GetProperty(name)!.SetMethod;
            setter.Should().NotBeNull();
            setter!.IsPublic.Should().BeFalse($"{name} must not be publicly settable");
        }
    }
}
