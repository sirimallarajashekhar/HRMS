using FluentAssertions;

namespace Hrms.SharedKernel.UnitTests;

public class TenantIdAndErrorTests
{
    [Fact]
    public void TenantId_Empty_Throws() =>
        FluentActions.Invoking(() => new TenantId(Guid.Empty)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TenantId_TryParse_RejectsMissingMalformedAndEmpty(string? text) =>
        TenantId.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void TenantId_TryParse_AcceptsAGuid()
    {
        var guid = Guid.CreateVersion7();

        TenantId.TryParse(guid.ToString(), out var id).Should().BeTrue();
        id.Value.Should().Be(guid);
    }

    [Fact]
    public void TenantContext_SetTwice_Throws()
    {
        var context = new TenantContext();
        context.Set(new TenantId(Guid.CreateVersion7()));

        FluentActions.Invoking(() => context.Set(new TenantId(Guid.CreateVersion7()))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Require_WithoutTenant_Throws() =>
        FluentActions.Invoking(() => new TenantContext().Require()).Should().Throw<TenantNotResolvedException>();

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Forbidden)]
    public void ErrorFactories_SetTheMatchingType(ErrorType type)
    {
        var error = type switch
        {
            ErrorType.Validation => Error.Validation("x.y", "m"),
            ErrorType.NotFound => Error.NotFound("x.y", "m"),
            ErrorType.Conflict => Error.Conflict("x.y", "m"),
            _ => Error.Forbidden("x.y", "m"),
        };

        error.Type.Should().Be(type);
    }

    [Fact]
    public void Error_WithoutClassification_DefaultsToUnexpected() =>
        new Error("x.y", "m").Type.Should().Be(ErrorType.Unexpected);

    [Fact]
    public void ToBusinessDate_InstantNearMidnightUtc_UsesTheLocationZone()
    {
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var instant = new DateTimeOffset(2026, 3, 31, 20, 0, 0, TimeSpan.Zero); // 01:30 on 1 April in IST

        BusinessTime.ToBusinessDate(instant, kolkata).Should().Be(new DateOnly(2026, 4, 1));
    }
}
