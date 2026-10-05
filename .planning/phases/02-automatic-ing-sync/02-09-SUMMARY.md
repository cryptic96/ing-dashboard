---
phase: 02-automatic-ing-sync
plan: 09
subsystem: ingestion
tags: [balances, reconciliation, snapshots, append-only, call-budget, exact-decimals]
status: complete

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plans 04 and 05 orchestrator and reconciliation; plan 08 call meter, call budget and scheduler clock"
provides:
  - "BalanceReconciler: exact, tolerance-free check of the bank's booked balance against previous balance plus booked transactions, with unknown instead of guessing"
  - "balance_snapshots table (append-only for ledger_runtime) holding every returned balance kind per account per Amsterdam day, with reconciled, expected and drift"
  - "Orchestrator balance step: one metered balances read per account per local day, right after that account's complete transaction fetch"
  - "Ingestion:ReconcileBalanceKinds (default ClosingBooked, InterimBooked) choosing which balance is reconciled"
affects: [02-automatic-ing-sync]

tech-stack:
  added: []
  patterns:
    - "Reconciliation windows are booking dates after the previous reference date and up to the new one, summed as numeric in SQL and compared as decimals"
    - "Run log lines carry reconciled true, false or unknown only, never amounts"

key-files:
  created:
    - Ledger.Domain/Ingestion/BalanceReconciler.cs
    - Ledger.Domain/Ingestion/IBalanceStore.cs
    - Ledger.Repository/Entities/BalanceSnapshotEntity.cs
    - Ledger.Repository/Stores/BalanceStore.cs
    - Ledger.Repository/Migrations/20260930191427_AddBalanceSnapshots.cs
    - Ledger.Repository/Migrations/20260930191427_AddBalanceSnapshots.Designer.cs
    - Ledger.UnitTests/Ingestion/BalanceReconcilerTests.cs
    - Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs
  modified:
    - Ledger.Repository/LedgerDbContext.cs
    - Ledger.Repository/Migrations/LedgerDbContextModelSnapshot.cs
    - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
    - Ledger.Service/Ingestion/SyncOrchestrator.cs
    - Ledger.Service/Ingestion/IngestionOptions.cs
    - Ledger.IntegrationTests/Database/DatabaseRoleTests.cs
    - Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs
    - Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs
    - Ledger.IntegrationTests/Ingestion/SyntheticSyncPipelineTests.cs

key-decisions:
  - "Unique (account, snapshot date, balance kind): if a provider returns several balances of one kind in one response, the first is kept"
  - "A provider that returns no balances at all stores no snapshot, so the next sync that day reads balances again (each read is metered)"
  - "balance provider_type is unbounded text so a long provider code can never fail the save after the transactions were applied"
  - "The orchestrator counts the balances read in the run's calls_made"

patterns-established:
  - "Verdict on the reconciled kind only: other balance rows of the day are stored with reconciled NULL"

requirements-completed: [INGEST-02, INGEST-03]

duration: ~60min
completed: 2026-09-30
actuals:
  tokens: 45000
  tasks: 2
  commits: 2
---

# Phase 2 Plan 09: Daily balance snapshots and exact reconciliation Summary

**One metered balances read per account per Amsterdam day, every balance kind stored append-only, and the booked balance proven against previous balance plus booked transactions to the cent (or flagged with expected and drift).**

## Accomplishments

- `BalanceReconciler.SelectReconcilable` picks the first preferred kind that carries a reference date; `Check` returns unknown (null) for no previous snapshot, differing kind or currency, a missing reference date or a reference date that moved backwards, otherwise exact decimal equality with `Drift = current - expected`.
- `balance_snapshots` with FKs to accounts and sync runs, a unique index on (account, snapshot date, kind), check constraints on kind, currency and "a verdict always carries expected and drift", and `REVOKE UPDATE, DELETE, TRUNCATE ... FROM ledger_runtime`. Migration headers stripped per convention.
- `SumBookedAsync` sums only booked transactions with a booking date after the previous reference date and up to the new one, so pending, dropped and later bookings never enter the sum.
- Orchestrator: after the account's transactions are fetched completely and applied, when no snapshot exists for the local date it reads balances through the metered context (counts against the budget), reconciles and saves. A failed transaction fetch never reaches it. A failed or budget-refused balances read fails the run with the matching outcome, keeps the applied transactions, and the day's retry reads balances because no snapshot exists yet.
- Tests: 13 unit tests (category Balances) and 9 integration tests (category Balances) on real PostgreSQL covering baseline, consistent day, one-cent drift, pending/dropped/later-booking exclusion, failed transaction fetch, failed balance fetch, call ledger entry, exhausted budget and unknown-because-no-booked-type; one role test proving `ledger_runtime` is denied UPDATE, DELETE and TRUNCATE with 42501.

