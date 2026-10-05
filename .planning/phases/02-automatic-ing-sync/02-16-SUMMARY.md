---
phase: 02-automatic-ing-sync
plan: 16
subsystem: reconciliation
tags: [balances, reconciliation, drift, postgres, reporting-views, metrics]
status: complete

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plan 09 balance snapshots and reconciler; plan 10 drift metric; plan 11 account status view; spike decision 4"
provides:
  - "Ingestion:ReconcileUndatedBalances (default false): an undated balance of a preferred kind, such as ING's expected balance, is reconciled on the fetch-time window"
  - "BalanceReconciler.SelectReconcilable(allowUndated) and CheckUndated; IBalanceStore.SumBookedSinceAsync (booked_at window)"
  - "Flagged-drift rule: the metric and reporting.account_status report not reconciled only when the latest checked snapshot and the previous checked snapshot of the same kind both mismatched"
  - "Migration FlagDriftOnConsecutiveSnapshots replacing reporting.account_status in place with the same columns"
affects: [plan 14 production defaults, alerts, dashboards]

tech-stack:
  added: []
  patterns:
    - "A balance without a reference date is placed on the timeline of the ledger's own fetches via transactions.booked_at"
    - "The ledger's expectation is carried forward between undated snapshots, so a gap persists and a booked reservation resolves"
    - "Per-snapshot recording stays exact; only the flag (metric and status view) is delayed"

key-files:
  created:
    - Ledger.Repository/Migrations/20261005152907_FlagDriftOnConsecutiveSnapshots.cs
    - Ledger.Repository/Migrations/20261005152907_FlagDriftOnConsecutiveSnapshots.Designer.cs
  modified:
    - Ledger.Domain/Ingestion/BalanceReconciler.cs
    - Ledger.Domain/Ingestion/IBalanceStore.cs
    - Ledger.Domain/Ingestion/IIngestionStatusStore.cs
    - Ledger.Repository/Stores/BalanceStore.cs
    - Ledger.Repository/Stores/IngestionStatusStore.cs
    - Ledger.Service/Ingestion/SyncOrchestrator.cs
    - Ledger.Service/Ingestion/IngestionOptions.cs
    - Ledger.UnitTests/Ingestion/BalanceReconcilerTests.cs
    - Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs
    - Ledger.IntegrationTests/Ingestion/AccountStatusViewTests.cs
    - Ledger.IntegrationTests/Metrics/SyncMetricsTests.cs

key-decisions:
  - "Undated reconciliation is opt-in through Ingestion:ReconcileUndatedBalances so a provider with dated booked balances keeps the booking-date window; plan 14 sets it on for ING together with Expected in Ingestion:ReconcileBalanceKinds"
  - "The window is booked_at after the previous same-kind snapshot's created_at and up to the current fetch; booked_at is set for rows inserted directly as booked as well as for pending-to-booked transitions, so no store change was needed"
  - "An undated check starts from the previous snapshot's own expected amount when it was checked, and from its bank amount when it was only a baseline (see deviations)"
  - "A first mismatch is shown as unknown on the status view (the existing yes/no/unknown mapping on the dashboards is unchanged) and as 0 on the metric; no dashboard change"
  - "The view is replaced with CREATE OR REPLACE VIEW so the reader role's existing SELECT-only grant is kept; Down restores the previous definition"

patterns-established:
  - "Status projections for operators and alerts apply the flagged-drift rule in one place per consumer (store for the metric, view for the dashboard) and are covered by tests on both sides"

requirements-completed: [INGEST-02, INGEST-03]

duration: ~75min
completed: 2026-10-05
actuals:
  tokens: 60000
  tasks: 2
  commits: 2
---

# Phase 2 Plan 16: Reconcile ING's undated expected balance and flag only persistent drift Summary

**An undated expected balance now reconciles to the cent on a fetch-time window, and drift is flagged on /metrics and the status view only when it persists across two consecutive checked snapshots.**

## Accomplishments

- The reconciler can select an undated balance (when `Ingestion:ReconcileUndatedBalances` is on) after dated preferred kinds, and `CheckUndated` compares it exactly with decimals and zero tolerance. Dated balances keep the booking-date window unchanged.
- The balance store sums booked, non-pending, non-dropped transactions by `booked_at` between the previous same-kind snapshot's fetch and the current fetch, so a transaction booked after the earlier fetch is counted in the next window exactly once.
- The drift metric (`IngestionStatusStore`) and `reporting.account_status` use the flagged-drift rule. The view keeps its columns; the reader role stays SELECT-only (tested through `has_table_privilege`).
- Each snapshot still records its own reconciled, expected and drift values exactly.

## Task Commits

