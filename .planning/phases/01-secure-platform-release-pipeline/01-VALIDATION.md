---
phase: 1
slug: secure-platform-release-pipeline
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-09-27
validated: 2026-09-29
---

# Phase 1 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 + FluentAssertions; `Microsoft.AspNetCore.Mvc.Testing` for in-process integration; bash behaviour tests under `deploy/tests/` and `build/tests/` (shared `deploy/tests/lib/host-guard.sh` keeps them off the real host); `build/lint.sh` with six self-tested checks (repo rules, workflows via actionlint and zizmor, shell via shellcheck, secrets via gitleaks, script tests, observability via promtool and a digest-pinned Grafana container) |
| **Config file** | `Ledger.slnx`, `Directory.Build.props`, `Ledger.IntegrationTests/xunit.runner.json`, committed `packages.lock.json`, `Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs` (local PostgreSQL via `dotnet user-secrets`; CI uses a PostgreSQL service container) |
| **Quick run command** | `dotnet test Ledger.UnitTests` (38 tests, about 4 s) |
| **Full suite command** | `build/package-release.sh --version 0.0.0 --commit "$(git rev-parse HEAD)" --output artifacts/release`, then `LEDGER_EFBUNDLE="$PWD/artifacts/release/stage/efbundle" dotnet test Ledger.IntegrationTests` (35 tests), then `build/lint.sh` (runs every offline shell test). Run the two test projects separately: a solution-wide `dotnet test` crashed the runtime once. |
| **Estimated runtime** | about 2 minutes locally; about 3 minutes with the network tests (`LEDGER_LINT_NETWORK=1`) |
---

## Sampling Rate

- **After every task commit:** Run `dotnet test Ledger.UnitTests`
- **After every plan wave:** Run `dotnet test` plus the CI security-lint job (zizmor, actionlint, gitleaks full history)
- **Before `/gsd-verify-work`:** Full suite green, CI security-lint green, and the manual checkpoints below actually performed
- **Max feedback latency:** 60 seconds

---

## Per-Task Verification Map

