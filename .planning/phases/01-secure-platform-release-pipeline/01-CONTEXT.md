# Phase 1: Secure Platform & Release Pipeline - Context

**Gathered:** 2026-09-27
**Status:** Ready for planning

<domain>
## Phase Boundary

A semver tag on `main` builds, tests and attests a release on GitHub-hosted runners. After the operator approves the deploy environment, the release is published, and the ledger LXC pulls it, verifies it and installs it. There is no self-hosted runner. The new LXC runs the app skeleton, PostgreSQL, Grafana and Prometheus as systemd services. It is locked down before any real bank data arrives:
- least-privilege database roles over a local Unix socket
- automatic EF Core migrations under the migrator role
- Data Protection keys that survive a restart and a redeploy
- encrypted local backups with a restore that has actually been performed
- Grafana with per-person viewer logins, reachable only from LAN/VPN through Traefik
- a REST API that rejects unauthenticated calls
- CI that enforces SHA-pinned actions, no shell injection and a clean full-history secret scan

Not in this phase: bank sync, dashboards with financial data, MCP/OAuth and any public route (later phases).

</domain>

<decisions>
## Implementation Decisions

### Release flow
- **D-01:** Release tags may only point to commits on `main`. Each phase reaches `main` through a PR and is tagged there. The build job refuses a tag whose commit isn't reachable from `main`, and refuses any tag that doesn't match a strict semver pattern. The tag value reaches shell steps only through `env:`. Tag creation is restricted with a tag ruleset.
- **D-02:** **Deploys are pull-based, with no self-hosted runner anywhere.** The flow is:
  1. A tag push runs a GitHub-hosted build/test job that produces the release artifact, a build-provenance attestation and an EF Core migration bundle.
  2. The artifact goes into a **draft** release, which isn't publicly visible or downloadable.
  3. A deploy GitHub Environment with the operator as required reviewer gates a GitHub-hosted job that **publishes** the release.
  4. A systemd timer on the LXC polls for the newest published release and hands it to the root installer (D-03).

  No GitHub-executed code ever runs on the finance LXC. The user chose this over a per-app runner (the pattern in their other homelab app) and over a central deploy LXC that SSHes into app LXCs. The central box would become a hub that can reach both apps, and self-hosted runners on a personal account are per-repository anyway.
- **D-03:** A root-owned installer (e.g. `ledger-deploy`) is the only thing that changes the running system. It:
  - downloads the release by tag itself
  - verifies the attestation (this repository, the release workflow, a `main` ref) **before unpacking**
  - refuses downgrades unless given an explicit rollback flag
  - takes a pre-migration backup when the release contains a migration
  - runs the migration bundle as the migrator role
  - installs Grafana/Prometheus provisioning files
  - restarts services and runs health checks

  The poll timer calls it, and so can the operator over SSH for a manual deploy or rollback.
- **D-04:** Releases live side by side under `releases/<version>` with a `current` symlink. If the post-start health check fails, the installer switches back to the previous release automatically, **unless the release applied a migration**. In that case it stops and fails loudly, because EF migrations only run forward.
- **D-05:** Deploy outcomes are reported by email (D-19) and as Prometheus metrics (deployed version, last deploy result and time), not back to GitHub. The GitHub Actions run ends at "release published".

### LXC provisioning
- **D-06:** The LXC is built by a documented `pct create` step on the Proxmox host, followed by an **idempotent, re-runnable `provision.sh`** run as root inside the LXC. The script installs the .NET runtime, PostgreSQL, Grafana and Prometheus. It also creates the OS users, directories and permissions, the systemd units (app, deploy poll timer, backup timer), the database and roles (D-11), the firewall rules and the Grafana viewer accounts (D-21). Any step that can't be scripted is documented step by step.
- **D-07:** Secrets live only in the server-side env file, owned by root and readable by the app's group only. Every value in the repo is a placeholder, including `.env.example`. Hostnames, IPs, subnets and domains appear only in server-side config, never in the repo.

