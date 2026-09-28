---
phase: 01-secure-platform-release-pipeline
plan: 05
subsystem: infra
tags: [bash, age, postgresql, pg_dump, pg_restore, systemd, prometheus-textfile]

requires:
  - phase: 01-secure-platform-release-pipeline
    provides: "deploy/lib/common.sh (ledger_log, ledger_die, ledger_write_textfile_metrics), deploy/sql/bootstrap-database.sql, roles ledger_backup/ledger_migrator, table data_protection_canary, __EFMigrationsHistory, and the installer's own systemctl start --wait ledger-backup@pre-migration.service call, from plans 01 and 04"
provides:
  - "ledger-backup nightly|pre-migration: encrypted (age, public-key-only), GFS-pruned (7 daily/4 weekly/12 monthly + newest pre-migration), freshness/failure metrics"
  - "ledger-restore --drill|--live --backup FILE --identity FILE|/dev/stdin: scratch-database drill with pass/fail report and freshness metric, live restore with typed confirmation and kept-not-dropped previous database"
  - "deploy/lib/backup.sh: ledger_backup_validate_recipients, ledger_backup_filename, ledger_backup_select_deletions"
  - "docs/backup-restore.md operator guide"
affects: [go-live provisioning/drill plan, any later plan touching backup/restore or the recipients file]

actuals:
  tokens: 7618
  tasks: 2
  commits: 2

tech-stack:
  added: [age (asymmetric backup encryption), grandfather-father-son retention over bash associative arrays]
  patterns:
    - "Recipient/identity validation is format-only (age1... bech32 shape, AGE-SECRET-KEY-1 identity rejection) — no age binary needed to prove the logic, so the offline test stubs pg_dump/age on PATH instead of requiring a real database or real encryption"
    - "GFS retention evaluated as a single per-name elif chain (day, then week, then month, then the one newest pre-migration name) over records sorted newest-first, so day/week/month buckets never double-count a name once an earlier (newer) name has already claimed that bucket"
    - "Backup failure metrics preserve the previous success timestamp from a state file, so a stale-vs-never-succeeded distinction survives repeated failed runs"

key-files:
  created:
    - deploy/bin/ledger-backup
    - deploy/bin/ledger-restore
    - deploy/lib/backup.sh
    - deploy/systemd/ledger-backup@.service
    - deploy/systemd/ledger-backup.timer
    - deploy/tests/backup-logic-test.sh
    - docs/backup-restore.md
  modified: []

key-decisions:
  - "GFS bucket assignment is a single sequential elif per name (day, else week, else month, else newest-pre-migration) rather than three independently pre-computed top-N sets, exactly matching the plan's literal ordering — this means a name that fails the day-bucket (because a newer name already claimed its day) can still win the week or month bucket on its own merits"
  - "The pre-migration special case is scoped to the single newest pre-migration name found during the same newest-first traversal used for day/week/month, not to 'first pre-migration name encountered per traversal position' — so if the newest pre-migration entry already independently wins a day/week/month slot, no second name is forced to be kept in its place"
  - "ledger-restore always drops the scratch drill database and warns about an on-disk identity via a single EXIT trap registered immediately after argument parsing, rather than inline cleanup calls at the end of each mode function — guarantees cleanup runs even if a `set -e` abort happens mid-drill (e.g. an unexpected admin-query failure)"
  - "Backup end-to-end pipeline behavior (success/failure metrics, size and age-header checks) is proven by stubbing pg_dump and age as fake executables on PATH rather than requiring a real database or a real age binary, per the orchestrator's constraint against running real backups/restores in this environment"

patterns-established:
  - "PATH-stubbed external tools (pg_dump, age) let an offline logic test exercise a script's full success/failure pipeline, including Prometheus textfile metrics content, without any real database or encryption tooling"

requirements-completed: [OPS-04, SEC-03]

