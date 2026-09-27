# Walking Skeleton — Household Ledger

**Phase:** 1
**Generated:** 2026-09-27

## Capability Proven End-to-End

The operator pushes a strict-semver tag on `main`, approves the `deploy` environment, and within one poll interval the ledger LXC — holding no GitHub credential — downloads the release, verifies its build-provenance attestation offline before unpacking, applies any new EF Core migration as the migrator role, restarts the app as a systemd service and confirms that `/health` on the loopback ops endpoint reports the app reading and writing one real row (the Data Protection canary) in PostgreSQL over the local Unix socket as the runtime role.

The spine, layer by layer, and the plan that owns each layer:

| Step | Layer | Owning plan |
|---|---|---|
| App reads/writes one real row (canary) over the socket as the runtime role; `/health` + `/metrics` on 127.0.0.1 | ASP.NET Core host + EF Core + PostgreSQL roles | 01-01 (tracer) |
| Release package: framework-dependent app + self-contained `efbundle` + `release-manifest.json` + `deploy/` tree in one zip | Build tooling | 01-01 (T3) |
| Tag push → strict-semver + reachable-from-`main` gate → test → package → `actions/attest-build-provenance` → draft release with zip + `.sigstore.json` bundle | GitHub-hosted CI | 01-03 |
| `deploy` environment approval → publish job re-verifies the draft's attestation → release published | GitHub Environment | 01-03 |
| systemd timer polls `/releases/latest` unauthenticated → downloads zip + bundle → `gh attestation verify --bundle` with no token → attested commit checked against `main` → refuse before unpack on any failure | LXC root installer | 01-04 |
| Pre-migration backup → `efbundle` as OS user `ledger_migrator` (peer auth) → atomic `current` symlink swap → restart → health + version check → auto-rollback unless a migration ran → email + textfile metrics | LXC root installer | 01-04, 01-05 |
| Socket-only PostgreSQL 18, peer auth, roles, systemd units, firewall | LXC provisioning | 01-09, 01-10 |
| First real tag deployed, restart + redeploy prove the canary still decrypts | Go-live | 01-12 |

Local proof (before any LXC exists): the tracer's integration tests migrate a fresh database as the migrator role through the same bootstrap SQL the LXC uses, boot the host as the runtime role, and prove the canary round-trip survives a restart and a different content root (redeploy simulation). The real end-to-end proof on the LXC is the go-live plan.

