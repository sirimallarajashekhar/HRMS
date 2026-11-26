using System.Globalization;
using FluentAssertions;

namespace Hrms.SharedKernel.UnitTests;

public class MoneyTests
{
    private static decimal Dec(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("inr", "INR")]
    [InlineData(" usd ", "USD")]
    public void Of_WithCurrencyCode_NormalisesToUpperCase(string input, string expected) =>
        Money.Of(1m, input).Currency.Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("IN")]
    [InlineData("INRR")]
    [InlineData("1NR")]
    public void Of_WithInvalidCurrency_Throws(string currency) =>
        FluentActions.Invoking(() => Money.Of(1m, currency)).Should().Throw<ArgumentException>();

    [Fact]
    public void Of_WithMoreThanFourDecimals_ThrowsInsteadOfRoundingSilently() =>
        FluentActions.Invoking(() => Money.Of(1.00005m, "INR")).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Of_WithFourDecimals_Succeeds() => Money.Of(1.2345m, "INR").Amount.Should().Be(1.2345m);

    [Theory]
    [InlineData("1.00005", MidpointRounding.ToEven, "1.0000")]
    [InlineData("1.00015", MidpointRounding.ToEven, "1.0002")]
    [InlineData("1.00005", MidpointRounding.AwayFromZero, "1.0001")]
    [InlineData("-1.00005", MidpointRounding.AwayFromZero, "-1.0001")]
    public void Rounded_AtTheMidpoint_FollowsTheStatedRule(string amount, MidpointRounding mode, string expected) =>
        Money.Rounded(Dec(amount), "INR", mode).Amount.Should().Be(Dec(expected));

    [Fact]
    public void Add_SameCurrency_Sums() =>
        (Money.Of(10.5m, "INR") + Money.Of(0.25m, "INR")).Should().Be(Money.Of(10.75m, "INR"));

    [Fact]
    public void Subtract_SameCurrency_Differences() =>
        (Money.Of(10m, "INR") - Money.Of(12.5m, "INR")).Should().Be(Money.Of(-2.5m, "INR"));

    [Fact]
    public void Add_DifferentCurrencies_Throws() =>
        FluentActions.Invoking(() => Money.Of(1m, "INR").Add(Money.Of(1m, "USD")))
            .Should().Throw<InvalidOperationException>();

    [Theory]
    [InlineData("100", "0.3333", MidpointRounding.ToEven, "33.3300")]
    [InlineData("10", "0.33335", MidpointRounding.AwayFromZero, "3.3335")]
    [InlineData("1", "0.00005", MidpointRounding.ToEven, "0.0000")]
    [InlineData("1", "0.00005", MidpointRounding.AwayFromZero, "0.0001")]
    public void Multiply_RoundsToStorageScaleWithTheStatedRule(string amount, string factor, MidpointRounding mode, string expected) =>
        Money.Of(Dec(amount), "INR").Multiply(Dec(factor), mode).Amount.Should().Be(Dec(expected));

    [Theory]
    [InlineData("12.345", 2, MidpointRounding.ToEven, "12.34")]
    [InlineData("12.345", 2, MidpointRounding.AwayFromZero, "12.35")]
    [InlineData("12.5", 0, MidpointRounding.ToEven, "12")]
    [InlineData("13.5", 0, MidpointRounding.ToEven, "14")]
    public void Round_ToFewerDecimals_FollowsTheStatedRule(string amount, int decimals, MidpointRounding mode, string expected) =>
        Money.Of(Dec(amount), "INR").Round(decimals, mode).Amount.Should().Be(Dec(expected));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void Round_WithDecimalsOutsideStorageScale_Throws(int decimals) =>
        FluentActions.Invoking(() => Money.Of(1m, "INR").Round(decimals, MidpointRounding.ToEven))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Equality_SameAmountAndCurrency_IsEqualRegardlessOfTrailingZeros() =>
        Money.Of(1.50m, "INR").Should().Be(Money.Of(1.5000m, "INR"));

    [Fact]
    public void Equality_SameAmountDifferentCurrency_IsNotEqual() =>
        Money.Of(1m, "INR").Should().NotBe(Money.Of(1m, "USD"));

    [Fact]
    public void ToString_UsesInvariantFourDecimals() => Money.Of(1234.5m, "INR").ToString().Should().Be("1234.5000 INR");

    [Fact]
    public void IsZeroAndIsNegative_ReflectTheAmount()
    {
        Money.Zero("INR").IsZero.Should().BeTrue();
        Money.Of(-0.0001m, "INR").IsNegative.Should().BeTrue();
        Money.Of(1m, "INR").Negate().IsNegative.Should().BeTrue();
    }
}
