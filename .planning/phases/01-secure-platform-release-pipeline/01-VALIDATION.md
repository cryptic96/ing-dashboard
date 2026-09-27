---
phase: 1
slug: secure-platform-release-pipeline
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-09-27
---

# Phase 1 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 + FluentAssertions + NSubstitute; `Microsoft.AspNetCore.Mvc.Testing` for in-process integration; zizmor / actionlint / gitleaks for CI static checks; bash smoke scripts under `deploy/tests/` |
| **Config file** | none — Wave 0 installs (`Ledger.slnx`, `Ledger.UnitTests`, `Ledger.IntegrationTests`, `Directory.Build.props`) |
| **Quick run command** | `dotnet test Ledger.UnitTests` |
| **Full suite command** | `dotnet test` (needs the user's local PostgreSQL container via `dotnet user-secrets`; CI uses a PostgreSQL service container) |
| **Estimated runtime** | ~60 seconds |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test Ledger.UnitTests`
- **After every plan wave:** Run `dotnet test` plus the CI security-lint job (zizmor, actionlint, gitleaks full history)
- **Before `/gsd-verify-work`:** Full suite green, CI security-lint green, and the manual checkpoints below actually performed
- **Max feedback latency:** 60 seconds

---

## Per-Task Verification Map

Requirement-level map seeded from research; the planner refines Task IDs per plan.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | TBD | TBD | SEC-02 | — | runtime role cannot run DDL; reader role SELECT-only on `reporting` | integration | `dotnet test Ledger.IntegrationTests --filter Category=DatabaseRoles` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-03 | — | Data Protection round-trip; no secrets in committed `appsettings*.json` | unit + CI grep | `dotnet test --filter Category=DataProtection` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-05 | — | connection string uses socket path only, no TCP host:port | unit | `dotnet test --filter Category=Configuration` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-06 | — | exception paths never log connection strings / keys | integration | `dotnet test --filter Category=LogRedaction` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-07 | — | tampered artifact fails offline `gh attestation verify --bundle`; installer refuses | smoke | `deploy/tests/verify-rejects-tampered-artifact.sh` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-08 | — | no `runs-on: self-hosted`; publish gated by deploy environment | static (CI) | `zizmor .github/workflows/` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-09 | — | all third-party actions SHA-pinned; no template injection | static (CI) | `zizmor` + `actionlint` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | SEC-10 | — | full history free of secret-shaped strings | CI | `gitleaks detect --source . --redact --exit-code 2` (fetch-depth 0) | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | OPS-05 | — | canary encrypted before restart/redeploy decrypts after | integration | `dotnet test --filter Category=DataProtectionRestart` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | OPS-07 | — | migration bundle applies pending migration as migrator role | integration | `dotnet test Ledger.IntegrationTests --filter Category=Migrations` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | API-02 | — | missing/invalid API key → 401 on every endpoint | integration | `dotnet test --filter Category=ApiAuth` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DASH-06 | — | provisioning YAML valid | static (CI) | YAML lint step in `ci.yml` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DASH-08 | — | anonymous, snapshots, public dashboards disabled in provisioned `grafana.ini` | static | assert step in `ci.yml` or unit test over template | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `Ledger.slnx` + Domain / Repository / Service / UnitTests / IntegrationTests projects
- [ ] `Ledger.IntegrationTests/DatabaseFixture.cs` — shared fixture against the local PostgreSQL container via `dotnet user-secrets`
- [ ] `.github/workflows/ci.yml` with SHA-pinned zizmor, actionlint, gitleaks (full history) steps and a PostgreSQL service container
- [ ] `deploy/tests/verify-rejects-tampered-artifact.sh`

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| App, PostgreSQL, Prometheus and Grafana all `active (running)` on the LXC after provisioning and first deploy | OPS-03 | Requires the real homelab LXC | `systemctl status ledger postgresql grafana-server prometheus` per the LXC setup doc |
| Encrypted backup actually restored following the documented procedure | OPS-04 | Must be performed once for real, not scripted | Follow the restore drill in the LXC setup doc; record date and outcome |
| Grafana, Prometheus and REST unreachable from the internet; REST without credentials rejected on LAN | DASH-09 | External network vantage point needed | curl from outside LAN/VPN (expect no response) and from LAN without API key (expect 401) |
| Tag push pauses for deploy-environment approval, then LXC pulls and runs the release | SEC-08 / OPS-03 | Requires real GitHub environment + LXC | Push a semver tag, approve, confirm timer pulls and service restarts |
| Each partner signs in with their own Grafana viewer login | DASH-08 | Real accounts on the live instance | Sign in as each viewer from LAN/VPN; confirm Viewer role only |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
