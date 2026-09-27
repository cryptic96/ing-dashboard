---
phase: 01-secure-platform-release-pipeline
plan: 04
subsystem: infra
tags: [bash, systemd, sigstore, gh-cli, prometheus-textfile, postgresql, grafana, deploy]

requires:
  - phase: 01-secure-platform-release-pipeline
    provides: "Release package contract (zip root app/, efbundle, release-manifest.json, deploy/ tree) and the ops endpoint's /health and /metrics shape, from plan 01"
provides:
  - "Root installer (deploy/bin/ledger-deploy) with poll, install, rollback and verify subcommands"
  - "Offline gh attestation verify --bundle flow with no GitHub credential ever read or used"
  - "Migration-gated activation: pre-migration backup, migrator-role efbundle run, atomic symlink swap, automatic rollback unless a migration ran"
  - "Deploy textfile metrics (ledger_deploy.prom) and secret-free notification emails"
  - "ledger.service, ledger-deploy-poll.service/.timer systemd units"
  - "docs/deploy.md operator documentation"
affects: [01-05, 01-08, 01-10, 01-12]

actuals:
  tokens: 19128
  tasks: 2
  commits: 2

tech-stack:
  added: [gh-cli-attestation-verify, sigstore-bundle-offline-verification, prometheus-node-exporter-textfile-collector]
  patterns:
    - "Config files are parsed line-by-line with printf -v against an allow-list, never sourced"
    - "Test-only LEDGER_DEPLOY_ROOT prefix relocates every filesystem path without changing what is verified; ownership checks compare against the effective UID under that prefix instead of hard-coded root"
    - "Shared Prometheus textfile metrics are merged per-metric-name (not overwritten wholesale) so poll and install, run as separate process invocations, can each update their own subset safely"
    - "Bash test scripts that call functions expected to exit non-zero run them in a subshell so ledger_die's exit only ends the subshell, not the test"

key-files:
  created:
    - deploy/bin/ledger-deploy
    - deploy/lib/common.sh
    - deploy/lib/deploy.sh
    - deploy/deploy.conf.example
    - deploy/systemd/ledger.service
    - deploy/systemd/ledger-deploy-poll.service
    - deploy/systemd/ledger-deploy-poll.timer
    - deploy/tests/fixtures/public-attested-artifact.env
    - deploy/tests/fixtures/public-attested-artifact.sigstore.jsonl
    - deploy/tests/verify-rejects-tampered-artifact-network-test.sh
    - deploy/tests/ledger-deploy-logic-test.sh
    - docs/deploy.md
  modified:
    - .gitignore

key-decisions:
  - "Carved .gitignore exceptions for deploy/bin/ (otherwise caught by the blanket bin/ build-output ignore) and deploy/tests/fixtures/*.env (otherwise caught by the blanket *.env secrets ignore) — both are legitimate, non-secret, non-build-output committed content this plan's files_modified explicitly requires"
  - "ledger_load_conf's ownership check compares against the effective UID, which is root in production but becomes the test-runner's own UID whenever LEDGER_DEPLOY_ROOT is set — preserves the real security property (only a privileged owner's config is trusted) while staying testable without root"
  - "Added a test-only LEDGER_POLL_LATEST_URL override (defaults to the real GitHub releases/latest URL) so poll's newer-vs-same-version behavior is provable against a local file:// fixture instead of real network state"

requirements-completed: [SEC-07, SEC-08, OPS-03, OPS-07, SEC-06]

