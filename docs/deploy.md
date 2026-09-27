# Deploying a release

This describes how a published release reaches the server, how to trigger or
undo a deploy by hand, and where to look when something goes wrong. Every
path, hostname and address below is a placeholder — replace them with the
server's own values.

## How a release reaches the server

1. A maintainer pushes a semver tag (for example `v1.2.3`) on the trunk
   branch. A build runs on a hosted CI runner, publishes the application,
   builds a self-contained migration bundle, and attests the resulting
   archive's provenance.
2. The attested archive and its attestation bundle are attached to a draft
   release. Nothing is downloadable yet.
3. An operator approves publishing that release. Approval is the only
   manual step; everything before and after it is automatic.
4. A timer on the server polls for the newest published release every few
   minutes. When it finds one newer than what is currently running, it hands
   the tag to the installer described below.
5. The installer downloads the release's archive and its attestation
   bundle, verifies the attestation fully offline (no server-held
   credential is ever used for this, or for anything else in this flow),
   and refuses to unpack anything that does not verify.
6. If the release includes a database migration, the installer takes a
   backup first, applies the migration, then activates the release. If the
   release has no migration, it activates the release directly.
7. The installer restarts the application, waits for it to report healthy,
   and reports the outcome (see "Where outcomes appear" below).

No code that ran inside CI ever runs on the server. The server only ever
runs its own installer against artifacts it has verified itself.

## Manual commands

The installer accepts a small set of subcommands. Run all of them as root.

```bash
# Check for and install the newest published release (the same thing the
# timer does automatically).
ledger-deploy poll

# Install a specific version directly, without waiting for the timer.
ledger-deploy install v1.2.3

# Roll back to a version that is still on disk under releases/. Refused if
# the database has applied a migration that version's manifest does not
# list.
ledger-deploy rollback 1.2.2
```

## Verify-only usage

To check whether a downloaded archive and its attestation bundle actually
verify, without installing anything:

```bash
ledger-deploy verify \
  --artifact /path/to/ledger-1.2.3.zip \
  --bundle /path/to/ledger-1.2.3.zip.sigstore.json \
  --tag v1.2.3
```

This prints the attested commit and exits non-zero on any failure. To
confirm the check actually catches tampering, flip a byte in a copy of a
genuine archive and run the same command against the copy — it must refuse.
There is no flag, environment variable or fallback that skips this check;
an unreachable verification service is treated the same as a failed
verification.

## Migrations and why rollback sometimes stops

When a release's manifest lists a database migration that has not yet been
applied, the installer takes a backup before applying it. If the release
then fails its post-start health check:

- **No migration ran:** the installer automatically reactivates the
  previous release and reports the failure. The service self-heals.
- **A migration ran:** the installer leaves the new release in place and
  stops, reporting a failure rather than rolling back. Migrations only ever
  run forward, so reactivating the previous release's code against an
  already-migrated database would be unsafe. This case needs a person to
  look at it — check the reported failure, restore from the pre-migration
  backup if needed, and only then decide the next step.

## Where outcomes appear

- **Email:** every install attempt and every poll send a short
  notification containing only the version, the result and timestamps —
  never a path, a connection string or any key material.
- **Metrics:** a small set of gauges record the last poll and the last
  install attempt (timestamp, success, whether a rollback happened, and the
  currently active version), so they can be graphed and alerted on
  alongside everything else already being monitored.
- **Logs:** `journalctl -u ledger-deploy-poll` shows every poll attempt;
  a manual `ledger-deploy install` or `ledger-deploy rollback` run prints
  directly to the terminal it was run from as well.

## When to re-run provisioning

The installer only ever touches the application, its Grafana dashboards and
its Prometheus configuration. If a release changes anything else that the
one-time server setup is responsible for — a system unit, a firewall rule,
an operating-system package — re-run the provisioning script from the newly
active release's own `deploy/` directory after the install completes.
