using FluentAssertions;
using Ledger.Domain.Ingestion;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies when the latest finished run of a connection counts as a failure, and which reason it carries.</summary>
[Trait("Category", "Metrics")]
public class SyncHealthTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly ScheduleSettings Settings = new(new TimeOnly(6, 30), Amsterdam, TimeSpan.FromHours(4));
    private static readonly DateTimeOffset MorningFinish = new(2026, 10, 26, 5, 31, 0, TimeSpan.Zero);

    [Fact]
    public void A_connection_without_a_finished_run_is_not_failing()
    {
        SyncHealth.FailingReason(null, MorningFinish, Settings).Should().BeNull();
    }

    [Fact]
    public void A_successful_run_is_not_failing()
    {
        var run = Finished(SyncTrigger.Scheduled, SyncOutcome.Succeeded, MorningFinish);

        SyncHealth.FailingReason(run, MorningFinish.AddDays(3), Settings).Should().BeNull();
    }

    [Theory]
    [InlineData(SyncOutcome.FailedRateLimited, "rate_limited")]
    [InlineData(SyncOutcome.QuotaExhausted, "rate_limited")]
    [InlineData(SyncOutcome.FailedConsent, "consent_rejected")]
    [InlineData(SyncOutcome.FailedProviderAuth, "provider_auth")]
    public void A_rejection_or_rate_limit_counts_at_once(SyncOutcome outcome, string expectedReason)
    {
        var run = Finished(SyncTrigger.Scheduled, outcome, MorningFinish);

        SyncHealth.FailingReason(run, MorningFinish, Settings).Should().Be(expectedReason);
    }

    [Theory]
    [InlineData(SyncOutcome.FailedTransient)]
    [InlineData(SyncOutcome.FailedMalformed)]
    [InlineData(SyncOutcome.Abandoned)]
    public void A_temporary_failure_is_not_failing_while_the_same_day_retry_is_still_due(SyncOutcome outcome)
    {
        var run = Finished(SyncTrigger.Scheduled, outcome, MorningFinish);

        SyncHealth.FailingReason(run, MorningFinish, Settings).Should().BeNull();
        SyncHealth.FailingReason(run, MorningFinish.AddHours(4).AddMinutes(9), Settings).Should().BeNull();
    }

    [Theory]
    [InlineData(SyncOutcome.FailedTransient)]
    [InlineData(SyncOutcome.FailedMalformed)]
    [InlineData(SyncOutcome.Abandoned)]
    public void A_temporary_failure_is_failing_once_the_retry_had_ten_minutes_and_nothing_replaced_it(SyncOutcome outcome)
    {
        var run = Finished(SyncTrigger.Scheduled, outcome, MorningFinish);

        SyncHealth.FailingReason(run, MorningFinish.AddHours(4).AddMinutes(10), Settings).Should().Be("transient");
        SyncHealth.FailingReason(run, MorningFinish.AddDays(1), Settings).Should().Be("transient");
    }

    [Theory]
    [InlineData(SyncTrigger.Retry)]
    [InlineData(SyncTrigger.Manual)]
    [InlineData(SyncTrigger.PostLink)]
    public void A_temporary_failure_of_a_run_no_retry_follows_counts_at_once(SyncTrigger trigger)
    {
        var run = Finished(trigger, SyncOutcome.FailedTransient, MorningFinish);

        SyncHealth.FailingReason(run, MorningFinish, Settings).Should().Be("transient");
    }

    [Fact]
    public void A_temporary_failure_whose_retry_would_fall_on_the_next_local_day_counts_at_once()
    {
        var lateEvening = new DateTimeOffset(2026, 10, 26, 21, 0, 0, TimeSpan.Zero);
        var run = Finished(SyncTrigger.Scheduled, SyncOutcome.FailedTransient, lateEvening);

        SyncHealth.FailingReason(run, lateEvening, Settings).Should().Be("transient");
    }

    [Fact]
    public void An_unfinished_run_is_not_failing()
    {
        var run = new SyncRunSummary(SyncTrigger.Scheduled, MorningFinish, null, null);

        SyncHealth.FailingReason(run, MorningFinish.AddDays(1), Settings).Should().BeNull();
    }

    [Theory]
    [InlineData(SyncOutcome.Succeeded, null)]
    [InlineData(SyncOutcome.FailedTransient, "transient")]
    [InlineData(SyncOutcome.FailedMalformed, "transient")]
    [InlineData(SyncOutcome.Abandoned, "transient")]
    [InlineData(SyncOutcome.FailedRateLimited, "rate_limited")]
    [InlineData(SyncOutcome.QuotaExhausted, "rate_limited")]
    [InlineData(SyncOutcome.FailedConsent, "consent_rejected")]
    [InlineData(SyncOutcome.FailedProviderAuth, "provider_auth")]
    public void Every_outcome_belongs_to_exactly_one_reason_or_none(SyncOutcome outcome, string? expectedReason)
    {
        SyncHealth.ReasonForOutcome(outcome).Should().Be(expectedReason);
    }

    private static SyncRunSummary Finished(SyncTrigger trigger, SyncOutcome outcome, DateTimeOffset finishedAt)
    {
        return new SyncRunSummary(trigger, finishedAt.AddMinutes(-1), finishedAt, outcome);
    }
}