## Architectural Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Framework | .NET 10 / ASP.NET Core minimal hosting, one `Ledger.slnx`: `Ledger.Domain` (no EF/HTTP), `Ledger.Repository` (EF Core, migrations, stores), `Ledger.Service` (host, REST, ops endpoint, CLI), `Ledger.UnitTests`, `Ledger.IntegrationTests` | Project constraint; mirrors the reference app's proven layering; EF packages stay in Repository |
| Data layer | PostgreSQL 18 (PGDG `noble-pgdg` — confirmed to publish `postgresql-18`) via `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x; snake_case table/column names via a Repository naming convention; migrations code-first | D-08; snake_case keeps later reporting-view SQL free of quoted identifiers (costly to change once views exist) |
| Database access | Unix socket only (`listen_addresses = ''`), peer auth; OS user `ledger` → role `ledger_runtime` (ident map), OS user `ledger_migrator` → role `ledger_migrator`, OS user `grafana` → role `grafana_reader` (ident map), OS user `ledger_backup` → role `ledger_backup` (member of `pg_read_all_data`); database `ledger` owned by `ledger_migrator` | D-09, D-10, D-11; no DB passwords exist anywhere |
| Grants | Default privileges `FOR ROLE ledger_migrator IN SCHEMA public`: DML on tables + USAGE/SELECT on sequences to `ledger_runtime`; `reporting` schema created by the initial migration with USAGE + default SELECT to `grafana_reader` only; runtime has no rights on `__EFMigrationsHistory` beyond SELECT | D-10 |
| Migrations at deploy | `dotnet ef migrations bundle --self-contained -r linux-x64` built in CI, run by the installer as OS user `ledger_migrator`; the app never migrates | OPS-07, D-03 |
| Secrets at rest | ASP.NET Core Data Protection, keys in `data_protection_keys` via `PersistKeysToDbContext`, application name `HouseholdLedger`, protected with a PFX certificate (path + password in the env file) using both `ProtectKeysWithCertificate` and `UnprotectKeysWithAnyCertificate` (Linux has no cert store to resolve the decryptor); canary row in `data_protection_canary` created once, never regenerated | SEC-03, OPS-05 |
| Auth (REST) | Named per-client API keys: token `ldg_<16-hex key id>_<43-char base64url secret>`, stored as key id + SHA-256 of the 256-bit secret, header `X-Api-Key`, fallback authorization policy requires an authenticated caller on every endpoint | D-22; 256-bit random secrets make a slow KDF pure per-request CPU cost on the low-power host |
| Auth (Grafana) | Grafana-native logins: renamed admin + two Viewer accounts created by provisioning prompts | D-21 |
| Network | Kestrel API endpoint `http://0.0.0.0:5080` (firewalled to Traefik only), ops endpoint `http://127.0.0.1:5081` (`/health` via `UseHealthChecks(path, port)`, `/metrics` via `UseMetricServer(port)` — both filtered by connection local port, never by Host header); Grafana 3000 (Traefik only); Prometheus 127.0.0.1:9090; node_exporter 127.0.0.1:9100; nftables input policy drop | D-20, D-23 |
| Deployment target | Proxmox unprivileged Ubuntu 24.04 LXC (2 vCPU, 2048 MB RAM, 16 GB disk); `aspnetcore-runtime-10.0` from Ubuntu `noble-updates` main (confirmed); Grafana from apt.grafana.com; Prometheus 3.13.x LTS official tarball with pinned SHA-256; `prometheus-node-exporter`, `age`, `msmtp`, `nftables` from Ubuntu; GitHub CLI from its official apt repo (Ubuntu's `gh` predates `attestation verify`) | D-06; Claude's discretion on OS and sources |
| Release contract | Tag `v<MAJOR>.<MINOR>.<PATCH>` (no pre-release/build metadata); assets `ledger-<version>.zip`, `ledger-<version>.zip.sha256`, `ledger-<version>.zip.sigstore.json`; zip root: `app/`, `efbundle`, `release-manifest.json` (`version`, `commit`, `migrations[]`), `deploy/` | Shared by CI (01-03) and installer (01-04); costly to change once releases exist |
| Attestation check | `gh attestation verify <zip> --bundle <bundle> --repo <owner/repo> --signer-workflow <owner/repo>/.github/workflows/release.yml --source-ref refs/tags/v<version> --deny-self-hosted-runners`, run with an empty `GH_CONFIG_DIR` and no token (verified 2026-09-27 against a public artifact: genuine → exit 0, tampered → exit 1, wrong source ref → exit 1); then the attested `sourceRepositoryDigest` must be an ancestor of `main` via the unauthenticated compare API | D-03. A tag-triggered build's certificate records `refs/tags/<tag>`, never `refs/heads/main`, so "a main ref" is proven on the attested commit (plus the build job's own reachability gate) |
| Paths | `/opt/ledger/releases/<version>/` + `/opt/ledger/current` symlink; `/etc/ledger/ledger.env` (640 root:ledger); `/etc/ledger/dataprotection.pfx` (640 root:ledger); `/etc/ledger/deploy.conf`, `/etc/ledger/provision.conf` (600 root); `/etc/ledger/backup-recipients.txt` (public keys only); `/etc/ledger/grafana.env` (640 root:grafana); `/var/backups/ledger/` (700 ledger_backup); `/var/lib/ledger-deploy/` (700 root); textfile metrics `/var/lib/prometheus/node-exporter/` (2775 root:ledger-metrics); scripts `/usr/local/sbin/ledger-{deploy,backup,restore,apikey,selfcheck}`, libraries `/usr/local/lib/ledger/` | D-06, D-07 |
| Operational metrics | App: `ledger_build_info{version,commit}`, `ledger_health_check_status{check}`; textfile: `ledger_deploy_last_run_timestamp_seconds`, `ledger_deploy_last_run_success`, `ledger_deploy_last_run_rolled_back`, `ledger_deploy_current_release_info{version}`, `ledger_backup_last_success_timestamp_seconds{reason}`, `ledger_backup_last_run_success{reason}`, `ledger_backup_last_size_bytes{reason}`; node_exporter systemd collector for unit state | D-05, D-18, D-19 |
| Alerting | Grafana unified alerting provisioned from files, one email contact point through the existing Postfix relay, platform rules only | D-19 |
| CI supply chain | All workflow `uses:` pinned to 40-char SHAs (zizmor `hash-pin` policy for `*`), scanners run from digest-pinned images in `build/lint/compose.yaml` through `build/lint.sh` (same locally and in CI), gitleaks full history with custom personal-data rules, Dependabot for github-actions, nuget and docker-compose | SEC-09, SEC-10 |
| Directory layout | `Ledger.*` projects at repo root; `build/` (package, lint, tag validation); `deploy/` (everything that runs on the LXC: `bin/`, `lib/`, `sql/`, `systemd/`, `provision.d/`, `provisioning/grafana`, `provisioning/prometheus`, `postgresql/`, `nftables/`, `traefik/`, `tests/`); `.github/workflows/` (`ci.yml`, `release.yml`); `docs/` | Keeps the "runs on the LXC" trust boundary visible in the tree |
| Local test database | The user's own long-running PostgreSQL container; admin connection string in `dotnet user-secrets` under UserSecretsId `be62dc27-a005-434a-aa75-576b7b30ade6`, key `ConnectionStrings:TestAdmin`; fixture creates a throwaway database per run and connects as each role with `Options=-c role=<role>`; CI uses a digest-pinned `postgres:18` service container with trust auth | D-13; no Testcontainers |

