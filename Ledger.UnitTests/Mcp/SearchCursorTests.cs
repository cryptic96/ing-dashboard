using System.Text;
using FluentAssertions;
using Ledger.Domain.Queries;

namespace Ledger.UnitTests.Mcp;

/// <summary>Verifies the search cursor round-trips its key, refuses anything it did not produce, and binds a page to its filters.</summary>
[Trait("Category", "Search")]
public class SearchCursorTests
{
    private static LedgerQueryFilter Filter(
        IReadOnlyList<string>? terms = null,
        IReadOnlyList<string>? descriptions = null,
        MoneyDirection direction = MoneyDirection.Both,
        decimal? minAmount = null,
        string to = "2025-12-31")
    {
        return new LedgerQueryFilter(
            new DateRange(new DateOnly(2025, 1, 1), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)),
            [],
            terms ?? [],
            [],
            descriptions ?? [],
            direction,
            minAmount,
            null);
    }

    [Fact]
    public void A_cursor_round_trips_the_key_of_the_last_row_and_the_filter_hash()
    {
        var seen = new DateTimeOffset(2025, 6, 15, 8, 30, 15, TimeSpan.Zero).AddTicks(1230);
        var id = Guid.NewGuid();
        var hash = SearchCursor.FilterHash(Filter(), "both");

        var cursor = SearchCursor.Encode(new DateOnly(2025, 6, 15), seen, id, hash);

        SearchCursor.TryDecode(cursor, out var position).Should().BeTrue();
        position.PeriodDate.Should().Be(new DateOnly(2025, 6, 15));
        position.FirstSeenAt.Should().Be(seen);
        position.Id.Should().Be(id);
        position.FilterHash.Should().Be(hash);
        cursor.Should().NotContainAny("+", "/", "=");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cursor")]
    [InlineData("eyJkIjoiMjAyNS0wNi0xNSIs")]
    [InlineData("e30")]
    public void Text_that_is_not_a_cursor_is_refused(string text)
    {
        SearchCursor.TryDecode(text, out _).Should().BeFalse();
    }

    [Fact]
    public void A_cursor_whose_json_lacks_a_field_is_refused()
    {
        var json = "{\"d\":\"2025-06-15\",\"t\":1,\"i\":\"" + Guid.NewGuid() + "\"}";
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        SearchCursor.TryDecode(cursor, out _).Should().BeFalse();
    }

    [Fact]
    public void A_very_long_cursor_is_refused()
    {
        SearchCursor.TryDecode(new string('A', 600), out _).Should().BeFalse();
    }

    [Fact]
    public void The_filter_hash_is_the_same_for_the_same_filters_in_another_term_order()
    {
        var first = SearchCursor.FilterHash(Filter(["alpha", "beta"], ["x", "y"]), "both");
        var second = SearchCursor.FilterHash(Filter(["beta", "alpha"], ["y", "x"]), "both");

        second.Should().Be(first);
        first.Should().HaveLength(16).And.MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void The_filter_hash_differs_when_any_filter_value_or_the_status_differs()
    {
        var baseline = SearchCursor.FilterHash(Filter(["alpha"]), "both");

        var others = new[]
        {
            SearchCursor.FilterHash(Filter(["alpha", "beta"]), "both"),
            SearchCursor.FilterHash(Filter(["alpha"], ["note"]), "both"),
            SearchCursor.FilterHash(Filter(["alpha"], direction: MoneyDirection.Out), "both"),
            SearchCursor.FilterHash(Filter(["alpha"], minAmount: 5m), "both"),
            SearchCursor.FilterHash(Filter(["alpha"], to: "2025-12-30"), "both"),
            SearchCursor.FilterHash(Filter(["alpha"]), "booked")
        };

        others.Should().OnlyContain(hash => hash != baseline);
        others.Distinct().Should().HaveCount(others.Length);
    }

    [Fact]
    public void The_filter_hash_ignores_trailing_zeros_of_an_amount_bound()
    {
        SearchCursor.FilterHash(Filter(minAmount: 5m), "both")
            .Should().Be(SearchCursor.FilterHash(Filter(minAmount: 5.00m), "both"));
    }
}
