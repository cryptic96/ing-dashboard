# Phase 1: Secure Platform & Release Pipeline - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-09-27
**Phase:** 01-secure-platform-release-pipeline
**Areas discussed:** Release & deploy, SQL Server TLS & logins (became: Database), Backups & key custody, LAN access: Grafana & REST, GitHub runner / deploy path (added by the user)

---

## Release & deploy

**Tag source**

| Option | Description | Selected |
|--------|-------------|----------|
| Main only | Each phase merged to main via PR, then tagged; build refuses tags not on main | ✓ |
| Pre-releases from the milestone branch | `v0.x.y-rc.N` from the milestone branch; stable tags only from main | |
| Any commit; only you can tag | Tag ruleset only, no branch check | |

**Failed deploy**

| Option | Description | Selected |
|--------|-------------|----------|
| Auto-rollback unless it migrated | Side-by-side releases + `current` symlink; switch back on a failed health check; stop loudly if a migration ran | ✓ |
| Always stop and fail | No automatic rollback | |
| Always auto-rollback | Relies on expand/contract migration discipline | |

**Provisioning**

| Option | Description | Selected |
|--------|-------------|----------|
| Idempotent script in repo | `pct create` + re-runnable `provision.sh` as root | ✓ |
| Ansible playbook | Declarative, but an extra tool and a private inventory | |
| Step-by-step doc only | Manual, drifts | |

**Deploy power**

| Option | Description | Selected |
|--------|-------------|----------|
| Trigger a root installer only | Runner may only run `sudo ledger-deploy <tag>`; the installer downloads, verifies, migrates and restarts | ✓ (later superseded by pull-based deploys, which keep the same root installer) |
| Runner holds migrator creds | A hijacked runner job would get DDL rights | |

---

## SQL Server TLS & logins → Database

| Question | Options | Selected |
|----------|---------|----------|
| quest-board's `sa` on the shared instance | Fix quest-board first / Separate SQL instance / Accept the risk | Fix quest-board first (later moot for the ledger, see below) |
| SQL TLS certificate | Self-signed trusted on app LXC / Small internal CA / Let's Encrypt DNS-01 | Self-signed (later moot) |
| Login setup | Committed script run once by you / provision.sh does it / Manual SSMS | Committed script (later replaced by provision.sh creating roles locally) |
| Who may reach port 1433 | App + quest-board, admin via SSH / plus desktop / LAN-wide | **User asked instead:** would it be easier to give the app its own database — SQLite, a local SQL Server, or Postgres in the same LXC? |

Comparison presented:

| | PostgreSQL in LXC | SQL Server Express in LXC | SQLite | Shared SQL CT |
|---|---|---|---|---|
| RAM | ~100–200 MB | ≥2 GB | ~0 | 0 extra |
| Separate logins/roles | yes | yes | no | yes |
| DB passwords | none (peer auth) | 3 | none | 3 |
| Network exposure | none (socket) | loopback | none | LAN, needs TLS + firewall |
| Grafana datasource | core | core | community plugin | core |
| Exact decimals in SQL aggregates | yes | yes | no (TEXT → floating point) | yes |
| Blocked by the quest-board `sa` fix | no | no | no | yes |

**Database location**

| Option | Description | Selected |
|--------|-------------|----------|
| PostgreSQL in the ledger LXC | Unix socket, peer auth, three roles, core Grafana datasource | ✓ |
| SQL Server Express in the ledger LXC | Same engine; ≥2 GB RAM, two instances to patch | |
| Keep the shared SQL Server CT | Original plan | |
| SQLite | No logins, decimals as text | |

**Local test DB:** own long-running local Postgres container ✓ (vs Testcontainers, vs compose file in repo)
**DB admin access:** no TCP listener, SSH only ✓ (vs 127.0.0.1 listener + SSH tunnel)
**Doc updates:** update PROJECT/REQUIREMENTS/ROADMAP/CLAUDE.md now ✓ (vs CONTEXT.md only)

---

## Backups & key custody

| Question | Options | Selected |
|----------|---------|----------|
| Destination | Offsite + local copy (recommended) / Another machine at home / Local only on the NUC | **Local only on the NUC** |
| Location on the NUC | Host directory outside the LXC (recommended) / Inside the LXC's own disk / Proxmox vzdump | **Inside the LXC's own disk** |
| Key custody | Public key on server, private key with you / Symmetric key on server / Secrets disposable | Public key on server, private key with you ✓ |
| Schedule | Nightly + pre-migration, GFS 7/4/12 / Nightly, 30 days / Nightly + pre-migration, 14 days | Nightly + pre-migration, GFS ✓ |
| Alert route | Email via existing Postfix / Home Assistant push / Both | Email via Postfix ✓ |

**Notes:** The user declined offsite and outside-LXC copies. Recorded as an accepted risk: losing the SSD, theft, fire or the LXC loses the data and its backups.

---

## LAN access: Grafana & REST

| Question | Options | Selected |
|----------|---------|----------|
| URL & TLS | Via Traefik, internal hostname + LE cert / Grafana's own HTTPS / Plain HTTP | Via Traefik ✓ |
| Accounts | Two viewers + separate admin / Partner viewer, you admin / You as Editor | Two viewers + separate admin ✓ |
| REST auth | Named API keys, hashed / Wait for OIDC / mTLS | Named API keys ✓ |
| Metrics exposure | Loopback only / Prometheus UI via Traefik | Loopback only ✓ |

---

## GitHub runner / deploy path (raised by the user)

The user already runs a self-hosted runner inside their other homelab app's LXC. They asked whether to repeat that, or to build a separate LXC with a runner that deploys both apps by SSHing into each LXC.

| Option | Description | Selected |
|--------|-------------|----------|
| Pull-based, no runner | Approval publishes the release; a timer on the LXC pulls it; the root installer verifies and installs; no GitHub-executed code on the finance box | ✓ |
| Runner in the ledger LXC | Hardened per-app pattern; deploy result visible in Actions | |
| Central deploy LXC via SSH | Becomes a hub that can reach both apps; inbound SSH into the finance LXC; still two runner registrations (per-repo on a personal account) | |

**Notes:** The user accepted that deploy results arrive by email and in Grafana, not as a green check in Actions. The other homelab app keeps its runner; changing it is a separate task.

---

## Claude's Discretion

- What the Phase 1 app skeleton does (health, metrics, Data Protection canary, API-key store, initial migration)
- LXC OS and sizing; PostgreSQL major version and source
- Poll interval; GitHub API auth for polling and attestation verification
- CI scanners (gitleaks, zizmor/actionlint), Dependabot config
- Logging destination (journald) and redaction approach
- Firewall tooling, and the names of users, roles and paths

## Deferred Ideas

- Offsite and outside-LXC backup copies
- Other homelab app: replace `sa` with a scoped login (separate repo)
- Other homelab app: adopt the pull-based deploy (separate repo)
- REST authentication through OIDC once the MCP OAuth server exists
- Grafana SSO through that same OAuth server