coverage:
  - id: D1
    description: "ledger-backup encrypts nightly and pre-migration dumps to public keys only (recipients file validated to contain only age1 keys, never an identity), publishes the result only after both pg_dump and age succeed and the output has a non-zero size with a genuine age header, and never reads the env file, the Data Protection certificate or any identity"
    requirement: "SEC-03"
    verification:
      - kind: unit
        ref: "deploy/tests/backup-logic-test.sh (recipient validation + end-to-end pipeline success/failure cases)"
        status: pass
      - kind: other
        ref: "acceptance-criteria greps: pipefail>=1, --recipients-file==1, secrets-grep empty, User=ledger_backup==1, Persistent=true==1, no planning references"
        status: pass
    human_judgment: false
  - id: D2
    description: "Grandfather-father-son retention keeps exactly the newest backup per day (7 newest days), per ISO week (4 newest weeks), per month (12 newest months) and the single newest pre-migration backup, deletes every other matching name, never touches a non-matching name, and is independent of input order"
    requirement: "OPS-04"
    verification:
      - kind: unit
        ref: "deploy/tests/backup-logic-test.sh (403-name synthetic retention test: exact kept count, notes.txt exclusion, newest/older pre-migration behavior, newest-nightly retention, shuffle-order determinism)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Every backup run atomically writes the three ledger_backup_ Prometheus textfile gauges with a reason label, and a failed run keeps the previous success timestamp rather than overwriting it"
    requirement: "OPS-04"
    verification:
      - kind: unit
        ref: "deploy/tests/backup-logic-test.sh (success/failure metrics content and timestamp-preservation cases)"
        status: pass
    human_judgment: false
  - id: D4
    description: "ledger-restore --drill decrypts a chosen backup with an operator-supplied identity, restores into a scratch database, compares migration history/canary row/table counts against the live database, reports pass or fail, records a freshness metric on success, and always drops the scratch database"
    requirement: "OPS-04"
    verification:
      - kind: other
        ref: "shellcheck -x (koalaman/shellcheck:v0.10.0) clean; bash -n syntax check; acceptance-criteria greps (bootstrap-database.sql>=1, pg_restore>=2, no cp/mv/install near 'identity', no planning references)"
        status: pass
    human_judgment: true
    rationale: "The drill's actual restore behavior against a real database, real age-encrypted backup and real migration history can only be proven end-to-end against a provisioned PostgreSQL instance — explicitly out of scope for this plan per its own <verification> section, which defers the real drill to the go-live plan with the operator's real identity"
  - id: D5
    description: "ledger-restore --live requires typed confirmation, stops the app, keeps the previous database under a timestamped name instead of dropping it, recreates ledger via the release's own bootstrap-database.sql, restores as ledger_migrator, and health-checks the app"
    requirement: "OPS-04"
    verification:
      - kind: other
        ref: "shellcheck -x clean; bash -n syntax check; acceptance-criteria greps"
        status: pass
    human_judgment: true
    rationale: "Live restore against a real database and a real running app instance is inherently a go-live-time operation, explicitly deferred by this plan's own <verification> section"
  - id: D6
    description: "docs/backup-restore.md documents age key generation and password-manager custody, what a backup does and does not contain, the drill and live restore steps, and the accepted local-disk risk, in plain language with no planning references or real hostnames/IPs"
    requirement: "OPS-04"
    verification:
      - kind: other
        ref: "acceptance-criteria greps: age-keygen present, password manager mentioned >=2x, same-disk/accepted-risk mentioned >=1x, no planning references, no private IP literals"
        status: pass
    human_judgment: false

duration: ~40min
completed: 2026-09-27
status: complete
---

# Phase 01 Plan 05: Encrypted Local Backups and Restore Drill Summary

Nightly and pre-migration `pg_dump` streams straight into `age` public-key encryption with no server-side decryption capability, pruned on a 7-daily/4-weekly/12-monthly-plus-newest-pre-migration grandfather-father-son schedule, exported as Prometheus textfile freshness/failure metrics, and restorable via a scratch-database drill or a confirmed live restore documented step by step for an operator with the private key in a password manager.

## Performance

- **Duration:** ~40 min
- **Tasks:** 2 completed
- **Files created:** 7

## Accomplishments

- `ledger-backup {nightly|pre-migration}` refuses to run against a recipients file containing anything other than `age1...` public keys (rejecting anything that looks like a private identity), and only publishes the final `.dump.age` file after checking both `pg_dump` and `age` exit statuses, a non-zero size, and a genuine age header — proven end-to-end against stubbed `pg_dump`/`age` executables so no real database or encryption tooling was needed
- Grandfather-father-son retention (`ledger_backup_select_deletions`) is proven against a 403-name synthetic dataset (400 consecutive nightly days plus 3 pre-migration backups far outside that range): exactly 24 names are kept (7 daily + 4 weekly + 12 monthly + 1 pre-migration), the result is unchanged when the input is shuffled, and a non-matching name (`notes.txt`) is never touched
- Backup failure preserves the previous success timestamp in the Prometheus textfile metrics rather than overwriting it, so a "stale" alert can still distinguish "never succeeded" from "succeeded a while ago, now failing"
- `ledger-restore --drill` restores into a throwaway `ledger_restore_drill` database, compares `__EFMigrationsHistory`, the `data_protection_canary` row count and per-schema table counts against the live database, and always drops the scratch database and warns about an on-disk identity via a single `EXIT` trap regardless of how the script exits
- `ledger-restore --live` requires the operator to type `ledger`, stops the app, renames the current database instead of dropping it, recreates `ledger` through the release's own `bootstrap-database.sql`, restores as `ledger_migrator`, and polls `/health` for the plain-text `Healthy` response the app actually returns
- `docs/backup-restore.md` walks an operator through generating and custodying the `age` key pair, what a backup does and does not contain, the drill and live restore procedures (identity supplied only via `/dev/stdin`, never written to disk), and the accepted same-disk risk

## Task Commits

