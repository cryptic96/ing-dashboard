using FluentAssertions;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>Proves the shared polling helper returns the settled state and fails loudly instead of returning a stale one.</summary>
public class WaitTests
{
    [Fact]
    public async Task It_returns_the_first_state_that_satisfies_the_condition()
    {
        var reads = 0;

        var result = await Wait.UntilAsync(
            () => Task.FromResult(++reads),
            count => count >= 3,
            "three reads",
            interval: TimeSpan.FromMilliseconds(1));

        result.Should().Be(3);
    }

    [Fact]
    public async Task It_throws_a_timeout_naming_what_was_waited_for_and_what_was_last_seen()
    {
        var act = () => Wait.UntilAsync(
            () => Task.FromResult(2),
            count => count >= 3,
            "three rows",
            count => $"{count} rows",
            timeout: TimeSpan.FromMilliseconds(50),
            interval: TimeSpan.FromMilliseconds(5));

        var timeout = await act.Should().ThrowAsync<TimeoutException>();
        timeout.Which.Message.Should().Contain("three rows").And.Contain("2 rows");
    }
}
