---
phase: 02-automatic-ing-sync
plan: 05
subsystem: ingestion
tags: [ingestion, reconciliation, pending-to-booked, drops, postgresql, synthetic-provider]

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plan 04: TransactionReconciler.Plan, LedgerStore.ApplyAsync, SyncOrchestrator, SyntheticBankScenario"
provides:
  - "Certain-match merge of a booked item into its pending row under a different reference (same internal id, both references mapped)"
  - "Unclear-match flags on doubtful pending rows; the booked item is stored separately"
  - "Drops after complete, non-empty fetches only; cancellations; restores; forward-only booked status; flag cleared on booking"
  - "PlannedMerge, ReconciliationPlan.Merges/FlagAmbiguous/Drops, PlannedUpdate.Restore"
  - "SyntheticBankScenario.Remove and WithReference"
affects: [02-automatic-ing-sync]

tech-stack:
  added: []
  patterns:
    - "Reconciler stays a pure function; the store applies merges, flags, drops and restores in the same database transaction as inserts and updates"
    - "Match candidates are computed per unresolved booked item and claimed per pending row, so a merge needs a unique, mutual match"

key-files:
  created:
    - Ledger.IntegrationTests/Ingestion/ReconciliationPipelineTests.cs
  modified:
    - Ledger.Domain/Ingestion/ReconciliationPlan.cs
    - Ledger.Domain/Ingestion/TransactionReconciler.cs
    - Ledger.Repository/Stores/LedgerStore.cs
    - Ledger.Service/Ingestion/SyncOrchestrator.cs
    - Ledger.Service/Ingestion/Synthetic/SyntheticBankScenario.cs
    - Ledger.UnitTests/Ingestion/TransactionReconcilerTests.cs

key-decisions:
  - "Pending rows carrying an ambiguous flag (set earlier or in the same run) are never dropped automatically; they stay visible and flagged until they book under their own reference or a person resolves them"
  - "A merge counts as an update in the run counters because sync_runs has no merged column"
  - "LoadStateAsync now also loads dropped rows regardless of date so a reappearing reference always resolves to its original row"

patterns-established:
  - "Effective date for matching and drops is TransactionDate, then BookingDate, then ValueDate, then the Amsterdam date of FirstSeenAt; an incoming item with no date at all is never a match candidate"
  - "Empty counterparty text after normalisation is treated as missing"

requirements-completed: [INGEST-02]

duration: 35min
completed: 2026-09-30
status: complete
actuals:
  tokens: 26000
  tasks: 2
  commits: 4
---

# Phase 2 Plan 05: Reconciliation Summary

**Pending payments that book under a changed bank reference stay one transaction, ghosts are dropped only on trustworthy evidence, and every uncertain pairing is stored separately and flagged instead of guessed.**

## Performance

- **Duration:** about 35 minutes
- **Tasks:** 2 (tracer plus one auto task, both test-first)
- **Files modified:** 7

## Accomplishments

- A booked item merges into an existing pending row only when exactly one pending candidate has equal signed amount, equal currency, equal non-empty normalised counterparty and dates within an inclusive five-day window, and that candidate matches no other incoming booked item. The merge keeps the internal id, adds the new reference (first status booked), appends a payload row and clears any flag.
- Two candidates for one item, one candidate for two items, or a missing counterparty on either side produce a separate insert and flag the pending rows as ambiguous. Different currency, amount or counterparty is no candidate at all.
- Drops require a fetch that enumerated every page, returned at least one item and covered the row's date; failed, partial and empty fetches drop nothing. A cancelled status drops a known pending row; booked rows ignore cancelled and pending observations; a dropped row whose reference returns is restored with the new status; booking under its own reference clears the flag.
- Tracer gate: the tracer's end-to-end verification (unit and integration reconciliation categories) was re-run after the tracer commit and passed before expansion.

## Task Commits

1. **Task 1 RED:** `e6f0ea5` test(02-05): failing tests for certain-match merging and unclear-match flags (also carries the plan contract records and the scenario helpers)
2. **Task 1 GREEN:** `5d09433` feat(02-05): merge certain pending matches and flag unclear ones
3. **Task 2 RED:** `caf8d7e` test(02-05): failing tests for drops, cancellations, restores and forward-only statuses
4. **Task 2 GREEN:** `4a24304` feat(02-05): drop vanished pending payments on trustworthy evidence and restore them when they return

## Verification

- Unit tests: 94 passed (48 in Category=Reconciliation)
- Integration tests: 55 passed, 1 skipped (pre-existing), including 9 in Category=Reconciliation and the Ingestion category, run against the local postgres-dev container
- `build/lint.sh repo-rules`: PASS

## Decisions Made

- **Flagged rows are not auto-dropped.** The plan text drops every unresolved pending row after a complete fetch, but the typical ambiguous case (pending rows gone from the feed, a new booked item present) would then drop the doubtful rows in the same run and the unclear flag would never be visible. Keeping flagged pending rows pending and flagged follows the goal of showing uncertainty instead of guessing. The cost is a possible double count (pending plus booked) until the row books under its own reference or a person resolves it, which is exactly the state the flag reports.
- **Merges count as updates** in the run counters and in the returned run result, because the sync run table has no merged column and adding one would need a migration owned by another plan in this wave.
- **Dropped rows are always loaded** for reconciliation, so a reference that reappears can never hit the reference uniqueness constraint through a date mismatch.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical functionality] Dropped rows loaded regardless of date**
- **Found during:** Task 2
- **Issue:** LoadStateAsync only loaded pending rows plus rows dated on or after the cutoff, so an old dropped row could be missed and a returning reference would fail the unique constraint instead of restoring.
- **Fix:** any non-booked row is now loaded whatever its date.
- **Files modified:** Ledger.Repository/Stores/LedgerStore.cs
- **Commit:** 4a24304

**2. [Decision] Flagged pending rows are excluded from drops** (see Decisions Made).

### Known limitation

- A cancelled status for a known reference drops the pending row but does not append a payload row for the cancelled observation, because the plan contract carries only the dropped row id. The payload for the original pending observation and every merge or update remains. Retaining cancelled payloads would need a richer drop record.

The orchestrator already treated an exception during page enumeration as a failed run; it now also passes an explicit completeness flag that becomes true only after the last page was read.

## Threat Model Coverage

- Wrong merges (high): merge only on a unique, mutual, non-empty-counterparty match; unit tests cover two-for-one, one-for-two, missing counterparty on either side, window edges, and case and spacing differences.
- Wrong drops (high): drops only after a complete, non-empty fetch covering the date; integration tests cover a failed last page, an empty feed, and restore of a returning reference; dropped rows are kept.
- Payload history (medium): merges append payload rows; see the cancelled-payload limitation above.

## Known Stubs

None.

## Threat Flags

None. No new endpoints, auth paths or schema changes; no migration was added.

## Self-Check: PASSED

- FOUND: Ledger.IntegrationTests/Ingestion/ReconciliationPipelineTests.cs
- FOUND commits: e6f0ea5, 5d09433, caf8d7e, 4a24304
