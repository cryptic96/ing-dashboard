---
phase: 01-secure-platform-release-pipeline
verified: 2026-09-28T21:49:57Z
status: human_needed
score: 5/5 roadmap success criteria verified (all with live/host evidence for the portions code alone cannot prove)
behavior_unverified: 0
overrides_applied: 0
human_verification:
  - test: "Cut release v0.1.2 from the current `main` (after merging the open PR with the migration-detection and alert-volume fixes) and observe a deploy that has nothing to migrate."
    expected: "The installer's pre-migration backup and the migrator efbundle run are both skipped, and the deploy still activates and reports healthy."
    why_human: "The migration-detection fix (commit 79e6f1c, reading EF's own `\"MigrationId\"` history column) is proven correct by an offline logic test and by the reporting function returning \"no migrations pending\" against the real v0.1.1 database, but no actual deploy has yet been run end-to-end with that fixed function in a state where it takes the skip branch — only version 0.1.0 (which had pending migrations, the bug era) and 0.1.1 (which incorrectly always saw migrations as pending, pre-fix) have been deployed live. This is explicitly called out as an open item in 01-12-SUMMARY.md's 'User Setup Required' section."
  - test: "Remove the orchestrator's temporary SSH access to the ledger LXC and the reverse proxy."
    expected: "No standing SSH credential for Claude/the orchestrator remains on either host."
    why_human: "Tracked as a pending, major todo (`.planning/todos/pending/2026-09-28-remove-temporary-claude-ssh-access-before-real-bank-data.md`), explicitly scoped to be resolved before Phase 2 (real bank data), not required for Phase 1's own success criteria, but worth surfacing so it isn't lost."
---

# Phase 1: Secure Platform & Release Pipeline Verification Report

**Phase Goal:** A tagged release deploys itself to the homelab LXC through a pipeline that cannot leak secrets. The running platform (app, PostgreSQL, Prometheus and Grafana) is locked down and ready to hold financial data before any real bank data arrives.
**Verified:** 2026-09-28T21:49:57Z
**Status:** human_needed
**Re-verification:** No — initial verification

## Verification Method

This report combines three kinds of evidence:

