---
phase: 2
slug: automatic-ing-sync
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-09-30
---

# Phase 2 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 (migrating 3.2.2 on VSTest → 4.0.1 on Microsoft.Testing.Platform as the first task), FluentAssertions 8.11.0, NSubstitute 6.2.0, EF InMemory, `Microsoft.AspNetCore.Mvc.Testing`, plus `Microsoft.Extensions.TimeProvider.Testing` |
| **Config file** | `global.json` (`"test": {"runner": "Microsoft.Testing.Platform"}` after migration), `Ledger.IntegrationTests/xunit.runner.json` (`parallelizeTestCollections: false`) |
| **Quick run command** | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj` (before migration: `dotnet test Ledger.UnitTests/Ledger.UnitTests.csproj`) |
| **Full suite command** | `dotnet test --solution Ledger.slnx --no-restore` (before migration: `dotnet test Ledger.slnx --no-restore`); needs `ConnectionStrings__TestAdmin` and `LEDGER_EFBUNDLE` as CI sets them |
| **Trait filter** | `dotnet test --solution Ledger.slnx --filter-trait "Category=<X>" --ignore-exit-code 8` |
| **Provisioning check** | `build/lint.sh observability` (boots the pinned Grafana with repo provisioning) |
| **Estimated runtime** | ~30 s unit, ~120 s full suite, ~90 s observability lint |

---

## Sampling Rate

- **After every task commit:** quick unit run filtered to the touched area's `Category` trait
- **After every plan wave:** full solution run against the local PostgreSQL container, plus `build/lint.sh observability` when alerts, dashboards or provisioning changed
- **Before `/gsd-verify-work`:** full suite green, `build/lint.sh` green, one manual Grafana check (EN and NL dashboards render against synthetic data)
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

Seeded from RESEARCH.md's requirement→test map; task IDs, plans and waves assigned during planning.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 02-01-T1 | 01 | 1 | INGEST-01 | T-02-01-02 | schedule math in Europe/Amsterdam across DST | unit (FakeTimeProvider, DST dates) | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Scheduler"` | ❌ W0 | ⬜ pending |
| 02-03-T1 | 03 | 1 | INGEST-01 (host) | T-02-03-01 | selfcheck finds leaked secrets in journal and /var/log without printing them | offline shell test | `bash deploy/tests/selfcheck-logic-test.sh` | ❌ W0 | ⬜ pending |
| 02-03-T3 | 03 | 1 | INGEST-01 (host) | T-02-03-05 | bank key generated password-protected, password never printed | offline shell test | `bash deploy/tests/bank-key-logic-test.sh` | ❌ W0 | ⬜ pending |
| 02-04-T1 | 04 | 2 | INGEST-02 | T-02-04-01 | re-run adds nothing; same-reference pending to booked stays one row | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Reconciliation"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Ingestion"` | ❌ W0 | ⬜ pending |
| 02-04-T1 | 04 | 2 | INGEST-06 | T-02-04-08 | whole pipeline runs on the synthetic provider | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Ingestion"` | ❌ W0 | ⬜ pending |
| 02-04-T2 | 04 | 2 | INGEST-03 | T-02-04-01 | runtime role cannot UPDATE or DELETE payloads or refs; exact fields kept | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Ingestion"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=DatabaseRoles"` | ❌ W0 | ⬜ pending |
| 02-04-T2 | 04 | 2 | DASH-07 | T-02-04-02 | grafana_reader SELECT-only on every reporting object; writes rejected with 42501 | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=DatabaseRoles"` | ❌ W0 | ⬜ pending |
| 02-05-T1, 02-05-T2 | 05 | 3 | INGEST-02 | T-02-05-01, T-02-05-02 | certain-match merge only; drops only after complete non-empty fetches | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Reconciliation"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Reconciliation"` | ❌ W0 | ⬜ pending |
| 02-06-T1, 02-06-T2 | 06 | 3 | DASH-05 | T-02-06-02 | EN and NL generated from one source; drift and missing keys fail CI | unit + lint (real Grafana) | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Dashboards"` / `build/lint.sh observability` | ❌ W0 | ⬜ pending |
| 02-06-T2 | 06 | 3 | DASH-07 | T-02-06-01 | every committed dashboard query runs as grafana_reader | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Dashboards"` | ❌ W0 | ⬜ pending |
| 02-07-T1 | 07 | 3 | INGEST-01 | T-02-07-01 | link start stores only a hashed state; callback stores a protected session | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Callback"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Callback"` | ❌ W0 | ⬜ pending |
| 02-07-T1 | 07 | 3 | INGEST-05 | — | first sync after selection uses the longest history | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Callback"` | ❌ W0 | ⬜ pending |
| 02-07-T2 | 07 | 3 | API-01 | T-02-07-04 | only the callback is anonymous; bad state gets an identical generic response | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Callback"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=ApiAuth"` | ❌ W0 | ⬜ pending |
| 02-07-T2 | 07 | 3 | SEC-06 (regression) | T-02-07-03 | code, state and session id never in logs, responses or metrics | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=LogRedaction"` | ❌ W0 | ⬜ pending |
| 02-07-T2 | 07 | 3 | INGEST-06 | T-02-07-05 | synthetic provider refused in Production | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Configuration"` | ❌ W0 | ⬜ pending |
| 02-07-T3 | 07 | 3 | INGEST-04 | — | consent state derived; renewal keeps history | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Consent"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Consent"` | ❌ W0 | ⬜ pending |
| 02-08-T1, 02-08-T2 | 08 | 4 | INGEST-01 | T-02-08-04 | daily Amsterdam run, catch-up, one safe retry | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Scheduler"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Sync"` | ❌ W0 | ⬜ pending |
| 02-08-T2, 02-08-T3 | 08 | 4 | INGEST-07 | T-02-08-01 | call budget, no same-day retry after 429, last-call refusal | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Sync"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Sync"` | ❌ W0 | ⬜ pending |
| 02-08-T3 | 08 | 4 | API-01 | T-02-08-02 | nothing interactive triggers a bank fetch | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Sync"` | ❌ W0 | ⬜ pending |
| 02-09-T1, 02-09-T2 | 09 | 5 | INGEST-02, INGEST-03 | T-02-09-01 | balance reconciles exactly to the cent or is flagged | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Balances"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Balances"` | ❌ W0 | ⬜ pending |
| 02-10-T1 | 10 | 6 | OPS-01 | T-02-10-01 | no IBAN or name in /metrics labels; seeded from the database | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Metrics"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Metrics"` | ❌ W0 | ⬜ pending |
| 02-10-T2 | 10 | 6 | OPS-02 | T-02-10-02 | alert texts carry no amounts, balances, counterparties or IBANs | lint (real Grafana) | `build/lint.sh observability` | ❌ W0 | ⬜ pending |
| 02-11-T1, 02-11-T2 | 11 | 6 | DASH-05, INGEST-04 | T-02-11-03 | status row matches the application's consent derivation | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Dashboards"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Consent"` | ❌ W0 | ⬜ pending |
| 02-13-T1 | 13 | 7 | INGEST-05 | T-02-13-05 | strategy=longest first, continuation with identical parameters | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=EnableBanking"` / `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=EnableBanking"` | ❌ W0 | ⬜ pending |
| 02-13-T2 | 13 | 7 | SEC-01 | T-02-13-01 | only allow-listed account-information requests leave the process; no payment member | unit (recording handler, reflection) | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=ReadOnlyGuard"` | ❌ W0 | ⬜ pending |
| 02-13-T2 | 13 | 7 | INGEST-03 | T-02-13-04 | exact decimal parse, rejection instead of rounding | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Ingestion"` | ❌ W0 | ⬜ pending |
| 02-14-T1, 02-14-T3 | 14 | 8 | INGEST-02 | T-02-14-05 | real captured pairs replayed through the real adapter and reconciler | unit (opt-in on real captures) | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Replay"` | ❌ W0 | ⬜ pending |
| 02-14-T2 | 14 | 8 | SEC-06 (regression) | T-02-14-02 | client token, key and session never in logs through the adapter | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=LogRedaction"` | ❌ W0 | ⬜ pending |
| 02-15-T2, 02-15-T3 | 15 | 9 | INGEST-01, INGEST-04, INGEST-05, OPS-01, DASH-07, SEC-01 | T-02-15-02 | live link, first sync and next-morning automatic sync observed with counts only | manual + SSH checks | see plan 02-15 | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Migrate to xunit.v3 4.x on Microsoft.Testing.Platform (`global.json`, both test csproj files, lock files, `ci.yml`, `release.yml`) — plan 02-01
- [ ] `Ledger.Dashboards` project added to `Ledger.slnx` with lock file, referenced by `Ledger.UnitTests` — plan 02-06
- [ ] `Microsoft.Extensions.TimeProvider.Testing` in both test projects — plan 02-01
- [ ] Synthetic provider and scenario builder shared by unit, integration and dev seeding — plan 02-04 (dev scenario in 02-07)
- [ ] Recording `HttpMessageHandler` test helper for the Enable Banking client — plan 02-13
- [ ] `build/lint/checks/60-observability.sh` expected rule count and dashboard assertions — plans 02-06 (dashboards) and 02-10 (rule count 16)

`LedgerWebApplicationFactory` already accepts `configureTestServices`, so the fake provider is injectable without changing the factory.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Real ING consent returns the joint account (and savings, if exposed) with longest history | INGEST-01, INGEST-05 | Needs the operator's ING app approval against a live Enable Banking application | Run the spike protocol in RESEARCH.md; record outcome against its decision table |
| Pending→booked reconciliation holds on real captured pairs | INGEST-02 | Real transaction data must stay outside the repository | Replay captured pairs kept outside the repo through the reconciler; record match results |
| Real consent duration and background quota | INGEST-04, INGEST-07 | Only observable on a live consent | Read `access.valid_until` and observe quota responses during the spike |
| EN and NL dashboards render with data | DASH-05 | Visual check in a real Grafana | Open both dashboards against synthetic data as a Viewer and confirm every panel renders |
| Alert email arrives through the Postfix contact point | OPS-02 | Needs the homelab mail relay | Force a sync-failure metric on the LXC and confirm the operator receives the email without financial details |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