## Stack Touched in Phase 1

- [ ] Project scaffold (solution, build props, SDK pin, dotnet-ef tool manifest, xUnit v3 test projects) — 01-01
- [ ] Routing — ops endpoint `/health` + `/metrics` (01-01), authenticated `GET /api/v1/status` (01-06)
- [ ] Database — canary row written once and read on every health check, as `ledger_runtime`, after migration as `ledger_migrator` — 01-01
- [ ] UI — no UI in this phase; the interactive surface is an authenticated REST call (`X-Api-Key`) returning the calling client's name, and Grafana sign-in with a Viewer account — 01-06, 01-10
- [ ] Deployment — tag → attested draft → approved publish → LXC pull, verify, migrate, restart, health — 01-03, 01-04, proven live in 01-12

## Out of Scope (Deferred to Later Slices)

- Bank sync, consent linking, any financial data or financial dashboards
- Any Grafana dashboard (every dashboard must exist in English and Dutch from one source; that generator arrives with the first data dashboard)
- MCP endpoint, OAuth server, any public route
- Offsite / outside-LXC backup copies (accepted risk)
- REST authentication through OIDC; Grafana SSO
- `Microsoft.Extensions.Http.Resilience` and MailKit in the app (no outbound HTTP client and no app-sent email in this phase)

## Subsequent Slice Plan

Each later phase adds one vertical slice on top of this skeleton without altering its architectural decisions:

- Phase 2: Automatic ING sync — provider interface, first real rows, first bilingual dashboard reading `reporting` views as `grafana_reader`, sync/consent metrics and alerts added to the provisioned alerting
- Phase 3: Claude reads the ledger — MCP endpoint and its OAuth path added as a new public Traefik route
- Phase 4: Trustworthy categorisation — audited write pattern on top of the same stores and roles
- Phase 5: Budgets, goals and forecast
- Phase 6: Advisor memory and scheduled reviews