### Database
- **D-08:** **PostgreSQL inside the ledger LXC replaces the shared network MS SQL Server.** The user asked whether a database of the app's own would be simpler, and chose PostgreSQL after comparing it with a local SQL Server Express, SQLite and the shared SQL Server CT. PostgreSQL removes cross-container TLS, firewall work on another CT, and the shared-instance risk (the other app on that instance connects as `sa`). It uses about 150 MB of RAM against SQL Server's 2 GB minimum. It keeps real roles and grants, and exact `numeric` decimals. It uses the Npgsql EF Core provider and Grafana's core PostgreSQL datasource. SQLite was rejected: it has no logins, so Grafana couldn't be limited to views, and EF stores decimals as TEXT, so aggregates lose precision. — **Reversibility:** costly — before real bank data lands, undoing this means rewriting the EF provider setup, migrations, reporting views, the Grafana datasource and the provisioning; after that it also needs a data migration between engines.
- **D-09:** PostgreSQL listens **only on its Unix socket** (no TCP listener at all). Authentication is **peer auth**, mapping OS users to roles, so the env file holds no database passwords.
- **D-10:** Three roles, and the app never uses the `postgres` superuser:
  - **runtime** (the app's service user): DML on the app's tables, no DDL
  - **migrator** (used only by the root installer): owns the schema objects and runs migrations
  - **Grafana reader**: SELECT on the `reporting` schema views only

  Default privileges give runtime access to new tables, and migrations manage the grants on reporting views.
- **D-11:** `provision.sh` creates the database and roles locally, through the `postgres` superuser over peer auth. No committed passwords, and nothing crosses the network.
- **D-12:** The operator reaches the database by SSHing into the LXC and using `psql`. GUI tools (DBeaver/pgAdmin) connect through SSH forwarding to the Unix socket.
- **D-13:** Local development and tests use the **user's own long-running local PostgreSQL container**, defined outside this repo. Its connection string lives in `dotnet user-secrets`. CI uses a PostgreSQL service container. No Testcontainers.
- **D-14:** Consequences to plan for:
  - SQL Server temporal tables (mentioned in research) aren't available; the application-level audit log stays the primary audit mechanism.
  - "Verified TLS to the database" is replaced by "the database is reachable only over the local Unix socket".
  - Requirement, roadmap, project and CLAUDE.md text was updated to match in the same commit as this context.

### Backups & key custody
- **D-15:** Backups stay **local only, inside the LXC's own disk**. **Accepted risk (user decision):** SSD failure, theft, fire, or deleting or breaking the LXC loses both the data and its backups. Offsite and outside-LXC copies are deferred.
- **D-16:** Backups are encrypted **asymmetrically** (e.g. `age`). The LXC holds only the public key, so the backup job can encrypt but never decrypt. The private key lives in the operator's password manager. The env file, the Data Protection certificate and the aggregator key are **not** in the backups; copies go in the password manager.
- **D-17:** A nightly `pg_dump`, plus a dump the root installer takes before applying a migration. Grandfather-father-son retention: 7 daily, 4 weekly, 12 monthly.
- **D-18:** The restore procedure is documented and actually performed once (the operator supplies the private key for the drill). Backup freshness and failures are visible as a metric with a Grafana alert (e.g. no successful backup in about 26 hours).

### Alerting
- **D-19:** The Grafana alert contact point is **email through the existing Postfix relay**. Alert texts never contain financial details. Platform alerts go to the operator: backup stale or failed, app or service down, deploy failed or rolled back. Household-facing alerts (sync, consent) come in the bank-sync phase and can add the partner.

### LAN access: Grafana & REST
- **D-20:** Grafana and the REST API are reached **through the existing Traefik on internal hostnames with Let's Encrypt certificates**. Local DNS in UniFi points the names at Traefik, and an IP allowlist middleware admits only LAN and VPN subnets. The LXC firewall accepts the app's and Grafana's ports only from Traefik. The Traefik route config lives on the Traefik CT; the repo ships a template with placeholders plus a documented step.
- **D-21:** Grafana accounts:
  - two **Viewer** accounts, one for the operator and one for the partner
  - the built-in admin is renamed, gets a strong password kept in the password manager, and is used for administration only
  - `provision.sh` creates the viewer accounts through Grafana's HTTP API, with passwords typed at the prompt

  Anonymous access, public dashboards and snapshot sharing are disabled in the provisioned Grafana config. Datasources and alerting are provisioned from the repo.
- **D-22:** REST authentication uses **named per-client API keys** (e.g. "operator", "grafana"). Each is created with a CLI command on the LXC, shown once, stored only as a hash, sent in a header and individually revocable. Every REST endpoint requires a key. This can move to OIDC once the OAuth server for the public MCP endpoint exists.
- **D-23:** `/metrics` and health are served on a **separate Kestrel endpoint bound to 127.0.0.1**. Prometheus also listens on loopback only. The operator sees metrics through Grafana's Prometheus datasource, or through an SSH tunnel. Neither Prometheus nor `/metrics` has a Traefik route.

### Claude's Discretion
- What the Phase 1 app skeleton does, at minimum:
  - health and `/metrics`
  - Data Protection with keys persisted to the database and protected by a certificate (path plus password in the env file)
  - a canary value that proves a decrypt still works after a restart and a redeploy
  - the API-key store and CLI command
  - an initial migration (Data Protection keys, API keys, an empty `reporting` schema with its grants)
- The LXC's OS (default Ubuntu 24.04 LTS, like the reference deployment) and its CPU/RAM/disk. Keep them modest: the host is low-power and its RAM is shared with other guests.
- The PostgreSQL major version and where it comes from (PGDG repo or the distro).
- The poll interval (a few minutes). How the poller and the installer authenticate to GitHub, if at all: unauthenticated API polling of a public repo is probably enough. Check whether `gh attestation verify` needs a token; if it does, prefer offline verification of the Sigstore bundle published as a release asset.
- CI scanners and linters: full-history gitleaks, zizmor/actionlint for injection and unpinned actions, and a Dependabot config for GitHub Actions and NuGet.
- Logging: journald through systemd. The redaction approach that keeps connection strings, tokens and keys out of logs, exceptions and metric labels.
- Firewall tooling (nftables or ufw), and the names of OS users, roles and paths.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Scope and requirements
- `.planning/ROADMAP.md` §Phase 1: goal and success criteria (updated for PostgreSQL and pull-based deploys)
- `.planning/REQUIREMENTS.md`: SEC-02, SEC-03, SEC-05, SEC-06, SEC-07, SEC-08, SEC-09, SEC-10, OPS-03, OPS-04, OPS-05, OPS-07, API-02, DASH-06, DASH-08, DASH-09
- `.planning/PROJECT.md` §Context "Security review of the reference deployment": the weaknesses this phase must not inherit; §Constraints; §Key Decisions
- `.claude/CLAUDE.md` §Hard rules: no planning references outside `.planning/`, `///` comments only, no personal data, branching, local database, security first

### Research (historical; the decisions above take precedence)
- `.planning/research/ARCHITECTURE.md` §Recommended Project Structure: Domain → Repository → Service layering, `grafana/` and `prometheus/` folders. §Security Architecture: trust boundaries. Its SQL Server logins and self-hosted runner details are superseded by D-02 and D-08..D-14.
- `.planning/research/PITFALLS.md` Pitfalls 10 (Grafana read-only is only a UI convention), 11 (anonymous access and snapshots), 12 (self-hosted runner on a public repo; now avoided entirely), 13 (mutable action tags), 14 (personal data in a public repo), 15 (Data Protection keys not persisted)
- `.planning/research/STACK.md` §6 Observability (prometheus-net, Grafana unified alerting), §7 Grafana as code, §8 Core .NET libraries (swap the SQL Server provider for Npgsql), §9 CI/CD supply-chain hardening

### Reference deployment (the user's other public homelab app; local read-only checkout)
- `/mnt/Data/repos/quest-board-dnd/docs/server-setup.md`: the reference LXC setup (systemd unit, env file, Traefik file-provider route, release zip). Reuse its shape; don't copy its weaknesses (a runner in the app LXC, `sa`, `TrustServerCertificate=true`, unverified artifacts).
- `/mnt/Data/repos/quest-board-dnd/.github/workflows/binary-release.yml`: the reference release workflow (mutable action tags, tag interpolated into shell). This is exactly what CI must now reject.
- `/mnt/Data/repos/quest-board-dnd/QuestBoard.slnx` and its project layout: the `.slnx` plus Domain/Repository/Service/UnitTests/IntegrationTests convention to mirror

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- None in this repository yet (greenfield: only `.claude/`, `.gitignore` and `.planning/`). `.gitignore` already excludes `.env` and `*.env` except `.env.example`.

### Established Patterns
- Reference app layering: `*.Domain` (no EF/HTTP), `*.Repository` (EF Core, migrations), `*.Service` (ASP.NET Core host), plus `*.UnitTests` and `*.IntegrationTests` in one `.slnx`. EF Core packages stay in Repository only.
- Test stack: xUnit v3, FluentAssertions, NSubstitute, EF Core InMemory for fast unit tests, `Microsoft.AspNetCore.Mvc.Testing` for in-process integration tests. Real-database tests run against the user's local PostgreSQL container, and a service container in CI.
- Reference deploy shape: an env file under `/etc/<app>/`, a systemd unit with `EnvironmentFile=`, a Traefik file-provider route, and `ReverseProxy__KnownProxies` so forwarded headers are trusted only from Traefik.

### Integration Points
- Existing Traefik CT: new internal-only routers for Grafana and REST (and, in a later phase, the public `/mcp` route).
- Existing Postfix CT: SMTP relay for Grafana alert emails.
- UniFi: local DNS records for the internal hostnames, and the VPN subnets for the allowlist.
- GitHub: the tag ruleset, the deploy Environment with a required reviewer, outside-contributor approval, secret scanning and push protection.

</code_context>

<specifics>
## Specific Ideas

- The user already runs a self-hosted runner inside their other homelab app's LXC and asked whether to repeat that or build a central deploy LXC. They chose to pull instead; the other app keeps its runner, and changing it is a separate task in that repo.
- The user questioned the shared SQL Server and asked whether the app's own database in the same LXC would be simpler. The comparison table in the discussion log records why PostgreSQL won.
- The user wants deploys hands-off after approval, and to learn about failures by email rather than by watching Actions.

</specifics>

<deferred>
## Deferred Ideas

- **Offsite and outside-LXC backup copies:** an encrypted copy offsite (e.g. object storage or a storage box) and/or on a host directory outside the LXC, so backups survive losing the SSD, theft, fire or the LXC. Declined for now; revisit before or soon after real bank data arrives.
- **Other homelab app, `sa` login:** replace the other app's `sa` connection with a scoped login and disable `sa` on that SQL Server. It no longer blocks this project; separate task in that repo.
- **Other homelab app, deploy path:** optionally adopt the same pull-based deploy instead of its in-LXC runner. Separate task in that repo.
- **REST authentication through OIDC:** move from API keys to the OAuth/OIDC server chosen for the public MCP endpoint, once it exists.
- **Grafana SSO:** single sign-on for Grafana through that same OAuth server, later.

</deferred>

---

*Phase: 01-secure-platform-release-pipeline*
*Context gathered: 2026-09-27*
