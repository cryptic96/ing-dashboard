---
phase: 02-automatic-ing-sync
plan: 11
subsystem: dashboards
tags: [grafana, reporting-views, consent, balances, postgres, i18n]
status: complete

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plan 06 dashboard generator and translations; plan 07 consent state; plan 08 sync runs; plan 09 balance snapshots"
provides:
  - "reporting.account_status view: one row per sync-enabled account with last success, consent state and days left, latest balance with date and reconciliation, unclear-match count"
  - "Status table as panel 1 at the top of the EN and NL sync dashboards; recent transactions moved to panel 2"
  - "AccountStatusViewTests proving consent parity with ConsentState.Derive, balance selection, empty states and the exact column list"
affects: [alerts, later dashboard work]

tech-stack:
  added: []
  patterns:
    - "Operational state the partners see is read from reporting views, not Prometheus, because it must carry display names"
    - "View SQL is held in a private const of the migration and dropped in Down"

key-files:
  created:
    - Ledger.Repository/Migrations/20260930193516_AddAccountStatusView.cs
    - Ledger.Repository/Migrations/20260930193516_AddAccountStatusView.Designer.cs
    - Ledger.IntegrationTests/Ingestion/AccountStatusViewTests.cs
  modified:
    - Ledger.Dashboards/Definitions/SyncDashboard.cs
    - Ledger.Dashboards/Model/FieldConfig.cs
    - Ledger.Dashboards/translations.json
    - deploy/provisioning/grafana/provisioning/dashboards/json/ledger-sync-en.json
    - deploy/provisioning/grafana/provisioning/dashboards/json/ledger-sync-nl.json
    - Ledger.IntegrationTests/Dashboards/DashboardQueryTests.cs

key-decisions:
  - "Consent state in SQL mirrors ConsentState.Derive: stored revoked and superseded win, provider_expired or valid_until at or before now is expired, under 14 days is expiring; a parity test runs both on each side of every boundary"
  - "Balance shown is the latest snapshot date, preferring closing_booked, then interim_booked, then any kind (matches the default reconcile kinds)"
  - "The status table clears the panel-wide decimals default and sets two decimals only on the balance column so day and count columns stay integers"
  - "The empty migration keeps the model snapshot unchanged; the generator header was stripped from the designer file and the snapshot edit reverted"

patterns-established:
  - "Status columns are pinned by an information_schema test so no identifier, session material or payload can be added unnoticed"

requirements-completed: [DASH-05, DASH-07, INGEST-04]

duration: ~40min
completed: 2026-09-30
actuals:
  tokens: 30000
  tasks: 2
  commits: 2
---

# Phase 2 Plan 11: Account status view and dashboard status table Summary

**reporting.account_status plus a translated status table at the top of both dashboards, with the view's consent derivation proven equal to the application's on each side of every boundary.**

## Accomplishments

- View `reporting.account_status` (migration `AddAccountStatusView`) selects exactly `account_key, account_name, last_success_at, consent_state, consent_days_left, balance_amount, balance_currency, balance_date, balance_reconciled, flagged_count` for sync-enabled accounts only. `grafana_reader` gets SELECT through the existing default privileges on the reporting schema; the data-driven write-denial role test covers it.
- Dashboards: status table as panel 1 (full width, height 6) above recent transactions (panel 2, moved to y=6). Translated column headers via organize, value mappings with colours for consent state (linked, expiring, expired, revoked, superseded) and reconciliation (yes, no, unknown), two-decimal balance, red threshold when unclear matches are above zero. New keys added in English and Dutch; both files regenerated and committed; drift, translation, parity and reporting-only tests pass.
- Typed model extended only with `Thresholds` and `ThresholdStep`.
- Tests: `DashboardQueryTests` now also asserts the status row of the seeded account (linked, last success present, balance 125.40, reconciliation unknown, no flagged rows) in both languages. `AccountStatusViewTests` (Category=Consent, 18 cases): consent parity at +14d+1min, +14d-1min, +1min, -1min and far past; provider_expired, revoked, superseded with far-off validity; days left 13 at 13d23h and 0 after expiry; last success ignores a later failed run; closing_booked beats interim_booked beats other kinds; later date wins; yes/no/unknown verdicts; no snapshot gives NULL balance and unknown; unselected account absent; 2 pending ambiguous rows plus a booked one count as 2; exact column list.
- Acceptance check: changing the warning period in the view from 14 to 13 days failed the boundary test; reverted before committing.

## Task Commits

1. Task 1 (tracer): view, status table, translations, regenerated JSON, query test - `e71fa0b`
2. Task 2: status view tests - `9ef1195`

The tracer feedback gate ran (auto mode): `dotnet run --project Ledger.Dashboards -- check`, unit and integration Dashboards categories and `build/lint.sh observability` passed before expansion.

## Deviations from Plan

None - plan executed as written. The Panels.cs file listed in the plan needed no change, since the existing table panel already carried everything the status table needs.

## Known Stubs

None. All test data is synthetic.

## Threat Flags

None beyond the plan's register: T-02-11-01 (column list pinned by test), T-02-11-02 (existing write-denial role test covers the new view), T-02-11-03 (parity test) are all mitigated.

## Issues Encountered

- `dotnet ef` needed a locked restore in the fresh worktree first. The generated snapshot only gained the auto-generated header, so it was reverted.
- Whether the pinned Grafana renders the thresholds and mappings against a live database was not exercised (lint only confirms both dashboards load); query correctness is covered by the integration tests.

## Self-Check: PASSED

- Migration, designer, tests, regenerated dashboards and this summary exist; commits `e71fa0b` and `9ef1195` exist.
- `dotnet test --solution Ledger.slnx`: 303 passed, 1 skipped (pre-existing), 0 failed. `dotnet run --project Ledger.Dashboards -- check`, `build/lint.sh observability` and `repo-rules` pass.
