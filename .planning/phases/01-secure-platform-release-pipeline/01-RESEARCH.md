# Phase 1: Secure Platform & Release Pipeline - Research

**Researched:** 2026-09-27
**Domain:** Pull-based release/deploy pipeline (GitHub Actions + Sigstore attestation), PostgreSQL least-privilege access, ASP.NET Core Data Protection, encrypted backups, Grafana/Prometheus/Traefik lockdown
**Confidence:** MEDIUM-HIGH (CI/CD supply-chain controls and PostgreSQL/EF Core facts are HIGH; a few implementation-shape details — Grafana-over-Unix-socket, `gh attestation verify` auth requirement, PostgreSQL major version choice — are MEDIUM and flagged for a Wave 0 spike)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Release flow**
- **D-01:** Release tags may only point to commits on `main`. Each phase reaches `main` through a PR and is tagged there. The build job refuses a tag whose commit isn't reachable from `main`, and refuses any tag that doesn't match a strict semver pattern. The tag value reaches shell steps only through `env:`. Tag creation is restricted with a tag ruleset.
- **D-02:** **Deploys are pull-based, with no self-hosted runner anywhere.** The flow is:
  1. A tag push runs a GitHub-hosted build/test job that produces the release artifact, a build-provenance attestation and an EF Core migration bundle.
  2. The artifact goes into a **draft** release, which isn't publicly visible or downloadable.
  3. A deploy GitHub Environment with the operator as required reviewer gates a GitHub-hosted job that **publishes** the release.
  4. A systemd timer on the LXC polls for the newest published release and hands it to the root installer (D-03).

  No GitHub-executed code ever runs on the finance LXC.
- **D-03:** A root-owned installer (e.g. `ledger-deploy`) is the only thing that changes the running system. It downloads the release by tag itself, verifies the attestation (this repository, the release workflow, a `main` ref) **before unpacking**, refuses downgrades unless given an explicit rollback flag, takes a pre-migration backup when the release contains a migration, runs the migration bundle as the migrator role, installs Grafana/Prometheus provisioning files, restarts services and runs health checks. The poll timer calls it, and so can the operator over SSH for a manual deploy or rollback.
- **D-04:** Releases live side by side under `releases/<version>` with a `current` symlink. If the post-start health check fails, the installer switches back to the previous release automatically, **unless the release applied a migration** (EF migrations only run forward, so it stops and fails loudly instead).
- **D-05:** Deploy outcomes are reported by email and as Prometheus metrics (deployed version, last deploy result and time), not back to GitHub. The GitHub Actions run ends at "release published".

**LXC provisioning**
- **D-06:** The LXC is built by a documented `pct create` step on the Proxmox host, followed by an idempotent, re-runnable `provision.sh` run as root inside the LXC. It installs .NET runtime, PostgreSQL, Grafana, Prometheus; creates OS users, directories, permissions; systemd units (app, deploy poll timer, backup timer); database and roles; firewall rules; Grafana viewer accounts. Anything unscriptable is documented step by step.
- **D-07:** Secrets live only in the server-side env file, owned by root and readable by the app's group only. Every value in the repo is a placeholder, including `.env.example`. Hostnames, IPs, subnets and domains appear only in server-side config, never in the repo.

**Database**
- **D-08:** **PostgreSQL inside the ledger LXC replaces the shared network MS SQL Server.** Uses the Npgsql EF Core provider and Grafana's core PostgreSQL datasource. SQLite was rejected. Reversibility: costly before real bank data lands, more costly after.
- **D-09:** PostgreSQL listens **only on its Unix socket** (no TCP listener at all). Authentication is **peer auth**, mapping OS users to roles, so the env file holds no database passwords.
- **D-10:** Three roles, and the app never uses the `postgres` superuser: **runtime** (DML only, no DDL), **migrator** (owns schema objects, runs migrations), **Grafana reader** (SELECT on `reporting` schema views only). Default privileges give runtime access to new tables; migrations manage grants on reporting views.
- **D-11:** `provision.sh` creates the database and roles locally through the `postgres` superuser over peer auth. No committed passwords, nothing crosses the network.
- **D-12:** The operator reaches the database by SSHing into the LXC and using `psql`. GUI tools connect through SSH forwarding to the Unix socket.
- **D-13:** Local development and tests use the **user's own long-running local PostgreSQL container**, defined outside this repo, connection string in `dotnet user-secrets`. CI uses a PostgreSQL service container. No Testcontainers.
- **D-14:** Consequences: no SQL Server temporal tables (application-level audit log stays the primary audit mechanism); "verified TLS to the database" is replaced by "reachable only over the local Unix socket".

**Backups & key custody**
- **D-15:** Backups stay **local only, inside the LXC's own disk**. Accepted risk: SSD failure/theft/fire/deleting the LXC loses both data and backups. Offsite copies deferred.
- **D-16:** Backups are encrypted **asymmetrically** (e.g. `age`). The LXC holds only the public key; the private key lives in the operator's password manager. The env file, the Data Protection certificate and the aggregator key are **not** in the backups; copies go in the password manager.
- **D-17:** A nightly `pg_dump`, plus a dump before applying a migration. Grandfather-father-son retention: 7 daily, 4 weekly, 12 monthly.
- **D-18:** The restore procedure is documented and actually performed once (operator supplies the private key). Backup freshness/failures visible as a metric with a Grafana alert (e.g. no successful backup in ~26 hours).

**Alerting**
- **D-19:** The Grafana alert contact point is email through the existing Postfix relay. Alert texts never contain financial details. Platform alerts go to the operator: backup stale/failed, app/service down, deploy failed/rolled back.

**LAN access: Grafana & REST**
- **D-20:** Grafana and REST are reached through the existing Traefik on internal hostnames with Let's Encrypt certificates. Local DNS in UniFi points names at Traefik; an IP allowlist middleware admits only LAN and VPN subnets. The LXC firewall accepts the app's and Grafana's ports only from Traefik. Traefik route config lives on the Traefik CT; the repo ships a template with placeholders plus a documented step.
- **D-21:** Grafana accounts: two Viewer accounts (operator, partner); built-in admin renamed with a strong password for administration only; `provision.sh` creates viewer accounts through Grafana's HTTP API with passwords typed at the prompt. Anonymous access, public dashboards and snapshot sharing disabled in provisioned config. Datasources and alerting provisioned from the repo.
- **D-22:** REST authentication uses named per-client API keys (e.g. "operator", "grafana"). Each created with a CLI command on the LXC, shown once, stored only as a hash, sent in a header, individually revocable. Every REST endpoint requires a key. Can move to OIDC once the OAuth server for the public MCP endpoint exists.
- **D-23:** `/metrics` and health served on a separate Kestrel endpoint bound to 127.0.0.1. Prometheus also listens on loopback only. Operator sees metrics through Grafana's Prometheus datasource or an SSH tunnel. Neither Prometheus nor `/metrics` has a Traefik route.

### Claude's Discretion

- What the Phase 1 app skeleton does at minimum: health and `/metrics`; Data Protection with keys persisted to the database and protected by a certificate (path + password in env file); a canary value proving decrypt survives restart and redeploy; the API-key store and CLI command; an initial migration (Data Protection keys, API keys, empty `reporting` schema with grants).
- The LXC's OS (default Ubuntu 24.04 LTS) and its CPU/RAM/disk — keep modest, host is low-power and shared.
- The PostgreSQL major version and where it comes from (PGDG repo or the distro).
- The poll interval (a few minutes); how the poller/installer authenticate to GitHub, if at all — unauthenticated API polling of a public repo is probably enough; check whether `gh attestation verify` needs a token, prefer offline verification of the Sigstore bundle if it does.
- CI scanners and linters: full-history gitleaks, zizmor/actionlint for injection and unpinned actions, Dependabot config for GitHub Actions and NuGet.
- Logging: journald through systemd; the redaction approach keeping connection strings, tokens and keys out of logs/exceptions/metric labels.
- Firewall tooling (nftables or ufw); names of OS users, roles and paths.

### Deferred Ideas (OUT OF SCOPE)

