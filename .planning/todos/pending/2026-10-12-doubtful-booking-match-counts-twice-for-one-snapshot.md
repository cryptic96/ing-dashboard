---
created: 2026-10-12T20:30:00.000Z
title: A doubtful pending-to-booked match counts the payment twice for one balance snapshot
area: ingestion
severity: minor
files:
  - Ledger.Domain/Ingestion/TransactionReconciler.cs
  - Ledger.Repository/Stores/BalanceStore.cs
  - Ledger.Service/Ingestion/SyncOrchestrator.cs
  - Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs
---

## Problem

Found while debugging `balance-drift-after-booking` (see `.planning/debug/resolved/` or `.planning/debug/`). It is a real,
deterministic defect, but it is NOT what caused that drift on the host (ING kept the entry reference from pending to booked on
all nine observed transitions, nothing was ever flagged).

When the bank books a pending payment under a NEW reference and the match to the pending row is doubtful (counterparty missing
on either side, or more than one candidate), the reconciler flags the pending row and inserts the booked item as a separate row.
Rows flagged in a run are never dropped in that same run, even though the bank no longer lists them. At that run's balance fetch
the flagged row is therefore still pending in the ledger and is subtracted from the bank's expected balance, while its booked
twin is counted in the booked-since sum: drift = minus the payment for that one snapshot. It heals the next day when the flagged
row is dropped. Two such bookings on consecutive days give two consecutive mismatches, and the two-snapshot persistence rule then
raises the "Account balance does not reconcile" alert.

## Why it was not fixed with the balance-drift fix

- Different mechanism (ingestion match flagging and the same-run drop rule) and a different trigger (reference change plus a
  doubtful match); not observed on the host.
- Effect is a one-day, self-healing drift; the alert needs two such bookings on consecutive days.
- Both fix options change ingestion semantics and need a decision:
  1. At the balance fetch, count as pending only rows the current fetch still listed as pending (for example record the
     provider's own pending sum, or exclude rows flagged in this run whose reference the fetch did not list).
  2. Let the same run drop a flagged pending row that the bank no longer lists, keeping the flag on the booked twin for review.

## Red test (kept out of the committed suite so CI stays green)

Add to `Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs`; it fails on current code with day 2 drift +20 and day 3
drift +30 (both not reconciled), then reconciles on day 4. Note `ReadFlaggedReconciliationAsync` returns the account's latest
verdict, so `BeTrue()` means "reconciles".

```csharp
[Fact]
public async Task Pending_items_that_book_on_consecutive_days_under_new_references_with_doubtful_matches_never_cause_drift_or_the_flag()
{
    var (scenario, account) = ScenarioWithUndatedBalance(950.00m);
    scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-pending-first", -20.00m, DayOne, counterparty: null));
    scenario.AddTransaction(account, IngestionTestSupport.Pending("entry-pending-second", -30.00m, DayOne, counterparty: null));
    await using var host = await StartAsync(scenario, DayOne, UndatedConfiguration);
    var connection = await host.LinkAsync(selectFirstAccountOnly: false);
    var accountKey = connection.Accounts[0].AccountKey;
    await SyncAsync(host, connection.Id);

    scenario.Remove(account, 1);
    scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-booked-first", -20.00m, DayTwo));
    host.Clock.SetUtcNow(InstantFor(DayTwo));
    var firstBooking = await SyncAsync(host, connection.Id);

    scenario.Remove(account, 1);
    scenario.AddTransaction(account, IngestionTestSupport.Booked("entry-booked-second", -30.00m, DayTwo.AddDays(1)));
    host.Clock.SetUtcNow(InstantFor(DayTwo.AddDays(1)));
    var secondBooking = await SyncAsync(host, connection.Id);
    var flaggedAfterBothBookings = await ReadFlaggedReconciliationAsync(host, accountKey);

    host.Clock.SetUtcNow(InstantFor(DayTwo.AddDays(2)));
    await SyncAsync(host, connection.Id);

    firstBooking.Flagged.Should().Be(1);
    secondBooking.Flagged.Should().Be(1);
    var snapshots = (await ReadSnapshotsAsync(host, accountKey)).Where(snapshot => snapshot.SnapshotDate > DayOne).ToList();
    snapshots.Should().HaveCount(3);
    snapshots.Should().OnlyContain(snapshot => snapshot.Reconciled == true && snapshot.Drift == 0m);
    flaggedAfterBothBookings.Should().BeTrue();
}
```
