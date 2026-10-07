using FluentAssertions;
using Ledger.Domain.Queries;
using Ledger.Repository.Stores;

namespace Ledger.UnitTests.Mcp;

/// <summary>Verifies the text rules behind totals: account number normalising and masking, counterparty references and literal LIKE patterns.</summary>
[Trait("Category", "Totals")]
public class LedgerTextTests
{
    [Fact]
    public void Normalizing_an_account_number_removes_whitespace_and_upper_cases()
    {
        IbanText.Normalize("xx00 test 0000 0000 02").Should().Be("XX00TEST0000000002");
        IbanText.Normalize(null).Should().BeNull();
    }

    [Fact]
    public void Masking_keeps_two_letters_four_bullets_and_the_last_four()
    {
        IbanText.Mask("xx00 test 0000 0000 02").Should().Be("XX••••0002");
    }

    [Theory]
    [InlineData("")]
    [InlineData("XX001")]
    [InlineData("XX00123")]
    public void Masking_a_short_number_shows_only_bullets(string iban)
    {
        IbanText.Mask(iban).Should().Be("••••");
    }

    [Fact]
    public void Masking_null_stays_null()
    {
        IbanText.Mask(null).Should().BeNull();
    }

    [Fact]
    public void A_counterparty_reference_ignores_case_and_spacing_but_not_the_name()
    {
        CounterpartyRef.For("Example Market").Should().Be(CounterpartyRef.For("EXAMPLE  market "));
        CounterpartyRef.For("Example Market").Should().NotBe(CounterpartyRef.For("Example Bakery"));
    }

    [Fact]
    public void A_counterparty_reference_is_well_formed_and_a_blank_name_has_none()
    {
        var reference = CounterpartyRef.For("Example Market");

        reference.Should().MatchRegex("^cp_[a-z2-7]{12}$");
        CounterpartyRef.IsWellFormed(reference).Should().BeTrue();
        CounterpartyRef.For("   ").Should().BeNull();
        CounterpartyRef.For(null).Should().BeNull();
    }

    [Theory]
    [InlineData("cp_")]
    [InlineData("cp_abcdefghijkl1")]
    [InlineData("cp_abcdefghijk1")]
    [InlineData("xx_abcdefghijkl")]
    [InlineData("cp_ABCDEFGHIJKL")]
    public void Malformed_references_are_recognised(string reference)
    {
        CounterpartyRef.IsWellFormed(reference).Should().BeFalse();
    }

    [Fact]
    public void Money_text_keeps_a_fourth_decimal()
    {
        MoneyText.Format(0.0125m).Should().Be("0.0125");
    }

    [Theory]
    [InlineData("market", "%market%")]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("back\\slash", "%back\\\\slash%")]
    public void Like_patterns_match_the_callers_characters_literally(string term, string expected)
    {
        LikePattern.Contains(term).Should().Be(expected);
    }
}
