using FluentAssertions;
using Ledger.Domain.Ingestion;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies the consent state is derived from the stored status and the validity end, with exact boundaries.</summary>
[Trait("Category", "Consent")]
public class ConsentStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Active_with_exactly_fourteen_days_left_is_linked()
    {
        var snapshot = ConsentState.Derive(ConnectionStatus.Active, Now.AddDays(14), Now);

        snapshot.State.Should().Be(ConsentView.Linked);
        snapshot.DaysUntilExpiry.Should().Be(14);
    }

    [Fact]
    public void Active_with_fourteen_days_minus_one_second_left_is_expiring()
    {
        var snapshot = ConsentState.Derive(ConnectionStatus.Active, Now.AddDays(14).AddSeconds(-1), Now);

        snapshot.State.Should().Be(ConsentView.Expiring);
    }

    [Fact]
    public void Active_with_exactly_zero_left_is_expired()
    {
        var snapshot = ConsentState.Derive(ConnectionStatus.Active, Now, Now);

        snapshot.State.Should().Be(ConsentView.Expired);
        snapshot.DaysUntilExpiry.Should().Be(0);
    }

    [Fact]
    public void Active_one_second_before_the_end_is_still_expiring()
    {
        var snapshot = ConsentState.Derive(ConnectionStatus.Active, Now.AddSeconds(1), Now);

        snapshot.State.Should().Be(ConsentView.Expiring);
    }

    [Fact]
    public void Days_until_expiry_is_negative_after_the_end()
    {
        var snapshot = ConsentState.Derive(ConnectionStatus.Active, Now.AddDays(-2), Now);

        snapshot.State.Should().Be(ConsentView.Expired);
        snapshot.DaysUntilExpiry.Should().Be(-2);
    }

    [Fact]
    public void Provider_expired_is_expired_even_with_days_left()
    {
        var snapshot = ConsentState.Derive(ConnectionStatus.ProviderExpired, Now.AddDays(100), Now);

        snapshot.State.Should().Be(ConsentView.Expired);
    }

    [Fact]
    public void Revoked_is_revoked_regardless_of_the_validity_end()
    {
        ConsentState.Derive(ConnectionStatus.Revoked, Now.AddDays(100), Now).State.Should().Be(ConsentView.Revoked);
        ConsentState.Derive(ConnectionStatus.Revoked, Now.AddDays(-100), Now).State.Should().Be(ConsentView.Revoked);
    }

    [Fact]
    public void Superseded_is_superseded_regardless_of_the_validity_end()
    {
        ConsentState.Derive(ConnectionStatus.Superseded, Now.AddDays(100), Now).State.Should().Be(ConsentView.Superseded);
        ConsentState.Derive(ConnectionStatus.Superseded, Now.AddDays(-100), Now).State.Should().Be(ConsentView.Superseded);
    }

    [Fact]
    public void The_warning_period_can_be_changed()
    {
        ConsentState.Derive(ConnectionStatus.Active, Now.AddDays(20), Now, warnDays: 30).State.Should().Be(ConsentView.Expiring);
        ConsentState.Derive(ConnectionStatus.Active, Now.AddDays(30), Now, warnDays: 30).State.Should().Be(ConsentView.Linked);
    }
}
