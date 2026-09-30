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

Seeded from RESEARCH.md's requirement→test map; the planner assigns task IDs, plans and waves.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | TBD | TBD | INGEST-01 | — | link start stores only a hashed state; callback stores a Data-Protection-protected session | integration | `--filter-trait "Category=Callback"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | INGEST-01 | — | N/A | unit (FakeTimeProvider, DST dates) | `--filter-trait "Category=Scheduler"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | INGEST-02 | — | N/A | unit + integration | `--filter-trait "Category=Reconciliation"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | INGEST-03 | — | runtime role cannot UPDATE raw payloads | unit + integration | `--filter-trait "Category=Ingestion"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | INGEST-04 | — | N/A | unit + integration | `--filter-trait "Category=Consent"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | INGEST-05 | — | N/A | unit (fake HTTP handler) | `--filter-trait "Category=EnableBanking"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | INGEST-06 | — | synthetic provider refused in Production | integration | `--filter-trait "Category=Ingestion"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | INGEST-07 | — | N/A | unit + integration | `--filter-trait "Category=Sync"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-01 | — | only allow-listed AIS requests leave the process; no payment method exists | unit (recording handler, reflection) | `--filter-trait "Category=ReadOnlyGuard"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | OPS-01 | — | no IBAN or name in `/metrics` labels | integration | `--filter-trait "Category=Metrics"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | OPS-02 | — | alert texts carry no amounts, balances, counterparties or IBANs | lint (real Grafana) | `build/lint.sh observability` | ✅ extend | ⬜ pending |
| TBD | TBD | TBD | DASH-05 | — | N/A | unit | `--filter-trait "Category=Dashboards"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DASH-07 | — | `grafana_reader` write attempts rejected with SQLSTATE 42501 | integration | `--filter-trait "Category=DatabaseRoles"` | ✅ extend | ⬜ pending |
| TBD | TBD | TBD | API-01 | — | only the callback is anonymous; bad state gets an identical generic response | integration | `--filter-trait "Category=Callback"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-06 (regression) | — | `code`, `state`, session id never in logs, responses or metrics | integration | `--filter-trait "Category=LogRedaction"` | ✅ extend | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] Migrate to xunit.v3 4.x on Microsoft.Testing.Platform (`global.json`, both test csproj files, lock files, `ci.yml`, `release.yml`, README/docs command lines)
- [ ] `Ledger.Dashboards` project added to `Ledger.slnx` with lock file, referenced by `Ledger.UnitTests`
- [ ] `Microsoft.Extensions.TimeProvider.Testing` in both test projects
- [ ] Synthetic provider and scenario builder shared by unit, integration and dev seeding
- [ ] Recording `HttpMessageHandler` test helper for the Enable Banking client
- [ ] `build/lint/checks/60-observability.sh` expected rule count and dashboard assertions

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
