---
phase: 02-automatic-ing-sync
plan: 03
subsystem: infra
tags: [selfcheck, systemd-sandboxing, openssl, secrets-hygiene, deploy]
requires: []
provides:
  - "ledger-selfcheck check_log_secrets, bank key mode check, Europe/Amsterdam zone check"
  - "Sandboxed ledger-deploy-poll.service and ledger-apikey"
  - "ledger-bank-key generate | show-certificate | configure"
  - "Env keys EnableBanking__PrivateKeyPath/PrivateKeyPassword/ApplicationId, Ingestion__Provider, BankLink__RedirectUrl"
affects: [go-live, bank-link, scheduler]
tech-stack:
  added: []
  patterns:
    - "Secret needles reach grep through /dev/fd/3, never argv"
    - "openssl passwords via env:, never argv"
key-files:
  created:
    - deploy/bin/ledger-bank-key
    - deploy/tests/bank-key-logic-test.sh
    - deploy/tests/sandboxing-test.sh
  modified:
    - deploy/bin/ledger-selfcheck
    - deploy/tests/selfcheck-logic-test.sh
    - deploy/systemd/ledger-deploy-poll.service
    - deploy/bin/ledger-apikey
    - deploy/bin/ledger-deploy
    - deploy/provision.d/10-packages.sh
    - docs/lxc-setup.md
key-decisions:
  - "Installer lock moved to /run/ledger-deploy/deploy.lock with RuntimeDirectory so ProtectSystem=strict needs no writable /run"
  - "/var/backups/ledger is not in the poll unit's ReadWritePaths: the installer only starts the backup unit through systemctl"
requirements-completed: [INGEST-01]
status: complete
duration: 35min
completed: 2026-09-30
actuals:
  tokens: 32000
  tasks: 3
  commits: 5
---

# Phase 2 Plan 03: Host hardening and aggregator key custody Summary

The platform's own selfcheck now catches secrets leaked into the journal or `/var/log` without revealing them, the two privileged entry points are sandboxed, and the operator can create the aggregator key on the host with `ledger-bank-key`.

## Tasks

| Task | Name | Commits |
| --- | --- | --- |
| 1 | Tracer: selfcheck log secret scan, bank key mode, zone check | a6d4dc8 (test, red), 97a290d (feat) |
| 2 | Sandbox installer unit and apikey wrapper, tzdata | dd00499 |
| 3 | ledger-bank-key | d6d3548 (test, red), af2ccb6 (feat) |

### Task 1
`check_log_secrets` scans the journal (`LEDGER_SELFCHECK_JOURNAL_CMD`, default the last 30 days) and text files under `LEDGER_SELFCHECK_LOG_ROOTS` for an age identity, an API-key token, a PEM private-key header (built from fragments), the Data Protection certificate password, the bank key password and the first body line of the bank key. Values under 16 characters are ignored as needles. Fixed needles go to `grep -F -f /dev/fd/3`; every grep uses `-q` or `-l`. FAIL lines name only the kind and the place. `check_files` checks the configured bank key at 640 root:ledger (SKIP when not configured) and the zone file. The tracer's verify (`selfcheck-logic-test.sh` plus shell lint) was re-run after the commit and passed before any further task.

### Task 2: derived write targets of the installer
Derived from every write in `ledger-deploy`, `lib/deploy.sh`, `lib/common.sh`, `lib/backup.sh`, `ledger-backup` and the msmtp config:

| Path | Why |
| --- | --- |
| /opt/ledger | release staging, releases, `current` symlink |
| /etc/grafana | grafana.ini and provisioning copy |
| /etc/prometheus | prometheus.yml |
| /var/lib/prometheus/node-exporter | deploy metrics textfile |
| /var/lib/ledger-deploy | downloads and state |
| -/var/log/msmtp.log | msmtp log (minus: the file only exists after the first mail) |
| /run/ledger-deploy (RuntimeDirectory) | installer lock |

Temporary files use `PrivateTmp`. The pre-migration backup runs as its own unit through `systemctl`, so `/var/backups/ledger` is not needed (the plan's expected set listed it; the code does not write there). CapabilityBoundingSet and RestrictAddressFamilies are intentionally absent on the poll unit, as specified. `ledger-apikey` passes all listed properties to `systemd-run`; argument validation is unchanged and still rejects a bad name before `systemd-run` is reached (tested with stubs). `tzdata` joins the apt install list.

### Task 3
`ledger-bank-key` implements the contract from the plan, with library mode. `generate` uses `openssl genpkey ... -aes-256-cbc -pass env:`, installs 640 root:ledger / 644, writes the path and password via atomic replace-or-append, shreds temporaries, prints only the certificate and public key plus the password-manager reminder, and refuses an existing key. `configure` validates the lowercase-UUID application id and an `https://HOST/api/v1/bank/callback` URL. The docs gain a "Bank link key" section and the selfcheck description is updated.

## Deviations from Plan

**1. [Rule 3 - Blocking] Installer lock path changed**
- **Found during:** Task 2
- **Issue:** the installer flocks `/run/ledger-deploy.lock`; under `ProtectSystem=strict` `/run` is read-only, so the sandboxed unit would fail to take its lock. Making all of `/run` writable would weaken the sandbox.
- **Fix:** lock moved to `/run/ledger-deploy/deploy.lock`; the unit declares `RuntimeDirectory=ledger-deploy` with `RuntimeDirectoryPreserve=yes` (keeps the lock inode stable between runs). Manual runs create the directory with `mkdir -p` as before.
- **Files modified:** deploy/bin/ledger-deploy, deploy/systemd/ledger-deploy-poll.service
- **Commit:** dd00499

**2. Tracer gate run as autonomous.** Auto mode flags were false, but the plan is `autonomous: true` and the tracer's verify is an automated offline test. The verify was re-run end to end after the tracer commit and passed, so no human checkpoint was raised.

## Not verified here (by design)
- The live effect of the new sandboxing (a real deploy, `systemd-analyze security`, `runuser` under `NoNewPrivileges`, `ledger-apikey create/list/revoke` under `ProtectSystem=strict`) is part of the go-live plan. If the apikey CLI needs a writable path the sandbox blocks, add a `ReadWritePaths` property there.
- Nothing was run against or deployed to the ledger host; no env file, certificate or key on the host was read.

## Known Stubs
None.

## Threat Flags
None. The threat register items were mitigated as planned (log scan, fd-3 needles, poll unit and apikey sandboxing, encrypted key at rest, atomic validated env edits, tzdata).

## Verification
- `bash deploy/tests/selfcheck-logic-test.sh`, `sandboxing-test.sh`, `bank-key-logic-test.sh`, `provision-logic-test.sh`, `ledger-deploy-logic-test.sh`: all pass
- `build/lint.sh shell`, `repo-rules`, `secrets`: all pass

## Self-Check: PASSED
Created files exist (ledger-bank-key, bank-key-logic-test.sh, sandboxing-test.sh) and commits a6d4dc8, 97a290d, dd00499, d6d3548, af2ccb6 exist on the branch.