- Offsite and outside-LXC backup copies — declined for now, revisit before/soon after real bank data arrives.
- Other homelab app's `sa` login replacement — separate task in that repo.
- Other homelab app's deploy path (adopting pull-based deploy) — separate task in that repo.
- REST authentication moving to OIDC — once the OAuth server for the public MCP endpoint exists (later phase).
- Grafana SSO through that same OAuth server — later.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| SEC-02 | Separate runtime/migrator/Grafana-reader DB roles; superuser never used by the app | §Database roles & peer auth; Code Examples: `pg_hba.conf`/`pg_ident.conf`, role DDL; Pitfall "Grafana read-only is a UI convention" |
| SEC-03 | Secrets only in server-side env file; consent tokens/keys encrypted at rest | §Data Protection; Code Examples: `PersistKeysToDbContext` + `ProtectKeysWithCertificate`; §Environment file layout |
| SEC-05 | DB reachable only over local Unix socket, peer auth | §Database roles & peer auth; D-09/D-11 (locked) |
| SEC-06 | No secrets in logs, exceptions, metric labels | §Common Pitfalls "Secrets leak through logs/metrics"; §Validation Architecture |
| SEC-07 | Semver tag → GitHub-hosted build → provenance attestation → server verifies before deploy | §Attestation verification (gh attestation verify, offline bundle); Architecture diagram |
| SEC-08 | Pull-based deploy, no self-hosted runner, Environment required reviewer, outside-contributor approval, tag-creation restricted | §Release pipeline architecture; §GitHub rulesets & Environments |
| SEC-09 | Third-party actions SHA-pinned + Dependabot; no untrusted values interpolated into shell | §CI hardening tooling (zizmor, actionlint); Code Examples: workflow skeleton |
| SEC-10 | No personal data in repo; secret scanning + push protection; full-history scan passes | §CI hardening tooling (gitleaks full-history); Common Pitfalls "Public repo leaks real data" |
| OPS-03 | App/PostgreSQL/Grafana/Prometheus as systemd services in one LXC; one-time steps documented | §Provisioning (`provision.sh`); Recommended Project Structure |
| OPS-04 | Encrypted backups; restore procedure documented and actually performed | §Backups (`age`, `pg_dump`, GFS retention); Code Examples: backup/restore script |
| OPS-05 | Data Protection keys persist across restart/redeploy | §Data Protection; Common Pitfall 15 (keys not persisted) |
| OPS-07 | EF Core migrations applied automatically by the migrator role during deploy | §EF Core migration bundles; Code Examples: `efbundle` invocation |
| API-02 | REST requires authentication even on the home network | §REST API-key auth pattern; Don't Hand-Roll (API key hashing) |
| DASH-06 | Dashboards/datasources/alerting provisioned as code from the repo | §Grafana as code; Code Examples: datasource YAML |
| DASH-08 | Per-partner Grafana viewer logins; anonymous/public/snapshot disabled | §Grafana hardening; Code Examples: `grafana.ini` keys |
| DASH-09 | Grafana/Prometheus/REST unreachable from the internet | §Network exposure (Traefik allowlist, loopback binding); Architecture diagram |
</phase_requirements>

## Summary

