---
created: 2026-09-28T22:05:00.000Z
title: Remove temporary Claude SSH access before /mcp goes public
area: security
severity: major
resolves_phase: 3
files:
  - deploy/bin/ledger-selfcheck
---

## Problem

During the first host bring-up the operator granted Claude temporary SSH access so provisioning, fixes and go-live checks could run directly:

- on the ledger host: a dedicated login with passwordless sudo, i.e. root;
- on the reverse proxy: a dedicated login without sudo, with write access only to the dynamic-configuration directory.

Both use one key, which lives unencrypted on the operator's workstation and is restricted to that workstation's address. That is acceptable while the host holds no real data, but it is root on the machine that will hold the household's complete financial history. Anyone who obtains that key file gets the same access.

The operator chose to keep the access while the project is in development. It must be gone before the automatic bank sync delivers real transactions.

**Update 2026-09-29 (Phase 2 discussion, D-04):** the operator decided to keep the access through the automatic-sync phase, even once real bank data is on the host, to speed up LXC development. Agreed mitigations:

- **During Phase 2:** put a passphrase on the key and load it into `ssh-agent` for sessions, so the key file on the workstation is useless on its own.
- **Hard removal point:** before `/mcp` goes public in Phase 3, when the host gets its first internet-facing surface.

## Solution

Passphrase-protect the key now (`ssh-keygen -p`), then load it with `ssh-add` per session.

Before `/mcp` goes public (Phase 3):

1. On the ledger host: delete the login and its sudoers drop-in.
2. On the reverse proxy: delete the login and remove its directory ACLs.
3. On the workstation: delete the key pair and its known-hosts entries.
4. Re-run `ledger-selfcheck --grafana-admin` and confirm 0 failures.
5. Optionally add a selfcheck that fails when any login other than the expected service accounts has sudo rights.

The exact accounts, paths and removal commands are in the operator's local, uncommitted notes (`.git/gsd-orch/infra-notes.md`), never in the repository.