coverage:
  - id: D1
    description: "Offline gh attestation verify --bundle refuses a tampered artifact, a mismatched repo, signer workflow or source ref, before anything is unpacked, with no GitHub token ever read"
    requirement: "SEC-07"
    verification:
      - kind: integration
        ref: "deploy/tests/verify-rejects-tampered-artifact-network-test.sh"
        status: pass
    human_judgment: false
  - id: D2
    description: "The attested commit must be identical to or behind main via the unauthenticated compare API; the installer never sends an Authorization header or holds a GitHub credential"
    requirement: "SEC-08"
    verification:
      - kind: integration
        ref: "deploy/tests/verify-rejects-tampered-artifact-network-test.sh"
        status: pass
    human_judgment: false
  - id: D3
    description: "Poll installs only a strictly newer semver tag; downgrades are refused outside the rollback subcommand"
    requirement: "SEC-07"
    verification:
      - kind: integration
        ref: "deploy/tests/verify-rejects-tampered-artifact-network-test.sh"
        status: pass
      - kind: unit
        ref: "deploy/tests/ledger-deploy-logic-test.sh (ledger_semver_gt cases)"
        status: pass
    human_judgment: false
  - id: D4
    description: "Pending migrations are computed correctly, including the unknown-migration refusal when the database is ahead of the release manifest"
    requirement: "OPS-07"
    verification:
      - kind: unit
        ref: "deploy/tests/ledger-deploy-logic-test.sh (ledger_pending_migrations cases)"
        status: pass
    human_judgment: false
  - id: D5
    description: "Release activation atomically repoints the current symlink and records the previous version, leaving no staging directory behind"
    requirement: "OPS-03"
    verification:
      - kind: unit
        ref: "deploy/tests/ledger-deploy-logic-test.sh (ledger_activate_release case)"
        status: pass
    human_judgment: false
  - id: D6
    description: "Rollback-vs-fail-without-rollback decision and rollback's migration-subset refusal"
    requirement: "OPS-07"
    verification:
      - kind: unit
        ref: "deploy/tests/ledger-deploy-logic-test.sh (ledger_rollback_decision, ledger_rollback_release cases)"
        status: pass
    human_judgment: false
  - id: D7
    description: "Old releases beyond the keep count are pruned, never the active or previous release"
    requirement: "OPS-03"
    verification:
      - kind: unit
        ref: "deploy/tests/ledger-deploy-logic-test.sh (ledger_prune_releases case)"
        status: pass
    human_judgment: false
  - id: D8
    description: "Deploy textfile metrics contain one sample per required metric name with the correct version label, and email bodies contain only version/result/timestamps"
    requirement: "SEC-06"
    verification:
      - kind: unit
        ref: "deploy/tests/ledger-deploy-logic-test.sh (metrics and email content cases)"
        status: pass
    human_judgment: false
  - id: D9
    description: "The live end-to-end run against a real LXC, real systemd units and a real database is out of scope for this plan and happens in the go-live plan"
    verification: []
    human_judgment: true
    rationale: "Requires a provisioned LXC, real PostgreSQL/Grafana/Prometheus services and a published GitHub release — explicitly deferred to the go-live plan per this plan's own <verification> section"

duration: ~25min
completed: 2026-09-27
status: complete
---

# Phase 01 Plan 04: Root Installer (Poll, Verify, Migrate, Activate, Roll Back, Report) Summary

Built the root-owned `ledger-deploy` installer and its systemd units: an unauthenticated poll against `releases/latest`, download-and-verify with `gh attestation verify --bundle` run fully offline (tokens unset, empty `GH_CONFIG_DIR`) before anything is unpacked, an attested-commit-on-main check via the unauthenticated compare API, downgrade refusal with an explicit rollback subcommand, pre-migration backup and migrator-role migration, atomic side-by-side release activation with automatic rollback unless a migration ran, Grafana/Prometheus provisioning installation gated on `promtool check config`, health-check polling, and outcome reporting through Prometheus textfile metrics and a secret-free notification email.

## Performance

- **Duration:** ~25 min
- **Tasks:** 2 completed
- **Files created:** 12
- **Files modified:** 1 (`.gitignore`)

## Accomplishments

