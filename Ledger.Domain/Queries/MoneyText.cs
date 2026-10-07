using System.Globalization;

namespace Ledger.Domain.Queries;

/// <summary>Renders money amounts as exact text, so a result never carries a rounded or floating-point number.</summary>
public static class MoneyText
{
    /// <summary>
    /// Formats the amount with the invariant culture and at least two decimals, keeping every further digit that is not a
    /// trailing zero: 210 becomes "210.00", 1234.5000 becomes "1234.50" and 0.0125 stays "0.0125".
    /// </summary>
    /// <param name="amount">The amount to render.</param>
    public static string Format(decimal amount)
    {
        var text = (amount / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
        var separator = text.IndexOf('.', StringComparison.Ordinal);

        if (separator < 0)
        {
            return text + ".00";
        }

        var decimals = text.Length - separator - 1;

        return decimals >= 2 ? text : text + new string('0', 2 - decimals);
    }
}
