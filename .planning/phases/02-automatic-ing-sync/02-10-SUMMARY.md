---
phase: 02-automatic-ing-sync
plan: 10
subsystem: observability
tags: [metrics, prometheus, grafana-alerting, consent-expiry, sync-health, notification-policy]

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plan 07 consent state and connections; plan 08 scheduler, sync runs and call budget; plan 09 balance reconciliation results"
provides:
  - "IIngestionStatusStore read model and SyncHealth.FailingReason"
  - "/metrics: consent days and state, last success, failing reason, error counter, calls remaining, reconciliation drift and flagged counts, all with opaque labels"
  - "SyncMetricsRefresher: database projection at startup and every minute, restart safe"
  - "Eight household alert rules in a Household folder and a daily-reminder child route to the operator email"
  - "Lint proof that the pinned Grafana loads sixteen rules, the Household route, no template markers and only metric names the app defines"
affects: [02-automatic-ing-sync]

tech-stack:
  added: []
  patterns:
    - "Metrics are a projection of database state (no in-memory event counting), so a restart can never reset or mask them"
    - "SyncMetrics tracks the series it published per gauge and removes the ones whose key is gone, since the prometheus-net registry is process wide"
    - "Alert text is static; lint rejects template markers in any rule file"

key-files:
  created:
    - Ledger.Domain/Ingestion/SyncHealth.cs
    - Ledger.Domain/Ingestion/IIngestionStatusStore.cs
    - Ledger.Repository/Stores/IngestionStatusStore.cs
    - Ledger.Service/Metrics/SyncMetrics.cs
    - Ledger.Service/Ingestion/SyncMetricsRefresher.cs
    - deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml
    - Ledger.UnitTests/Ingestion/SyncHealthTests.cs
    - Ledger.IntegrationTests/Metrics/SyncMetricsTests.cs
  modified:
    - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
    - Ledger.Service/Ingestion/IngestionServiceCollectionExtensions.cs
    - deploy/provisioning/grafana/provisioning/alerting/notification-policies.yaml
    - build/lint/checks/60-observability.sh
    - docs/monitoring.md

key-decisions:
  - "Rate limit and quota exhaustion both map to the rate_limited reason; transient, malformed and abandoned runs map to transient"
  - "The 24h repeat interval is stored by Grafana as 1d; lint asserts the normalised value"
  - "Last-success series exist only once an account has a success on its current connection, so the stale rule never fires for a connection that has not synced yet (the failing and consent rules cover that)"

patterns-established:
  - "Status reads query per connection and per account in small loops (a household has a handful) rather than one clever grouped query"
  - "Integration metric tests use a fresh database per test, a fake clock and SchedulerTestHost, and scrape the ops port"

requirements-completed: [OPS-01, OPS-02, INGEST-04, INGEST-07]

duration: ~70min
completed: 2026-09-30
status: complete
actuals:
  tokens: 16600
  tasks: 2
  commits: 2
---

# Phase 2 Plan 10: Sync metrics and household alerts Summary

**Consent days and state, last successful sync, failing reason, error counts, call allowance, reconciliation drift and review flags now reach /metrics as a database projection with opaque labels only, and eight household alert rules email the operator on every silent failure mode, at 14 and 7 days before consent expiry, and on expiry, repeating once a day while they fire.**

## Performance

- **Duration:** ~70 min
- **Tasks:** 2 (tracer, alert rules and lint)
- **Files changed:** 13 (8 created, 5 modified), 1,383 insertions

## Accomplishments

- **Failure policy in one place:** `SyncHealth.FailingReason` returns `rate_limited`, `consent_rejected` and `provider_auth` at once. For transient, malformed and abandoned runs it returns null while the same-day retry is still due (up to the retry instant plus ten minutes) and `transient` afterwards, and `transient` straight away when the failed run was a Retry, Manual or PostLink run or the retry would fall on the next local day.
- **Read model:** `IngestionStatusStore` returns the active and provider-expired connections with their latest finished run, the selected accounts with last success on their current connection, background call times of the last 48 hours (enough for either quota window), latest reconciled verdict, ambiguous pending count, and failed runs by reason over all history.
- **Metrics:** exactly the eight names and labels of the contract. Consent days are fractional, consent and failing state are one-hot, `ledger_sync_calls_remaining` goes through `CallBudget.Remaining`, the error counter is raised with `IncTo` to the database count and exists at zero for all four reasons from startup. Series of connections or accounts that disappear are removed.
- **Refresher:** `SyncMetricsRefresher` is always registered, refreshes immediately and every 60 seconds on the injected `TimeProvider`, serialises concurrent refreshes, and on a database error logs only the exception type and keeps the previous values.
- **Alerts:** `household-rules.yaml` holds the eight rules from the plan (group `household`, folder `Household`, `noDataState: OK`, empty labels, static summaries). The consent rules are bands so the group changes at 14 days, 7 days and expiry.
- **Mail cap:** the root policy is unchanged; a child route for `grafana_folder = Household` goes to the same operator contact point with hourly group interval and `repeat_interval: 24h`.
- **Lint:** `60-observability.sh` now expects sixteen rule uids (the pattern accepts digits, which the 14d and 7d uids need), asserts every household uid, asserts no rule file contains a double opening brace, asserts every `ledger_` metric name in the household rules is defined in `SyncMetrics.cs`, and asserts through Grafana's API that the Household child route and the 12h root route loaded.
- **Docs:** `docs/monitoring.md` lists the metrics in plain words, states that labels are opaque keys, and gives each household alert its meaning and action.
- **Verification (local PostgreSQL container):** unit Metrics (25), integration Metrics (9), then the full suites: unit 204 passed, integration 113 passed and 1 skipped (the bundle test, as before). `build/lint.sh observability` and `repo-rules` pass.

