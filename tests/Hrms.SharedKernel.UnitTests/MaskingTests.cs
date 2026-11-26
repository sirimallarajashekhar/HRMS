using FluentAssertions;

namespace Hrms.SharedKernel.UnitTests;

public class MaskingTests
{
    [Theory]
    [InlineData("123456789012", SensitiveKind.BankAccount, "9012")]
    [InlineData("987654321098", SensitiveKind.NationalId, "1098")]
    [InlineData("+919876543210", SensitiveKind.Phone, "3210")]
    public void Mask_ShowsTheLastFourOfALongValueAndHidesTheRest(string value, SensitiveKind kind, string tail)
    {
        var masked = Masking.Mask(value, kind)!;

        masked.Should().EndWith(tail);
        masked[..^4].Should().MatchRegex("^X+$");
        masked.Length.Should().Be(value.Length);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("123")]
    [InlineData("12")]
    [InlineData("1")]
    public void Mask_NeverRevealsMoreThanAThirdOfAShortValue(string value)
    {
        var masked = Masking.Mask(value, SensitiveKind.BankAccount)!;

        masked.Length.Should().Be(value.Length);
        masked.Count(c => c != 'X').Should().BeLessThanOrEqualTo(value.Length / 3);
    }

    [Theory]
    [InlineData("asha.rao@example.com", "a***@example.com")]
    [InlineData("x@y.io", "x***@y.io")]
    public void MaskEmail_KeepsTheFirstLetterAndTheDomain(string email, string expected) =>
        Masking.Mask(email, SensitiveKind.Email).Should().Be(expected);

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    public void MaskEmail_FullyRedactsAnythingThatIsNotAnEmail(string value) =>
        Masking.Mask(value, SensitiveKind.Email).Should().Be(Masking.Redacted);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Mask_LeavesNullAndEmptyAlone(string? value) =>
        Masking.Mask(value, SensitiveKind.BankAccount).Should().Be(value);

    [Fact]
    public void MaskUnless_ReturnsTheRealValueOnlyForThoseWhoMaySeeIt()
    {
        Masking.MaskUnless(true, "123456789012", SensitiveKind.BankAccount).Should().Be("123456789012");
        Masking.MaskUnless(false, "123456789012", SensitiveKind.BankAccount).Should().Be("XXXXXXXX9012");
    }

    [Fact]
    public void HideUnless_ReturnsNoValueAtAllForThoseWhoMayNotSeeIt()
    {
        Masking.HideUnless<decimal>(true, 1_200_000m).Should().Be(1_200_000m);
        Masking.HideUnless<decimal>(false, 1_200_000m).Should().BeNull();
        Masking.HideUnless<decimal>(false, null).Should().BeNull();
    }

    [Fact]
    public void Mask_IsNotReversible_TheMaskedFormNeverContainsTheHiddenPart()
    {
        var masked = Masking.Mask("123456789012", SensitiveKind.BankAccount)!;

        masked.Should().NotContain("12345678");
    }
}