Each task was committed atomically:

1. **Task 1: Encrypted nightly and pre-migration backups with GFS retention and freshness metrics** - `e34bdb4` (feat)
2. **Task 2: Restore drill and live restore, with the backup and restore guide** - `6377da5` (feat)

_Note: this SUMMARY is committed separately by the wave orchestrator (worktree mode); STATE.md and ROADMAP.md are updated centrally after the wave completes._

## Files Created/Modified

- `deploy/lib/backup.sh` - Recipient validation, filename shaping, and GFS retention selection, shared by both backup and restore scripts
- `deploy/bin/ledger-backup` - Encrypts, verifies, publishes and prunes a nightly or pre-migration dump; writes success/failure metrics
- `deploy/bin/ledger-restore` - Scratch-database drill with a pass/fail report, and a confirmed live restore that keeps the previous database
- `deploy/systemd/ledger-backup@.service` - Sandboxed oneshot unit, `User=ledger_backup`, read-write only to the backup directory and the textfile metrics directory
- `deploy/systemd/ledger-backup.timer` - Nightly trigger for `ledger-backup@nightly.service`, `Persistent=true`
- `deploy/tests/backup-logic-test.sh` - Offline test: recipient validation, filename shaping, 403-name retention selection, and the full backup pipeline via stubbed `pg_dump`/`age`
- `docs/backup-restore.md` - Operator guide: key setup, backup contents, drill and live restore, accepted risk

## Decisions Made

- GFS retention is a single sequential elif chain per name (day, else week, else month, else the newest pre-migration name), matching the plan's literal wording, rather than three independently pre-computed top-N sets — this lets a name that loses its own day-bucket slot to a newer same-day name still win a week or month slot on its own merits.
- `ledger-restore`'s cleanup (dropping the scratch database, warning about an on-disk identity) runs from a single `EXIT` trap registered immediately after argument parsing, so it fires even if a `set -e` abort happens mid-drill, rather than relying on cleanup calls placed at the end of each mode function.
- The offline test proves `ledger-backup`'s full success/failure pipeline — including the actual Prometheus textfile metrics content — by stubbing `pg_dump` and `age` as fake executables on `PATH`, rather than needing a real database or a real `age` binary, consistent with the orchestrator's constraint against running real backups in this environment.

## Deviations from Plan

None — plan executed exactly as written. Both tasks' `<action>`, `<behavior>` and acceptance criteria were implemented and verified directly; no auto-fixes, architectural questions, or scope additions were needed.

## Issues Encountered

None.

## User Setup Required

None for this plan's own scope. The operator still needs to generate the `age` key pair on their own workstation and populate `/etc/ledger/backup-recipients.txt` with the public key before backups can run for real — this is documented step by step in `docs/backup-restore.md` and is expected to happen during LXC go-live provisioning, not as part of this plan.

## Known Stubs

None. Every function, script and systemd unit listed in the plan's `must_haves` is implemented and covered by the offline logic test (recipient validation, filename shaping, retention selection, full backup pipeline success/failure) or by shellcheck/syntax/acceptance-criteria checks for `ledger-restore`. The real restore drill against a provisioned LXC, a real PostgreSQL instance and the operator's actual `age` identity is explicitly out of scope for this plan — it happens in the go-live plan, per this plan's own `<verification>` section.

## Next Phase Readiness

- `deploy/bin/ledger-backup`, `deploy/lib/backup.sh` and the two systemd units are ready to be installed onto the LXC by the provisioning plan, and the installer from plan 01-04 (`systemctl start --wait ledger-backup@pre-migration.service`) now has a real unit to call.
- `deploy/bin/ledger-restore` is ready for the operator's real drill and, if ever needed, a real live restore, once the LXC, PostgreSQL and a real `age` identity exist.
- No blockers for this plan's own scope.

## Self-Check: PASSED

- All 7 declared files confirmed present on disk (`deploy/bin/ledger-backup`, `deploy/bin/ledger-restore`, `deploy/lib/backup.sh`, `deploy/systemd/ledger-backup@.service`, `deploy/systemd/ledger-backup.timer`, `deploy/tests/backup-logic-test.sh`, `docs/backup-restore.md`)
- Commit `e34bdb4` — present in `git log`
- Commit `6377da5` — present in `git log`
- `bash deploy/tests/backup-logic-test.sh` — 22/22 checks passed, re-verified immediately before writing this summary
- shellcheck (`koalaman/shellcheck:v0.10.0 -x`) clean on `deploy/bin/ledger-backup`, `deploy/lib/backup.sh`, `deploy/tests/backup-logic-test.sh` and `deploy/bin/ledger-restore`
- Full `build/lint.sh` (repo-rules, workflows, shell, secrets, script-tests) — all PASS
- Every acceptance-criteria grep for both tasks re-run and confirmed passing

---
*Phase: 01-secure-platform-release-pipeline*
*Completed: 2026-09-27*