## Task Commits

1. **Task 1 (tracer): database state becomes opaque-labelled metrics** - `d536582` (feat)
2. **Task 2: household alert rules, daily-reminder route, lint proof, runbook** - `41dfa24` (feat)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] The plan's rule-uid pattern would not see the two digit-bearing uids**
- **Found during:** Task 2 (raising the rule count)
- **Issue:** `ledger-[a-z-]+` cannot match `ledger-bank-consent-expiring-14d` or `-7d`, so the count would have been 14, not 16.
- **Fix:** the lint pattern is `ledger-[a-z0-9-]+`.
- **Files modified:** build/lint/checks/60-observability.sh
- **Commit:** 41dfa24

**2. [Rule 3 - Blocking] Grafana normalises `24h` to `1d`**
- **Found during:** Task 2 (proving the route loaded)
- **Issue:** the provisioning API reports the child route's interval as `1d`, so asserting `24h` against the API fails even though the file says `24h`.
- **Fix:** the file keeps `repeat_interval: 24h` as the plan requires; the API assertion looks for `1d`.
- **Commit:** 41dfa24

### Plan reading choices

- The lint additionally checks the notification policy through the API (Household matcher, daily and 12h intervals). The plan did not ask for it, but the file is otherwise unproven by the pinned Grafana.
- The account flag metric also counts only pending transactions with an ambiguous match, as the plan describes; transactions already reviewed never reappear in it.
- Integration tests assert error counters with a minimum rather than an exact value, because the prometheus-net registry is shared by the whole test process and a counter cannot go down between tests.

### Process notes

- **Test-first order:** as in earlier plans, implementation and tests were written together and committed per task; no separate failing-test commits. The plan type is `execute`, so no plan-level TDD gate applies.
- **Tracer gate:** auto mode was not active and `human_verify_mode` is `end-of-phase`, so the human check is deferred to phase verification. The tracer's automated verify (unit and integration Metrics, repo-rules lint) passed before Task 2.
- **Shared files:** STATE.md and ROADMAP.md were not touched, per the parallel-execution instructions. No EF migration, Ledger.Dashboards or dashboard JSON was touched.
- **Review-queue metric:** deferred as flagged in the plan; it ships with the review queue.

## Notes for later plans

- The go-live plan should prove live mail delivery of a household alert through the relay; the rules are only proven to load in Grafana here.
- The stale-sync rule uses `min(ledger_sync_last_success_timestamp_seconds)` over selected accounts, so a newly linked connection whose first sync has not succeeded yet produces no series until it does. The failing, consent and first-sync warning paths cover that window.
- The dashboard executor's status table can read the same state through its `reporting.account_status` view; nothing here depends on it.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model. Mitigations implemented and tested: only opaque connection and account keys and fixed reason and state words as labels (integration test scrapes with distinctive names, IBANs, provider names and counterparties and asserts none appears), static alert titles and summaries with a lint that rejects template markers, metrics seeded from the database (restart test with an unrelated database in between to prove the registry is refilled from storage), the root mail grouping and hourly group interval kept with a 24h household repeat.

## Issues Encountered

None beyond the deviations above.

## Self-Check: PASSED

- Files present: every file listed under key-files exists.
- Commits present: d536582, 41dfa24.
- Acceptance checks: one `last_success_timestamp_seconds` and an `IncTo` in SyncMetrics.cs; no `"iban"`, `"name"`, `"display_name"` or `"counterparty"` label; eight `uid: ledger-` lines; one `repeat_interval: 24h` and one `repeat_interval: 12h`; `alert_rule_count" -ne 16` present; no `{{` in the household rules; `build/lint.sh observability` and `repo-rules` exit 0.
