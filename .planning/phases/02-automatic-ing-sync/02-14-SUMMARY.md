---
phase: 02-automatic-ing-sync
plan: 14
subsystem: bank-adapter-readiness
tags: [replay, reconciler, production-validation, log-redaction, runbook, spike-defaults]

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plan 13 Enable Banking adapter and mapping; plan 11 reconciler; plan 16 ledger store semantics; spike decisions"
provides:
  - "InMemoryLedger test helper applying a reconciliation plan with the store's semantics"
  - "CapturedPairReplayTests: opt-in replay of decrypted capture pages through the real mapping and reconciler, counts only"
  - "Production configuration validation for the Enable Banking provider, naming only keys"
  - "HttpClient logging at Warning and sentinel redaction tests through the adapter"
  - "Ingestion defaults set to the measured spike values"
  - "Operator runbook docs/bank-link.md and placeholder env lines"
  - "Real replay counts and spike data deletion recorded in the spike notes"
affects: [02-15, 02-16]

tech-stack:
  added: []
  patterns:
    - "Replay test reads a directory from an environment variable, skips when unset, and only ever asserts, prints or writes counts so real bank content cannot leak"
    - "Test helper mirrors the store's apply semantics so the real reconciler is exercised end to end without a database"

key-files:
  created:
    - Ledger.UnitTests/Ingestion/InMemoryLedger.cs
    - Ledger.UnitTests/Ingestion/EnableBanking/CapturedPairReplayTests.cs
  modified:
    - docs/bank-link.md
    - Ledger.Service/Hosting/ProductionConfigurationValidator.cs
    - Ledger.Service/Ingestion/IngestionOptions.cs
    - Ledger.Service/appsettings.json
    - Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs
    - Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs
    - Ledger.IntegrationTests/Security/LogRedactionTests.cs
    - Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs
    - Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs
    - deploy/ledger.env.example
    - docs/rest-api.md
    - .planning/phases/02-automatic-ing-sync/02-SPIKE.md

key-decisions:
  - "Ingestion:BackgroundCallsPerDay default is 12 (was 4), following the spike measurement of 44 header-less calls in a day with no rejection"
  - "The expected balance is reconciled on the fetch-time window and undated reconciliation is on by default, because ING sends only the expected balance and no reference date"
  - "Production refuses an incomplete Enable Banking configuration at startup and names only the configuration keys, never values"
  - "The real replay needs no reconciler fix: all invariants held on 3507 rows from 53 real pages"

patterns-established:
  - "Operator-assisted verification of real data: the operator decrypts into a mode 700 in-memory directory, the agent runs the test and only sees a validated counts-only report line, then everything is deleted"

requirements-completed: [INGEST-01, INGEST-02, INGEST-04, SEC-01]

duration: ~1h
completed: 2026-10-05
status: complete

actuals:
  tokens: 15900
  tasks: 3
  commits: 4
---

# Phase 2 Plan 14: Replay against real captures and production readiness Summary

**The real ING captures replay through the production mapping and reconciler with 3507 rows and every invariant intact, production refuses an incomplete bank configuration, and the spike defaults and operator runbook are in place with all real spike data deleted.**

## Performance

- **Tasks:** 3 (tracer, production readiness, operator replay and deletion)
- **Commits:** 4 including the documentation commit for the replay and this summary
- **Files:** 14 changed in the plan diff before this summary, +1352 / -29 lines
- The duration is approximate; the exact start time was not recorded in this continuation.

## Accomplishments

