using FluentAssertions;
using Ledger.Domain.Queries;

namespace Ledger.UnitTests.Mcp;

/// <summary>Verifies money amounts render as exact text with at least two decimals and no lost digit.</summary>
[Trait("Category", "McpTools")]
public class MoneyTextTests
{
    [Theory]
    [InlineData("210", "210.00")]
    [InlineData("210.5", "210.50")]
    [InlineData("1234.5000", "1234.50")]
    [InlineData("0.0125", "0.0125")]
    [InlineData("-42.5", "-42.50")]
    [InlineData("0", "0.00")]
    [InlineData("1000000.10", "1000000.10")]
    public void Format_keeps_two_decimals_and_every_further_significant_digit(string amount, string expected)
    {
        MoneyText.Format(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(expected);
    }
}