1. **Commands re-run by this verifier**, in the actual working tree at `HEAD` of `milestone/v1-household-ledger` (99f463e): `dotnet build`, `dotnet test Ledger.UnitTests` (37/37), `dotnet test Ledger.IntegrationTests` against the operator's own local PostgreSQL container (35/35, including the database-role, migration-bundle, Data-Protection-restart and log-redaction tests), `build/lint.sh` (all six checks — repo-rules, workflows, shell, secrets/full-history gitleaks, script-tests, observability/live-Grafana-boot — PASS), the four offline `deploy/tests/*-logic-test.sh` / `render-templates-test.sh` scripts (all PASS, host-guard confirmed no real `systemctl`/`pkexec` call), `build/check-github-settings.sh` against the live repository (11/11 PASS), and `gh run list` / `gh pr list` / `gh release list` against the live repository.
2. **Direct code reading** of the release workflow, installer, bootstrap SQL, Grafana/Prometheus provisioning, firewall template, `Program.cs` auth wiring, and env-file examples.
3. **Human-observed evidence already on record** for behaviors that require the real LXC, the real GitHub environment approval, or an outside network vantage point (restart/redeploy survival, tampered-artifact refusal on the host, restore drill with the operator's private key, LAN/VPN-only reachability, REST auth through the reverse proxy, per-partner Grafana logins, alert/outcome email) — recorded in `01-11-SUMMARY.md` and the "Go-live evidence" table of `01-12-SUMMARY.md`, dated 2026-09-28, observed by the orchestrator over SSH and by the operator. This is treated as evidence, not as a SUMMARY claim taken on faith, because the code that produces each behavior was independently located and read (cited below per truth).

No host command (`provision.sh`, the installer, `systemctl`, `nft`) was run by this verifier, per instruction.

## Goal Achievement

### Observable Truths (mapped from ROADMAP.md Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1a | A pushed semver tag builds on a GitHub-hosted runner and pauses for deploy-environment approval | ✓ VERIFIED | `.github/workflows/release.yml`: `build` job has no `environment:`, `publish` job has `environment: deploy` and `needs: build`; live evidence — v0.1.0 and v0.1.1 both paused for and received operator approval (01-12 evidence table) |
| 1b | After approval, the release is published and the LXC pulls and runs the new version as a systemd service alongside PostgreSQL, Prometheus and Grafana | ✓ VERIFIED | `deploy/systemd/ledger.service`, `ledger-deploy-poll.timer` (5 min) exist and are installed by `deploy/provision.d/40-services.sh`; live evidence — v0.1.0 and v0.1.1 installed, activated, `/health` Healthy, running alongside the other three services (01-12 evidence table, `ledger-selfcheck` 57 checks / 0 failures) |
| 1c | An artifact whose build-provenance attestation fails verification is refused before it is unpacked | ✓ VERIFIED | `deploy/lib/deploy.sh:ledger_verify_attestation` runs `gh attestation verify --bundle` fully offline (env-scrubbed, empty `GH_CONFIG_DIR`) before any unpack step; live evidence — `ledger-deploy verify` on a one-byte-modified copy of the real published release exits 1 with no release directory created, the genuine copy exits 0 (01-12 evidence table); `build/verify-published-release.sh v0.1.0` (workstation-side, credential-free) reproduces the same result and was re-confirmed by this verifier's read of the script, which sources `deploy/lib/deploy.sh`'s own functions rather than reimplementing them |
| 2a | No self-hosted runner exists, no GitHub-executed code runs on the server; the LXC only pulls approved, attested releases from `main` | ✓ VERIFIED | `build/check-github-settings.sh` re-run live by this verifier: "PASS: zero self-hosted runners are registered"; `deploy/lib/deploy.sh:ledger_commit_on_branch` checks the attested commit against `main` via the unauthenticated compare API before activation |
| 2b | Outside-contributor workflow runs wait for approval; release-tag creation is restricted | ✓ VERIFIED | `build/check-github-settings.sh` re-run live: "PASS: approval is required for every outside contributor's workflow run", "PASS: tag ruleset restricts creation, update and deletion of v* tags to the admin role" |
| 2c | CI fails on any third-party action not pinned to a commit SHA and on any untrusted value interpolated into a shell command | ✓ VERIFIED | `build/lint.sh` re-run by this verifier: `workflows` check (zizmor hash-pin policy + expression-in-`run:` detection, with its own passing/failing self-test fixtures) PASS; `release.yml`/`ci.yml` read directly — every `uses:` is a 40-char SHA with a version comment, tag/version values reach shell only via `env:` |
| 2d | A full-history secret scan of the repository passes | ✓ VERIFIED | `build/lint.sh secrets` check re-run by this verifier: gitleaks over full git history plus working tree, PASS; self-test proves a since-removed fake token still fails the scan |
| 3a | The app reaches its own database only through dedicated runtime and migrator roles over the local Unix socket, never as superuser, plus a SELECT-only Grafana reader role | ✓ VERIFIED | `deploy/sql/bootstrap-roles.sql`/`bootstrap-database.sql` read directly (all four roles `NOSUPERUSER NOCREATEDB NOCREATEROLE`); `Ledger.IntegrationTests.Database.DatabaseRoleTests` re-run by this verifier against the operator's real local PostgreSQL — all 4 tests pass, proving `ledger_runtime` gets 42501 on DDL, `grafana_reader` gets 42501 outside `reporting`, no role is superuser; host socket-only listener config (`listen_addresses=''`, peer-only `pg_hba.conf`) is asserted by `deploy/provision.d/30-postgresql.sh`'s own self-check and was confirmed 0-failure on the real LXC by `ledger-selfcheck` (01-11/01-12 evidence) |
| 3b | A deploy with a new EF Core migration applies it automatically with the migrator role, no manual step | ✓ VERIFIED | `Ledger.IntegrationTests.Database.MigrationBundleTests.Packaged_bundle_migrates_a_bootstrapped_database_idempotently` re-run by this verifier — pass; live evidence — v0.1.0 install applied `InitialCreate` and `AddApiKeys` as the `ledger_migrator` OS user (01-12 evidence table) |
| 3c | Secrets live only in the server-side env file; a value encrypted before restart/redeploy still decrypts afterwards | ✓ VERIFIED | `deploy/ledger.env.example`, `deploy/grafana.env.example` contain placeholders only (read directly, RFC 5737/example.com); `CommittedConfigurationTests` (part of the 37 unit tests re-run) assert no secret values in committed `appsettings*.json`; live evidence — Data Protection canary still Healthy after a real service restart and after the v0.1.1 redeploy into a new release directory (01-12 evidence table) |
| 3d | Logs, exception messages and metric labels contain no secrets | ✓ VERIFIED | `Ledger.IntegrationTests.Security.LogRedactionTests` re-run by this verifier (part of the 35 integration tests) — sentinel secrets sent raw/URL-encoded/base64 in headers, query strings, paths, plus a sentinel cert password and connection-string password, appear in none of logs, exceptions, problem-details or `/metrics` |
| 4a | An encrypted backup has actually been restored following the documented procedure | ✓ VERIFIED | `docs/backup-restore.md` read directly; live evidence — `ledger-restore --drill` PASS with the operator's key supplied on stdin, migration history/canary/table counts matched, scratch database dropped, no age identity found on disk afterward (01-12 evidence table) |
| 4b | Every one-time LXC setup step is documented | ✓ VERIFIED | `docs/lxc-setup.md` read directly — orders every step from `pct create` through the restore drill and external reachability test, placeholders only |
| 5a | Each partner signs in to Grafana with their own Viewer login; anonymous/public/snapshot access disabled; datasources provisioned from the repo | ✓ VERIFIED | `deploy/provisioning/grafana/grafana.ini` read directly (anonymous/snapshots/sign-up disabled, `auto_assign_org_role = Viewer`); `build/lint.sh observability` re-run by this verifier boots the real digest-pinned Grafana image against the repo's provisioning and confirms anonymous requests get 401 and both datasources + all 8 alert rules + the contact point load; live evidence — every household Viewer account and the renamed admin signed in, selfcheck confirms every non-admin user is Viewer and the default admin password is rejected (01-12 evidence table) |
| 5b | Grafana, Prometheus and REST unreachable from the internet; REST without credentials rejected even on the home network | ✓ VERIFIED | `deploy/nftables/ledger.nft.in` read directly (default-drop input, only SSH from admin/VPN sources and 5080/3000 from the Traefik address); `Ledger.Service/Program.cs`'s `FallbackPolicy = RequireAuthenticatedUser()` read directly (applies to every mapped endpoint by construction, not per-endpoint `[Authorize]`); live evidence — from mobile data with VPN off neither hostname answers, from the LAN a call without an API key gets 401 and with a valid key gets 200 (01-12 evidence table) |

**Score:** 16/16 truths verified (0 present-but-behavior-unverified)

### Open Item Flagged as Not Yet Fully Closed (does not fail a roadmap success criterion)

**"A deploy that includes a new EF Core migration applies it automatically... with no manual database step"** (Success Criterion 3) is verified — v0.1.0 proved the apply path. The *inverse* path — a deploy with nothing to migrate correctly skipping the pre-migration backup and the migrator efbundle run — is not yet proven on a real release. The bug that made every deploy see all migrations as "pending" (querying `migration_id` instead of EF's own `"MigrationId"` column) is fixed (commit `79e6f1c`, confirmed present in `deploy/lib/deploy.sh:240` by this verifier, and covered by a passing offline logic-test case: "pending migrations: [a,b,c] applied [a,b,c] yields nothing"). `01-12-SUMMARY.md` itself records this as still open: *"Proving that a deploy skips backup and migration when nothing is pending needs the next release (v0.1.2)"* and lists it under "User Setup Required." At verification time, `v0.1.2` has not been tagged; PR #10 (the branch containing this fix, plus the alert-volume cap) is open against `main`, confirmed via `gh pr list`.

**Judgment:** this does not block the phase goal. The roadmap success criterion is about migrations being applied automatically, which is proven; skipping unnecessary work is an operational/cost refinement, not a security or correctness requirement of Phase 1, and the fixed function itself already reports the correct (empty) pending-set against the real v0.1.1 database. It is nonetheless real, human-actionable follow-up work and is surfaced above as a human-verification item rather than silently absorbed into a passing score.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `.github/workflows/release.yml` | Tag-triggered build, attest, draft, environment-gated publish | ✓ VERIFIED | Read directly; matches must-haves in 01-03-PLAN.md exactly (env-only tag, SHA-pinned actions, least-privilege job permissions, no caches) |
| `.github/workflows/ci.yml` | Build/test + lint jobs, digest-pinned PostgreSQL service | ✓ VERIFIED | Read directly; `postgres:18@sha256:...` digest pin, `--locked-mode` restore; `gh run list` shows the latest push/PR runs both green |
| `build/lint.sh` + `build/lint/checks/*.sh` | Single lint entry point, 6 checks | ✓ VERIFIED, WIRED | Re-run: all 6 PASS |
| `build/check-github-settings.sh` | Read-only verification of every GitHub-side control | ✓ VERIFIED, WIRED | Re-run live: 11/11 PASS |
| `deploy/bin/ledger-deploy`, `deploy/lib/deploy.sh` | Poll/install/rollback/verify installer | ✓ VERIFIED | Read directly; offline logic tests (30 cases) PASS; live host evidence for install/rollback-refusal/redeploy |
| `deploy/bin/ledger-backup`, `deploy/bin/ledger-restore`, `deploy/lib/backup.sh` | Encrypted, GFS-retained backups; drill/live restore | ✓ VERIFIED | Re-run offline logic test (21 cases) PASS; live restore drill PASS |
| `Ledger.Domain/Auth/ApiKeyToken.cs`, `Ledger.Service/Auth/ApiKeyAuthenticationHandler.cs`, `Program.cs` FallbackPolicy | Hashed API keys, default-deny auth on every endpoint | ✓ VERIFIED, WIRED | Read directly; live evidence — 401 without key, 401 with a made-up key, 200 with a valid key, 401 after revocation, through the reverse proxy |
| `Ledger.Service/Security/DataProtectionSetup.cs`, `ProductionConfigurationValidator.cs` | Certificate-protected key ring, fail-fast Production config | ✓ VERIFIED, WIRED | Read directly; integration tests re-run PASS; live canary-survives-restart/redeploy evidence |
| `deploy/provisioning/grafana/**`, `deploy/provisioning/prometheus/prometheus.yml` | Hardened Grafana, datasources, 8 alert rules, loopback-only Prometheus | ✓ VERIFIED, WIRED | Read directly; `build/lint.sh observability` boots the real image and confirms |
| `deploy/provision.sh` + `deploy/provision.d/*.sh` | Idempotent provisioning orchestrator | ✓ VERIFIED | Read directly; offline logic tests (`provision-logic-test.sh`, `render-templates-test.sh`) PASS; live host evidence — `ledger-selfcheck --pre-deploy --grafana-admin` 0 failures after provisioning |
| `deploy/nftables/ledger.nft.in` | Default-drop firewall | ✓ VERIFIED | Read directly; offline render test confirms admits only Traefik/SSH sources; live evidence — source outside allow-list gets 403 |
| `docs/{releasing,github-repository-settings,deploy,backup-restore,monitoring,lxc-setup,rest-api}.md` | Operator documentation | ✓ VERIFIED | All present, read; no personal data or planning references found in a scan of the go-live SUMMARY evidence tables |

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| `build/check-github-settings.sh` | `docs/github-repository-settings.md` | checks exactly the controls the document lists | ✓ WIRED | Confirmed by reading both; live run of the script matches every documented control |
| `build/verify-published-release.sh` | `deploy/lib/deploy.sh` | sources the installer's verification functions instead of re-implementing them | ✓ WIRED | Confirmed by reading `build/verify-published-release.sh`'s `source` line against `deploy/lib/deploy.sh` |
| `Program.cs` | `ApiKeyAuthenticationHandler` | `AddAuthentication(...).AddScheme<...>` + `FallbackPolicy.RequireAuthenticatedUser()` | ✓ WIRED | Read directly; live 401/200 evidence through the reverse proxy confirms end-to-end |
| `deploy/provision.d/40-services.sh` | `deploy/provisioning/grafana/**`, `deploy/provisioning/prometheus/**` | installs initial provisioning files, installer re-installs `grafana.ini` on every deploy | ✓ WIRED | Read directly; `01-08-PLAN.md`'s backstop truth ("grafana.ini... re-installed on every deploy so a UI change cannot durably re-enable anonymous access") — real-host evidence in 01-11 shows the module runs its config-render/restart step on every provisioning run |

### Data-Flow Trace

Not applicable in the traditional (UI-renders-DB-data) sense — this phase has no dashboards or user-facing views yet (`DASH-06`'s dashboards provider is intentionally empty, reserved for Phase 2+). The equivalent "data flow" here is the deploy/backup/auth pipeline, traced above through Key Link Verification and the truths table.

### Behavioral Spot-Checks / Test Suite Execution

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Unit tests | `dotnet test Ledger.UnitTests` | 37/37 passed | ✓ PASS |
| Integration tests (real local PostgreSQL) | `LEDGER_EFBUNDLE=... dotnet test Ledger.IntegrationTests` | 35/35 passed | ✓ PASS |
| Lint (all 6 checks incl. full-history gitleaks, live Grafana boot) | `build/lint.sh` | 6/6 PASS | ✓ PASS |
| Deploy/backup/provisioning offline logic | `bash deploy/tests/{backup-logic,ledger-deploy-logic,provision-logic,render-templates}-test.sh` | 4/4 scripts, all cases PASS, host-guard confirms no real `systemctl`/`pkexec` reached | ✓ PASS |
| GitHub repository controls (live) | `build/check-github-settings.sh` | 11/11 PASS | ✓ PASS |
| CI status (live) | `gh run list --branch milestone/v1-household-ledger` | latest push and PR runs both `success` | ✓ PASS |
| Network-dependent offline tests | `deploy/tests/versions-network-test.sh`, `verify-rejects-tampered-artifact-network-test.sh` | not run (require `LEDGER_LINT_NETWORK=1`, external network access) | ? SKIP — covered instead by live evidence in 01-12-SUMMARY.md (`build/verify-published-release.sh v0.1.0` observed passing all 7 steps) |

### Probe Execution

Not applicable — this project defines no `scripts/*/tests/probe-*.sh` convention; its equivalent runnable-check surface is `build/lint.sh` and `deploy/tests/*.sh`, both executed above.

### Requirements Coverage

| Requirement | Source Plan(s) | Status | Evidence |
|---|---|---|---|
| SEC-02 | 01-01, 01-09, 01-10 | ✓ SATISFIED | Non-superuser roles (bootstrap SQL), `DatabaseRoleTests` pass, live selfcheck confirms peer mapping |
| SEC-03 | 01-01, 01-05, 01-06, 01-07, 01-09 | ✓ SATISFIED | `ledger.env.example` placeholders only; canary encryption survives restart/redeploy (live) |
| SEC-05 | 01-01, 01-07, 01-09, 01-10 | ✓ SATISFIED | Socket-only Postgres config + live selfcheck 0 failures |
| SEC-06 | 01-04, 01-06, 01-07, 01-08 | ✓ SATISFIED | `LogRedactionTests` pass; alert rule titles carry no financial data (read directly) |
| SEC-07 | 01-03, 01-04, 01-12 | ✓ SATISFIED | Attestation step in `release.yml`; offline verify in `deploy.sh`; live tampered-copy refusal |
| SEC-08 | 01-02, 01-03, 01-04, 01-10, 01-11, 01-12 | ✓ SATISFIED | `check-github-settings.sh` live 11/11; zero runners; deploy environment gate exercised twice live |
| SEC-09 | 01-02, 01-03 | ✓ SATISFIED | `build/lint.sh workflows` (zizmor hash-pin) PASS live |
| SEC-10 | 01-02, 01-11 | ✓ SATISFIED | `build/lint.sh secrets` (full-history gitleaks) PASS live; `check-github-settings.sh` confirms push protection/secret scanning enabled live |
| OPS-03 | 01-04, 01-09, 01-10, 01-11, 01-12 | ✓ SATISFIED | Systemd units read directly; live selfcheck confirms all four services running |
| OPS-04 | 01-05, 01-12 | ✓ SATISFIED | Live restore drill PASS; `docs/backup-restore.md` |
| OPS-05 | 01-01, 01-07, 01-12 | ✓ SATISFIED | Canary healthy after live restart and redeploy |
| OPS-07 | 01-01, 01-04, 01-12 | ✓ SATISFIED | `MigrationBundleTests` pass; live v0.1.0 migration applied as migrator role |
| API-02 | 01-06, 01-12 | ✓ SATISFIED | `FallbackPolicy`; live 401/200/401-after-revoke through the reverse proxy |
| DASH-06 | 01-08 | ✓ SATISFIED | Datasources/alerting/dashboards-provider all provisioned from the repo, confirmed by the live Grafana-boot lint check |
| DASH-08 | 01-08, 01-10, 01-11, 01-12 | ✓ SATISFIED | `grafana.ini` hardening; live per-partner Viewer sign-ins, default admin password rejected |
| DASH-09 | 01-10, 01-11, 01-12 | ✓ SATISFIED | nftables template + Traefik allow-list; live unreachable-from-outside-VPN and 403-outside-allow-list evidence |

No orphaned requirements: every ID in the phase's declared requirement set (SEC-02, SEC-03, SEC-05, SEC-06, SEC-07, SEC-08, SEC-09, SEC-10, OPS-03, OPS-04, OPS-05, OPS-07, API-02, DASH-06, DASH-08, DASH-09) is claimed by at least one plan's frontmatter and is marked "Complete" in `.planning/REQUIREMENTS.md`'s traceability table, consistent with this verifier's own findings.

### Anti-Patterns Found

A scan of every file created/modified across all 12 plans (Ledger.* projects, `deploy/`, `build/`, `.github/`) for `TBD`/`FIXME`/`XXX`/`TODO`/`HACK`/`PLACEHOLDER` found no debt markers outside two `mktemp ... XXXXXX` template strings (not comments). Two todos exist and are correctly captured in `.planning/todos/pending/` per the project's "no planning references outside `.planning/`" rule, not left as inline code comments:
- Remove temporary Claude/orchestrator SSH access before real bank data (major, surfaced above as a human-verification item)
- Migrate tests to Microsoft.Testing.Platform for xunit v4 (minor, explicitly deferred post-go-live)

No blockers found.

### Process Note (not a phase-goal gap)

`.planning/phases/01-secure-platform-release-pipeline/01-VALIDATION.md` is still frontmatter `status: draft`, `nyquist_compliant: false`, with every per-task verification row marked `⬜ pending` and the sign-off checklist unchecked. This looks stale relative to the actual execution record (every referenced automated command was in fact run repeatedly across the 12 plans per their SUMMARYs, and this verifier independently re-ran the equivalent commands and got the expected results). This is a planning-artifact bookkeeping gap, not evidence that the underlying checks didn't happen — but it is noted so the record can be reconciled.

### Human Verification Required

### 1. Cut v0.1.2 and confirm the migration-skip path

**Test:** Merge PR #10 (Grafana mail cap, migration-detection fix, test-isolation fix) to `main`, tag `v0.1.2`, approve the deploy, and let the LXC install it.
**Expected:** The installer's pre-migration backup and migrator efbundle run are both skipped (nothing pending), and the deploy still activates and reports healthy.
**Why human:** Requires cutting a real release and observing the live host; the underlying function is already proven correct by an offline test and by its own report against the real database, but the end-to-end skip path itself has not yet been exercised on a real deploy. Explicitly flagged as open in `01-12-SUMMARY.md`.

### 2. Remove temporary orchestrator SSH access

**Test:** Confirm no SSH credential for the orchestrator/Claude remains on the ledger LXC or the reverse proxy.
**Expected:** Access removed, per the operator's own local notes referenced in `01-11-SUMMARY.md`.
**Why human:** Host-side credential removal; tracked as a pending major todo, scoped to be done before Phase 2 (real bank data), not a Phase 1 success-criterion blocker.

### Gaps Summary

No gaps were found against any of the phase's 5 roadmap success criteria or its 16 requirement IDs — every truth resolved to VERIFIED with a mix of re-run automated tests/checks (unit, integration, lint, offline deploy logic, live GitHub-settings check, live CI status) and previously-recorded, code-traceable human observation on the real LXC and GitHub environment. Two items are surfaced for human attention rather than treated as blockers: proving the migration-skip path on an actual v0.1.2 release, and closing out the temporary SSH-access todo before Phase 2. Neither maps to a failed roadmap success criterion or requirement.

---

*Verified: 2026-09-28T21:49:57Z*
*Verifier: Claude (gsd-verifier)*