- A tampered artifact, a mismatched repository, a mismatched signer workflow, or a mismatched source ref is refused before any directory is created, proven against a genuine public attested artifact (`cli/cli` v2.101.0) downloaded fresh over HTTPS and checked against its own recorded checksum
- The installer never holds or sends a GitHub credential: `gh attestation verify` runs with `GH_TOKEN`/`GITHUB_TOKEN`/`GH_ENTERPRISE_TOKEN` unset and a fresh empty `GH_CONFIG_DIR`; the compare-API and releases/latest calls send no `Authorization` header
- A verified release is unpacked to a staging directory, migrated (with a pre-migration backup) as the `ledger_migrator` OS user only when the manifest lists a migration not yet applied, atomically activated via `mv -T`, and automatically rolled back on a failed health check — unless a migration ran, in which case it fails loudly instead
- Deploy outcomes are always reported: one shared `ledger_deploy.prom` textfile carries the last poll and last run metrics (merged per-metric-name so separate poll/install processes never clobber each other's values), and the notification email body contains only version, result and timestamps

## Task Commits

1. **Task 1: Poll, download and offline verification that refuses before unpacking** - `02dbb32` (feat)
2. **Task 2: Migrate, activate, health-check, roll back and report, plus systemd units and docs** - `61125c8` (feat)

## Files Created/Modified

- `deploy/bin/ledger-deploy` - Root installer: library resolution, config loading, locking, and the poll/install/rollback/verify subcommand dispatch
- `deploy/lib/common.sh` - Logging, safe allow-listed config parsing (never sourced), merged textfile-metrics writer, email helper, semver comparison
- `deploy/lib/deploy.sh` - Offline attestation verification, commit-on-branch check, pending-migration computation, atomic activation, rollback decision, release pruning, health-wait, provisioning install, and the full install/rollback orchestration
- `deploy/deploy.conf.example` - Every allowed config key with placeholder values and defaults
- `deploy/systemd/ledger.service` - App unit: `User=ledger`, `EnvironmentFile=/etc/ledger/ledger.env`, full sandboxing except `MemoryDenyWriteExecute` (the .NET JIT needs writable-executable memory)
- `deploy/systemd/ledger-deploy-poll.service` / `.timer` - Oneshot poll unit and its 5-minute timer
- `deploy/tests/fixtures/public-attested-artifact.env` / `.sigstore.jsonl` - Public, non-secret fixture recording a genuinely attested `cli/cli` release used to prove verification end to end
- `deploy/tests/verify-rejects-tampered-artifact-network-test.sh` - Network test: genuine/tampered/mismatched-repo/mismatched-workflow/mismatched-ref cases, commit-on-branch cases, and full-installer tampered-artifact and poll cases
- `deploy/tests/ledger-deploy-logic-test.sh` - Logic test: semver, pending-migrations, activation, rollback decision, pruning, rollback's migration-subset refusal, and metrics/email content — no root, network, systemd or database
- `docs/deploy.md` - Plain-language operator documentation: the pull-based flow, manual commands, verify-only usage, the migration rollback exception, where outcomes appear, and when to re-run provisioning
- `.gitignore` - Carved narrow exceptions for `deploy/bin/` and `deploy/tests/fixtures/*.env`

## Decisions Made

- `.gitignore`'s blanket `bin/` (build output) and `*.env` (secrets) rules would have silently excluded two paths this plan's `files_modified` explicitly requires (`deploy/bin/ledger-deploy`, the public fixture env file). Added narrow negation exceptions rather than renaming the installer's own conventional `bin/` directory or the fixture's conventional `.env` extension.
- `ledger_load_conf`'s ownership check targets the effective UID (root in production, the test-runner's own UID whenever `LEDGER_DEPLOY_ROOT` is set) rather than a hard-coded UID 0, since the installer must run as root in production but the network and logic tests run unprivileged — this keeps the same real check (config must be owned by whoever is running the installer, and never group/world-writable) testable without root.
- Added a test-only `LEDGER_POLL_LATEST_URL` override (defaults to the real `releases/latest` URL) purely so poll's "same version does nothing / newer version installs" behavior could be proven against a local fixture instead of live GitHub release state.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `.gitignore` silently excluded two plan-required paths**
- **Found during:** Task 1, first attempt to stage `deploy/bin/ledger-deploy` and the fixture `.env` file
- **Issue:** The repository's blanket `bin/` and `*.env` ignore rules (intended for build output and local secrets) also matched this plan's own conventional installer directory name and fixture file extension, silently leaving both untracked after `git add`.
- **Fix:** Added narrow `!deploy/bin/`, `!deploy/bin/**` and `!deploy/tests/fixtures/*.env` negation exceptions immediately after the general rules.
- **Files modified:** `.gitignore`
- **Commit:** `02dbb32`

**2. [Rule 1 - Bug] `curl` without `--location` silently downloaded 0 bytes from a GitHub release asset URL**
- **Found during:** Task 1, first run of the network test
- **Issue:** GitHub release asset download URLs 302-redirect to a signed Azure Blob URL; `curl --fail --silent --show-error` without `--location` follows no redirect and writes an empty file, which then fails the checksum check.
- **Fix:** Added `--location` to both the network test's own download and `download_release_asset` in `deploy/bin/ledger-deploy`.
- **Files modified:** `deploy/bin/ledger-deploy`, `deploy/tests/verify-rejects-tampered-artifact-network-test.sh`
- **Commit:** `02dbb32`

**3. [Rule 1 - Bug] shellcheck findings: `A && B || C` idiom, an unreachable double-return guard, and unresolved `source=` directives**
- **Found during:** Task 1, first shellcheck run
- **Issue:** `[ -n "$artifact" ] && [ -n "$bundle" ] && [ -n "$tag" ] || { ... }` triggers SC2015 (the else-branch can run when the first two tests are true and the die-block itself fails); `return 0 2>/dev/null || exit 0` double-sourcing guards triggered SC2317 (unreachable); and `# shellcheck source=/dev/null` directives on lib/fixture sources triggered SC2153 false-positive "misspelling" warnings since shellcheck could no longer see the real variable definitions.
- **Fix:** Replaced the `&&`/`||` idiom with an explicit `if`; simplified the guards to a plain `return 0`; pointed every `source=` directive at the real relative path so shellcheck can follow it.
- **Files modified:** `deploy/bin/ledger-deploy`, `deploy/lib/common.sh`, `deploy/lib/deploy.sh`, `deploy/tests/verify-rejects-tampered-artifact-network-test.sh`
- **Commit:** `02dbb32`

