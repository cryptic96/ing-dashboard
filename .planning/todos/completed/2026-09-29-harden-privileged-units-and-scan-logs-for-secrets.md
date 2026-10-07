---
created: 2026-09-29T18:10:00.000Z
title: Harden privileged units and scan logs for secrets
area: security
severity: minor
files:
  - deploy/systemd/ledger-deploy-poll.service
  - deploy/bin/ledger-apikey
  - deploy/bin/ledger-selfcheck
---

## Problem

The end-of-phase code review of the host scripts found three defence-in-depth gaps. None exposes anything today:

1. **`ledger-deploy-poll.service` has no systemd sandboxing.** It runs the root installer, while `ledger.service` and `ledger-backup@.service` are sandboxed (ProtectSystem, NoNewPrivileges, restricted address families, an empty capability set, …).
2. **`deploy/bin/ledger-apikey` runs the CLI through `systemd-run --uid=ledger` without sandboxing properties**, although it handles API-key secrets.
3. **`ledger-selfcheck`'s secrets-hygiene checks never look at logs.** A secret that leaked into `/var/log` or the journal (like the backup-recipients leak fixed before go-live) would not be caught by the platform's own acceptance check.

## Solution

1. **Poll service:** add the sandboxing that the installer's real needs allow. It writes under `/opt/ledger`, `/etc/grafana`, `/etc/prometheus` and the textfile-metrics directory, calls `systemctl` and `runuser`, and reaches GitHub over HTTPS. Use `ReadWritePaths=` for those paths, plus `ProtectHome`, `PrivateTmp`, kernel/control-group protection, `RestrictNamespaces` and `LockPersonality`. Verify with a real deploy (tag a patch release) and `systemd-analyze security`.
2. **`ledger-apikey`:** pass `-p` sandboxing properties to `systemd-run` (NoNewPrivileges, ProtectSystem=strict, ProtectHome, PrivateTmp, an empty CapabilityBoundingSet, RestrictAddressFamilies=AF_UNIX). Check that create, list and revoke still work.
3. **Selfcheck:** scan the recent journal and `/var/log` for complete secret shapes: a full age identity, an API-key token, and the Data Protection certificate password read from the env file and matched without ever being printed. Fail without echoing any match.
4. Run the offline tests, `build/lint.sh`, and `ledger-selfcheck --grafana-admin --restart-check` on the host.
