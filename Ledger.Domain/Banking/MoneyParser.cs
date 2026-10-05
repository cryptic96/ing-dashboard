using System.Globalization;

namespace Ledger.Domain.Banking;

/// <summary>
/// Parses the amount strings a bank data provider sends into exact decimals. The text must be plain digits with an optional
/// fraction; anything else is rejected rather than interpreted or rounded, so a malformed amount can never be stored.
/// </summary>
public static class MoneyParser
{
    /// <summary>The most digits allowed before the decimal point.</summary>
    public const int MaxIntegerDigits = 15;

    /// <summary>The most digits allowed after the decimal point.</summary>
    public const int MaxFractionDigits = 4;

    /// <summary>
    /// Parses an unsigned amount and applies the direction: money leaving the account is negative. Only ASCII digits and one
    /// decimal point are accepted. A sign, an exponent, a thousands separator, a decimal comma, surrounding whitespace, more than
    /// four decimals and sixteen or more integer digits are all rejected.
    /// </summary>
    /// <param name="amount">The amount text exactly as the provider sent it.</param>
    /// <param name="isDebit">Whether the amount is money leaving the account.</param>
    /// <returns>The exact amount, negative for a debit.</returns>
    /// <exception cref="BankProviderException">The text is not a well-formed amount.</exception>
    public static decimal Parse(string amount, bool isDebit)
    {
        if (!IsWellFormed(amount))
        {
            throw new BankProviderException(
                ProviderErrorKind.MalformedData,
                "malformed_amount",
                "The provider sent an amount that is not a plain decimal number.");
        }

        var value = decimal.Parse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return isDebit ? -value : value;
    }

    private static bool IsWellFormed(string? amount)
    {
        if (string.IsNullOrEmpty(amount))
        {
            return false;
        }

        var separator = amount.IndexOf('.', StringComparison.Ordinal);
        var integerPart = separator < 0 ? amount : amount[..separator];
        var fractionPart = separator < 0 ? string.Empty : amount[(separator + 1)..];

        if (integerPart.Length is 0 or > MaxIntegerDigits || !IsAsciiDigits(integerPart))
        {
            return false;
        }

        if (separator < 0)
        {
            return true;
        }

        return fractionPart.Length is >= 1 and <= MaxFractionDigits && IsAsciiDigits(fractionPart);
    }

    private static bool IsAsciiDigits(string text)
    {
        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