**4. [Rule 1 - Bug] Test script hung/crashed on functions that call `ledger_die`**
- **Found during:** Task 2, first run of the logic test
- **Issue:** `ledger_die` calls `exit 1` (correct for the real installer process). The logic test sources the libraries directly rather than exec'ing a subprocess, so calling a function expected to hit `ledger_die` (the rollback migration-subset-refusal case) terminated the entire test script immediately after that line, silently skipping every remaining check.
- **Fix:** Wrapped that specific call in a `( ... )` subshell, so `exit` only ends the subshell and the test script continues with the captured exit code.
- **Files modified:** `deploy/tests/ledger-deploy-logic-test.sh`
- **Commit:** `61125c8`

**5. [Rule 1 - Bug] shellcheck SC2034 on nameref output variables that were never read**
- **Found during:** Task 2, shellcheck run after adding `ledger_install_provisioning`
- **Issue:** The rollback path in `ledger_install_verified_release` populated `rollback_grafana`/`rollback_prometheus` via nameref but never acted on them, unlike the primary install path's equivalent variables.
- **Fix:** Used the values to also restart Grafana/reload Prometheus and pass them into the rollback health-check, matching the primary path's behavior.
- **Files modified:** `deploy/lib/deploy.sh`
- **Commit:** `61125c8`

---

**Total deviations:** 5 auto-fixed (3 Rule 1, 1 Rule 1, 1 Rule 3 — see above)
**Impact on plan:** All fixes were necessary for correctness (broken download, a genuine test-harness bug) or to keep the codebase clean under the plan's own verification tooling (shellcheck, `.gitignore` coverage). No scope creep.

## Issues Encountered

None beyond the deviations documented above.

## User Setup Required

None — no external service configuration required. This plan produces installer code and systemd unit files only; provisioning them onto a real LXC (installing to `/usr/local/sbin`, `/usr/local/lib/ledger`, `/etc/systemd/system`) is a later plan's responsibility per this plan's own interfaces section.

## Known Stubs

None. Every function, subcommand and systemd unit listed in the plan's `must_haves` is implemented and covered by either the network test (offline attestation verification, full-installer tampered-artifact refusal, poll behavior) or the logic test (migration computation, activation, rollback decision, pruning, metrics and email content). The live end-to-end run against a real LXC, database and published GitHub release is explicitly out of scope for this plan — it happens in the go-live plan, as stated in this plan's own `<verification>` section.

## Next Phase Readiness

- `deploy/bin/ledger-deploy`, `deploy/lib/{common,deploy}.sh` and the systemd units are ready to be installed onto the LXC by the provisioning plan (`/usr/local/sbin`, `/usr/local/lib/ledger`, `/etc/systemd/system`, per this plan's interfaces section)
- The backup plan (providing `ledger-backup@.service` instance `pre-migration`) must land before a real migrated deploy can be exercised end to end — the installer already calls `systemctl start --wait ledger-backup@pre-migration.service` and requires it to succeed
- No blockers for this plan's own scope

## Self-Check: PASSED

- All 12 declared files confirmed present on disk (`deploy/bin/ledger-deploy`, `deploy/lib/common.sh`, `deploy/lib/deploy.sh`, `deploy/deploy.conf.example`, `deploy/systemd/{ledger.service,ledger-deploy-poll.service,ledger-deploy-poll.timer}`, `deploy/tests/fixtures/{public-attested-artifact.env,public-attested-artifact.sigstore.jsonl}`, `deploy/tests/{verify-rejects-tampered-artifact-network-test.sh,ledger-deploy-logic-test.sh}`, `docs/deploy.md`)
- Commit `02dbb32` — present in `git log`
- Commit `61125c8` — present in `git log`
- `bash deploy/tests/verify-rejects-tampered-artifact-network-test.sh` — 14/14 checks passed, re-verified immediately before writing this summary
- `bash deploy/tests/ledger-deploy-logic-test.sh` — 29/29 checks passed, re-verified immediately before writing this summary
- shellcheck (`koalaman/shellcheck:v0.10.0 -x`) clean on all four scripts
- Every acceptance-criteria grep for both tasks re-run and confirmed passing

---
*Phase: 01-secure-platform-release-pipeline*
*Completed: 2026-09-27*