This phase builds no product feature — it builds the pipe the product will ship through, and the box the product will run on, and both must be trustworthy before real bank data exists anywhere in the system. Two things dominate the risk surface: the release pipeline (a public repo, GitHub-hosted build, human-gated publish, and an LXC that pulls and self-installs with no inbound GitHub access at all) and the database access model (PostgreSQL over a Unix socket with peer auth and three roles, replacing the project-level research's shared SQL Server design entirely per D-08–D-14). Everything else — Data Protection key persistence, encrypted local backups, Grafana lockdown, REST API-key auth, loopback-only metrics — is a well-trodden ASP.NET Core / Postgres / Grafana pattern that mainly needs correct wiring, not invention.

The most consequential open question the user already flagged is whether `gh attestation verify` needs a GitHub token to check a public repo's attestation via the API. It does today (confirmed below) — which changes the recommended design from "unauthenticated API polling is enough" to "the release workflow must also publish the Sigstore bundle as a release asset, and the installer verifies fully offline with `--bundle`, needing no token at all." This is the single biggest actionable finding of this research and should anchor the Walking Skeleton slice: tag → build → attest → **publish artifact + bundle as release assets** → approve → publish release → LXC timer polls (unauthenticated) → **downloads artifact + bundle** → `gh attestation verify --bundle` (fully offline) → migrate → restart → health check.

**Primary recommendation:** Build the Walking Skeleton around the pull-based deploy loop first (tag → build → attest-with-bundle-asset → environment-gated publish → poll → offline-verify → install → health-check), because every other requirement in this phase (DB roles, Data Protection, Grafana lockdown, backups) hangs off "a release can safely and repeatably reach the LXC." Layer the database, Data Protection, backups and Grafana/Traefik lockdown afterward, each independently testable against the already-working deploy loop.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Build, test, attest release artifact | CI/CD Pipeline (GitHub-hosted Actions) | — | Must never touch the LXC; only GitHub-hosted runners can execute untrusted PR code |
| Publish approval gate | CI/CD Pipeline (GitHub Environment) | Host/OS (email/metrics feedback) | Approval is a GitHub-side control; deploy *outcome* reporting is host-side (D-05) |
| Artifact download + attestation verification | Host/OS (root installer on LXC) | — | Must happen before unpacking, entirely on the LXC, with no GitHub code execution |
| EF Core migration execution | Database/Storage (via migrator role) | Host/OS (installer invokes the bundle) | Schema change is a DB-tier operation; the installer only triggers it, never runs SQL itself |
| Runtime data access | API/Backend (ASP.NET Core host) | Database/Storage (runtime role enforces it) | App requests DML only; DB enforces no-DDL via role grants, not app-level discipline |
| Secret custody | Host/OS (env file, root:group perms) | API/Backend (reads at startup only) | Secrets never enter the repo or CI; the app is a *consumer* of the env file, not its owner |
| Data Protection key material | Database/Storage (`DataProtectionKeys` table) | Host/OS (protecting certificate + password in env file) | Keys must survive redeploy — DB is the durable store; the cert is host-custodied |
| Backup creation & encryption | Host/OS (systemd timer + `pg_dump` + `age`) | Database/Storage (source of the dump) | Encryption happens host-side with a public key only; DB never needs to know about backups |
| Dashboard rendering & access control | Observability (Grafana) | Database/Storage (SELECT-only reporting views) | Grafana is a read-only consumer; the DB enforces the boundary via role grants, not Grafana config |
| Metrics scraping | Observability (Prometheus) | API/Backend (`/metrics` endpoint) | App exposes metrics passively; Prometheus and Grafana are both loopback-only, never routed by Traefik |
| Public/LAN network boundary | Reverse Proxy (Traefik) | Host/OS (LXC firewall) | Traefik decides *what's routed at all*; the firewall is defense-in-depth if Traefik is ever misconfigured |
| REST request authentication | API/Backend (API-key middleware) | — | Must reject unauthenticated calls even on the LAN — network trust is not a substitute for authN |

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Npgsql.EntityFrameworkCore.PostgreSQL` | **10.0.3** `[VERIFIED: nuget.org registry, 2026-09-27]` | EF Core provider for PostgreSQL | Official Npgsql-maintained provider; replaces `Microsoft.EntityFrameworkCore.SqlServer` per D-08 |
| `Microsoft.EntityFrameworkCore.Design` | **10.0.12** `[VERIFIED: nuget.org registry]` | `dotnet ef` tooling, migrations, bundles | Needed to author migrations and build the `efbundle` used by the installer (OPS-07) |
| `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` | **10.0.12** `[VERIFIED: nuget.org registry]` | Persist Data Protection key ring to the database | `PersistKeysToDbContext<T>()` requires this package; closes Pitfall 15 |
| `prometheus-net.AspNetCore` | **8.2.1** `[VERIFIED: nuget.org registry]` | `/metrics` endpoint, HTTP request metrics | Purpose-built, minimal-setup Prometheus exporter (OTel's Prometheus exporter is still experimental per project-level STACK.md) |
| `Microsoft.Extensions.Http.Resilience` | **10.10.0** `[VERIFIED: nuget.org registry]` | Retry/circuit-breaker on outbound `HttpClient`s | Standard resilience story for .NET 10; used later for aggregator/Anthropic calls, wire it in now so the pattern exists |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `MailKit` | **4.18.0** `[VERIFIED: nuget.org registry]` | SMTP via existing Postfix relay | Grafana alert contact point is email (D-19); deploy-outcome email (D-05) |
| `Microsoft.AspNetCore.Mvc.Testing` | **10.0.12** `[VERIFIED: nuget.org registry]` | In-process integration tests | REST auth tests (API-02), health-check tests |
| `Microsoft.EntityFrameworkCore.InMemory` | **10.0.12** `[VERIFIED: nuget.org registry]` | Fast unit tests | Anything not exercising Postgres-specific behaviour (peer auth, role grants, reporting-view SELECT) |

### Development/Test Tools

| Tool | Version | Purpose | Notes |
|------|---------|---------|-------|
| `xunit.v3` | **4.0.1** `[VERIFIED: nuget.org registry — newer than the 3.2.2 cited in project-level STACK.md/CLAUDE.md]` | Test framework | Verify the 3.x→4.x jump doesn't break the reference project's conventions before pinning; 3.2.2 remains a safe fallback if 4.x has breaking API changes |
| `xunit.runner.visualstudio` | **4.0.0** `[VERIFIED: nuget.org registry]` | Test discovery/runner | Matches whichever `xunit.v3` major is pinned |
| `FluentAssertions` | **8.11.0** `[VERIFIED: nuget.org registry]` | Assertions | — |
| `NSubstitute` | **6.2.0** `[VERIFIED: nuget.org registry]` | Mocking | — |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `age` for backup encryption | GPG | `age` has no config files, no legacy ciphers, and is purpose-built for exactly "encrypt to a public key, decrypt only with the private key" (D-16); GPG is heavier and has more footguns for this narrow use case `[CITED: github.com/FiloSottile/age]` |
| Peer auth (OS-user-to-role mapping) | `scram-sha-256` password auth over the Unix socket | Peer auth means zero DB passwords exist anywhere, matching D-09/D-07 exactly; password auth would need a secret in the env file for no security benefit on a single-instance local socket |
| `dotnet ef migrations bundle --self-contained` | Running `dotnet ef database update` directly on the LXC | The LXC only has the ASP.NET Core **runtime**, not the SDK (per the reference deployment's install script) — a self-contained bundle needs no SDK/project source on the target machine `[CITED: learn.microsoft.com/ef/core/managing-schemas/migrations/applying]` |
| Grafana's core PostgreSQL datasource | `yesoreyeram-infinity-datasource` for dashboards | Infinity is for computed/business-logic data from the REST API (later phases); Phase 1 has no financial data yet, so the core SQL datasource against `reporting` views is the only need |

**Installation:**
```bash
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.3
dotnet add package Microsoft.EntityFrameworkCore.Design --version 10.0.12
dotnet add package Microsoft.AspNetCore.DataProtection.EntityFrameworkCore --version 10.0.12
dotnet add package prometheus-net.AspNetCore --version 8.2.1
dotnet add package Microsoft.Extensions.Http.Resilience --version 10.10.0
dotnet add package MailKit --version 4.18.0
```

**Version verification performed:** all NuGet versions above were confirmed live against `api.nuget.org/v3-flatcontainer/<id>/index.json` on 2026-09-27 (excluding `.NET 11` preview/rc versions, which are already appearing in the feed but do not apply — this project targets .NET 10). Re-verify at implementation time since several of these packages (Npgsql provider, resilience) ship frequently.

## Package Legitimacy Audit

> This phase's ecosystem is **NuGet**, which the `package-legitimacy check` seam does not cover (npm/PyPI/crates only). The equivalent manual check below was performed directly against `api.nuget.org` (the authoritative NuGet registry) for every package this phase introduces. All are long-established, high-download, Microsoft- or maintainer-verified packages — no slopsquatting risk was found.

| Package | Registry | Publisher | Verdict | Disposition |
|---------|----------|-----------|---------|-------------|
| `Npgsql.EntityFrameworkCore.PostgreSQL` | nuget.org | Npgsql project (`npgsql/efcore.pg`, long-standing OSS project, millions of downloads) | OK | Approved |
| `Microsoft.EntityFrameworkCore.Design` | nuget.org | Microsoft (first-party) | OK | Approved |
| `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` | nuget.org | Microsoft (first-party) | OK | Approved |
| `prometheus-net.AspNetCore` | nuget.org | `prometheus-net` OSS project (`prometheus-net/prometheus-net`, de facto standard .NET Prometheus client) | OK | Approved |
| `Microsoft.Extensions.Http.Resilience` | nuget.org | Microsoft (first-party) | OK | Approved |
| `MailKit` | nuget.org | jstedfast (long-established, widely used .NET mail library) | OK | Approved |
| `xunit.v3`, `xunit.runner.visualstudio`, `FluentAssertions`, `NSubstitute` | nuget.org | Respective OSS maintainers, all pre-existing conventions from the reference project | OK | Approved |

**Packages removed due to SLOP verdict:** none.
**Packages flagged as suspicious [SUS]:** none.

Two non-NuGet, host-level tools this phase also installs deserve the same scrutiny even though they're not package-manager dependencies:
- `age`/`age-keygen` — official `FiloSottile/age` (maintained by a Go/cryptography maintainer, no known supply-chain issues) `[CITED: github.com/FiloSottile/age]`. Install via the distro/PPA or a pinned GitHub release binary with checksum verification — do not `curl | sh` an installer.
- `gitleaks`, `zizmor`, `actionlint` — all install as **GitHub Actions steps or pinned binaries in CI only**, never on the LXC. Pin each action reference to a commit SHA per SEC-09 (the same rule these tools exist to enforce).

## Architecture Patterns

### System Architecture Diagram

```
Developer                GitHub (public repo)                         Household LXC
──────────                ─────────────────────                        ─────────────

git tag v1.2.3 ──push──▶ [tag ruleset: must be                         
                           reachable from main,                        
                           strict semver, only                         
                           trusted users can create]                   
                              │                                        
                              ▼                                        
                  ┌─────────────────────────┐                          
                  │ build job (GitHub-hosted) │                        
                  │  - dotnet publish          │                        
                  │  - dotnet ef migrations    │                        
                  │    bundle --self-contained │                        
                  │  - zip artifact            │                        
                  │  - actions/attest-build-   │                        
                  │    provenance (Sigstore    │                        
                  │    public-good instance)   │                        
                  │  - attach bundle.sigstore  │                        
                  │    .json as a release      │                        
                  │    asset (for OFFLINE      │                        
                  │    verification later)     │                        
                  └───────────┬───────────────┘                        
                              │ creates DRAFT release                   
                              ▼                                        
                  ┌─────────────────────────┐                          
                  │ Environment: deploy        │                        
                  │  required reviewer =       │                        
                  │  operator (manual click)   │                        
                  └───────────┬───────────────┘                        
                              │ approved                                
                              ▼                                        
                  ┌─────────────────────────┐                          
                  │ publish job (GitHub-      │                        
                  │  hosted): marks release   │                        
                  │  PUBLISHED. Actions run   │                        
                  │  ends here (D-05).        │                        
                  └───────────┬───────────────┘                        
                              │                                        
                              │ unauthenticated poll,                  
                              │ GET /releases/latest                    
                              │ (well under the 60 req/hr               
                              │  unauthenticated API limit              
                              │  at a multi-minute interval)            
                              ▼                                        
                                                        ┌──────────────────────────┐
                                                        │ systemd timer (LXC)        │
                                                        │  every N minutes            │
                                                        └───────────┬──────────────┘
                                                                    ▼
                                                        ┌──────────────────────────┐
                                                        │ ledger-deploy (root)       │
                                                        │  1. download artifact.zip  │
                                                        │     + bundle.sigstore.json │
                                                        │     (public release assets,│
                                                        │     no token needed)       │
                                                        │  2. gh attestation verify  │
                                                        │     --bundle <file>        │
                                                        │     --owner <org>          │
                                                        │     --repo <org>/<repo>    │
                                                        │     --signer-workflow ...  │
                                                        │     --source-ref refs/     │
                                                        │       heads/main           │
                                                        │     (FULLY OFFLINE — no    │
                                                        │     GitHub API call, no    │
                                                        │     token)                 │
                                                        │  3. refuse + alert if      │
                                                        │     verify fails, BEFORE   │
                                                        │     unpacking              │
                                                        │  4. pre-migration backup   │
                                                        │     (if migration present) │
                                                        │  5. run efbundle as        │
                                                        │     migrator role          │
                                                        │  6. unpack to releases/    │
                                                        │     <version>, swap        │
                                                        │     current symlink        │
                                                        │  7. restart systemd unit   │
                                                        │  8. health check; auto-    │
                                                        │     rollback UNLESS a      │
                                                        │     migration ran          │
                                                        │  9. report result (email + │
                                                        │     Prometheus metric)     │
                                                        └───────────┬──────────────┘
                                                                    ▼
                                              ┌─────────────────────────────────────┐
                                              │ ASP.NET Core host (systemd, user=ledger)│
                                              │  - main Kestrel endpoint (LAN/VPN only, │
                                              │    behind Traefik)                     │
                                              │  - /metrics + /health on 127.0.0.1 only │
                                              │  - reads /etc/ledger/env (640 root:ledger)│
                                              └───────┬─────────────────┬────────────┘
                                                       │ Unix socket      │ SELECT only
                                                       ▼                  ▼
                                          ┌─────────────────────┐  ┌───────────────┐
                                          │ PostgreSQL            │  │ Grafana         │
                                          │  no TCP listener,      │  │  Viewer logins  │
                                          │  peer auth only        │  │  (2), anon/     │
                                          │  - runtime role (DML)  │◄─┤  snapshots off  │
                                          │  - migrator role (DDL) │  │  reachable via  │
                                          │  - grafana_reader      │  │  Traefik +      │
                                          │    (SELECT on          │  │  IP allowlist   │
                                          │    reporting views)    │  └───────────────┘
                                          └─────────────────────┘
                                                       ▲
                                                       │ pg_dump (nightly + pre-migration)
                                          ┌─────────────────────┐
                                          │ backup timer          │
                                          │  pg_dump | age -r     │
                                          │  <public key>          │
                                          │  → local disk, GFS     │
                                          │  retention (7/4/12)    │
                                          └─────────────────────┘
```

### Recommended Project Structure

```
Ledger.slnx
Ledger.Domain/             # POCOs, IBankProvider (future), no EF/HTTP refs
Ledger.Repository/         # EF Core DbContext, migrations, IDataProtectionKeyContext
Ledger.Service/            # ASP.NET Core host: REST controllers, health, /metrics endpoint
Ledger.UnitTests/          # xUnit v3, EF Core InMemory, NSubstitute
Ledger.IntegrationTests/   # xUnit v3, Mvc.Testing, real Postgres (user-secrets locally / service container in CI)
deploy/
├── provision.sh           # idempotent LXC setup: packages, users, roles, systemd units, firewall
├── ledger-deploy           # root installer: download, verify, backup, migrate, install, restart, health-check
├── ledger-deploy-poll.timer / .service   # systemd timer unit calling ledger-deploy
├── ledger-backup.timer / .service        # nightly pg_dump | age
└── grafana/
    ├── provisioning/datasources/postgres.yaml
    ├── provisioning/dashboards/           # empty in Phase 1 — no financial dashboards yet
    └── grafana.ini.template               # anonymous/snapshots disabled, admin renamed
.github/
├── workflows/release.yml   # build + attest + draft release (GitHub-hosted only)
├── workflows/publish.yml   # environment-gated publish job
├── workflows/ci.yml         # zizmor, actionlint, gitleaks (full history), dotnet test
└── dependabot.yml           # github-actions + nuget ecosystems
docs/
└── lxc-setup.md             # every one-time, unscriptable step (OPS-03)
```

### Structure Rationale

Mirrors the reference project's Domain → Repository → Service layering (already an established pattern per `01-CONTEXT.md` canonical refs) with a `deploy/` directory holding everything that runs **on the LXC** as a distinct trust boundary from everything that runs **in GitHub Actions** (`.github/workflows/`). Keeping these physically separate in the repo makes the "no GitHub-executed code ever runs on the server" boundary (D-02) visible in the file tree, not just in prose.

### Pattern 1: Offline attestation verification via a bundle release asset

**What:** The release-build job attests the artifact with `actions/attest-build-provenance`, then also runs `gh attestation download` (or captures the attestation the action already produces) and uploads the resulting Sigstore bundle JSON as an additional release asset alongside the artifact zip and checksums.

**When to use:** Any time the verifying party (here, the LXC installer) must not hold a GitHub token, and the repo/artifact is public. This is exactly this project's situation: D-02 mandates no GitHub-executed code and CONTEXT.md's own discretion note says "unauthenticated API polling ... is probably enough" for the poll — but attestation verification against the live API is a *separate* call with its own auth requirement.

**Example:**
```yaml
# Source: actions/attest-build-provenance docs + gh-cli attestation docs
- name: Attest build provenance
  uses: actions/attest-build-provenance@<pinned-sha>  # v2.x
  id: attest
  with:
    subject-path: 'ledger-${{ github.ref_name }}.zip'

- name: Save bundle for offline verification
  run: |
    cp "${{ steps.attest.outputs.bundle-path }}" "ledger-${{ github.ref_name }}.sigstore.json"

- name: Attach bundle to draft release
  uses: softprops/action-gh-release@<pinned-sha>
  with:
    draft: true
    files: |
      ledger-${{ github.ref_name }}.zip
      ledger-${{ github.ref_name }}.sigstore.json
      ledger-${{ github.ref_name }}.efbundle
```
```bash
# Source: cli.github.com/manual/gh_attestation_verify — installer side, fully offline
gh attestation verify "/tmp/ledger-${TAG}.zip" \
  --bundle "/tmp/ledger-${TAG}.sigstore.json" \
  --owner <github-org-or-user> \
  --repo <owner>/<repo> \
  --signer-workflow "<owner>/<repo>/.github/workflows/release.yml" \
  --source-ref "refs/heads/main"
```
`[CITED: cli.github.com/manual/gh_attestation_verify]` — `--bundle` performs verification against attestations stored on disk; no GitHub API call is made when `--bundle` is supplied, so no token is needed on the LXC.

### Pattern 2: Peer-authenticated, role-separated PostgreSQL over a Unix socket only

**What:** `postgresql.conf` has no `listen_addresses` (or is set to `''`), so there is no TCP listener at all. `pg_hba.conf` uses `peer` for all `local` entries. Three roles exist; OS users map to them via `pg_ident.conf` where the OS user name and role name differ (e.g. Grafana's OS user is `grafana`, but the role should be named `grafana_reader` — peer auth's default 1:1 name match only works when they're identical, otherwise an explicit ident map is required).

**When to use:** Single-instance, single-LXC deployment where no other host ever needs network access to the database (D-09, D-12).

**Example:**
```
# /etc/postgresql/<version>/main/pg_hba.conf
# Source: postgresql.org/docs/current/auth-pg-hba-conf.html
# TYPE  DATABASE  USER            ADDRESS  METHOD
local   ledger    ledger_runtime           peer
local   ledger    ledger_migrator          peer map=ledger_migrator_map
local   ledger    grafana_reader           peer map=grafana_map
```
```
# /etc/postgresql/<version>/main/pg_ident.conf
# MAPNAME              SYSTEM-USERNAME  PG-USERNAME
ledger_migrator_map    root             ledger_migrator
grafana_map            grafana          grafana_reader
```
`[CITED: postgresql.org/docs/current/auth-pg-hba-conf.html]` — peer auth "obtains the client's operating system user name from the operating system and checks if it matches the requested database user name"; `map=` is the documented mechanism for OS-user-to-role names that don't match 1:1. **This ident-map detail is not explicitly stated in D-09/D-11 and should be a Wave 0 spike item** — confirm the exact OS user Grafana's systemd unit runs as before writing `provision.sh`.

### Pattern 3: EF Core migration bundle run by a role with no interactive login

**What:** `dotnet ef migrations bundle --self-contained -r linux-x64 -o efbundle` produces a single native executable requiring no .NET SDK on the target machine. The installer runs it with `--connection` pointed at the migrator role over the Unix socket.

**When to use:** Every deploy that includes schema changes (OPS-07); the LXC only has the ASP.NET Core *runtime* installed, not the SDK.

**Example:**
```bash
# Source: learn.microsoft.com/ef/core/managing-schemas/migrations/applying
# Build (CI, GitHub-hosted):
ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations bundle \
  --self-contained -r linux-x64 \
  --project Ledger.Repository --startup-project Ledger.Service \
  -o ./artifacts/efbundle

# Apply (installer, on the LXC, as root invoking the migrator-role connection):
./efbundle --connection "Host=/var/run/postgresql;Database=ledger;Username=ledger_migrator"
```
Peer auth means this connection string carries no password — the OS user the installer runs the bundle as (mapped via `pg_ident.conf`) supplies the identity.

### Pattern 4: Data Protection keys persisted to EF Core, protected by a certificate

**What:** `PersistKeysToDbContext<T>()` stores the key ring in a `DataProtectionKeys` table (via a context implementing `IDataProtectionKeyContext`); `ProtectKeysWithCertificate(thumbprint)` encrypts the key material at rest using a certificate whose private key lives in the env file (path + password, per Claude's Discretion note).

**Example:**
```csharp
/// Registers Data Protection with database-persisted, certificate-protected keys.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<LedgerDbContext>()
    .ProtectKeysWithCertificate(builder.Configuration["DataProtection:CertificateThumbprint"]!)
    .SetApplicationName("HouseholdLedger");
```
`[CITED: learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers]` — confirms `PersistKeysToDbContext` requires `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` and a `DbContext` implementing `IDataProtectionKeyContext`, and that `ProtectKeysWithCertificate` takes a certificate thumbprint. `SetApplicationName` must be called explicitly so the key ring is stable across redeploys (a fresh `WorkingDirectory`/deployment path would otherwise be treated as a different application) — this directly targets OPS-05 and Pitfall 15.

### Anti-Patterns to Avoid

- **Naming the Grafana database role identically to the app's runtime role "for simplicity":** collapses the read-only boundary Grafana's own docs describe as a UI convention, not a DB guarantee (project-level PITFALLS.md Pitfall 10). Always a dedicated `grafana_reader` role, SELECT-only, scoped to the `reporting` schema.
- **Running `dotnet ef database update` directly against the production connection string from a workflow or ad hoc SSH session:** bypasses the pre-migration backup, the migrator-role isolation, and the "installer is the only thing that changes the running system" invariant (D-03). Migrations only ever run through the `efbundle` inside `ledger-deploy`.
- **Treating the GitHub Environment reviewer gate as sufficient isolation on its own:** research confirms Environments control *whether a job runs*, not what it can do once running — this project sidesteps the whole class of risk by having no self-hosted runner at all (D-02), which is stronger than the mitigations project-level PITFALLS.md Pitfall 12 recommends for a runner-based design that no longer applies here.
- **Skipping the bundle-asset step and assuming unauthenticated `gh attestation verify` "just works" against a public repo:** it does not — a GitHub CLI discussion thread and the rate-limit changelog both confirm attestation API verification currently requires authentication even for public repos `[CITED: github.com/cli/cli/discussions/11007]`. Always ship the offline bundle.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|--------------|-----|
| Artifact integrity verification | A custom checksum-file-and-GPG-signature scheme | `actions/attest-build-provenance` + `gh attestation verify --bundle` | Sigstore-backed, keyless, tied to the exact workflow/ref that built it; a hand-rolled checksum only proves the file wasn't corrupted in transit, not that GitHub Actions actually built it |
| Secret-at-rest encryption for Data Protection keys / consent tokens | A bespoke AES-GCM wrapper around a hardcoded key | ASP.NET Core Data Protection (`PersistKeysToDbContext` + `ProtectKeysWithCertificate`) | Handles key rotation, versioning, and purpose separation correctly; a hand-rolled wrapper is exactly Pitfall 15's failure mode (ephemeral key ring) waiting to happen again under a different name |
| Backup encryption | A custom OpenSSL `enc` script with a passphrase | `age` with a recipient public key | Asymmetric — the LXC that creates backups never holds a key that can decrypt them (D-16); a passphrase-based scheme means the passphrase itself becomes a secret the LXC must store, defeating the purpose |
| GitHub Actions security auditing | Hand-written grep for `uses:` lines | `zizmor` (dedicated static analyzer, SARIF output, GitHub code-scanning integration) + `actionlint` (shellcheck-backed) | Purpose-built tools cover template-injection, excessive permissions, and unpinned-action classes of bugs that a grep script will always miss some of `[CITED: zizmor.sh]` |
| API-key hashing/storage for REST auth | Storing keys in plaintext or with a fast hash (MD5/SHA-256 alone) | A slow, salted hash (e.g. `Rfc2898DeriveBytes`/PBKDF2, or `BCrypt.Net-Next`) applied once at creation time, key shown once | API keys are long-lived bearer credentials; a fast unsalted hash is crackable if the DB ever leaks, and this is exactly the kind of "everyone gets this subtly wrong" problem a library exists for |

**Key insight:** every item in this table already has a documented, well-audited solution referenced directly in this project's own canonical research (project-level PITFALLS.md, STACK.md) or in official vendor docs (`gh` CLI, ASP.NET Core Data Protection). The discipline this phase requires is *wiring these correctly together*, not inventing new primitives — which is exactly why the pitfalls below matter more than the stack choices.

## Common Pitfalls

### Pitfall 1: `gh attestation verify` silently assumed to work unauthenticated against a public repo

**What goes wrong:** The installer is written to call `gh attestation verify <artifact> --owner <org>` (API mode, no `--bundle`) on the assumption that a public repo's attestations are publicly readable. It works during manual testing (because the developer's own `gh` session is authenticated) and then fails in production on the LXC, which has no GitHub token by design (D-02's "unauthenticated API polling is probably enough").

**Why it happens:** GitHub's REST API rate-limits and gates attestation lookups by authentication even for public repos, and this is easy to miss because `gh` CLI silently uses any ambient credentials (a maintainer's `gh auth login` session) during development `[CITED: github.com/cli/cli/discussions/11007]`.

**How to avoid:** Always attach the Sigstore bundle as a release asset (Pattern 1 above) and verify with `--bundle` on the LXC — this path performs no API call and needs no token.

**Warning signs:** The installer works when run manually by a developer over SSH (their `gh` session is authenticated) but fails identically every time the systemd timer triggers it unattended.

### Pitfall 2: Grafana's OS user doesn't match its PostgreSQL role name, so peer auth fails silently

**What goes wrong:** `provision.sh` creates a `grafana_reader` role and assumes peer auth "just works" because that's the pattern used for `ledger_runtime`/`ledger_migrator` — but Grafana's systemd unit runs as OS user `grafana` (the package's default), not `grafana_reader`, so peer auth's default exact-name-match rejects the connection.

**Why it happens:** Peer auth's simplest form requires the OS username and DB role name to be identical; `pg_ident.conf` mapping (Pattern 2) is a separate, easy-to-forget step, and the failure mode is a plain "peer authentication failed" error that looks identical to a typo in the role name.

**How to avoid:** Decide the exact OS user each systemd service runs as *before* writing role-creation DDL, and write the `pg_ident.conf` map entries explicitly for any role whose name won't match its OS user 1:1.

**Warning signs:** Grafana's datasource health check fails with a peer-authentication error even though `psql -U grafana_reader` works fine when run manually as `sudo -u grafana`.

### Pitfall 3: Grafana's PostgreSQL datasource Unix-socket URL format is undocumented in the mainstream config examples

**What goes wrong:** The datasource provisioning YAML is written with a `host:port` style `url` (copying the common TCP example from Grafana's docs), which fails against a socket-only PostgreSQL with a confusing "invalid URL escape" or connection-refused error.

**Why it happens:** Grafana's most-copied provisioning examples assume TCP; the Unix-socket form (`url: /var/run/postgresql`, no port) is documented but far less prominent, and older Grafana versions had outstanding bugs specifically around socket paths containing colons `[CITED: github.com/grafana/grafana issues #58690, #29680, #22119 — MEDIUM confidence, some of these issues are several years old and may be fixed in the current Grafana major]`.

**How to avoid:** Provision the datasource with `url: /var/run/postgresql` (no port) and verify with an actual Grafana query against the `reporting` schema in Wave 0, rather than assuming the docs example transfers directly — this is exactly the kind of "looks done but isn't" gap Nyquist validation exists to catch.

**Warning signs:** Grafana's datasource "Save & Test" reports a connection error that doesn't match any PostgreSQL-side log entry (because the request never reached PostgreSQL — Grafana rejected the malformed URL first).

### Pitfall 4: `pct create` / provisioning script assumes PostgreSQL from the Ubuntu 24.04 default repo, but the distro version is stale

**What goes wrong:** `apt install postgresql` on Ubuntu 24.04 installs whatever major version Ubuntu shipped with 24.04's release (historically several majors behind current), silently locking the project out of newer PostgreSQL features and a longer support window, without anyone noticing until an upgrade is needed later.

**Why it happens:** Ubuntu LTS point-in-time-freezes package versions; PostgreSQL's own release cadence (annual majors, ~5-year support window) moves faster than Ubuntu's package refresh.

**How to avoid:** Install from the **PGDG APT repository** (`apt.postgresql.org`) rather than Ubuntu's default repo, and explicitly pin the major version in `provision.sh` (PostgreSQL 18 is current as of this research — released 2025-09-25, at patch 18.6 as of 2026-08-13 `[CITED: postgresql.org/about/news/postgresql-18-released-3142]`). Confirm PGDG has a `noble` (24.04) repo before committing to this in Wave 0.

**Warning signs:** `psql --version` on the LXC reports a major version noticeably older than the current PostgreSQL release.

### Pitfall 5: Data Protection key ring lost on redeploy despite "persisting to the database"

**What goes wrong:** Keys are persisted to `DataProtectionKeys` (closing the "ephemeral in-memory key ring" failure mode) but `SetApplicationName` is never called, or the deploy process changes the working directory / assembly name between the old and new release folders (`releases/<version>/...`) in a way ASP.NET Core's default application-discriminator treats as a *different* application — so the new release can't decrypt what the old release encrypted, even though both point at the same database table.

**Why it happens:** The default Data Protection application discriminator is derived from the content root path by default in some hosting scenarios; a `releases/<version>` symlink-swap deploy layout (D-04) changes that path on every release unless explicitly overridden.

**How to avoid:** Call `SetApplicationName("HouseholdLedger")` (or similar, hardcoded) explicitly, and **verify this with an actual test**: encrypt a canary value, deploy a new release (path changes), decrypt the same canary — this is precisely what OPS-05 already asks for, so build it as an automated check rather than a one-time manual glance.

**Warning signs:** Every previously-encrypted value fails to decrypt starting exactly at the first deploy after the initial one, even though the database itself is unchanged (this is project-level PITFALLS.md Pitfall 15, made worse by this project's specific `releases/<version>` layout).

### Pitfall 6: Semver tag pattern in the workflow trigger is not the same as a validated semver in the shell

**What goes wrong:** `on: push: tags: ['v*.*.*']` is a *glob*, not a regex — it will also match `v1.2.3-evil` or `vXX.YY.ZZ`-shaped garbage that isn't a real semver, and if that raw tag value is later interpolated into a `run:` shell step (e.g. to build a download URL or a version string), it becomes a shell-injection vector.

**Why it happens:** GitHub Actions' tag-filter glob syntax looks like it constrains input more than it does; the real validation has to happen in a script step, and the tag value has to reach that step only via `env:`, never string-interpolated into `run:` (this is exactly D-01's requirement, restated here as a concrete workflow bug class).

**How to avoid:**
```yaml
# Source: derived from D-01 + GitHub's own untrusted-input guidance
- name: Validate tag is strict semver and reachable from main
  env:
    TAG: ${{ github.ref_name }}
  run: |
    if ! [[ "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
      echo "::error::Tag '$TAG' is not a strict semver tag" >&2
      exit 1
    fi
    git merge-base --is-ancestor "$TAG" origin/main || { echo "::error::Tag is not reachable from main"; exit 1; }
```

**Warning signs:** Any workflow step that writes `${{ github.ref_name }}` (or any other attacker-influenceable context value) directly inside a `run:` block's command text rather than reading it from `$TAG`.

## Runtime State Inventory

Not applicable — this is Phase 1 of a greenfield project (only `.claude/`, `.gitignore` and `.planning/` exist in the repo today, per `01-CONTEXT.md`'s own Existing Code Insights). There is no prior running system, no prior database, no prior OS-registered state to migrate away from. **Skip condition met — omitted.**

## Common Pitfalls (continued from project-level research, re-verified as still applicable)

The following project-level pitfalls (`01-CONTEXT.md`'s canonical refs point here) remain directly applicable to this phase and were re-checked against this phase's pull-based (not self-hosted-runner) design:

- **Grafana's read-only promise is a UI convention, not a DB guarantee** — still fully applicable; addressed by D-10/D-21 and Pattern 2 above.
- **Grafana anonymous access / public snapshots / link sharing** — still fully applicable; addressed by D-21 (`auth.anonymous.enabled = false`, `snapshots.enabled = false` `[CITED: grafana.com/docs/grafana/latest/setup-grafana/configure-access/configure-authentication/anonymous-auth]`).
- **Self-hosted runner exposed to fork PRs (Pitfall 12)** — **superseded**: D-02 eliminates the self-hosted runner entirely, so this specific attack class does not apply to this project's deploy path. The residual risk it points at (outside-contributor workflow runs, tag-creation restriction) is still directly relevant to SEC-08 and is retained via GitHub's environment-approval and outside-collaborator-approval settings.
- **Mutable Action tags (Pitfall 13)** — still fully applicable; addressed by SEC-09/zizmor.
- **Public repo leaks real data (Pitfall 14)** — still fully applicable; addressed by SEC-10/gitleaks full-history + GitHub secret scanning + push protection.
- **Data Protection keys not persisted (Pitfall 15)** — still fully applicable and sharpened above (Pitfall 5 in this document) for this project's specific `releases/<version>` deploy layout.

## Code Examples

### Verified patterns from official sources

### GitHub Environment required reviewers + outside-collaborator approval (repo settings, not workflow YAML)
`[CITED: docs.github.com/actions/reference/workflows-and-actions/deployments-and-environments; docs.github.com/repositories/.../managing-github-actions-settings-for-a-repository]`
- Repo Settings → Environments → `deploy` → **Required reviewers**: add the operator (GitHub allows up to 6 reviewers per environment; only one approval is needed to proceed).
- Repo Settings → Actions → General → **Fork pull request workflows**: set to **"Require approval for all outside collaborators"** (not the GitHub default, which only requires approval for first-time contributors).

### GitHub ruleset restricting tag creation (repo/org settings)
`[CITED: docs.github.com/organizations/managing-organization-settings/creating-rulesets-for-repositories-in-your-organization]`
- Create a **tag ruleset** (not the deprecated "tag protection rules", which are removed in favor of rulesets going forward) targeting tags matching `v*.*.*` (fnmatch syntax), with **Restrict creations** enabled so only users with bypass permission (the repo owner) can create matching tags.

### Dependabot config for SHA-pinned GitHub Actions + NuGet
```yaml
# Source: docs.github.com/code-security/dependabot — Dependabot fully supports
# updating SHA-pinned actions (proposes a PR bumping both SHA and version comment)
version: 2
updates:
  - package-ecosystem: "github-actions"
    directory: "/"
    schedule:
      interval: "weekly"
  - package-ecosystem: "nuget"
    directory: "/"
    schedule:
      interval: "weekly"
```

### Traefik IP allowlist for LAN/VPN-only routes, behind an existing edge Traefik
```yaml
# Source: doc.traefik.io/traefik/v3.2/middlewares/http/ipallowlist (file-provider dynamic config)
http:
  middlewares:
    lan-vpn-only:
      ipAllowList:
        sourceRange:
          - "<LAN subnet>/24"
          - "<VPN subnet>/24"
        ipStrategy:
          depth: 1   # set to the number of trusted hops between the client and Traefik
  routers:
    ledger-grafana:
      rule: "Host(`grafana.<internal-domain>`)"
      entryPoints: [websecure]
      middlewares: [lan-vpn-only]
      service: ledger-grafana
      tls:
        certResolver: letsencrypt
```
`[CITED: doc.traefik.io/traefik/v3.2/middlewares/http/ipallowlist]` — the `depth`/`forwardedHeaders.trustedIPs` pairing must match the reference deployment's existing `ReverseProxy__KnownProxies` pattern (already documented in `quest-board-dnd`'s `server-setup.md`) so the app also trusts `X-Forwarded-For` only from Traefik's own IP, not from the internet.

### Grafana datasource provisioning against a Unix-socket-only PostgreSQL
```yaml
# Source: grafana.com/docs/grafana/latest/datasources/postgres/configure/ (adapted for socket path)
apiVersion: 1
datasources:
  - name: Ledger Reporting
    type: postgres
    access: proxy
    url: /var/run/postgresql
    user: grafana_reader
    jsonData:
      database: ledger
      sslmode: disable
      postgresVersion: 1800   # PostgreSQL 18.x — verify against the version actually installed
    # no secureJsonData.password — peer auth supplies identity, matching D-09
```

### `grafana.ini` hardening keys (provisioned, not clicked)
```ini
; Source: grafana.com/docs/grafana/latest/setup-grafana/configure-access/configure-authentication/anonymous-auth
[auth.anonymous]
enabled = false

[snapshots]
enabled = false

; Source: WebSearch — "Public Dashboards" renamed "Shared Dashboards" in Grafana 11.5+;
; setting below is the current key name — verify against the installed Grafana version's changelog
[public_dashboards]
enabled = false
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|---------------|--------|
| Self-hosted GitHub Actions runner in the app LXC (reference deployment's pattern) | Fully pull-based: no runner anywhere, LXC polls and self-installs | This project's own D-02 decision, 2026-09-27 | Eliminates the entire fork-PR-executes-on-my-box attack class (project-level Pitfall 12) instead of merely mitigating it |
| GitHub "tag protection rules" | GitHub **rulesets** (tag protection rules deprecated in GHES 3.16+, GitHub.com already steering toward rulesets) | Ongoing GitHub platform migration | Use rulesets from day one — don't build on a feature already flagged for removal |
| GitHub release assets mutable after publish | **Immutable releases**, GA as of 2025-10-28 | GitHub Changelog, 2025-10-28 `[CITED: github.blog/changelog/2025-10-28-immutable-releases-are-now-generally-available]` | Enable at the repo level — combined with SHA-pinning and attestation, this closes the "a compromised maintainer account swaps the release asset after the fact" gap entirely |
| MS SQL Server shared instance, `sa` login | PostgreSQL in-LXC, Unix socket, peer auth, three scoped roles | This project's own D-08 decision, 2026-09-27 | Removes cross-container TLS/firewall work and the shared-instance blast radius entirely; project-level STACK.md's SQL Server recommendations no longer apply to this phase |
| Grafana "Public Dashboards" | Renamed "Shared/Externally Shared Dashboards" | Grafana 11.5 | Same underlying risk (Pitfall 11) — verify the config key name (`public_dashboards`) against whatever Grafana version is actually installed, since the feature has been renamed at least once |

**Deprecated/outdated:**
- Tag protection rules (GitHub) — superseded by rulesets; don't build new automation against the deprecated feature.
- `runs-on: self-hosted` deploy pattern from the reference project — explicitly not carried forward per D-02; do not reuse `quest-board-dnd`'s `binary-release.yml` deploy job shape, only its build-job shape (with attestation added).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Grafana's systemd unit runs as OS user `grafana` by default on a PGDG/distro-packaged install | Pattern 2, Pitfall 2 | If the actual OS user differs, the `pg_ident.conf` map entry is wrong and Grafana's datasource fails at Wave 0 — cheap to verify by checking `ps aux \| grep grafana` right after `provision.sh` installs it |
| A2 | PostgreSQL 18 (not 17) is the right default major version to pin in `provision.sh` | Standard Stack, Pitfall 4 | This is explicitly Claude's Discretion in CONTEXT.md — 18 is confirmed current via `[CITED: postgresql.org]`, but the PGDG `noble` (24.04) repo's availability for major 18 was not directly confirmed this session and should be checked in Wave 0 before committing |
| A3 | `xunit.v3` 4.0.1 is a safe upgrade from the reference project's pinned 3.2.2 | Standard Stack | If 4.x has breaking changes vs. the reference project's test-writing conventions, pin to 3.2.2 instead (still current and supported) rather than blocking on an untested major bump |
| A4 | Grafana's PostgreSQL datasource accepts a bare directory path (`url: /var/run/postgresql`) for Unix-socket connections in the currently-installed Grafana version, without the older colon-escaping bugs resurfacing | Pattern 3 (Code Examples), Pitfall 3 | Some cited GitHub issues are several years old; if unresolved, the fallback is `scram-sha-256` password auth over the socket for Grafana specifically (breaking the "zero DB passwords" property only for this one role) — flag as a Wave 0 spike, not an assumption to build the whole design around |
| A5 | An unauthenticated poll of `GET /repos/<owner>/<repo>/releases/latest` at a multi-minute interval stays comfortably under GitHub's 60-req/hour unauthenticated rate limit | Architecture diagram, Don't Hand-Roll | If the household ever also runs other unauthenticated GitHub API calls from the same LXC's IP, the shared budget could be exhausted; low risk at household scale, but worth a comment in the poller code |

**If this table is empty:** N/A — five assumptions logged above, all should be confirmed in a Wave 0 spike before the corresponding task is built out fully.

## Open Questions

1. **Does the PGDG APT repository actually publish PostgreSQL 18 packages for Ubuntu 24.04 ("noble") today?**
   - What we know: PostgreSQL 18 is current upstream (`[CITED: postgresql.org]`); PGDG has historically added `noble` support promptly after each Ubuntu LTS release.
   - What's unclear: This session did not directly query `apt.postgresql.org`'s repo listing for a `noble`+`18` combination.
   - Recommendation: Confirm with `apt-cache policy postgresql-18` (or the PGDG repo's package listing page) during Wave 0, before `provision.sh` is finalized. Fallback: PostgreSQL 17 (definitely available) if 18 isn't yet packaged for `noble`.

2. **What OS user does the packaged Grafana systemd unit actually run as on this specific install path (PGDG-adjacent APT vs. Grafana Labs' own APT repo)?**
   - What we know: The Grafana Labs official Debian/Ubuntu package conventionally creates and runs as a `grafana` OS user.
   - What's unclear: Not independently confirmed for the exact install method `provision.sh` will use.
   - Recommendation: `ps aux | grep grafana` immediately after provisioning, before writing the final `pg_ident.conf` map — this is a two-minute check that de-risks Pitfall 2 entirely.

3. **Exact `--signer-workflow` value format for a repo with the release workflow split across two files (build vs. publish, per D-02's two-job design)?**
   - What we know: `--signer-workflow` expects `[host/]<owner>/<repo>/<path>/<to>/<workflow>` and must match the workflow that *signed* the attestation (i.e., the build job's workflow file, not the publish job's).
   - What's unclear: Whether splitting build and publish into two separate workflow files (as D-02 implies — a build job and a separate environment-gated publish job) changes which workflow path is recorded as the signer, versus keeping them as two jobs in one workflow file.
   - Recommendation: Keep build and attest in the **same** workflow file as a job (`release.yml`), and have a *separate* workflow (`publish.yml`) only flip the release from draft to published — this avoids ambiguity about which workflow file signed the attestation, since attestation happens in the build job regardless of which workflow later publishes the release.

## Environment Availability

| Dependency | Required By | Available (this session's dev machine) | Version | Fallback |
|------------|------------|:---:|---------|----------|
| .NET SDK 10 | Building/testing the app skeleton | ✓ | 10.0.112 | — |
| GitHub CLI (`gh`) | Attestation verification pattern (developed/tested locally before LXC deploy) | ✓ | 2.101.0 (2026-09-15) | — |
| Docker | Local Postgres container for tests (D-13) | ✓ | 29.8.1 | — |
| Local PostgreSQL container | Integration tests against real Postgres-specific behaviour | ✗ (not currently running in this session — user's memory confirms a long-running container exists outside this repo) | — | Start the user's existing container before running integration tests; CI uses a Postgres **service container** regardless |
| `psql` | Manual DB inspection during development | ✗ | — | Not required on the dev machine — the operator reaches Postgres via SSH into the LXC itself (D-12), or via a container's `psql` |
| `age` / `age-keygen` | Backup encryption (host-level, not a dev dependency) | ✗ | — | Only needed on the LXC; install via `provision.sh` |
| `gitleaks`, `zizmor`, `actionlint` | CI-only security linting | ✗ (not installed locally) | — | Run exclusively as pinned GitHub Actions steps in `ci.yml`; not required on any dev machine or the LXC |

**Missing dependencies with no fallback:** none — every missing tool above is either CI-only (runs inside GitHub-hosted Actions, installed fresh each run) or LXC-only (installed by `provision.sh`), not a dev-machine blocker.

**Missing dependencies with fallback:** the local Postgres container needs to be started before integration tests run; this is an existing user convention (see memory: "Local Postgres container"), not a gap this phase needs to solve.

## Validation Architecture

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 (`xunit.v3` — pin 3.2.2 per the reference project's proven convention, or 4.0.1 if A3 above is confirmed safe; verify at implementation time) |
| Config file | none yet — greenfield; Wave 0 creates `Ledger.UnitTests`/`Ledger.IntegrationTests` projects and a root `Directory.Build.props`/`.editorconfig` if the reference project uses one |
| Quick run command | `dotnet test Ledger.UnitTests` |
| Full suite command | `dotnet test` (requires the local Postgres container running for `Ledger.IntegrationTests`; CI substitutes a Postgres service container) |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|--------------------|-------------|
| SEC-02 | `ledger_runtime` role cannot execute DDL; `grafana_reader` cannot execute any DML/DDL outside `reporting` SELECT | integration (real Postgres) | `dotnet test Ledger.IntegrationTests --filter Category=DatabaseRoles` | ❌ Wave 0 |
| SEC-03 | A value encrypted via Data Protection round-trips correctly; secrets never appear in `appsettings*.json` committed to the repo | unit + CI grep check | `dotnet test --filter Category=DataProtection` / `ci.yml` grep step | ❌ Wave 0 |
| SEC-05 | App connection string contains no host:port TCP form, only a socket path | unit (config assertion) | `dotnet test --filter Category=Configuration` | ❌ Wave 0 |
| SEC-06 | Triggering a known exception path never logs the raw connection string or API keys | integration (log capture assertion) | `dotnet test --filter Category=LogRedaction` | ❌ Wave 0 |
| SEC-07 | A tampered artifact fails `gh attestation verify --bundle`; installer refuses to unpack it | smoke (bash script, CI or manual) | `deploy/tests/verify-rejects-tampered-artifact.sh` | ❌ Wave 0 |
| SEC-08 | Workflow file has no `runs-on: self-hosted`; publish job is gated by the `deploy` Environment | static check (CI job) | `zizmor .github/workflows/` (part of `ci.yml`) | ❌ Wave 0 |
| SEC-09 | Every third-party `uses:` is SHA-pinned; no untrusted value reaches a `run:` block unquoted via string interpolation | static check (CI job) | `zizmor` + `actionlint` (part of `ci.yml`) | ❌ Wave 0 |
| SEC-10 | Full git history contains no secret-shaped strings | scheduled/CI job | `gitleaks detect --source . --redact --exit-code 2` with `fetch-depth: 0` | ❌ Wave 0 |
| OPS-03 | `systemctl status ledger postgresql grafana-server prometheus` all report `active (running)` after `provision.sh` | manual smoke (checkpoint:human-verify) | documented in `docs/lxc-setup.md` | ❌ Wave 0 |
| OPS-04 | A backup created today can actually be restored following the documented steps | manual, performed once (checkpoint:human-verify per D-18) | documented restore drill | ❌ Wave 0 |
| OPS-05 | A canary value encrypted before a simulated restart/redeploy still decrypts after | integration | `dotnet test --filter Category=DataProtectionRestart` | ❌ Wave 0 |
| OPS-07 | `efbundle` applies a pending migration as the migrator role with no manual step | integration (real Postgres) | `dotnet test Ledger.IntegrationTests --filter Category=Migrations` | ❌ Wave 0 |
| API-02 | A REST call with no/invalid API key returns 401 on every endpoint | integration (`Mvc.Testing`) | `dotnet test --filter Category=ApiAuth` | ❌ Wave 0 |
| DASH-06 | Grafana datasource/dashboard/alerting YAML under `deploy/grafana/provisioning/` parses as valid YAML and matches Grafana's provisioning schema | static check (CI job) | a small YAML-lint step in `ci.yml` | ❌ Wave 0 |
| DASH-08 | `grafana.ini` (as provisioned) has `auth.anonymous.enabled=false`, `snapshots.enabled=false`, `public_dashboards.enabled=false` | static check (config assertion) | grep/assert step in `ci.yml` or a unit test over the template file | ❌ Wave 0 |
| DASH-09 | Grafana, Prometheus and REST return no response when curled from outside the LAN/VPN | manual, external reachability test (checkpoint:human-verify) | documented in `docs/lxc-setup.md` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test Ledger.UnitTests` (fast, no external dependencies)
- **Per wave merge:** `dotnet test` (full suite, requires local Postgres container) + `zizmor`/`actionlint`/`gitleaks` CI job
- **Phase gate:** Full suite green, full CI security-lint job green, plus the two `checkpoint:human-verify` items (OPS-04 restore drill, DASH-09 external reachability) actually performed — not just scripted — before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `Ledger.slnx` + `Ledger.Domain`/`Ledger.Repository`/`Ledger.Service`/`Ledger.UnitTests`/`Ledger.IntegrationTests` projects — none exist yet (greenfield)
- [ ] `Ledger.IntegrationTests/DatabaseFixture.cs` — shared fixture connecting to the user's local Postgres container via `dotnet user-secrets`, matching D-13
- [ ] `.github/workflows/ci.yml` with `zizmor`, `actionlint`, `gitleaks` (full-history) steps — framework install: pin each as a SHA-referenced Action
- [ ] `deploy/tests/verify-rejects-tampered-artifact.sh` — a small bash smoke test that corrupts a byte in a test artifact and asserts `gh attestation verify` exits non-zero

## Security Domain

### Applicable ASVS Categories

ASVS 5.0 restructured its chapter numbering from 4.0's `V1 Encoding`→`V14 Config`-style layout; the current (5.0) chapter list was confirmed directly against the OWASP ASVS GitHub repository this session `[CITED: github.com/OWASP/ASVS/tree/master/5.0/en]`.

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V6 Authentication | yes | Grafana Viewer logins (D-21) via Grafana's own auth; REST per-client API keys (D-22, API-02) — no custom password/session scheme is hand-rolled |
| V8 Authorization | yes | PostgreSQL role-based least privilege (D-10); Grafana Viewer role (no Editor/Admin for partner accounts); API-key scoping per client name |
| V11 Cryptography | yes | ASP.NET Core Data Protection (certificate-protected key ring); `age` asymmetric encryption for backups (D-16) — never a hand-rolled cipher |
| V12 Secure Communication | yes | Traefik terminates TLS (Let's Encrypt) for all LAN/VPN routes; PostgreSQL replaces "verified TLS" with "no network listener at all" (D-14) — a stronger control than TLS for a same-host connection |
| V13 Configuration | yes | This is the phase's core: **13.3.1** "a secrets management solution... Secrets must not be included in application source code or build artifacts" (env file, D-07); **13.3.2** least privilege on secret access (root:ledger 640 perms); **13.2.2** least-privilege service accounts (the three Postgres roles) `[VERIFIED: raw.githubusercontent.com/OWASP/ASVS/master/5.0/en/0x22-V13-Configuration.md — requirement text quoted directly above]` |
| V14 Data Protection | yes | Backup encryption (D-16); Data Protection key-ring persistence (OPS-05); env file never included in backups |
| V16 Security Logging and Error Handling | yes | SEC-06 — no secrets in logs/exceptions/metric labels; journald as the log sink |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|----------------------|
| Malicious PR modifies a workflow to exfiltrate secrets via a runner | Information Disclosure | No self-hosted runner exists at all (D-02) — the entire attack class is architecturally absent, not merely mitigated |
| Third-party Action tag silently repointed to malicious code (tj-actions-class supply chain compromise) | Tampering | SHA-pin every third-party action; Dependabot keeps pins current; `zizmor` blocks unpinned actions in CI |
| Release artifact tampered with after build, before the LXC installs it | Tampering | `actions/attest-build-provenance` + offline `gh attestation verify --bundle` before unpacking (SEC-07) |
| Grafana Viewer account (or a leaked dashboard) used to run an arbitrary SQL query beyond intended dashboards | Information Disclosure / Elevation of Privilege | `grafana_reader` role is SELECT-only on the `reporting` schema of views, never the app's runtime role or raw tables |
| REST endpoint reachable without credentials from any LAN device (e.g. a compromised IoT device on the same network) | Elevation of Privilege | Every REST endpoint requires a per-client API key (API-02, D-22) — LAN trust is never treated as authentication |
| Data Protection key ring lost on redeploy, permanently bricking every encrypted value | Denial of Service (data-integrity flavor) | Keys persisted to Postgres, protected by a certificate, `SetApplicationName` pinned, verified by an actual restart/redeploy test (OPS-05) |
| Backup exfiltrated from the LXC's disk (theft, off-box copy) | Information Disclosure | Backups are `age`-encrypted to a public key the LXC cannot decrypt with (D-16) — a stolen backup file alone is useless without the operator's private key in their password manager |

## Sources

### Primary (HIGH confidence)
- `cli.github.com/manual/gh_attestation_verify` — flag semantics (`--owner`, `--repo`, `--bundle`, `--signer-workflow`, `--source-ref`), offline-bundle verification behavior (WebFetch, official docs)
- `postgresql.org/docs/current/auth-pg-hba-conf.html` — peer authentication semantics, `pg_ident.conf` mapping (WebSearch, official docs)
- `learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers` and `.../configuration/overview` — `PersistKeysToDbContext`, `ProtectKeysWithCertificate` (WebSearch, official Microsoft docs)
- `learn.microsoft.com/ef/core/managing-schemas/migrations/applying` — `dotnet ef migrations bundle --self-contained`, `--connection` usage (WebSearch, official docs)
- `api.nuget.org/v3-flatcontainer/*` — direct registry queries for every NuGet package version cited in this document (Bash `curl`, authoritative registry, performed 2026-09-27)
- `github.com/OWASP/ASVS` (master/5.0/en) — chapter structure and V13 Configuration requirement text (WebFetch, official standard repository)
- `github.blog/changelog/2025-10-28-immutable-releases-are-now-generally-available` — GA date and scope of immutable releases (WebSearch, official GitHub changelog)
- `docs.github.com/organizations/.../creating-rulesets-for-repositories-in-your-organization` — rulesets superseding tag protection rules (WebSearch, official docs)

### Secondary (MEDIUM confidence)
- `github.com/cli/cli/discussions/11007` — "attestation verification currently requires authentication... for public repositories" (WebSearch, official GitHub CLI maintainers' discussion, not a formal doc page — cross-checked against the rate-limit changelog)
- `github.blog/changelog/2025-05-08-updated-rate-limits-for-unauthenticated-requests` — unauthenticated REST rate limit (60/hr) (WebSearch, official changelog)
- `zizmor.sh` / `github.com/zizmorcore/zizmor` — audit rule descriptions for unpinned actions, template injection (WebSearch, official project docs)
- `github.com/rhysd/actionlint` — shellcheck integration, usage (WebSearch, official project README)
- `doc.traefik.io/traefik/v3.2/middlewares/http/ipallowlist` — `ipAllowList`, `forwardedHeaders.trustedIPs`, `ipStrategy.depth` (WebSearch, official docs)
- `grafana.com/docs/grafana/latest/setup-grafana/configure-access/configure-authentication/anonymous-auth` and `.../datasources/postgres/configure` — hardening keys, Unix-socket datasource form (WebSearch, official docs)
- `postgresql.org/about/news/postgresql-18-released-3142` — PostgreSQL 18 GA date and current patch (WebSearch, official news)

### Tertiary (LOW confidence — flagged for Wave 0 verification)
- Grafana Unix-socket datasource GitHub issues (`#58690`, `#29680`, `#22119`) — some several years old; current-version behavior not independently re-tested this session (WebSearch only)
- Grafana's OS user identity on a PGDG-adjacent/Grafana-Labs-APT install path — not independently confirmed this session, standard convention assumed (training knowledge, `[ASSUMED]`)
- PGDG `noble` (Ubuntu 24.04) repo availability for PostgreSQL 18 specifically — not directly queried this session (training knowledge + general PGDG cadence pattern, `[ASSUMED]`)

## Metadata

**Confidence breakdown:**
- Standard stack (NuGet packages): HIGH — every version confirmed live against the NuGet registry this session
- CI/CD supply-chain controls (attestation, SHA-pinning, rulesets, environments): HIGH — corroborated by official GitHub docs/changelog and a maintainer discussion thread for the one contested claim (token requirement)
- Database/peer-auth architecture: MEDIUM-HIGH — the core mechanism (peer auth, Unix socket, `pg_ident.conf`) is well-documented and stable; the specific OS-user-to-role mapping for Grafana and the exact PGDG/Ubuntu 24.04 package availability are flagged as Wave 0 spikes, not fully verified this session
- Grafana hardening: MEDIUM — config keys confirmed against current docs, but the Unix-socket connection form has a thin, partly-dated evidence base and should be smoke-tested early
- Pitfalls: HIGH — sourced from a mix of this project's own prior security research (already cross-checked once) and fresh official-source verification this session

**Research date:** 2026-09-27
**Valid until:** 30 days for the stable facts (PostgreSQL/EF Core/Data Protection APIs, ASVS chapter structure) — but re-verify the `gh attestation verify` auth requirement and NuGet package versions at implementation time regardless, since GitHub CLI and the fast-moving NuGet packages (Npgsql, resilience) ship frequently, and the immutable-releases/rulesets features are recent enough that GitHub's own UI may still be migrating defaults.
