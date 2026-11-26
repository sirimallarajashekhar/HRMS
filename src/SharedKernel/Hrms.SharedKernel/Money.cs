using System.Globalization;
using System.Text.RegularExpressions;

namespace Hrms.SharedKernel;

/// <summary>
/// An amount of money in one ISO 4217 currency, stored as decimal(18,4) plus the currency code.
/// Construction never rounds silently: an amount with more than <see cref="Scale"/> decimals is rejected,
/// and every operation that can create more decimals takes an explicit <see cref="MidpointRounding"/>.
/// </summary>
public sealed partial record Money
{
    /// <summary>Decimal places stored in the database (decimal(18,4)).</summary>
    public const int Scale = 4;

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    /// <summary>Upper-case ISO 4217 alphabetic code, for example INR.</summary>
    public string Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    /// <summary>Creates money from an amount that already fits the storage scale; throws if it has more than 4 decimals.</summary>
    public static Money Of(decimal amount, string currency)
    {
        if (decimal.Round(amount, Scale, MidpointRounding.ToEven) != amount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), "Amount has more than 4 decimal places; round it explicitly with Money.Rounded.");
        }

        return new Money(amount, NormaliseCurrency(currency));
    }

    /// <summary>Creates money from a computed amount, rounding to the storage scale with the stated rule.</summary>
    public static Money Rounded(decimal amount, string currency, MidpointRounding mode) =>
        new(decimal.Round(amount, Scale, mode), NormaliseCurrency(currency));

    public static Money Zero(string currency) => new(0m, NormaliseCurrency(currency));

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Negate() => new(-Amount, Currency);

    /// <summary>Multiplies by a factor (a rate, a proration) and rounds to the storage scale with the stated rule.</summary>
    public Money Multiply(decimal factor, MidpointRounding mode) =>
        new(decimal.Round(Amount * factor, Scale, mode), Currency);

    /// <summary>Rounds to fewer decimals (for example 2 for a payslip line) with the stated rule.</summary>
    public Money Round(int decimals, MidpointRounding mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, Scale);
        return new Money(decimal.Round(Amount, decimals, mode), Currency);
    }

    public static Money operator +(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Add(right);
    }

    public static Money operator -(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Subtract(right);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount.ToString("F4", CultureInfo.InvariantCulture)} {Currency}");

    private void EnsureSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot combine {Currency} with {other.Currency}.");
        }
    }

    private static string NormaliseCurrency(string currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        var code = currency.Trim().ToUpperInvariant();
        if (!CurrencyCode().IsMatch(code))
        {
            throw new ArgumentException("Currency must be a three-letter ISO 4217 code.", nameof(currency));
        }

        return code;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}
