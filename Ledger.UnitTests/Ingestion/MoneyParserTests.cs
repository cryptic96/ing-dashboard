using FluentAssertions;
using Ledger.Domain.Banking;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies that amount text is parsed exactly or rejected, never rounded or interpreted.</summary>
[Trait("Category", "Ingestion")]
public class MoneyParserTests
{
    [Theory]
    [InlineData("0.00", false, "0.00")]
    [InlineData("12.3456", false, "12.3456")]
    [InlineData("12.50", false, "12.50")]
    [InlineData("12.50", true, "-12.50")]
    [InlineData("7", false, "7")]
    [InlineData("123456789012345", false, "123456789012345")]
    [InlineData("123456789012345.9999", true, "-123456789012345.9999")]
    public void Well_formed_amounts_parse_exactly_and_the_direction_decides_the_sign(string text, bool isDebit, string expected)
    {
        MoneyParser.Parse(text, isDebit).Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("12.34567")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("1,00")]
    [InlineData("1.000,00")]
    [InlineData("1 000.00")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("")]
    [InlineData("1234567890123456")]
    [InlineData("12.")]
    [InlineData(".5")]
    [InlineData("1.2.3")]
    [InlineData("0x10")]
    [InlineData("١٢")]
    [InlineData("NaN")]
    public void Malformed_amounts_are_rejected_as_malformed_data(string text)
    {
        var act = () => MoneyParser.Parse(text, isDebit: false);

        var thrown = act.Should().Throw<BankProviderException>().Which;
        thrown.Kind.Should().Be(ProviderErrorKind.MalformedData);
        thrown.Message.Should().NotContain(text.Length == 0 ? "~" : text);
    }

    [Fact]
    public void A_missing_amount_is_rejected_as_malformed_data()
    {
        var act = () => MoneyParser.Parse(null!, isDebit: true);

        act.Should().Throw<BankProviderException>().Which.Kind.Should().Be(ProviderErrorKind.MalformedData);
    }

    [Fact]
    public void The_parse_does_not_depend_on_the_current_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("nl-NL");

            MoneyParser.Parse("1234.5", isDebit: false).Should().Be(1234.5m);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