1. Task 1 (tracer): undated expected balance on the fetch-time window: dce6949
2. Task 2: flag drift only when it persists on two consecutive snapshots: 541342f

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Delta-only undated reconciliation defeats the two-consecutive-snapshots rule**
- **Found during:** Task 2 (working out the test for "persistent drift")
- **Issue:** If each undated check starts from the previous bank balance, a card payment in flight gives drift -X on day one and +X on day two (the booked payment is added to a balance that already contained it). That is two consecutive drifted snapshots, so the rule would raise exactly the false alarm it exists to prevent. A transaction the ledger never receives gives one drift and then matches forever, so the rule would never flag it. The stated outcomes ("a card payment in flight can cause at most one recorded drift, never an alert; a real gap still alerts the next day") need the ledger's expectation carried forward.
- **Fix:** `CheckUndated` starts from the previous snapshot's `expected_amount` when that snapshot was checked, and from its bank amount when it was only a baseline. The first check after a baseline is identical to the plan text; later checks chain. Dated balances keep the plan-09 semantics unchanged.
- **Files modified:** `Ledger.Domain/Ingestion/BalanceReconciler.cs`, `Ledger.Repository/Stores/BalanceStore.cs` (snapshot state carries the fetch time and expected amount)
- **Verification:** integration tests for a reservation that books the next day (drift -50.00 then match) and for an unexplained cent that repeats exactly on each following day; unit test for the chain
- **Commit:** 541342f

**2. [Rule 3 - Blocking] Small documentation edit outside the listed files**
- `Ledger.Domain/Ingestion/IIngestionStatusStore.cs`: the doc comment of `AccountHealth.LatestReconciled` described the old meaning; updated to the flagged-drift meaning. No behaviour change.

**3. [Process] Tracer feedback gate**
- The project runs with `human_verify_mode = end-of-phase` and auto mode off. No mid-flight human checkpoint was emitted after the tracer commit; the tracer's automated verify (unit and integration `Category=Balances`) was run end to end and passed before the expansion task started.

**4. [Test change] Existing metric test updated, not weakened**
- `A_balance_that_did_not_reconcile_raises_the_drift_flag_for_that_account_only` asserted a flag after a single mismatch. It now asserts 0 after the first mismatch and 1 after a second consecutive mismatch, and a new test asserts that a mismatch followed by a match never raises the flag. The existing theory on the view's reconciliation verdict now expects `unknown` for a single mismatch.

## Authentication Gates

None.

## Verification

- `dotnet test --project Ledger.UnitTests`: 210 passed.
- `dotnet test --project Ledger.IntegrationTests --filter-trait` run per category (Balances 16, Metrics 10, Consent 30, Dashboards 1, DatabaseRoles 8, Ingestion 10, Sync 22, Reconciliation 9, Callback 14, ApiKeyCli 6, LogRedaction 7, DataProtectionRestart 4): all passed with clean teardown.
- Full `dotnet test --project Ledger.IntegrationTests` (two runs): 146 passed, 1 skipped, 0 test failures, but 147 "Test Collection Cleanup Failure" entries (see Issues).
- `bash build/lint.sh repo-rules`: PASS.

## Issues Encountered

- **Full integration run reports collection cleanup failures.** In both full runs `DatabaseFixture.DisposeAsync` throws `Npgsql.NpgsqlException ... System.TimeoutException : Timeout during reading attempt` while dropping the throwaway databases, which xunit reports once per test in the collection (147 entries). No individual test failed, and every category run on its own tears down cleanly. The shared `postgres-dev` container held 165 leftover `ledger_it_*` databases at that point (earlier failed teardowns, possibly including runs by the parallel executor), so the serial drop loop probably exceeds the command timeout under load. This is in the fixture, outside this plan's files, and was not changed. Suggested follow-up: drop leftover `ledger_it_*` databases in the local container and give the drop command a longer timeout or drop in parallel.
- **Dated balances and the persistence rule.** For a dated booked balance the check still starts from the previous bank balance (unchanged on purpose), so a transaction the ledger never receives produces a single mismatch followed by matches, which the two-consecutive rule never flags. This does not affect ING (undated, chained). If a provider with dated booked balances is ever used, the dated check would need the same carried-forward expectation.
- **A persistent unexplained difference stays flagged** until the ledger agrees with the bank again (the missing transaction arrives) or a person intervenes; there is no automatic re-baselining.

## Known Stubs

None.

## Threat Flags

None. Logs and metrics carry reconciled state only; no amounts were added to log messages or metric labels (T-02-16-02), and every snapshot keeps its exact recorded drift (T-02-16-01).

## Next Phase Readiness

Plan 14 should set `Ingestion:ReconcileBalanceKinds` to include `Expected` and `Ingestion:ReconcileUndatedBalances` to `true` for the ING provider. The status view keeps the same columns and the same `yes/no/unknown` values, so no dashboard change is needed.

## Self-Check: PASSED

- Files exist: both new migration files, the 11 modified files (checked via git status before commit).
- Commits dce6949 and 541342f exist on the worktree branch.
