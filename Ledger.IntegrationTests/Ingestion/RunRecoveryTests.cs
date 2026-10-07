using FluentAssertions;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Repository.Stores;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Verifies that a run left open by a crash or a database failure never blocks a connection for good: orphaned runs are
/// closed when the service starts whatever the scheduler setting, a failing start-up cleanup is retried and never stops the
/// host, and a failure to record the end of a run is retried.
/// </summary>
[Collection("Database")]
[Trait("Category", "Sync")]
public class RunRecoveryTests(DatabaseFixture fixture)
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly DateOnly Monday = new(2026, 10, 26);

    [Fact]
    public async Task A_cleanup_that_fails_at_start_does_not_stop_the_host_and_is_retried_until_it_works()
    {
        var scenario = Scenario();
        var start = Instant(5, 0);
        var switches = new RunStoreSwitches();
        string databaseName;
        Guid runId;

        await using (var first = await SchedulerTestHost.StartAsync(fixture, scenario, start))
        {
            databaseName = first.DatabaseName;
            var linked = await first.LinkAsync(selectFirstAccountOnly: true);
            runId = await first.StartUnfinishedRunAsync(linked.Id, SyncTrigger.Manual);
        }

        switches.FailAbandonCalls = int.MaxValue;

        await using var second = await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            start.AddMinutes(10),
            existingDatabase: databaseName,
            configureServices: services => services.AddScoped<ISyncRunStore>(provider =>
                new FlakyRunStore(ActivatorUtilities.CreateInstance<SyncRunStore>(provider), switches)));

        second.Factory.CapturedLogMessages.Should().Contain(message => message.Contains("will be tried again", StringComparison.Ordinal));
        (await second.ReadRunsAsync()).Single(run => run.Id == runId).Outcome.Should().BeNull();

        switches.FailAbandonCalls = 0;
        second.Clock.Advance(TimeSpan.FromSeconds(6));

        (await second.WaitForRunOutcomeAsync(runId)).Should().Be("abandoned");
    }

    [Fact]
    public async Task A_failure_to_record_the_end_of_a_run_is_retried_so_the_run_does_not_stay_open()
    {
        var scenario = Scenario();
        var switches = new RunStoreSwitches { FailFinishCalls = 2 };
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            Instant(6, 30),
            configureServices: services => services.AddScoped<ISyncRunStore>(provider =>
                new FlakyRunStore(ActivatorUtilities.CreateInstance<SyncRunStore>(provider), switches)));
        await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);

        var run = (await host.ReadRunsAsync()).Should().ContainSingle().Which;
        run.Outcome.Should().Be("succeeded");
        host.Factory.CapturedLogMessages.Should().Contain(message => message.Contains("Recording the end of sync run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_whose_end_can_never_be_recorded_is_reported_without_a_misleading_failure()
    {
        var scenario = Scenario();
        var switches = new RunStoreSwitches { FailFinishCalls = int.MaxValue };
        await using var host = await SchedulerTestHost.StartAsync(
            fixture,
            scenario,
            Instant(6, 30),
            configureServices: services => services.AddScoped<ISyncRunStore>(provider =>
                new FlakyRunStore(ActivatorUtilities.CreateInstance<SyncRunStore>(provider), switches)));
        await host.LinkAsync(selectFirstAccountOnly: true);

        (await host.RunDueAsync()).Should().Be(1);

        host.Factory.CapturedLogMessages.Should().Contain(message => message.Contains("The run stays open until the service restarts", StringComparison.Ordinal));
        host.Factory.CapturedLogMessages.Should().NotContain(message => message.Contains("scheduled sync of connection", StringComparison.Ordinal) && message.Contains("failed", StringComparison.Ordinal));
    }

    private static DateTimeOffset Instant(int hour, int minute)
    {
        return SyncSchedule.InstantFor(Monday, new TimeOnly(hour, minute), Amsterdam);
    }

    private static SyntheticBankScenario Scenario()
    {
        var scenario = SyntheticBankScenario.Create();
        var joint = scenario.AddAccount(Ledger.Domain.Banking.AccountKind.Current);
        scenario.AddAccount(Ledger.Domain.Banking.AccountKind.Savings);
        scenario.AddTransaction(joint, IngestionTestSupport.Booked("entry-001", -1.5m, new DateOnly(2026, 10, 20)));
        return scenario;
    }
}

/// <summary>How many calls of each kind the flaky run store refuses before it behaves like the real one.</summary>
public sealed class RunStoreSwitches
{
    private int _failAbandonCalls;
    private int _failFinishCalls;

    /// <summary>How many more abandon calls fail.</summary>
    public int FailAbandonCalls
    {
        get => Volatile.Read(ref _failAbandonCalls);
        set => Volatile.Write(ref _failAbandonCalls, value);
    }

    /// <summary>How many more finish calls fail.</summary>
    public int FailFinishCalls
    {
        get => Volatile.Read(ref _failFinishCalls);
        set => Volatile.Write(ref _failFinishCalls, value);
    }

    /// <summary>Consumes one planned abandon failure and returns whether this call must fail.</summary>
    public bool TakeAbandonFailure()
    {
        return TryTake(ref _failAbandonCalls);
    }

    /// <summary>Consumes one planned finish failure and returns whether this call must fail.</summary>
    public bool TakeFinishFailure()
    {
        return TryTake(ref _failFinishCalls);
    }

    private static bool TryTake(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);

            if (current <= 0)
            {
                return false;
            }

            var next = current == int.MaxValue ? current : current - 1;

            if (Interlocked.CompareExchange(ref counter, next, current) == current)
            {
                return true;
            }
        }
    }
}

/// <summary>A run store that behaves like the real one except where the switches make a call fail.</summary>
public sealed class FlakyRunStore(ISyncRunStore inner, RunStoreSwitches switches) : ISyncRunStore
{
    /// <inheritdoc />
    public Task<Guid> StartAsync(Guid connectionId, SyncTrigger trigger, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        return inner.StartAsync(connectionId, trigger, startedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> FinishAsync(Guid runId, SyncRunCompletion completion, CancellationToken cancellationToken)
    {
        return switches.TakeFinishFailure()
            ? throw new InvalidOperationException("The store was told to fail.")
            : inner.FinishAsync(runId, completion, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncRunSummary>> ListRunsSinceAsync(Guid connectionId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        return inner.ListRunsSinceAsync(connectionId, since, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HasUnfinishedRunAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return inner.HasUnfinishedRunAsync(connectionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> AbandonUnfinishedAsync(DateTimeOffset now, DateTimeOffset startedAtOrBefore, CancellationToken cancellationToken)
    {
        return switches.TakeAbandonFailure()
            ? throw new InvalidOperationException("The store was told to fail.")
            : inner.AbandonUnfinishedAsync(now, startedAtOrBefore, cancellationToken);
    }
}
