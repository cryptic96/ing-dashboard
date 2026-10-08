using FluentAssertions;
using Ledger.Domain.Queries;
using Ledger.Service.Queries;

namespace Ledger.UnitTests.Mcp;

/// <summary>Verifies that the time zone a query uses is always an IANA zone, and that an unusable name is refused with a fixed message.</summary>
[Trait("Category", "Periods")]
public class QueryTimeZoneTests
{
    [Fact]
    public void An_iana_zone_is_used_as_it_is()
    {
        QueryTimeZone.Resolve("Europe/Amsterdam").Id.Should().Be("Europe/Amsterdam");
    }

    [Fact]
    public void A_windows_zone_name_resolves_to_its_iana_zone()
    {
        QueryTimeZone.Resolve("W. Europe Standard Time").Id.Should().Be("Europe/Berlin");
    }

    [Theory]
    [InlineData("Not/AZone")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_zone_that_does_not_exist_is_refused_with_a_fixed_message(string? name)
    {
        var act = () => QueryTimeZone.Resolve(name);

        act.Should().Throw<LedgerQueryException>().WithMessage("*IANA*");
    }
}