- A replay test maps captured Enable Banking pages through the real adapter mapping and replays them per account in time order through the real reconciler, using an in-memory ledger that mirrors the store's apply semantics. It checks that every reference sits on exactly one row, no booked row returns to pending, no live pending row has a certain booked partner, and that re-applying a snapshot changes nothing. Only counts are asserted, printed or written. It is proven on synthetic captures and skipped unless the replay directory variable is set.
- Real replay on 2026-10-05: 2 accounts, 8 captures, 53 pages, 4020 items, 3507 rows, all inserted, none merged, upgraded, flagged, dropped or restored, 3507 live booked, 0 live pending, no row with two references, no reference on two rows, 0 unreadable pages, 0 incomplete captures, 0 changes on re-apply. 3507 equals the distinct booked count from the analyze step exactly. 8 tests passed, 0 failed, 0 skipped.
- Production with the Enable Banking provider refuses a missing or non-GUID application id, a missing key file or an empty key password, naming only the keys.
- HttpClient logging is at Warning, and sentinel tests show the client token, key password, key body, session id and authorisation code never reach logs, responses or metrics through a full link and sync through the adapter.
- Ingestion defaults follow the measured spike values, with placeholder bank link settings in the env example and a runbook in `docs/bank-link.md` covering registration, key setup on the host, linking, selection, renewal, revocation, failure reading and the joint-accounts-only fallback.
- All real spike data was deleted after the replay: the decrypted replay directory, the report and run log, and the whole spike directory including the spike and sandbox private keys and every encrypted capture. Both the spike directory and the replay directory were verified absent.

## Task Commits

1. **Task 1 (tracer): replay a capture set through the real mapping and reconciler** - `a7c0187` (test)
2. **Task 2: production readiness** - `7034c13` (feat)
3. **Task 3: operator replay and deletion** - recorded in `16ef5d3` (docs); the operator ran the decryption and deletion
4. **Summary** - docs commit following this file

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Tests pinned to the old defaults now state their values explicitly**
- **Found during:** Task 2
- **Issue:** The scheduler and quota tests and the balance snapshot tests relied on the old default call budget of 4 and on undated reconciliation being off. Changing the defaults to the spike values would have broken or silently changed them.
- **Fix:** They now set a budget of 4 and undated reconciliation off explicitly, so they test what they were written to test regardless of the defaults.
- **Files modified:** `Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs`, `Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs`
- **Commit:** `7034c13`

**2. [Rule 2 - Missing critical functionality] Tests pinning the new defaults added**
- **Found during:** Task 2
- **Issue:** Nothing asserted that the committed configuration and the options defaults match the spike's chosen values, so a later edit could drift from them unnoticed.
- **Fix:** Added defaults-pinning tests to the committed configuration tests.
- **Files modified:** `Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs`
- **Commit:** `7034c13`

### Plan Conditions Not Triggered

- **No migration needed.** The plan allowed a migration "only if the balance-kind preference changed". The default is an options value, not stored schema, so no migration was created.
- **Production validation key names.** The validator names the Enable Banking keys (application id, key file path, key password) in its failure message and never a value, as the plan required.

### Process notes

- **TDD:** the plan's tasks are tagged for test-driven work, but the tracer's helper and its tests were committed together (`a7c0187`, a `test` commit) and the production readiness changes with their tests in one `feat` commit (`7034c13`), so separate RED and GREEN commits do not exist for this plan.
- **Tracer gate:** handled autonomously rather than as an interactive stop, because auto mode was active; the tracer's synthetic Replay run passed before the expansion task started.
- **Task 3 split:** at the operator's request the work was split. The operator held the age identity and decrypted into the in-memory directory; the orchestrator ran the replay and saw only the summary and a pattern-validated counts line. Nothing from the real captures entered the repository, a log or this summary beyond counts.

## Auth Gates

None for the agent. The age identity was supplied by the operator at a hidden prompt as designed.

## Issues Encountered

None. Every invariant held on the real data on the first real replay, so the reconciler needed no fix before the real link.

## Known Stubs

None.

## Threat Flags

None. No new network surface, auth path or schema was introduced; the changes tighten configuration validation and log redaction.

## Open Items

- The ING app check that the aggregator's access is gone after the spike revocation is still pending with the operator (recorded in the spike notes).

## Self-Check: PASSED

- Commits `a7c0187`, `7034c13` and `16ef5d3` exist in history.
- `docs/bank-link.md`, `Ledger.UnitTests/Ingestion/InMemoryLedger.cs` and `Ledger.UnitTests/Ingestion/EnableBanking/CapturedPairReplayTests.cs` exist.
- The spike directory and the replay directory in the in-memory filesystem do not exist.