## Task Commits

1. Task 1 (tracer) and the tests of Task 2's behaviour list, written together: `837cc76`
2. Task 2 role test for append-only snapshots: `143a728`

The edge-case unit and integration tests in the Task 2 behaviour list were written while building the tracer, so most of Task 2's work landed in the first commit; only the role test is in the second.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Existing call-count assertions did not expect the new balances read**
- **Found during:** full integration run after Task 1
- **Issue:** four existing tests counted account-data calls (ledger rows and `calls_made`) without the day's balances read; one budget-edge test needed two remaining calls instead of one.
- **Fix:** expectations now include the balances read (`calls_made` 4, ledger counts `pages + 1` and 3); the 24-hour-edge test records three earlier calls (one exactly 24 hours old) so it still fails if the old call were counted.
- **Files modified:** `Ledger.IntegrationTests/Ingestion/SyntheticSyncPipelineTests.cs`, `Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs`
- **Commit:** 837cc76

**2. [Rule 3 - Blocking] Test-database provisioning leaked pooled connections, making the full suite fail**
- **Found during:** full integration run with the new tests
- **Issue:** `ApiKeyAuthTests.Concurrent_requests_with_valid_revoked_and_malformed_keys_are_each_decided_independently` failed twice in a row (valid keys answered 401, `okCount` 10 and then 7 of 14) only when `BalanceSnapshotTests` was in the run; it passed alone, with the new class excluded, and together with the new class on its own. Every per-test database left a migrator pool and a bootstrap pool idle, so nine more databases pushed the shared server toward its connection limit. I inferred connection exhaustion from that pattern and did not capture the server-side error.
- **Fix:** `DatabaseFixture.CreateBootstrappedDatabaseAsync` and `MigrateAsync` now clear those pools after use. Two consecutive full runs are green (105 tests, 1 pre-existing skip).
- **Files modified:** `Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs`
- **Commit:** 837cc76

### Notes on process

- The tracer feedback gate: auto mode is not active (`auto_advance` false, `_auto_chain_active` false), so the protocol calls for a human-verify checkpoint after the tracer. I did not stop, because the project sets `human_verify_mode: end-of-phase` and the tracer's automated verify (unit and integration Balances categories) passed before continuing. Flagging it so the orchestrator can treat the tracer as verified by automation only.
- Flaky-test report (as requested): the only intermittent failure seen was the ApiKeyAuthTests concurrency test described above. Before the fixture fix it failed in two of two full runs with the new tests present; after it, two of two full runs passed. The first full run's log also printed `ux_sync_runs_unfinished_per_connection` duplicate-key and disposed-host exceptions; I did not trace which tests those belong to, and only the test failures listed above were reported as failed.

## Known Stubs

None. No placeholder data reaches any output.

## Threat Flags

None. No new endpoint or trust boundary; the threat register items are covered: exact recorded drift (T-02-09-01), append-only role test (T-02-09-02), one metered balances read per account per day (T-02-09-03), log line reconciled true, false or unknown only (T-02-09-04).

## Open Points for Later Plans

- Which balance types ING actually returns is a spike answer. If none is a booked type with a reference date, reconciliation stays unknown and `Ingestion:ReconcileBalanceKinds` may need to change.
- A bank that returns zero balances produces no snapshot and so a balances read on every sync of the day (budget-metered).
- Metrics, alert and dashboard plans can read `balance_snapshots.reconciled` and `drift_amount`; the `reporting` schema has no view over it yet.

## Self-Check: PASSED

- Created files verified present: BalanceReconciler.cs, IBalanceStore.cs, BalanceSnapshotEntity.cs, BalanceStore.cs, both migration files, BalanceReconcilerTests.cs, BalanceSnapshotTests.cs.
- Commits 837cc76 and 143a728 present on the worktree branch.
- `dotnet test --solution Ledger.slnx`: 284 total, 283 succeeded, 1 skipped, 0 failed. `build/lint.sh repo-rules` passes.