Refined by the planner with real plan and task IDs (task ID format: {plan}-T{n}). Test categories use plain names only.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 01-01-T2 | 01-01 | 1 | OPS-05, SEC-03 | T-01-03 | canary written once, read per health check; survives restart and content-root change; lost key ring reported, never healed | integration | `LEDGER_EFBUNDLE=... dotnet test Ledger.IntegrationTests --filter "Category=Health\|Category=DataProtectionRestart"` | ✅ | ✅ green |
| 01-01-T2 | 01-01 | 1 | SEC-05 | T-01-04 | ops endpoint loopback-only; health/metrics filtered by local port | unit + integration | `dotnet test Ledger.UnitTests` | ✅ | ✅ green |
| 01-01-T3 | 01-01 | 1 | OPS-07 | T-01-07 | packaged efbundle migrates as ledger_migrator, idempotent re-run | integration | `LEDGER_EFBUNDLE=... dotnet test Ledger.IntegrationTests --filter Category=Migrations` | ✅ | ✅ green |
| 01-01-T3 | 01-01 | 1 | SEC-02 | T-01-01, T-01-02 | runtime cannot run DDL; reader SELECT-only on reporting; no superuser roles | integration | `dotnet test Ledger.IntegrationTests --filter Category=DatabaseRoles` | ✅ | ✅ green |
| 01-02-T1 | 01-02 | 2 | SEC-09, SEC-10 | T-02-01..04 | pin, injection, secret and personal-data checks each proven able to fail by self-tests | static + self-test | `build/lint.sh repo-rules shell secrets script-tests` | ✅ | ✅ green |
| 01-02-T2 | 01-02 | 2 | SEC-08, SEC-09, SEC-10 | T-02-05 | CI green on GitHub; GitHub-hosted runners only; full-history gitleaks | CI | `build/lint.sh` + `gh run watch --exit-status` | ✅ | ✅ green |
| 01-03-T1 | 01-03 | 3 | SEC-07 | T-03-01, T-03-02 | strict v-semver tag, reachable from main, via env only | script test | `bash build/tests/validate-release-tag-test.sh` | ✅ | ✅ green |
| 01-03-T2 | 01-03 | 3 | SEC-07, SEC-08 | T-03-03..06 | attested draft, environment-gated re-verifying publish, no caches | static | `build/lint.sh workflows repo-rules secrets` | ✅ | ✅ green |
| 01-04-T1 | 01-04 | 2 | SEC-07, SEC-08 | T-04-01..04 | tampered/mismatched artifact refused before unpacking; no credentials used | network smoke | `bash deploy/tests/verify-rejects-tampered-artifact-network-test.sh` | ✅ | ✅ green |
| 01-04-T2 | 01-04 | 2 | OPS-07, OPS-03 | T-04-06..08 | backup-before-migrate, atomic activation, rollback unless migrated, pruning, metrics, email content | script test | `bash deploy/tests/ledger-deploy-logic-test.sh` | ✅ | ✅ green |
| 01-05-T1 | 01-05 | 3 | OPS-04 | T-05-01, T-05-02, T-05-05 | public-key-only encryption, GFS 7/4/12 by name, freshness metrics | script test | `bash deploy/tests/backup-logic-test.sh` | ✅ | ✅ green |
| 01-05-T2 | 01-05 | 3 | OPS-04 | T-05-03, T-05-07 | drill and live restore scripts, identity never copied | script test | `bash deploy/tests/restore-logic-test.sh` (+ shellcheck) | ✅ | ✅ green |
| 01-06-T1 | 01-06 | 2 | API-02 | T-06-02, T-06-08 | keys hashed, shown once, revocable; CLI via service identity | unit + integration | `dotnet test Ledger.IntegrationTests --filter Category=ApiKeyCli` | ✅ | ✅ green |
| 01-06-T2 | 01-06 | 2 | API-02 | T-06-01, T-06-03..06 | every endpoint 401 without key (enumerated); immediate revocation; concurrency | integration | `dotnet test Ledger.IntegrationTests --filter Category=ApiAuth` | ✅ | ✅ green |
| 01-07-T1 | 01-07 | 3 | SEC-03, OPS-05, SEC-05 | T-07-01, T-07-02, T-07-06 | cert-protected key ring survives restart/redeploy; unsafe Production config refused | unit + integration | `dotnet test Ledger.UnitTests` + `LEDGER_EFBUNDLE=... dotnet test Ledger.IntegrationTests --filter "Category=DataProtection\|Category=DataProtectionRestart"` | ✅ | ✅ green |
| 01-07-T2 | 01-07 | 3 | SEC-06, SEC-03 | T-07-03..05 | sentinels (raw/URL/base64) absent from logs, errors, metrics; committed config has no secrets | integration + unit | `dotnet test Ledger.UnitTests --filter Category=Configuration` + `dotnet test Ledger.IntegrationTests --filter Category=LogRedaction` | ✅ | ✅ green |
| 01-08-T1 | 01-08 | 3 | DASH-08, DASH-06 | T-08-01..03 | anonymous/public/snapshots disabled; datasources provisioned without passwords | static | `build/lint.sh repo-rules secrets` | ✅ | ✅ green |
| 01-08-T2 | 01-08 | 3 | DASH-06, DASH-08 | T-08-04, T-08-05 | provisioning loads in a real Grafana; anonymous 401; 8 rules; promtool passes | container smoke | `build/lint.sh observability` | ✅ | ✅ green |
| 01-09-T1 | 01-09 | 2 | OPS-03 | T-09-SC | install pins confirmed against real sources | network check | `bash deploy/tests/versions-network-test.sh` | ✅ | ✅ green |
| 01-09-T2 | 01-09 | 2 | OPS-03, SEC-03 | T-09-03..05 | verified sources, accounts, env file and certificate without printing secrets | script test | `bash deploy/tests/provision-logic-test.sh` | ✅ | ✅ green |
| 01-09-T3 | 01-09 | 2 | SEC-05, SEC-02 | T-09-01, T-09-02 | socket-only PostgreSQL, local peer rules only | script test | `bash deploy/tests/postgresql-config-test.sh` (runs live in go-live too) | ✅ | ✅ green |
| 01-10-T1 | 01-10 | 4 | DASH-09, OPS-03 | T-10-01..03 | default-drop firewall, loopback observability, atomic reload | script test | `bash deploy/tests/render-templates-test.sh` | ✅ | ✅ green |
| 01-10-T2 | 01-10 | 4 | DASH-08, SEC-02, SEC-05, SEC-08 | T-10-04..06 | Viewer-only accounts; on-host selfcheck | script test | `bash deploy/tests/selfcheck-logic-test.sh` + `bash deploy/tests/grafana-accounts-logic-test.sh` | ✅ | ✅ green |
| 01-10-T3 | 01-10 | 4 | DASH-09, OPS-03 | T-10-01, T-10-07 | LAN/VPN-only Traefik template; complete setup guide | static | `build/lint.sh repo-rules secrets` | ✅ | ✅ green |
| 01-11-T2 | 01-11 | 5 | SEC-08, SEC-10 | T-11-01..04 | GitHub protections confirmed read-only | live check | `build/check-github-settings.sh` | ✅ | ✅ green |
| 01-12-T2 | 01-12 | 6 | SEC-07 | T-12-01 | published release verifies offline without credentials; modified copy refused | live check | `build/verify-published-release.sh v0.1.0` | ✅ | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `Ledger.slnx` + Domain / Repository / Service / UnitTests / IntegrationTests projects (01-01-T2)
- [x] `Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs` against the local PostgreSQL container via `dotnet user-secrets` (01-01-T2, after the 01-01-T1 checkpoint)
- [x] `build/lint.sh` with digest-pinned actionlint, zizmor, shellcheck, gitleaks and `.github/workflows/ci.yml` with a PostgreSQL service container (01-02)
- [x] `deploy/tests/verify-rejects-tampered-artifact-network-test.sh` (01-04-T1)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions | Recorded |
|----------|-------------|------------|-------------------|----------|
| App, PostgreSQL, Prometheus and Grafana all `active (running)` on the LXC after provisioning and first deploy | OPS-03 | Requires the real homelab LXC | `ledger-selfcheck --grafana-admin --restart-check` on the host | 2026-09-28: 57 checks, 0 failures on v0.1.1 (01-12 summary) |
| Encrypted backup actually restored following the documented procedure | OPS-04 | Must be performed once for real, not scripted | `ledger-restore --drill` with the identity on stdin | 2026-09-28: drill PASS, scratch database dropped, no identity on disk (01-12 summary) |
| Grafana, Prometheus and REST unreachable from the internet; REST without credentials rejected on LAN | DASH-09 | External network vantage point needed | From mobile data with the VPN off, and from the LAN with and without an API key | 2026-09-28: no answer from outside; 401 without or with a revoked key, 200 with a valid one (01-12 summary) |
| Tag push pauses for deploy-environment approval, then LXC pulls and runs the release | SEC-08 / OPS-03 | Requires real GitHub environment + LXC | Tag, approve, poll, check `current` and health | 2026-09-28: v0.1.0 and v0.1.1 (01-12 summary) |
| Each partner signs in with their own Grafana viewer login | DASH-08 | Real accounts on the live instance | Sign in as each viewer from LAN/VPN; confirm Viewer role only | 2026-09-28: all accounts signed in; selfcheck confirms Viewer-only (01-11/01-12 summaries) |
---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 60s (quick run about 4 s)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated 2026-09-29

---

## Validation Audit 2026-09-29

| Metric | Count |
|--------|-------|
| Gaps found | 4 (all partial: host scripts checked only statically or live) |
| Resolved | 4 |
| Escalated | 0 |

New tests: `deploy/tests/postgresql-config-test.sh` (9 checks), `deploy/tests/restore-logic-test.sh` (38), `deploy/tests/selfcheck-logic-test.sh` (29) and `deploy/tests/grafana-accounts-logic-test.sh` (37). To make the last two testable without root, the selfcheck's provision.conf path and the accounts module's state marker now accept an environment override, defaulting to the real paths. No implementation bug was found by the new tests; each was mutation-checked where a recent fix was involved (prefix-sharing proxy address, passwords in process arguments).
