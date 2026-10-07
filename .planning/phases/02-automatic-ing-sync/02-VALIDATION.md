---
phase: 2
slug: automatic-ing-sync
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-09-30
---

# Phase 2 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 4.0.1 on Microsoft.Testing.Platform, FluentAssertions 8.11.0, NSubstitute 6.2.0, EF InMemory, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`; plain bash shell tests; `build/lint.sh` with pinned docker images (including promtool) |
| **Config file** | `global.json` (`"test": {"runner": "Microsoft.Testing.Platform"}` after migration), `Ledger.IntegrationTests/xunit.runner.json` (`parallelizeTestCollections: false`) |
| **Quick run command** | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj` (before migration: `dotnet test Ledger.UnitTests/Ledger.UnitTests.csproj`) |
| **Full suite command** | `dotnet test --solution Ledger.slnx --no-restore` (before migration: `dotnet test Ledger.slnx --no-restore`); needs `ConnectionStrings__TestAdmin` and `LEDGER_EFBUNDLE` as CI sets them |
| **Trait filter** | `dotnet test --solution Ledger.slnx --filter-trait "Category=<X>" --ignore-exit-code 8` |
| **Provisioning check** | `build/lint.sh observability` (boots the pinned Grafana with repo provisioning) |
| **Estimated runtime** | ~3 s unit, ~80 s integration, ~90 s observability lint (measured 2026-10-07) |

---

## Sampling Rate

- **After every task commit:** quick unit run filtered to the touched area's `Category` trait
- **After every plan wave:** full solution run against the local PostgreSQL container, plus `build/lint.sh observability` when alerts, dashboards or provisioning changed
- **Before `/gsd-verify-work`:** full suite green, `build/lint.sh` green, one manual Grafana check (EN and NL dashboards render against synthetic data)
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

Audited on 2026-10-07 against the executed code (release v0.2.3): every row ran green. Checkpoint tasks (02-02-T2, 02-12-T2, 02-12-T3, 02-14-T3, 02-15-T2, 02-15-T3) and the deleted spike tool's self-tests (02-02-T1, 02-12-T1) are manual-only, see below.

| Task ID | Plan | Wave | Requirement | Behaviour verified | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|--------------------|-----------|-------------------|-------------|--------|
| 02-01-T1 | 01 | 1 | INGEST-01 | DST-safe Amsterdam schedule math | unit | `dotnet test --project Ledger.UnitTests --filter-trait "Category=Scheduler"` (UT/Ingestion/SyncScheduleTests.cs) | ✅ | ✅ green |
| 02-01-T2 | 01 | 1 | INGEST-01 | CI and release run the whole suite with the migration bundle (never weakened) | unit (workflow guard) | `dotnet test --project Ledger.UnitTests --filter-trait "Category=Configuration"` (Ci_and_release_workflows_run_the_whole_suite_with_the_migration_bundle, plus six weakened variants rejected) | ✅ | ✅ green |
| 02-03-T1 | 03 | 1 | INGEST-01 (host) | selfcheck finds leaked secrets without printing them | offline shell test | `bash deploy/tests/selfcheck-logic-test.sh` | ✅ | ✅ green |
| 02-03-T2 | 03 | 1 | INGEST-01 (host) | sandboxed installer unit and apikey wrapper, tzdata, apt sources converge | offline shell test | `bash deploy/tests/sandboxing-test.sh`, `bash deploy/tests/provision-logic-test.sh`, `bash deploy/tests/render-templates-test.sh` | ✅ | ✅ green |
| 02-03-T3 | 03 | 1 | INGEST-01 (host) | bank key generated password-protected, password never printed | offline shell test | `bash deploy/tests/bank-key-logic-test.sh` | ✅ | ✅ green |
| 02-04-T1 | 04 | 2 | INGEST-02, INGEST-06 | re-run adds nothing; synthetic provider through the same pipeline | unit + integration | `--filter-trait "Category=Reconciliation"` (unit) / `"Category=Ingestion"` (integration) | ✅ | ✅ green |
| 02-04-T2 | 04 | 2 | INGEST-03, DASH-07 | exact fields, payload kept, append-only history, reporting read-only | integration | `--filter-trait "Category=Ingestion"` and `"Category=DatabaseRoles"` (integration) | ✅ | ✅ green |
| 02-05-T1/T2 | 05 | 3 | INGEST-02 | certain merges, unclear flags, drops only after complete fetches, restores | unit + integration | `--filter-trait "Category=Reconciliation"` (unit and integration) | ✅ | ✅ green |
| 02-06-T1/T2 | 06 | 3 | DASH-05, DASH-07 | EN/NL from one source, drift, parity, queries as grafana_reader | unit + integration + lint | `--filter-trait "Category=Dashboards"`, `dotnet run --project Ledger.Dashboards -- check`, `build/lint.sh observability` | ✅ | ✅ green |
| 02-07-T1 | 07 | 3 | INGEST-01, INGEST-04, INGEST-05 | link, callback, selection, first sync with longest history | unit + integration | `--filter-trait "Category=Callback"` (unit and integration) | ✅ | ✅ green |
| 02-07-T2 | 07 | 3 | SEC-01, INGEST-06 | one anonymous route, generic failures, no secrets in logs, Production refuses synthetic | integration + unit | `"Category=Callback"`, `"Category=ApiAuth"`, `"Category=LogRedaction"` (integration), `"Category=Configuration"` (unit) | ✅ | ✅ green |
| 02-07-T3 | 07 | 3 | INGEST-04 | consent state boundaries, renewal keeps history, revoke | unit + integration | `--filter-trait "Category=Consent"` (unit and integration) | ✅ | ✅ green |
| 02-08-T1..T3 | 08 | 4 | INGEST-01, INGEST-07 | 06:30 scheduler, retry rules, call budget, sync-now | unit + integration | `"Category=Scheduler"` (unit), `"Category=Sync"` (integration) | ✅ | ✅ green |
| 02-09-T1/T2 | 09 | 5 | INGEST-02, INGEST-03 | daily balance snapshot reconciles to the cent | unit + integration | `--filter-trait "Category=Balances"` (unit and integration) | ✅ | ✅ green |
| 02-10-T1 | 10 | 6 | OPS-01 | metrics from the database, opaque labels only | unit + integration | `--filter-trait "Category=Metrics"` (unit and integration) | ✅ | ✅ green |
| 02-10-T2 | 10 | 6 | OPS-02, INGEST-04, INGEST-07 | household alert rules fire exactly at their boundaries and carry no financial text | script (promtool) + lint | `bash build/tests/household-alert-expressions-test.sh` (28 cases, 10 mutations detected), `build/lint.sh observability` | ✅ | ✅ green |
| 02-11-T1/T2 | 11 | 6 | DASH-05, DASH-07, INGEST-04 | status view matches consent derivation, SELECT-only | integration | `"Category=Consent"` and `"Category=Dashboards"` (integration) | ✅ | ✅ green |
| 02-13-T1/T2 | 13 | 7 | SEC-01, INGEST-01, INGEST-03, INGEST-05..07 | AIS-only guard, token minting, error mapping, exact amounts | unit + integration | `"Category=EnableBanking"`, `"Category=ReadOnlyGuard"`, `"Category=Ingestion"` (unit), `"Category=EnableBanking"` (integration) | ✅ | ✅ green |
| 02-14-T1/T2 | 14 | 8 | INGEST-01, INGEST-02, SEC-01 | replay invariants, production validation, redaction, chosen defaults | unit + integration | `"Category=Replay"`, `"Category=Configuration"` (unit), `"Category=LogRedaction"` (integration) | ✅ | ✅ green |
| 02-16-T1/T2 | 16 | 7 | INGEST-02, INGEST-03 | undated balance on the fetch-time window, pending neutral, flagged only when persistent | unit + integration | `"Category=Balances"` (unit and integration), `"Category=Metrics"` (integration) | ✅ | ✅ green |
| 02-15-T1 | 15 | 9 | all of the above | release candidate: whole suite with the bundle, dashboards check, full lint | full suite | `LEDGER_EFBUNDLE=... dotnet test --solution Ledger.slnx --no-restore`, `build/lint.sh` | ✅ | ✅ green |

---

## Wave 0 Requirements

- [x] Migrate to xunit.v3 4.x on Microsoft.Testing.Platform (`global.json`, both test csproj files, lock files, `ci.yml`, `release.yml`) — plan 02-01
- [x] `Ledger.Dashboards` project added to `Ledger.slnx` with lock file, referenced by `Ledger.UnitTests` — plan 02-06
- [x] `Microsoft.Extensions.TimeProvider.Testing` in both test projects — plan 02-01
- [x] Synthetic provider and scenario builder shared by unit, integration and dev seeding — plan 02-04 (dev scenario in 02-07)
- [x] Recording `HttpMessageHandler` test helper for the Enable Banking client — plan 02-13
- [x] `build/lint/checks/60-observability.sh` expected rule count and dashboard assertions — plans 02-06 (dashboards) and 02-10 (rule count 16)

`LedgerWebApplicationFactory` already accepts `configureTestServices`, so the fake provider is injectable without changing the factory.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Evidence / Instructions |
|----------|-------------|------------|-------------------|
| Real ING consent returns the joint accounts with the longest history | INGEST-01, INGEST-05 | Needs the operator's ING app approval | Done 2026-10-06: two joint current accounts, history back to 2024-10-06; no savings account offered (accepted fallback) |
| Pending→booked reconciliation holds on real data | INGEST-02 | Real transaction data must stay outside the repository | Real captures replayed 2026-10-05 (3507 rows, invariants held, data deleted); the live transition of the two real pending payments is a follow-up todo |
| Real consent duration and background quota | INGEST-04, INGEST-07 | Only observable on a live consent | Recorded in the spike notes (180 days, no limit up to 44 calls a day) |
| EN and NL dashboards render with real data for every household login | DASH-05 | Visual check in a real Grafana | Passed in the acceptance test on 2026-10-07 (admin and both Viewer logins) |
| Alert email arrives through the mail relay without financial detail | OPS-02 | Needs the homelab mail relay and a real firing alert | Contact-point test email arrived 2026-10-07; the first real alert is a follow-up todo |
| Guided renewal on the live bank keeps history | INGEST-04 | Needs a live consent near expiry (around March 2027) | Follow-up todo; renewal logic covered by integration tests |
| Unattended 06:30 sync, sandboxed installer and read-only reporting role on the live host | INGEST-01, SEC-01, DASH-07 | Real clock, host and peer-auth logins | Observed 2026-10-07 (scheduled run succeeded; DELETE as grafana_reader refused with 42501) |
| Spike tool self-tests | INGEST-01, INGEST-02, INGEST-04, INGEST-05, INGEST-07 | The workstation-only spike tool was deleted after the spike by design | Results recorded in the spike notes and summaries |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 120s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated 2026-10-07

---

## Validation Audit 2026-10-07

| Metric | Count |
|--------|-------|
| Tasks with a verify block | 32 |
| Covered before the audit | 28 |
| Gaps found | 3 |
| Resolved with new tests | 2 (household alert rule expressions with promtool; CI and release whole-suite guard) |
| Escalated / deferred | 1 (the review-queue size metric moved from OPS-01 to OPS-08 in phase 4; nothing to test in phase 2) |
| Manual-only behaviour groups | 8 |
