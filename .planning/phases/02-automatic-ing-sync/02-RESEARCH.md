# Phase 2: Automatic ING Sync - Research

**Researched:** 2026-09-30
**Domain:** Open-banking ingestion (Enable Banking AIS) into a PostgreSQL ledger, ops metrics/alerts, generated Grafana dashboards, on the existing .NET 10 / EF Core / prometheus-net / Grafana 13.2.2 platform
**Confidence:** MEDIUM. The Enable Banking API surface, the Grafana provisioning mechanics and the existing-code extension points are HIGH (read from the official OpenAPI file and the repo, or executed). Everything that depends on how ING NL behaves (savings coverage, pending transactions, identifier stability, real quota, real consent length) is LOW until the spike answers it.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Spike & fallback**
- **D-01:** The real-consent spike runs **from the operator's workstation**. Every captured payload goes into an **age-encrypted folder outside the repository**. That data is deleted once the synthetic reconciliation fixtures modelled on it are written. Nothing real is ever committed. When the spike ends, its Enable Banking session is revoked so no stray consent is left behind. The spike answers four questions:
  1. Does the ING NL consent include the savings account?
  2. What is the real consent validity and renewal behaviour (180 days advertised, ~90 reported)?
  3. What are the actual rate limits?
  4. How do pending transactions turn into booked ones? This needs fetches over a few days to capture real pairs, and shows whether ING returns pending transactions at all and which identifiers stay stable.
- **D-02:** **Fallback if the savings account isn't covered: ship with the joint account only.** Savings coverage stays open behind the provider interface. Transfers to and from savings remain visible on the joint account. Interest and direct withdrawals from savings would be missing until coverage is added. No second provider (Salt Edge) in this phase.
- **D-03:** **The spike runs in parallel with the rest of the phase.** The following are built and tested against the synthetic provider while the spike runs:
  - ledger schema, idempotent upsert, pending/booked reconciliation and balance reconciliation
  - consent state machine, REST link/renew/callback endpoints, sync scheduler
  - metrics, alerts and the dashboard generator

  Only the Enable Banking adapter, and anything the spike's answers reshape, waits for the spike.

**Temporary Claude SSH access (folded todo, reshaped)**
- **D-04:** Claude's temporary SSH access (sudo on the ledger LXC, the dynamic-config login on the reverse proxy) **stays during this phase**, including once real bank data is on the host. The user's reasoning: Claude reads the data through MCP later anyway, and the access speeds up LXC development. Two agreed mitigations:
  1. **In this phase:** the key gets a passphrase and is loaded into `ssh-agent` for sessions, so the key file on the workstation is useless on its own.
  2. **Hard removal point:** before `/mcp` goes public in Phase 3, when the host gets its first internet-facing surface. The todo moves to Phase 3.

  Claude still never reads the env file, the Data Protection certificate, the Enable Banking private key or any credential on the host.

**Linking & renewal**
- **D-05:** **Link and renewal start with an authenticated REST call only.** No CLI and no web page, because it happens about four times a year after an alert email. A committed `.http` file and documented `curl` snippets use the operator API key (the header scheme from Phase 1, D-22). The call returns the Enable Banking/ING authorisation URL. The operator opens it and approves in the ING app. ING then redirects the browser to the callback on the internal REST hostname.
- **D-06:** **The callback is the only REST endpoint without an API key**, because a bank redirect can't carry one. It is protected instead by a one-time `state` value that the authenticated start call creates:
  - the state is unguessable, single-use and short-lived, and bound to that start request
  - the callback rejects anything else
  - its response reveals nothing beyond the outcome (e.g. "linked 2 accounts")

  The planner must treat this as a deliberate, tested exception to the fallback "require authenticated user" policy. — **Reversibility:** reversible — a single endpoint; replacing it later (e.g. with OIDC-authenticated linking) touches only the linking flow.
- **D-07:** **The callback is reachable from the home network or VPN only**, through the existing internal Traefik route. Linking or renewing works only while the browser handling the ING redirect is at home or on the VPN. Nothing new becomes internet-facing.
- **D-08:** **One consent, one expiry clock.** The operator's ING login shows the joint account and the savings account, and not the partner's personal accounts. The data model may support several bank connections at low cost, but the flow, alerts and dashboard are designed around one.
- **D-09:** **Accounts are chosen at link time.** The start or finish step lists what the consent exposes: masked IBAN, account type and ING's name. The operator marks which accounts to sync and gives each a display name (e.g. "Joint", "Savings"). Unselected accounts, such as the operator's own personal account, are recorded but **never fetched**, so their transactions never enter the database. A renewal keeps the selection and display names, and maps the renewed session's accounts back to the existing ledger accounts, so history continues unbroken. Account identity and names live in the database, never in the repository.

**Sync schedule & quota**
- **D-10:** **One scheduled sync early each morning, Amsterdam time, plus at most one automatic retry a few hours later on a transient failure.** A rate-limit rejection is never retried the same day. This stays well inside the bank's ~4 unattended calls per account per day.
- **D-11:** **The first sync runs automatically right after a successful link or renewal.** There is also an **authenticated REST "sync now"** call. It refuses when it would use the last remaining call of the day's per-account quota. Nothing interactive (dashboard load, MCP later) ever triggers a sync.
- **D-12:** **Balances are fetched once per day**, on the first successful sync of the day, and stored as a daily balance snapshot per account. The ledger checks **previous balance + that day's booked transactions = new balance** per account. Any drift is flagged as a metric and on the dashboard. Balance snapshots also give the budgets and goals phase real savings balances.

**Pending transactions & reconciliation**
- **D-13:** **Pending transactions are stored and shown**, clearly marked pending, and turned into booked in place when the booked version arrives. This applies if the spike shows ING reports them. Totals default to booked only; later phases decide per figure whether pending counts.
- **D-14:** **Pending and booked merge only when the match is certain:** a unique match on a stable provider identifier, or a unique match on amount + counterparty within a short date window. Ambiguous cases are **never guessed**: they stay separate and are flagged, with a metric count and a dashboard marker. A pending transaction that disappears from the bank feed without booking is marked **dropped**: kept for audit, hidden from the recent-transactions view and excluded from totals. Every transaction has an immutable internal ID separate from any provider ID. — **Reversibility:** costly — the ledger identity and reconciliation model becomes the base every later phase (categorisation, audit, MCP) keys on; changing it after real data lands needs a data migration.

**Dashboard**
- **D-15:** **The EN and NL dashboards are generated by a C# console tool inside the solution.** It uses a small typed model of the Grafana panel types actually used, plus one EN/NL translation file. The generated JSON is committed and provisioned into the existing `Household Ledger` folder. CI fails when the committed JSON is out of date with the generator, and when a translation key is missing in either language. There is no official Grafana SDK for .NET, so the model is hand-written against the Grafana 13 dashboard schema and validated by loading it in a real Grafana. — **Reversibility:** costly — every later phase's dashboards are written in this generator; switching tools means porting all of them.
- **D-16:** **One dashboard, with status on top.**
  - Top row, per account: last successful sync, consent state with days left, today's balance and whether it reconciles, and the count of flagged or unclear matches.
  - Below: a recent-transactions table with an account selector (default last 30 days). Pending transactions are marked, dropped ones are hidden.
  - Financial data (balances, transactions) is read through `reporting` views as `grafana_reader`. Operational state (sync, consent) may come from Prometheus or from reporting views, at Claude's discretion.
  - Both partners use the same dashboard through their Viewer logins.

**Alerts**
- **D-17:** **Sync and consent alert emails go to the operator only**, through the existing Postfix contact point. Only the operator's ING app can approve a renewal. The partner sees the state on the dashboard's status row. Alert texts never contain amounts, balances, counterparties or IBANs.
- **D-18:** **When alerts fire:**
  - **Transient failures:** only after the day's retry has also failed, or when no successful sync has happened in about 26 hours (this catches a stalled scheduler).
  - **Consent/auth rejections and rate-limit rejections:** right away, as their own distinct alert, because retrying won't fix them.
  - **Consent expiry:** at **14 and 7 days** before expiry, and on expiry.
  - The existing mail-volume cap (grouped policy, hourly group interval) stays in force.

### Claude's Discretion

- How long raw provider payloads are kept (default: indefinitely, alongside the normalised row, for replay and diagnosis). How they're stored (JSON column or separate append-only table).
- Where the Enable Banking private key lives. Default: generated once; the file sits under `/etc/ledger` with its path in the env file; a copy goes in the password manager; it's excluded from backups, like the Data Protection certificate (Phase 1, D-16). The workstation copy used by the spike is deleted afterwards. Whether the spike and production share one Enable Banking application.
- How the stored Enable Banking session is encrypted at rest: use the existing `ISecretProtector` / Data Protection setup.
- Exact sync time and retry delay, the pending-match date window, and the reconciliation tolerance (must be exact to the cent).
- Metric names and labels. Per-account metrics must use an opaque internal account key, never an IBAN or account number (SEC-06).
- Whether a reconciliation drift also raises an alert (default: yes, operator only, since unexplained drift means the ledger isn't complete).
- Alert wording, and whether household alerts live in their own rule folder/group next to `Platform`.
- Provider interface shape and the synthetic provider's fixture design. The design must let the whole pipeline run against the synthetic provider with no changes outside ingestion.
- How the "no payment path" guarantee is proven. For example: the Enable Banking client exposes only AIS endpoints, a test asserts no payment-initiation route exists, and consent requests only account-information access.

### Deferred Ideas (OUT OF SCOPE)

- **Savings-account coverage through a second provider** (e.g. Salt Edge). Only needed if the spike shows Enable Banking's ING consent misses the savings account. Revisit behind the provider interface after v1.
- **CLI or web page for linking/renewal.** Not needed at ~4 uses a year. The v2 review/edit web page could host it later.
- **Public consent callback.** Rejected; linking stays home/VPN-only.
- **Removal of Claude's SSH access.** Moved to before `/mcp` goes public (Phase 3), per D-04.

### Folded todos (in scope, from CONTEXT)

- Remove temporary Claude SSH access: reshaped by D-04 (passphrase + `ssh-agent` now; removal moves to Phase 3; the todo file is updated to match).
- Harden privileged units and scan logs for secrets: sandbox `ledger-deploy-poll.service` and the `ledger-apikey` `systemd-run` call; `ledger-selfcheck` scans the journal and `/var/log` for complete secret shapes (including the new Enable Banking key and session material) without printing a match.
- Migrate tests to Microsoft.Testing.Platform for xunit v4: do it early so the many new ingestion tests are written on the new platform; the category/trait filters in CI and the release workflow must keep working.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| INGEST-01 | Link ING accounts once via aggregator consent flow; daily sync with no manual steps | Enable Banking `POST /auth` -> browser -> callback -> `POST /sessions`; restricted-mode account whitelist; scheduler with Europe/Amsterdam next-run math; REST link/renew/select endpoints |
| INGEST-02 | Re-run adds nothing; pending that books stays one transaction | `transaction_refs` identity table + unique index, internal immutable id, certain-match-only merge algorithm, dropped state, overlap-window re-fetch |
| INGEST-03 | Exact decimals, both dates, counterparty, description, status, raw payload retained | `numeric(19,4)` + string-to-decimal parse, `date` columns for booking/value/transaction dates, append-only `transaction_payloads` (jsonb) |
| INGEST-04 | Consent status explicit; warn >=14 days ahead; guided renewal without losing history | `access.valid_until` from `POST /sessions`, derived linked/expiring/expired, `identification_hash` account mapping across sessions, renewal creates new connection and re-maps |
| INGEST-05 | First sync requests longest history | `strategy=longest` on first link/renew, run immediately (full history is typically available only ~1 hour after authorisation) |
| INGEST-06 | Provider interface; other aggregator/file import can be added | `IBankDataProvider` in Ledger.Domain, provider-agnostic DTOs, synthetic provider, no EB types outside the adapter |
| INGEST-07 | Respect rate limits; failures visible | ~4 background fetches/day/account (EB FAQ), quota ledger `provider_calls`, 429 `ASPSP_RATE_LIMIT_EXCEEDED` never retried same day, `sync_runs` table, metrics + alerts |
| SEC-01 | Bank access read-only; no payment code path | AIS-only provider interface, `AisOnlyGuardHandler` allow-list on the outbound HttpClient, recorded-request test, endpoint/route inventory test |
| OPS-01 | `/metrics` with last successful sync, sync errors, days until consent expiry | DB-seeded gauges via a refresher hosted service + pre-initialised counters, opaque account keys |
| OPS-02 | Alerts on sync failure and consent 14/7 days | Provisioned Grafana rules in a `Household` folder against Prometheus, child notification route for repeat cadence |
| DASH-05 | Every dashboard in EN and NL from one source | `Ledger.Dashboards` console generator + one translation file + drift/translation-completeness tests |
| DASH-07 | Grafana reads via SELECT-only role on reporting views; cannot write | `reporting` views created by the migrator (owner rights), default SELECT grant already exists, data-driven privilege test over every object in the schema |
| API-01 (named in CONTEXT) | REST endpoints for consent link/renew callback etc. | Minimal-API endpoint group under `/api/v1/bank/...`, callback is the single `AllowAnonymous` endpoint guarded by single-use state |
</phase_requirements>

## Project Constraints (from CLAUDE.md)

Source: `/mnt/Data/repos/ing-dashboard/.claude/CLAUDE.md` (read this session). These have the same authority as locked decisions.

- **No planning references outside `.planning/`**: no requirement keys, decision IDs, phase/plan numbers or planning document names in docs, code, comments, XML docs, string literals, log/exception messages, test names, Grafana dashboard titles/descriptions/alert texts, `.http` files, scripts, config or workflow files. Only git commit messages may carry them.
- **Comments: `///` only.** No `//` comments anywhere in C#. Rename or extract instead.
- **Public repository, no personal data**: no names, IBANs, account numbers, addresses, emails, real domains/hostnames/IPs, API keys, household-specific merchant/counterparty names or real transaction data. Use `example.com` / `example.org`. All fixtures synthetic. Categories/rules that name counterparties live in the database only.
- **Never commit to `main`**; work on a branch (milestone branch `milestone/v1-household-ledger` is current).
- **Local DB**: tests use the user's own local PostgreSQL container via `dotnet user-secrets` (`ConnectionStrings:TestAdmin`); no Testcontainers; do not tear the container down. CI uses a PostgreSQL service container.
- **No LLM API calls from the app.**
- **Security first**: bank access read-only; only `/mcp` is internet-facing (Phase 3); least-privilege DB roles; secrets never in logs, exceptions, metric labels or MCP output.
- **Stack overrides**: PostgreSQL via Npgsql over the Unix socket with peer auth; Grafana core PostgreSQL datasource; pull-based deploys; ignore SQL Server / Testcontainers.MsSql / Foundation SDK specifics in the historical research.
- Project skills: none found (`.claude/skills/` etc. absent). GSD workflow enforcement: file-changing work goes through a GSD command.

## Summary

Enable Banking's API is small and well specified (official OpenAPI file, fetched and read this session). The ingestion flow is `POST /auth` (returns a URL), the browser round-trip through ING, a redirect back carrying `code` and `state`, `POST /sessions {code}` (returns `session_id`, the accounts and `access.valid_until`), then per account `GET /accounts/{uid}/balances` and `GET /accounts/{uid}/transactions` with a `continuation_key` loop. Auth is an RS256 JWT with header `kid` = application id and claims `iss`/`aud`/`iat`/`exp`. The `Microsoft.IdentityModel.JsonWebTokens` handler produces exactly that (plus an extra `nbf`), verified by running it.

Six findings change how the phase should be planned, beyond what CONTEXT already assumes:

1. **Restricted mode means accounts are whitelisted in the Enable Banking Control Panel first.** A restricted-mode application returns only the accounts "linked" there; any other account is stripped from the `POST /sessions` response, and an unlinked account yields an empty list. The "which accounts exist at ING" question is therefore answered at Control Panel link time, before any code runs, and D-09's "recorded but never fetched" only applies to accounts the API actually returns.
2. **Savings accounts are probably not reachable at all.** DNB states that savings accounts with fixed contra accounts are outside PSD2 scope and that banks may (but need not) grant access outside PSD2; Dutch budgeting tools report banks blocking savings via PSD2. Plan for the D-02 fallback (joint account only) as the likely outcome, and keep the schema and dashboard account-generic. This is LOW confidence for ING specifically until the spike.
3. **Full transaction history is typically available only for about one hour after authorisation**, after which many banks restrict data to the last 90 days. The first sync after every link or renewal must start immediately, be safe to restart within that hour, and must not be starved of quota. The operator's account-selection step therefore has to be a fast call, not a form.
4. **The background quota (about 4 fetches a day per account) is lifted when the request carries PSU headers.** Enable Banking says a user-triggered fetch should send that user's headers and a scheduled fetch none. The post-link sync and "sync now" are user-triggered, so they can send headers taken from the operator's request. This is a design choice the planner must confirm (Open Question 2).
5. **`entry_reference` is not guaranteed stable across pending to booked.** Enable Banking's own FAQ says pending transactions only carry a reference when the bank keeps it unchanged after booking, and a Firefly III issue shows a bank changing `entry_reference` (and the bank transaction code) at booking. The identity model must never depend on reference stability; certain-match-only merging (D-14) is the right call, and a one-to-many `transaction_refs` table makes re-fetches idempotent even when a pending reference reappears.
6. **Three infrastructure gaps in Phase 1 code will bite:** the dashboard provider reads `/var/lib/grafana/dashboards/ledger` but the installer only copies the `provisioning` directory to `/etc/grafana/provisioning`; the lint script hard-codes exactly 8 alert rules; and `Microsoft.Extensions.Http.Resilience`'s standard handler would retry 429s, burning the rate limit the design protects.

**Primary recommendation:** Build the provider-agnostic core first (Domain reconciler, consent state machine, quota ledger, scheduler math, synthetic provider, schema, reporting views) test-first on the migrated MTP test platform, put the Enable Banking adapter behind an AIS-only allow-listed `HttpClient`, generate the dashboards from a typed C# model into `/etc/grafana/provisioning/dashboards/json`, and treat the ING spike as a parallel operator task whose answers are captured in a decision table (below) that tells the planner exactly which design branches to keep.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Start link/renew, sync-now, account selection | API / Backend (minimal-API endpoints, API-key auth) | — | Authenticated operator actions; no browser UI in this phase |
| Consent callback (single-use `state`) | API / Backend (anonymous exception, LAN/VPN via Traefik) | Reverse proxy (IP allowlist) | Bank redirect cannot carry an API key; exposure limited by Traefik allowlist |
| Session exchange, transactions, balances fetch | API / Backend (provider adapter, `HttpClient`) | — | Server-side only; JWT private key never leaves the host |
| Reconciliation (pending to booked, dedupe, balance check) | Domain (pure functions) | Database (unique indexes as safety net) | Pure logic is unit-testable; DB constraints guarantee idempotency under races |
| Scheduling (06:30 Europe/Amsterdam, one retry) | API / Backend (`BackgroundService`) | Database (`sync_runs` as the source of truth) | Single host; persisted runs make it restart-safe |
| Persistence of ledger, sessions, payloads, snapshots | Database / Storage (PostgreSQL) | — | Exact `numeric`, unique indexes, append-only grants |
| Session/consent secret at rest | API / Backend (`ISecretProtector`) | Database (ciphertext only) | Existing Data Protection ring, certificate-protected |
| Operational metrics | API / Backend (prometheus-net, DB-seeded) | Prometheus (scrape) | Metrics are a projection of DB state so restarts do not reset them |
| Alert rules | Grafana unified alerting (provisioned files) | Prometheus (query source) | Existing pattern; Alertmanager deliberately absent |
| Dashboard rendering + language | Grafana (provisioned JSON) | Build-time C# generator | Grafana has no runtime i18n of dashboard content; two generated files |
| Financial read path for dashboards | Database (`reporting` views as `grafana_reader`) | — | View owner rights + SELECT-only grant is the hard read-only guarantee |
| Read-only bank access guarantee | API / Backend (AIS-only interface + outbound allow-list) | Tests | Structural: no payment method exists, and the HTTP layer refuses non-allow-listed calls |

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| .NET / ASP.NET Core | 10.0.112 SDK (`global.json`), net10.0 | Host, minimal APIs, `BackgroundService`, `IHttpClientFactory`, `TimeProvider` | Fixed by project; `IHttpClientFactory` and `TimeProvider` are in the shared framework [VERIFIED: /mnt/Data/repos/ing-dashboard/global.json:3 `"version": "10.0.112"`] |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.3 (already locked) | EF Core provider, `numeric`, `jsonb`, partial unique indexes | Already in `Ledger.Repository` [VERIFIED: Ledger.Repository/Ledger.Repository.csproj:8 `Version="10.0.3"`] |
| `prometheus-net.AspNetCore` | 8.2.1 (already locked) | `/metrics`; gauges/counters | Already used [VERIFIED: Ledger.Service/Ledger.Service.csproj:9 `Version="8.2.1"`] |
| `Microsoft.IdentityModel.JsonWebTokens` | 8.23.0 | Mint the RS256 client-assertion JWT for Enable Banking | Microsoft-maintained; verified this session to emit `{"alg":"RS256","kid":...,"typ":"JWT"}` and `aud/iss/exp/iat/nbf` [VERIFIED: local probe, nuget.org owners `AzureAD, Microsoft`] |
| Grafana | 13.2.2 (pinned) | Dashboards + unified alerting | Pinned in `deploy/versions.env` `GRAFANA_VERSION_PIN=13.2.2` and in `build/lint/compose.yaml` |
| Prometheus | 3.13.3 (pinned) | Scrape target for the ops metrics | `PROMETHEUS_VERSION=3.13.3` in `deploy/versions.env` |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `Microsoft.Extensions.TimeProvider.Testing` | 10.10.0 | `FakeTimeProvider` for scheduler and quota tests | Test projects only (DST and rolling-24h quota tests) |
| `xunit.v3` | 4.0.1 (from 3.2.2) | Test framework on Microsoft.Testing.Platform | Folded todo; trial in a scratch copy of the repo compiled and ran all 38 unit tests [VERIFIED: local run] |
| FluentAssertions 8.11.0, NSubstitute 6.2.0, `Microsoft.EntityFrameworkCore.InMemory` 10.0.12, `Microsoft.AspNetCore.Mvc.Testing` 10.0.12 | already locked | Assertions, mocks, in-process host | Existing test stack [VERIFIED: Ledger.UnitTests/Ledger.UnitTests.csproj, Ledger.IntegrationTests/Ledger.IntegrationTests.csproj] |
| `System.Text.Json` (BCL) | in-box | Typed Grafana model serialization; Enable Banking DTOs | No package needed; deterministic output via fixed options |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Microsoft.IdentityModel.JsonWebTokens` | ~15 lines of BCL `RSA.SignData` + base64url | Zero dependency, but hand-rolled JWT framing; the context explicitly anticipates a JWT library, and the package is first-party-grade |
| Timer-driven DB-seeded gauge refresher | `Metrics.DefaultRegistry.AddBeforeCollectCallback` (refresh on scrape) | Scrape-time refresh couples `/metrics` availability to DB health; refresher tolerates DB blips |
| `Microsoft.Extensions.Http.Resilience` standard handler | none for the Enable Banking client (single explicit retry at the sync-run level) | The standard handler retries HTTP 429 and 5xx; every extra call spends the ~4/day quota. STACK.md recommended it; this phase should not use it on this client |
| Cron library (Cronos, Quartz) | `TimeZoneInfo` + a minute-granularity due-check loop | One daily job needs no library; DST math verified below |
| Separate synthetic-provider assembly | Provider class inside `Ledger.Service` behind config, refused in Production | Keeps a dev/dashboard seeding path; production validator must reject it |

**Installation:**
```bash
dotnet add Ledger.Service/Ledger.Service.csproj package Microsoft.IdentityModel.JsonWebTokens --version 8.23.0
dotnet add Ledger.UnitTests/Ledger.UnitTests.csproj package Microsoft.Extensions.TimeProvider.Testing --version 10.10.0
dotnet add Ledger.IntegrationTests/Ledger.IntegrationTests.csproj package Microsoft.Extensions.TimeProvider.Testing --version 10.10.0
dotnet restore Ledger.slnx
```
Every project has `RestorePackagesWithLockFile` and CI restores in locked mode ([VERIFIED: Directory.Build.props:9 `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`]), so commit the regenerated `packages.lock.json` files, including one for the new `Ledger.Dashboards` project.

**Version verification:** `nuget.org` v3 flat container: `Microsoft.IdentityModel.JsonWebTokens` latest stable 8.23.0, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0, `xunit.v3` 4.0.1 (checked 2026-09-30). The `gsd-tools package-legitimacy` seam supports only npm/pypi/crates, so NuGet packages were checked manually (see audit).

## Package Legitimacy Audit

The seam rejected `--ecosystem nuget` ("Usage: ... --ecosystem <npm|pypi|crates>"), so verification was done against the NuGet search API (owners, downloads, repository, verified prefix). NuGet `PackageReference` has no install-time scripts; the residual risk is MSBuild props/targets or analyzers shipped in a package, which none of these do.

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| Microsoft.IdentityModel.JsonWebTokens | NuGet | 8.23.0 latest of a long-running line | 3.98B total | github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet | OK (manual: owners AzureAD, Microsoft; verified) | Approved |
| Microsoft.Extensions.TimeProvider.Testing | NuGet | 10.10.0, .NET team | 47.1M total | dot.net | OK (manual: owners dotnetframework, Microsoft; verified) | Approved (test projects only) |
| xunit.v3 (4.0.1) | NuGet | already in use at 3.2.2 | n/a | xunit/xunit | OK (already approved; version bump only) | Approved |
| Microsoft.Extensions.Http.Resilience | NuGet | 10.10.0 | 158M total | dot.net | OK but NOT recommended for this phase | Not used on the Enable Banking client (retries 429) |

**Packages removed due to SLOP verdict:** none
**Packages flagged as suspicious SUS:** none

## Architecture Patterns

### System Architecture Diagram

```
 Operator (REST + X-Api-Key)                 Operator browser (home/VPN)
        |                                            ^   |
        | POST /api/v1/bank/connections/link         |   | approve in ING app
        v                                            |   v
 +--------------------+  state(hash stored)   +------------------------+
 | Bank endpoints     |---------------------->| bank_authorizations    |
 | (authenticated)    |   POST /auth          | (state hash, TTL, used)|
 +--------------------+---------------------> Enable Banking API ---> ING
        ^                                       (RS256 JWT, AIS only)
        |  authorizationUrl                          |
        |                                            | redirect_url?code&state
        |                                            v
        |                        +----------------------------------+
        |                        | GET /api/v1/bank/callback        |
        |                        | AllowAnonymous, single-use state |
        |                        | -> POST /sessions {code}         |
        |                        +---------------+------------------+
        |                                        v
        |          bank_connections (session id protected via ISecretProtector,
        |                            valid_until)  +  accounts (identification_hash)
        |                                        |
        |   PUT account selection / display name  |  first sync starts immediately
        v                                        v
 +---------------------------+     due? (06:30 Europe/Amsterdam, +1 retry)
 | SyncScheduler (Background)|<-----  sync_runs, provider_calls (quota ledger)
 +-------------+-------------+
               | per selected account (advisory/partial-unique lock)
               v
 +---------------------------+   IBankDataProvider (Domain interface)
 | SyncOrchestrator          |-----> EnableBankingProvider --> AisOnlyGuardHandler --> api.enablebanking.com
 |  1 balances (once/day)    |-----> SyntheticProvider (tests/dev only)
 |  2 transactions pages     |
 +-------------+-------------+
               v
 +---------------------------+   pure Domain
 | TransactionReconciler     |   plan = inserts / status upgrades / certain merges /
 |  + BalanceReconciler      |          ambiguous flags / drops (only after a complete fetch)
 +-------------+-------------+
               v single DB transaction per account
 +----------------------------------------------------------+
 | transactions, transaction_refs, transaction_payloads,    |
 | balance_snapshots, sync_runs, provider_calls   (public)  |
 +--------------------+-------------------------------------+
                      | views owned by ledger_migrator (owner rights)
                      v
 +---------------------------+     +----------------------------+
 | reporting.* views         |---->| Grafana (grafana_reader)   |  EN + NL dashboards
 +---------------------------+     +----------------------------+
 SyncMetricsRefresher (timer) reads DB -> prometheus-net gauges -> Prometheus -> Grafana alert rules
 -> operator-email contact point (no financial text)
```

### Recommended Project Structure

```
Ledger.Domain/
  Banking/            IBankDataProvider, provider DTOs (ProviderSession, ProviderAccount, ProviderTransaction, ProviderBalance, ProviderError kinds)
  Ingestion/          TransactionReconciler, BalanceReconciler, ConsentState, SyncSchedule (next-run math), CallBudget, MoneyParser
Ledger.Repository/
  Entities/           BankConnection, BankAuthorization, LedgerAccount, LedgerTransaction, TransactionRef, TransactionPayload, BalanceSnapshot, SyncRun, ProviderCall
  Stores/             implement Domain store interfaces; raw-SQL upsert helpers
  Migrations/         tables + reporting views + REVOKE UPDATE/DELETE on payloads
Ledger.Service/
  Ingestion/          SyncOrchestrator, SyncScheduler (BackgroundService), SyncMetricsRefresher
  Ingestion/EnableBanking/   client, JWT minter, AisOnlyGuardHandler, DTOs, error mapper
  Ingestion/Synthetic/       SyntheticBankDataProvider + scenario builder (refused in Production)
  Endpoints/          BankEndpoints (link, renew, callback, accounts, sync-now, revoke)
  Metrics/            SyncMetrics (alongside LedgerMetrics)
Ledger.Dashboards/    console tool: typed Grafana model, dashboard definitions, translations.json, `generate` and `check` commands
Ledger.UnitTests/     reconciler, consent, scheduler, quota, money, dashboard drift, guard tests
Ledger.IntegrationTests/  ingestion pipeline on real PostgreSQL, role tests, callback tests, log redaction
deploy/provisioning/grafana/provisioning/
  alerting/household-rules.yaml     new rule group in folder "Household"
  dashboards/json/                  generated EN and NL JSON (path fix below)
docs/                 bank-link runbook, .http file (placeholders), monitoring additions
```

### Pattern 1: Provider interface that hides Enable Banking entirely (INGEST-06)

**What:** Domain defines a narrow, read-only interface and provider-neutral DTOs. Nothing outside the adapter references Enable Banking names, enums (`BOOK`, `PDNG`, `CRDT`, `DBIT`) or JSON shapes; the adapter maps them.
**When to use:** always; the synthetic provider must feed the identical pipeline.
**Example:**
```csharp
namespace Ledger.Domain.Banking;

/// <summary>Read-only access to a bank aggregator. It exposes account information only; no payment operation exists.</summary>
public interface IBankDataProvider
{
    /// <summary>Starts a consent and returns the URL the operator must open.</summary>
    Task<AuthorizationStart> StartAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken);

    /// <summary>Exchanges the one-time code from the redirect for a session and its accounts.</summary>
    Task<ProviderSession> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken);

    /// <summary>Reads balances for one account of a session.</summary>
    Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(ProviderAccountRef account, FetchContext context, CancellationToken cancellationToken);

    /// <summary>Streams every page of transactions, following continuation keys until the provider signals the end.</summary>
    IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(ProviderAccountRef account, TransactionQuery query, FetchContext context, CancellationToken cancellationToken);

    /// <summary>Ends a session at the aggregator.</summary>
    Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken);
}
```
`FetchContext` carries the optional PSU headers (Open Question 2). `ProviderTransaction` carries `Amount` as a signed `decimal`, `Status` as a provider-neutral enum (`Pending`, `Booked`), the three dates as `DateOnly?`, `EntryReference` (nullable), `CounterpartyName`, `CounterpartyIban`, `Description` and `RawJson`.

### Pattern 2: Identity model = internal id + one-to-many provider refs (costly-reversibility item)

**What:** `transactions.id` (uuid, immutable, never a provider value). `transaction_refs(account_id, ref, transaction_id, first_status, first_seen_at)` with a unique index on `(account_id, ref)` maps every provider reference ever seen (or a synthetic fingerprint when the bank sends none) to the one transaction. A pending reference that reappears after booking still resolves to the same row instead of creating a duplicate.
**Why:** Enable Banking says `entry_reference` is "unique and immutable for accounts with the same identification hashes" but also that pending transactions only carry a reference if the bank keeps it after booking [CITED: enablebanking.com/docs/faq/]; a public issue shows a bank changing `entry_reference` at booking [CITED: github.com/firefly-iii/firefly-iii/issues/11925].
**Fingerprint refs** (`fp:` prefix, only when no `entry_reference`): `sha256(account | booking_date | signed amount | currency | counterparty | description)` plus an occurrence index for identical rows on the same day. The occurrence index depends on feed order and is the weakest link; the spike must show whether ING supplies `entry_reference` (if so this path stays dormant and is covered only by synthetic tests).

### Pattern 3: Reconciliation as a pure function producing a plan

**What:** `TransactionReconciler.Plan(existing, incoming, fetchCoverage)` returns `Inserts`, `StatusUpgrades` (pending to booked), `FieldUpdates`, `Merges`, `AmbiguousFlags`, `Drops`. The store applies the plan in one database transaction; unique indexes are the safety net, not the logic.
**Algorithm (per account, per sync, after ALL pages of the fetch succeeded):**
1. For each incoming item resolve `ref` -> existing transaction. Found: apply only forward transitions (pending -> booked; never booked -> pending); update mutable fields; append a payload row if the payload hash changed.
2. Not found and the item is booked: find pending candidates in the same account with equal signed amount and currency, and normalised counterparty equal, whose `transaction_date`/`booking_date` differ by at most the match window (default 5 days, configurable). Exactly one candidate AND that candidate is a candidate for no other unresolved booked item: merge (the pending row becomes booked, gains the new ref in `transaction_refs`, keeps its internal id). Zero: insert. Two or more on either side: insert the booked item unmerged, set `match_flag = ambiguous` on the pending row and the new row, increment the flagged count.
3. Not found and pending: insert as pending.
4. Drops: an existing pending row whose ref and fingerprint are absent from a fetch that (a) completed every page without error and (b) covered its date (`date_from` at or before the row's transaction date) is marked `dropped` with `dropped_at`. A failed or partial fetch never drops anything.
5. `CNCL`/`RJCT` statuses for a known ref also become `dropped`; `SCHD`/`OTHR` items are not stored (raw payload still logged in `provider_calls` counters only).

### Pattern 4: Consent as an explicit state machine, derived not inferred

**What:** Persisted `bank_connections.status` records what the provider told us (`pending_authorization`, `active`, `provider_expired`, `revoked`, `superseded`, `failed`). The externally visible state is a pure function `Derive(status, valid_until, now, warnDays)` returning `Linked`, `Expiring` (within 14 days), `Expired`. `EXPIRED_SESSION` / `CLOSED_SESSION` / `REVOKED_SESSION` / `SESSION_DOES_NOT_EXIST` errors flip the stored status immediately, even before `valid_until` (Enable Banking documents premature expiry causes: a second authorisation replacing the first, a yearly bank KYC step, certificate migration [CITED: enablebanking.com/docs/faq/]).
**Renewal:** create a new `bank_connections` row via the same start/callback path; map returned accounts to existing `accounts` by `identification_hash` (the FAQ states account uids differ per session and `identification_hash` is "a stable identifier for an account across different sessions"); update each account's current `provider_account_uid` and `bank_connection_id`; mark the old connection `superseded`. Do not call `DELETE /sessions` on the superseded session automatically (Assumption A9).

### Pattern 5: Scheduler = minute-granularity due-check over persisted runs

**What:** A `BackgroundService` ticks once a minute (`PeriodicTimer` with the injected `TimeProvider`) and asks a pure function whether a run is due: local date in Europe/Amsterdam, scheduled time reached (default 06:30), no successful run for that local date, day's retry not yet used. Restart-safe because state is in `sync_runs`; a process restart after 06:30 runs the catch-up. 06:30 avoids the DST gap hour (02:00-03:00). Verified: 06:30 Amsterdam is 04:30 UTC on 2027-03-28 and 05:30 UTC on 2026-10-25.
**Retry rule (D-10):** transient failure -> one retry at least 4 hours later the same local day; rate-limited or consent/auth rejection -> no retry that day.
**Concurrency:** partial unique index on `sync_runs(bank_connection_id) WHERE finished_at IS NULL` so scheduler, post-link sync and "sync now" cannot overlap; on startup mark any orphaned unfinished run as `abandoned`.

### Pattern 6: Quota ledger

**What:** Every outbound account-data call is written to `provider_calls(account_id, called_at, kind, background bool, http_status, provider_error)` before/after sending. "Calls remaining" = configured limit (default 4) minus background calls for that account in the trailing 24 hours (conservative: a rolling window over-counts relative to a calendar day, which is the safe side; the true window semantics are a spike question). Count every page and every balance/details call. "Sync now" refuses when a plan would consume the last remaining call unless it carries PSU headers (Open Question 2).

### Pattern 7: Metrics as a projection of database state

`SyncMetricsRefresher` (hosted service, every 60 s) reads connections, accounts, last runs and drift/flag counts and sets gauges; on startup the first refresh runs before the first scrape matters. Counters are pre-initialised for every label value so `increase()` works from the first event. See the metrics table below.

### Anti-Patterns to Avoid

- **Keying transactions on `entry_reference`.** Use the internal id + `transaction_refs`.
- **Dropping pending rows on a partial or errored fetch.** Only after every page of a successful fetch that covered the date.
- **In-memory-only gauges.** A restart would reset "last success" to 0 and fire the stale-sync alert.
- **`AddStandardResilienceHandler()` on the Enable Banking client.** It retries 429/5xx and burns the quota.
- **Reading `entry_reference` from `transaction_id`.** `transaction_id` "can not be used to uniquely identify transactions and may change" [CITED: enablebanking.com/docs/api/reference/ Transaction schema].
- **`security_invoker` views.** They would need base-table grants for `grafana_reader`; the owner-rights default is the intended hard boundary.
- **Deleting the superseded session after renewal without evidence.** May close the new consent at banks that keep one consent per PSU.
- **Any sync trigger from a dashboard, MCP or health path.**

## Enable Banking API Reference (for the adapter)

All facts below are from the official OpenAPI file `https://enablebanking.com/docs/api/reference/enablebanking-api.yaml` (version `1.0.0-ef13dc17`, fetched 2026-09-30) unless noted. [CITED]

| Item | Value |
|------|-------|
| Base URL | `https://api.enablebanking.com` (`api.tilisy.com` is deprecated) |
| Auth header | `Authorization: Bearer <JWT>`; JWT header `{"typ":"JWT","alg":"RS256","kid":"<application id>"}`; claims `iss`=`enablebanking.com`, `aud`=`api.enablebanking.com`, `iat`, `exp`; max TTL 86400 s (docs quick start uses 3600) |
| Start | `POST /auth` body: `access{valid_until, balances?, transactions?, accounts?}`, `aspsp{name,country}`, `state`, `redirect_url`, `psu_type` (`personal`/`business`; docs "highly recommend always provide"), optional `language` (2-letter), `psu_id`. Response: `url`, `authorization_id`, `psu_id_hash` |
| Exchange | `POST /sessions {code}` -> `session_id`, `accounts[]` (AccountResource), `aspsp`, `psu_type`, `access.valid_until` |
| Session | `GET /sessions/{session_id}` -> `status` in `INVALID, PENDING_AUTHORIZATION, RETURNED_FROM_BANK, AUTHORIZED, EXPIRED, CLOSED, REVOKED, CANCELLED`, `accounts_data[]`, `access`, `created/authorized/closed` |
| Revoke | `DELETE /sessions/{session_id}` -> `{"message":"OK"}`; "PSU's bank consent will be closed automatically if possible" |
| Account fields | `uid` (session-scoped, may be absent when the account cannot be fetched), `identification_hash` (stable across sessions), `identification_hashes[]`, `account_id{iban|other}`, `name`, `details`, `product`, `currency`, `cash_account_type` in `CACC, CASH, CARD, LOAN, SVGS, OTHR`, `usage` `PRIV|ORGA` |
| Balances | `GET /accounts/{uid}/balances` -> `balances[]` of `{name, balance_amount{currency,amount}, balance_type, reference_date?, last_change_date_time?, last_committed_transaction?}`; `balance_type` in `CLAV, CLBD, FWAV, INFO, ITAV, ITBD, OPAV, OPBD, PRCD, OTHR, VALU, XPCD`; FAQ: banks support a limited set, commonly `CLBD, ITAV, XPCD` |
| Transactions | `GET /accounts/{uid}/transactions?date_from&date_to&transaction_status&continuation_key&strategy` (`strategy` in `default|longest`; `date_from/date_to` are inclusive dates, "UTC timezone is assumed") -> `{transactions[], continuation_key}` |
| Transaction fields | `entry_reference`, `transaction_id`, `status` in `BOOK, CNCL, HOLD, OTHR, PDNG, RJCT, SCHD`, `credit_debit_indicator` in `CRDT, DBIT`, `transaction_amount{currency, amount:string ^-?\d+(\.\d+)?$}`, `booking_date`, `value_date`, `transaction_date` (date strings), `creditor{name}`, `creditor_account{iban}`, `debtor{name}`, `debtor_account{iban}`, `remittance_information[]`, `reference_number`, `bank_transaction_code{code, sub_code, description}`, `balance_after_transaction`, `merchant_category_code`, `exchange_rate`, `note` |
| Errors | body `{message, code, error, detail}`; `error` enum includes `ASPSP_RATE_LIMIT_EXCEEDED`, `ASPSP_ERROR`, `ASPSP_TIMEOUT`, `ASPSP_ACCOUNT_NOT_ACCESSIBLE`, `EXPIRED_SESSION`, `CLOSED_SESSION`, `REVOKED_SESSION`, `SESSION_DOES_NOT_EXIST`, `WRONG_SESSION_STATUS`, `EXPIRED_AUTHORIZATION_CODE`, `WRONG_AUTHORIZATION_CODE`, `REDIRECT_URI_NOT_ALLOWED`, `WRONG_TRANSACTIONS_PERIOD`, `WRONG_CONTINUATION_KEY`, `PSU_HEADER_NOT_PROVIDED`, `PSU_HEADER_INVALID`, `UNAUTHORIZED_ACCESS`, `UNAUTHORIZED_IP`, `ACCESS_DENIED`, `NO_ACCOUNTS_ADDED`, `AUTHORIZATION_NOT_PROVIDED`; HTTP 400/401/403/404/408/422/429/500 |
| Discovery | `GET /aspsps?country=NL&service=AIS` -> per ASPSP `maximum_consent_validity` (seconds), `required_psu_headers`, `auth_methods`, `psu_types`; `GET /application` -> app `kid`, `environment` (`SANDBOX|PRODUCTION`), `redirect_urls`, `active`, `services` (`AIS|PIS`) |
| Payment routes | `/payments`, `/payments/{id}`, `/payments/{id}/submit`, `/payments/{id}/transactions/{id}` exist in the same API. They must be unreachable from this codebase (SEC-01) |

Behavioural facts from the FAQ [CITED: enablebanking.com/docs/faq/]:
- **Continuation:** repeat the request with `continuation_key` until it is null; all other GET parameters must equal the first request; an empty page with a key still requires continuing; page size varies; the key is "Only valid in current session".
- **History depth:** full history "is typically available only for a short period of time after the initial authorisation... generally around one hour", after which "many ASPSPs restrict access... to just the past 90 days". `strategy=longest` "finds the earliest available transaction", uses `date_from` as a lower border, ignores `date_to`, may use extra ASPSP calls, and returns an empty list instead of `WRONG_TRANSACTIONS_PERIOD`. `strategy=default` returns `WRONG_TRANSACTIONS_PERIOD` for an unavailable range.
- **Rate limits:** "many ASPSPs have the limit of 4 times a day for data fetches when PSU is not online"; online is signalled by PSU headers (`Psu-Ip-Address`, `Psu-User-Agent`, ...). "Either all required PSU headers or none". After `ASPSP_RATE_LIMIT_EXCEEDED` "we recommend to continue data fetching after 6 hours". For `ASPSP_ERROR` retry after roughly 1 minute, 1 h, 2 h, 4 h.
- **Consent:** `valid_until` is chosen by the client, capped at now + `maximum_consent_validity` ("For the majority of ASPSPs... 180 days"), no separate refresh; premature `EXPIRED_SESSION` (HTTP 401) must be handled by starting a new authorisation.
- **Restricted production mode:** activation by "linking accounts" in the Control Panel; "you can only fetch data from accounts linked to the application"; other accounts are "stripped from the response"; the linking flow does not authorise the API app, so the API flow must be run separately [CITED: enablebanking.com/docs/api/linked-accounts/, /docs/faq/].
- **Application key:** the Control Panel lets you "generate the private externally (e.g., via OpenSSL) and provide the public key", so the production private key can be created on the host and never travel [CITED: enablebanking.com/docs/api/control-panel/].
- **Sandbox:** a Mock ASPSP can be driven from the Control Panel for end-to-end tests without a real bank; it returns transactions in batches of 10 (good for exercising continuation) [CITED: enablebanking.com/docs/api/sandbox/].

## Spike Protocol and Decision Table

The spike is a workstation-only, parallel operator task (D-01, D-03). Suggested shape: a throwaway script or file-based C# app kept **outside** the repository, output piped straight through `age -r <public key>` into an encrypted folder. Local tooling available: `age 1.2.1`, `openssl 3.5.5`, `jq`, `curl` [VERIFIED: local commands].

| Step | What to do / capture | Answers |
|------|----------------------|---------|
| S0 | Register a **sandbox** app first; run the whole adapter against the Mock ASPSP (no bank, no quota). Confirm the JWT (including the extra `nbf`) is accepted. | Adapter shape, JWT acceptance (A5) |
| S1 | Register a **production** app (keep separate from the server's production app, Open Question 3), "Activate by linking accounts" and note which ING accounts the link screen offers. Check whether a savings account is listed and linkable. | Q1 savings coverage |
| S2 | `GET /aspsps?country=NL&service=AIS`: record ING `maximum_consent_validity`, `required_psu_headers`, `auth_methods`, `psu_types`. Run `POST /auth` with `psu_type=personal`, `valid_until = now + maximum`; record `access.valid_until` returned by `POST /sessions`, `accounts[].cash_account_type`, `product`, `name` shape (masked or not). | Q2 validity, D-09 listing fields |
| S3 | **Immediately** after authorisation: `strategy=longest` fetch of each account with no `date_from`; count pages and calls; record the earliest date. About 2 hours later repeat and record how far back it now reaches. | INGEST-05, 1-hour window |
| S4 | Fetch balances: which `balance_type` values ING returns, whether `reference_date` / `last_committed_transaction` are present, whether `CLBD` equals the sum implied by transactions. | D-12 reconciliation basis |
| S5 | Over 3+ days, before each fetch make a card purchase and an iDEAL payment. Capture with `transaction_status` unset: `status` values present, `entry_reference` present/absent for pending and booked, whether it changes at booking, `booking_date` on pending, `transaction_id`, `bank_transaction_code`, `balance_after_transaction`, remittance layout, duplicates of identical amounts on one day. | Q4, Patterns 2-3 |
| S6 | On the last day, deliberately exceed the background quota once: note which calls (transactions page, balances, details) count, whether continuation pages count, the moment of the 429, and whether it resets at midnight or after 24 h. Repeat one call **with** PSU headers to see whether it is admitted. | Q3, Pattern 6, Open Question 2 |
| S7 | `DELETE /sessions/{id}` on the spike session; confirm status becomes `CLOSED` and the bank shows the consent gone. Delete the workstation key. | D-01 cleanup |
| S8 | Write only synthetic fixtures modelled on the captures; delete the encrypted folder. | D-01 |

| Spike outcome | Design consequence |
|---------------|--------------------|
| Savings account absent | Ship joint-account-only (D-02). Keep `accounts.cash_account_type` and generic dashboard; balance snapshots for savings simply do not exist. Note in docs that savings need a manual or non-PSD2 route later |
| ING sends no `entry_reference` | Enable fingerprint refs with occurrence index; add a synthetic-scenario test for two identical same-day payments; consider raising the flag rate threshold |
| ING sends stable `entry_reference` across pending to booked | Step 1 of the algorithm does nearly all the work; step 2 remains for banks that change it |
| ING returns no pending transactions | Keep pending support (schema, dashboard marker, reconciler) but it is exercised only by synthetic tests; simplify docs |
| Full history really is 90 days after ~1 h | Confirms the "sync immediately" design; document that a missed window means renew again to re-obtain full history |
| Quota counts every page | Post-link and manual syncs must send PSU headers; ongoing daily fetches use a tight `date_from` so one page usually suffices |
| Quota is calendar-day rather than rolling 24 h | Optionally switch the trailing window to the Amsterdam calendar day |
| `maximum_consent_validity` about 90 days | Expiring window of 14 days still fits; alert cadence unchanged |

## Data Model (recommended; identity model is the costly-reversibility part)

All timestamps `timestamptz` (UTC); bank calendar dates `date` (never converted); money `numeric(19,4)` with currency `char(3)`; snake_case via the existing `SnakeCaseNaming` convention.

| Table | Key columns | Notes |
|-------|-------------|-------|
| `bank_connections` | `id` uuid, `provider` text, `aspsp_name`, `aspsp_country`, `status`, `session_id_protected` text, `authorized_at`, `valid_until`, `superseded_by_id`, `closed_at` | Session id stored through `ISecretProtector.Protect`; never logged |
| `bank_authorizations` | `id`, `state_sha256` bytea unique, `authorization_id`, `purpose` (`link|renew`), `created_at`, `expires_at`, `consumed_at` | Only the hash of `state` is stored; consumption is one atomic `UPDATE ... WHERE consumed_at IS NULL AND expires_at > now()` |
| `accounts` | `id` uuid, `account_key` opaque text (random, used in metrics), `bank_connection_id`, `provider_account_uid`, `identification_hash` unique per provider, `iban`, `display_name`, `cash_account_type`, `currency`, `sync_enabled` | IBAN and names live in the database only; `account_key` is what metrics/labels use |
| `transactions` | `id` uuid, `account_id`, `status` (`pending|booked|dropped`), `booking_date`, `value_date`, `transaction_date`, `amount`, `currency`, `counterparty_name`, `counterparty_iban`, `description`, `match_flag`, `first_seen_at`, `booked_at`, `dropped_at` | `status` and `match_flag` mapped as text with check constraints |
| `transaction_refs` | `account_id`, `ref`, `transaction_id`, `first_status`, `first_seen_at` | `UNIQUE (account_id, ref)` is the idempotency lock |
| `transaction_payloads` | `id`, `transaction_id`, `sync_run_id`, `observed_at`, `payload jsonb`, `payload_sha256` | Append-only: `REVOKE UPDATE, DELETE, TRUNCATE ... FROM ledger_runtime`, mirroring how the initial migration already revokes on the EF history table |
| `balance_snapshots` | `id`, `account_id`, `snapshot_date` (Amsterdam date), `balance_type`, `amount`, `currency`, `reference_date`, `expected_amount`, `drift_amount`, `reconciled bool`, `sync_run_id` | `UNIQUE (account_id, snapshot_date, balance_type)` |
| `sync_runs` | `id`, `bank_connection_id`, `trigger` (`scheduled|retry|post_link|manual`), `started_at`, `finished_at`, `outcome`, `provider_error`, `calls_made`, `inserted`, `updated`, `dropped` | Partial unique index enforcing one unfinished run per connection |
| `provider_calls` | `account_id`, `called_at`, `kind`, `background`, `http_status`, `provider_error` | Quota ledger and diagnostic trail |

Reporting views (created in a migration by `ledger_migrator`, so they inherit the existing default `SELECT` grant to `grafana_reader`): `reporting.accounts`, `reporting.recent_transactions` (excludes `dropped`, exposes `effective_date = COALESCE(booking_date, transaction_date)` and text-cast dates), `reporting.account_status` (last success, consent state derived in SQL, days left, latest balance and `reconciled`, flagged count). No view exposes `transaction_payloads`, `session_id_protected` or other secrets.

Enum-like columns should be plain text with check constraints (as the canary table already uses `ck_data_protection_canary_id`) so views and Grafana value mappings work without Postgres enum types.

## Dashboard Generator (`Ledger.Dashboards`)

- **Project:** console `net10.0` project in `Ledger.slnx`, no packages, `generate` and `check` commands; public generator class referenced by unit tests. Follow `Directory.Build.props` (nullable, warnings as errors, lock file).
- **Typed model** covers only what is used: `Dashboard` (uid, title, schemaVersion, editable=false, time, timezone, tags, templating, links, panels), `Templating.Query` (multi-value `account` variable), `StatPanel`, `TablePanel`, `TextPanel` (optional), `Target` (postgres `rawSql`, `format`, `editorMode`), `FieldConfig` (unit, decimals, thresholds, value mappings, overrides), `Transformation` (`organize` with `renameByName`/`indexByName`/`excludeByName`).
- **Determinism:** fixed `JsonSerializerOptions`, declaration-order properties, `\n` newlines, sequential panel ids, no timestamps or GUIDs; fixed uids `ledger-sync-en` / `ledger-sync-nl`. A unit test regenerates in memory and compares bytes with the committed files; another asserts every translation key exists in both languages and every key referenced by a definition is present.
- **Datasource ids:** a file-provisioned datasource declared `type: postgres` is stored by Grafana 13.2.2 as `grafana-postgresql-datasource` (verified by booting the pinned image with the repo's datasource file: `/api/datasources` returned `ledger-reporting  grafana-postgresql-datasource`). Panels and variables should reference `{"type":"grafana-postgresql-datasource","uid":"ledger-reporting"}`.
- **Classic v1 JSON provisions fine on 13.2.2:** a dashboard with `schemaVersion` 41, a multi-value query variable, a `stat` panel and a `table` panel loaded from file, was reported `provisioned: true` in folder "Household Ledger", and `schemaVersion` was preserved [VERIFIED: local run of `grafana/grafana:13.2.2`].
- **Language handling:** SQL returns language-neutral codes (`pending`, `booked`, `linked`, `expiring`, `expired`); the generator emits value mappings and `organize` `renameByName` per language. Panel titles, descriptions, headers, mapping text and the dashboard title are the translated surface. A dashboard `links` entry can jump EN <-> NL.
- **Read-only proof in the generator tests:** every target's datasource uid must be `ledger-reporting` (or, if any operational panel uses it, `prometheus`), and every `rawSql` may reference only `reporting.` objects.
- **Time zone:** set `"timezone": "Europe/Amsterdam"`; cast bank dates to text in the views so Grafana cannot shift a `date` across midnight.
- **Multi-value variable in SQL:** use `IN (${account:sqlstring})` (Grafana's postgres query docs show `IN($hostname)` for multi-select) [CITED: grafana.com/docs/grafana/latest/datasources/postgres/query-editor/]; macros `$__timeFilter(col)` for the time range.

### Installer and lint gaps to close

| Gap | Evidence | Fix |
|-----|------|-----|
| Dashboard files are not installed where the provider looks | provider path `path: /var/lib/grafana/dashboards/ledger` [VERIFIED: deploy/provisioning/grafana/provisioning/dashboards/ledger.yaml:16] but the installer runs `rm -rf /etc/grafana/provisioning` then `cp -a "$provisioning_src" /etc/grafana/provisioning` [VERIFIED: deploy/lib/deploy.sh:394-395] and `package-release.sh` ships `deploy/` (minus `tests`) | Commit JSON under `deploy/provisioning/grafana/provisioning/dashboards/json/` and set the provider `path` to `/etc/grafana/provisioning/dashboards/json` (verified working in the pinned Grafana with exactly this layout). Also update the file's header comment, which currently says no dashboards ship |
| Lint hard-codes the alert count | `if [ "$alert_rule_count" -ne 8 ]; then` [VERIFIED: build/lint/checks/60-observability.sh:266] | Raise the count by the number of new rules; extend the same check to assert the dashboards uids exist via `/api/search` and that the `Household` folder rules are loaded |
| Notification tree is one provisioned resource | Grafana docs: the whole tree is "a single, large resource" [CITED: grafana.com alerting file-provisioning docs] | Add the household child route inside the existing `notification-policies.yaml`, do not create a second file |

## Metrics, Alerts and Notification Cadence

Metric names and labels (Claude's discretion; `account`/`connection` labels are opaque keys, never IBAN/name):

| Metric | Type | Labels | Source |
|--------|------|--------|--------|
| `ledger_bank_consent_days_until_expiry` | gauge | `connection` | refresher: `(valid_until - now)/86400`, may go negative |
| `ledger_bank_consent_state` | gauge (one-hot) | `connection`, `state` in `linked|expiring|expired` | derived state |
| `ledger_sync_last_success_timestamp_seconds` | gauge | `account` | latest successful run per account |
| `ledger_sync_failing` | gauge (one-hot) | `connection`, `reason` in `transient|rate_limited|consent_rejected|provider_auth` | 1 when the latest run failed in an alert-worthy way (transient only after the day's retry also failed); cleared by the next success |
| `ledger_sync_errors_total` | counter | `reason` | pre-initialised for each reason |
| `ledger_sync_calls_remaining` | gauge | `account` | quota ledger |
| `ledger_balance_reconciliation_drift` | gauge | `account` | 1 when latest snapshot does not reconcile |
| `ledger_transactions_flagged` | gauge | `account` | ambiguous pending/booked flags |

Alerts (new file `alerting/household-rules.yaml`, group `household`, folder `Household`, same rule format as `platform-rules.yaml`: Prometheus query node `A`, threshold expression node `C`, summary-only annotations, `noDataState: OK` because no series exists before the first link, `execErrState: Alerting`):

| Rule | Expression idea | Fires |
|------|-----------------|-------|
| Bank sync failing | `max(ledger_sync_failing{reason="transient"})` > 0 | after the retry failed |
| Bank sync rate limited | `reason="rate_limited"` > 0 | immediately |
| Bank consent rejected | `reason=~"consent_rejected|provider_auth"` > 0 | immediately |
| Bank sync stale | `time() - max(ledger_sync_last_success_timestamp_seconds)` > 93600 | 26 h, same threshold the backup-stale rule uses [VERIFIED: platform-rules.yaml:153 `params: [93600]`] |
| Bank consent expires within 14 days | `min(ledger_bank_consent_days_until_expiry)` < 14 | from day 14 |
| Bank consent expires within 7 days | same, < 7 | from day 7 |
| Bank consent expired | same, < 0 or `state="expired"` | on expiry |
| Balance does not reconcile | `max(ledger_balance_reconciliation_drift)` > 0 | operator only |

Titles and summaries name the rule only (existing convention, D-17). **Repeat cadence:** the root policy is `group_by: [grafana_folder]`, `group_interval: 1h`, `repeat_interval: 12h` [VERIFIED: notification-policies.yaml:14-18], so a rule that stays firing from day 14 would email about twice a day for two weeks. Recommended: add a child route matching the household consent rules with `repeat_interval: 24h` (one reminder a day until renewed) and leave the root cap in force; the literal alternative is one-shot windows, which risk being missed if Grafana is down during the window. See Open Question 1.

## Callback and Linking Flow Details

- **Endpoints (authenticated unless noted), all under the internally routed `/api/`:** `POST /api/v1/bank/connections/link`, `POST /api/v1/bank/connections/{id}/renew`, `GET /api/v1/bank/callback` (**AllowAnonymous**), `GET /api/v1/bank/connections`, `GET /api/v1/bank/accounts`, `PUT /api/v1/bank/accounts/{key}` (display name, `syncEnabled`), `POST /api/v1/bank/sync`, `DELETE /api/v1/bank/connections/{id}`. Traefik already routes `PathPrefix(/api/)` behind the LAN/VPN allowlist [VERIFIED: deploy/traefik/ledger.yml.example: `rule: "Host(`ledger-api.example.com`) && PathPrefix(`/api/`)"`], so the callback needs no new route, but the redirect URL registered in the Control Panel must be that exact URL.
- **State:** 32 random bytes from `RandomNumberGenerator`, base64url; only its SHA-256 is stored; TTL 15 minutes; consumed atomically; a second use, an unknown state, an expired state, or a `purpose` mismatch all return the same generic failure. Response: plain, minimal text (e.g. "Linked 2 accounts."), `Cache-Control: no-store`, `Referrer-Policy: no-referrer`. Never log the query string, `code` or `state`.
- **Code exchange:** call `POST /sessions` inside the callback request (codes expire: `EXPIRED_AUTHORIZATION_CODE`), then persist the connection, accounts and protected session id in one transaction.
- **Account selection:** the callback records all returned accounts with `sync_enabled = false`; the operator's `PUT` calls set names and flags; the first sync starts when selection is confirmed. Because the full-history window is about an hour, the runbook must say "select immediately after linking". On renewal, accounts map by `identification_hash` and the first sync starts automatically.
- **Validate the URL EB returns** before handing it to the operator: HTTPS and a host under `enablebanking.com`.
- **Inventory test:** enumerate `EndpointDataSource` endpoints and assert every one carries authorization metadata except an explicit allow-list containing exactly the callback route (the fallback policy is `RequireAuthenticatedUser()` [VERIFIED: Ledger.Service/Program.cs:83-84]).

## Code Examples

### Mint the Enable Banking client JWT (verified by running it)

```csharp
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Service.Ingestion.EnableBanking;

/// <summary>Mints short-lived RS256 tokens for the Enable Banking API from the application's private key.</summary>
public sealed class EnableBankingTokenMinter : IDisposable
{
    private readonly RSA _rsa = RSA.Create();
    private readonly RsaSecurityKey _key;
    private readonly TimeProvider _time;

    /// <summary>Loads the PEM private key and binds the application id as the key id (becomes the JWT kid header).</summary>
    public EnableBankingTokenMinter(string privateKeyPem, string applicationId, TimeProvider time)
    {
        _rsa.ImportFromPem(privateKeyPem);
        _key = new RsaSecurityKey(_rsa) { KeyId = applicationId };
        _time = time;
    }

    /// <summary>Creates a token valid for thirty minutes.</summary>
    public string Create()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "enablebanking.com",
            Audience = "api.enablebanking.com",
            IssuedAt = now,
            Expires = now.AddMinutes(30),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <inheritdoc />
    public void Dispose() => _rsa.Dispose();
}
```
Probe output for this exact configuration: header `{"alg":"RS256","kid":"<id>","typ":"JWT"}`, claims `aud, iss, exp, iat, nbf`. Setting `kid` through `AdditionalHeaderClaims` throws `IDX14116`; it must come from `RsaSecurityKey.KeyId`. `nbf` is added automatically and is not listed in the Enable Banking docs; confirm acceptance in S0, and if rejected build the token from `SecurityTokenDescriptor.Claims` instead.

### AIS-only outbound guard (SEC-01 defence in depth)

```csharp
using System.Text.RegularExpressions;

namespace Ledger.Service.Ingestion.EnableBanking;

/// <summary>Refuses to send any request that is not an account-information call. Payment routes are unreachable by construction.</summary>
public sealed partial class AisOnlyGuardHandler : DelegatingHandler
{
    [GeneratedRegex(@"^/(auth|sessions|application|aspsps)$|^/sessions/[0-9a-f-]{36}$|^/accounts/[0-9a-f-]{36}/(details|balances|transactions)$")]
    private static partial Regex AllowedPath();

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var readOnlyVerb = request.Method == HttpMethod.Get
            || (request.Method == HttpMethod.Post && (path == "/auth" || path == "/sessions"))
            || (request.Method == HttpMethod.Delete && path.StartsWith("/sessions/", StringComparison.Ordinal));

        if (!readOnlyVerb || !AllowedPath().IsMatch(path))
        {
            throw new InvalidOperationException("Outbound bank request refused: only account-information routes are allowed.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
```
The unit test runs the whole adapter against a recording inner handler and asserts the set of `(method, path template)` pairs equals the allow-list, and that a hand-built `/payments` request throws.

### Next scheduled run in Europe/Amsterdam (verified conversions)

```csharp
namespace Ledger.Domain.Ingestion;

/// <summary>Computes scheduled sync instants for a wall-clock time in a named zone, safe across daylight saving changes.</summary>
public static class SyncSchedule
{
    /// <summary>Returns the UTC instant of the given local time on the given local date.</summary>
    public static DateTimeOffset InstantFor(DateOnly localDate, TimeOnly localTime, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate.ToDateTime(localTime), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>Returns the local calendar date in the zone for the given instant.</summary>
    public static DateOnly LocalDate(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
}
```
Probe: `06:30` Amsterdam maps to `05:30Z` on 2026-10-25 and `04:30Z` on 2027-03-28; `IsInvalidTime` is true for 02:30 on 2027-03-28. Tests use `FakeTimeProvider` around both dates.

### Continuation loop with consistent parameters

```csharp
public async IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(
    ProviderAccountRef account, TransactionQuery query, FetchContext context,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    string? continuationKey = null;
    do
    {
        var uri = TransactionUri(account.Uid, query, continuationKey);
        using var response = await SendAsync(HttpMethod.Get, uri, context, cancellationToken);
        var page = await ReadPageAsync(response, cancellationToken);
        continuationKey = page.ContinuationKey;
        yield return page;
    }
    while (!string.IsNullOrEmpty(continuationKey));
}
```
`TransactionUri` must emit the same `date_from`, `strategy` and `transaction_status` on every page and add only `continuation_key`. Each `SendAsync` writes a `provider_calls` row. Amounts are parsed with `decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)`; `CRDT` keeps the sign, `DBIT` negates it; more than four decimals is an error, never a silent round.

### Callback opts out of the fallback policy, and a test proves it is the only one

```csharp
endpoints.MapGet("/api/v1/bank/callback", CallbackHandler.HandleAsync).AllowAnonymous();
```
```csharp
[Fact]
[Trait("Category", "Callback")]
public void Only_the_bank_callback_endpoint_allows_anonymous_access()
{
    var anonymous = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        .Select(e => e.RoutePattern.RawText)
        .ToList();

    anonymous.Should().BeEquivalentTo(["/api/v1/bank/callback"]);
}
```

### Pre-initialised, DB-seeded metrics

```csharp
private static readonly Counter SyncErrors = Prometheus.Metrics.CreateCounter(
    "ledger_sync_errors_total", "Failed sync runs by reason.", "reason");

/// <summary>Creates every reason series at zero so increase() works from the first failure.</summary>
public static void InitialiseCounters()
{
    foreach (var reason in new[] { "transient", "rate_limited", "consent_rejected", "provider_auth" })
    {
        SyncErrors.WithLabels(reason);
    }
}
```
Follows the existing static-gauge pattern in `LedgerMetrics.cs` (`Prometheus.Metrics.CreateGauge(name, help, labelNames)`, `.WithLabels(...).Set(...)`) [VERIFIED: Ledger.Service/Metrics/LedgerMetrics.cs:10-13, 26].

### Views run with owner rights; prove it for every object

```csharp
[Fact]
[Trait("Category", "DatabaseRoles")]
public async Task Grafana_reader_has_only_select_on_every_reporting_object_and_nothing_in_public()
{
    var offending = await QueryAsync("ledger_backup", """
        SELECT c.relname
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'reporting' AND c.relkind IN ('r','v','m')
          AND (has_table_privilege('grafana_reader', c.oid, 'INSERT')
            OR has_table_privilege('grafana_reader', c.oid, 'UPDATE')
            OR has_table_privilege('grafana_reader', c.oid, 'DELETE')
            OR has_table_privilege('grafana_reader', c.oid, 'TRUNCATE'))
        """);
    offending.Should().BeEmpty();
}
```
Complements the existing `AssertDeniedAsync("grafana_reader", ..., "42501")` pattern in `DatabaseRoleTests`, which already covers a denied `INSERT` and `CREATE TABLE` [VERIFIED: Ledger.IntegrationTests/Database/DatabaseRoleTests.cs:35-46]. Add: for each generated dashboard query, execute it as `grafana_reader` against the migrated database (macros replaced with `TRUE` and sample values) to catch SQL errors before Grafana does.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| RS256 client JWT | manual base64url + `SignData` framing | `Microsoft.IdentityModel.JsonWebTokens` `JsonWebTokenHandler` | Header/claim framing and encoding pitfalls; verified output |
| Encrypting the bank session at rest | custom AES/keys | existing `ISecretProtector` (Data Protection, certificate-protected key ring in the database) | Already tested with a restart canary; purpose string fixed `HouseholdLedger.Secrets` |
| One-time state / random tokens | `Guid`/`Random` | `RandomNumberGenerator.GetBytes(32)` + `SHA256.HashData` | Unguessable and hash-at-rest |
| Idempotent insert | select-then-insert races | `INSERT ... ON CONFLICT` via `ExecuteSqlInterpolatedAsync` plus the unique index | EF Core 10 has no native upsert; the index is the real guarantee |
| Decimal money | `double`, culture-dependent parse | `decimal` with `CultureInfo.InvariantCulture`, `numeric(19,4)` | Exactness to the cent |
| Cron parsing | Cronos/Quartz | `TimeZoneInfo` + due-check loop | Single daily job; DST verified |
| HTTP retry/backoff | generic retry handler on this client | explicit run-level retry rule | Retrying 429/5xx consumes the bank quota |
| Read-only DB guarantee | app-level SQL filtering in Grafana | grants on `reporting` views for `grafana_reader` | Grafana does not restrict SQL; only grants do |
| Alert routing and dedup | custom email code | Grafana provisioned rules + notification policy | Existing contact point and volume cap |
| Metrics exposition | hand-written `/metrics` | `prometheus-net` | Already in use |
| Dashboard JSON authoring | hand-edited EN/NL copies | the typed C# generator (locked decision) | Cannot drift; deterministic |

**Key insight:** in this domain the expensive bugs are not algorithmic; they are silent quota burn, silent duplicates, and silent staleness. Each "don't hand-roll" entry above has a constraint that makes the naive version fail quietly.

## Common Pitfalls

### Pitfall 1: Missing the one-hour full-history window
**What goes wrong:** the first sync runs on the next scheduler tick or after a slow account-selection step, and the bank now returns only 90 days.
**Why:** many banks restrict history shortly after authorisation [CITED: enablebanking.com/docs/faq/].
**How to avoid:** start the sync from the callback/selection request path; make it restartable; on a partial failure retry within the hour; document that renewing again restores the window.
**Warning signs:** earliest booking date is roughly 90 days back after the first sync.

### Pitfall 2: Restricted-mode account whitelist surprises
**What goes wrong:** authorisation succeeds but `accounts` is empty or lacks the savings account.
**Why:** only accounts linked in the Control Panel are returned in restricted mode; unlinked ones are stripped.
**How to avoid:** link every wanted account first (S1); treat "empty accounts" as a distinct, actionable link error with a specific operator message; test it with a synthetic empty session.

### Pitfall 3: Retries that spend the quota
**What goes wrong:** a resilience handler retries a 429 or each page of a long history; the real scheduled sync then fails for the day.
**How to avoid:** no automatic HTTP retry on the Enable Banking client; count every call in `provider_calls`; refuse "sync now" at the last remaining call.

### Pitfall 4: Gauges that reset on restart
**What goes wrong:** the app restarts, `ledger_sync_last_success_timestamp_seconds` reads 0, the stale alert fires (or worse, is masked).
**How to avoid:** seed from the database in the refresher; the first refresh runs immediately at startup; alert rules use `noDataState: OK` only for the pre-link period.

### Pitfall 5: Trusting identifiers
**What goes wrong:** duplicates or ghost pending rows.
**How to avoid:** Patterns 2 and 3; the reconciliation test suite uses synthetic pairs for each scenario: same ref, changed ref, no ref, two identical same-day payments, pending that vanishes, pending that reappears after booking, booked that arrives before the pending is ever seen.

### Pitfall 6: Dropping on incomplete data
**What goes wrong:** a rate-limited or errored fetch is treated as "the bank no longer lists it", and real pending rows are dropped.
**How to avoid:** drops only after a complete, error-free fetch that covered the row's date; a fetch cut short by `WRONG_CONTINUATION_KEY` restarts from the first page.

### Pitfall 7: Session-scoped account uids
**What goes wrong:** after renewal the old `provider_account_uid` no longer works and history looks disconnected.
**How to avoid:** join renewed accounts on `identification_hash`; update uid/connection on the existing `accounts` row; never key ledger rows on the uid.

### Pitfall 8: Date and time-zone drift
**What goes wrong:** `date_from` is interpreted as UTC while bank dates are local; Grafana shifts a `date` by a day in a non-NL browser; a transaction lands in the wrong day.
**How to avoid:** keep bank dates as `date`; fetch with an overlap (default 14 days) so boundary transactions are re-seen; set the dashboard timezone and cast display dates to text; take the sync day from `LocalDate(now, Europe/Amsterdam)`; assert the zone resolves at startup (the LXC provisioning does not mention `tzdata`).

### Pitfall 9: Reconciliation false positives
**What goes wrong:** the balance is read a moment after transactions and a new booking lands between the two calls, so "drift" is reported for a healthy ledger.
**How to avoid:** fetch balances after the last transaction page, compare booked-only sums against `CLBD` (not `ITAV`/`XPCD`), exclude pending and dropped, and treat a first-seen drift as "unverified" until the next day's snapshot confirms it, while still exposing the raw drift number.

### Pitfall 10: Standard test-runner filter exit code
**What goes wrong:** after moving to Microsoft.Testing.Platform, `dotnet test --solution Ledger.slnx --filter-trait "Category=X"` fails with exit code 8 because one project contains no matching test [VERIFIED: local run showing "Zero tests ran" and exit code 8].
**How to avoid:** add `--ignore-exit-code 8` for filtered solution-wide runs or run per project.

### Pitfall 11: Log leakage through built-in loggers
**What goes wrong:** `System.Net.Http.HttpClient` logs full outbound URLs at Information; exception messages carry URLs.
**How to avoid:** set `System.Net.Http.HttpClient` to Warning in `appsettings.json` next to the existing per-category levels [VERIFIED: Ledger.Service/appsettings.json:12-19 sets `"Microsoft.AspNetCore.Hosting.Diagnostics": "Warning"`]; extend `LogRedactionTests` with sentinels for `code`, `state` and a session id; do not include response bodies in exceptions.

### Pitfall 12: Alert email flood
**What goes wrong:** a firing consent rule re-sends every 12 hours for two weeks.
**How to avoid:** child route with 24 h repeat for consent rules (Open Question 1); keep the root cap.

### Pitfall 13: Synthetic provider in production
**What goes wrong:** a misconfigured `Ingestion:Provider` feeds fake data into the real ledger.
**How to avoid:** `ProductionConfigurationValidator` names the offending key and refuses `Synthetic` in Production; when `EnableBanking` settings are absent the ingestion services stay idle and link endpoints answer "not configured" (no crash), so Phase 2 can deploy before the operator has registered an application.

## Runtime State Inventory

Not applicable: this is a greenfield feature phase, not a rename/refactor/migration. (The one runtime-state item worth noting is new: the Enable Banking application registration and its whitelisted-account list live in the aggregator's Control Panel, outside git and outside the database, and must be documented in the runbook.)

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| GoCardless Bank Account Data | Enable Banking restricted production | GoCardless closed new signups July 2025 (project research) | Enable Banking is the chosen route |
| xunit.v3 3.x on VSTest | xunit.v3 4.x on Microsoft.Testing.Platform 2 | xunit 4.0 (Dependabot failure recorded in the todo) | `dotnet test` syntax changes: `--solution`, `--project`, `--filter-trait`; exit code 8 for empty filters |
| Grafana provisioned datasource type `postgres` | stored as `grafana-postgresql-datasource` | Grafana 10+, observed on 13.2.2 | Dashboards reference the new type id |
| Provisioning Grafana dashboards through Grafonnet/Foundation SDK | typed C# model (decision D-15) | this project | No .NET SDK exists; validation is by loading in a real Grafana |

**Deprecated/outdated:** `api.tilisy.com` (use `api.enablebanking.com`); `DBTI` as the debit indicator (the OpenAPI enum is `DBIT`).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | ING NL does not expose savings accounts through Enable Banking. Evidence is DNB scope guidance and Dutch budgeting-tool statements, not ING or Enable Banking documentation | Summary, Spike table | If savings are reachable, D-02 fallback is unnecessary and savings balances must be supported; schema already generic |
| A2 | ING returns `entry_reference` for booked transactions and may return no pending transactions | Pattern 2, Spike | Fingerprint refs become primary and dedupe is weaker |
| A3 | The ~4/day background quota is per account and counts pages and balance calls; it may be rolling 24 h rather than a calendar day | Pattern 6 | Quota ledger window or size wrong; first sync could be cut short |
| A4 | Enable Banking accepts a JWT carrying an extra `nbf` claim and a 30-minute lifetime | Code Examples | Every call would 401; fixed by building the token from explicit claims |
| A5 | A user-triggered call (post-link, sync-now) carrying PSU headers is admitted outside the background quota, and ING needs both `Psu-Ip-Address` and `Psu-User-Agent` | Summary, Open Question 2 | Post-link first sync could be rate limited |
| A6 | Restricted-mode account links persist across renewals without re-linking in the Control Panel | Pattern 4 | Renewal returns an empty account list until re-linked |
| A7 | The redirect URL may be an https URL on an internal-only hostname and is browser-side only | Callback flow | Enable Banking may reject non-public or non-https redirect hosts |
| A8 | The Control Panel accepts an externally generated RSA public key (PEM or certificate; format not confirmed) | Standard stack, key custody | Key must be browser-generated and transferred to the host instead |
| A9 | `DELETE /sessions/{id}` on a superseded session could close the new consent at ING | Pattern 4 | If false, tidy revocation of old sessions is safe and preferable |
| A10 | The LXC has `tzdata` so `Europe/Amsterdam` resolves (true on the dev machine, not verified on the host) | Pitfall 8 | Scheduler cannot start; mitigate with a startup assertion and a selfcheck line |
| A11 | Enable Banking sends `error`/`error_description` style params on a cancelled redirect | Callback flow | Cancel path must be handled generically; verify in S1 |
| A12 | `numeric(19,4)` is sufficient for every amount ING returns (EUR, two decimals) | Data model | Reject-not-round rule surfaces a violation loudly |

## Open Questions

1. **Consent alert repeat cadence.** D-18 says alerts "at 14 and 7 days"; a rule that stays true re-notifies under the existing 12 h repeat.
   - What we know: root policy repeat is 12 h; a child route can lengthen it; one-shot windows are fragile.
   - Recommendation: daily reminders via a child route, keep the mail cap for everything else. Needs the operator's OK because it slightly extends the literal decision.
2. **PSU headers on user-triggered fetches.** Enable Banking says a user-triggered fetch should send the user's headers and that this lifts the background limit; D-11 was written assuming the sync-now call consumes background quota.
   - Recommendation: send headers for the post-link/renew sync (operator is present) and for sync-now; keep scheduled syncs header-less; keep D-11's refusal rule for header-less manual calls. Confirm in S6.
3. **One Enable Banking application or two?** A separate spike application keeps the workstation key disposable, but a second consent at the same bank may replace the first if ING allows only one.
   - Recommendation: two applications, run the production link only after the spike session is revoked.
4. **Where the synthetic provider lives.** Test-project-only is safest; `Ledger.Service` behind config also gives a way to seed real Grafana with fake data for dashboard validation.
   - Recommendation: `Ledger.Service`, refused in Production by the validator.
5. **Account selection under the one-hour window.** REST-only selection is one `PUT` per account; the first sync waits for it.
   - Recommendation: keep it, document the timing, and log (not alert) when selection is still pending after 30 minutes.
6. **jsonb vs json for raw payloads.** `jsonb` normalises key order and whitespace; `json` keeps bytes verbatim.
   - Recommendation: `jsonb` plus `payload_sha256` over the original bytes; amounts are strings so numeric normalisation is not a concern.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.112 | none |
| Docker (with pinned images) | Grafana/promtool lint, local Grafana probe | yes | images present: grafana 13.2.2, prometheus v3.13.3, postgres 18 | none |
| Local PostgreSQL container | real-DB integration tests | yes | `postgres-dev`, PostgreSQL 18.6; `ConnectionStrings:TestAdmin` present in user-secrets (value not printed) | CI service container |
| `age`, `openssl`, `jq`, `curl` | spike capture, key generation | yes | age 1.2.1, OpenSSL 3.5.5 | none |
| `Europe/Amsterdam` tzdata | scheduler | yes on dev machine | `/usr/share/zoneinfo/Europe/Amsterdam` | LXC not verified (A10); add selfcheck line |
| Enable Banking account + registered applications | adapter live tests, spike | no (operator action) | none | Adapter can be built against the OpenAPI file and the sandbox Mock ASPSP once a sandbox app exists |
| Operator's ING app and the ability to link accounts in the Control Panel | spike, first link | assumed yes | n/a | none; blocks live validation only |
| Internal Traefik hostname for the callback | redirect URL | existing route (`ledger-api.example.com` placeholder in the example) | n/a | none |
| Outbound HTTPS from the LXC | Enable Banking calls | yes | nftables output policy `accept` [VERIFIED: deploy/nftables/ledger.nft.in `policy accept`] | none |

**Missing with no fallback:** Enable Banking registration and the real-consent spike (external, operator-owned). Everything else can proceed against the synthetic provider (D-03).
**Missing with fallback:** LXC tzdata unknown (verify at deploy).

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 today (VSTest); migrate to 4.0.1 on Microsoft.Testing.Platform first (folded todo). FluentAssertions 8.11.0, NSubstitute 6.2.0, EF InMemory 10.0.12, `Microsoft.AspNetCore.Mvc.Testing` 10.0.12, add `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 |
| Config file | `global.json` (add `"test": {"runner": "Microsoft.Testing.Platform"}` on migration), `Ledger.IntegrationTests/xunit.runner.json` (`parallelizeTestCollections: false`) |
| Quick run command | MTP: `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj` (VSTest today: `dotnet test Ledger.UnitTests/Ledger.UnitTests.csproj`) |
| Full suite command | MTP: `dotnet test --solution Ledger.slnx --no-restore` (VSTest today: `dotnet test Ledger.slnx --no-restore`); needs `ConnectionStrings__TestAdmin` and `LEDGER_EFBUNDLE` for the migration-bundle tests, as CI sets them |
| Trait filter | MTP: `dotnet test --solution Ledger.slnx --filter-trait "Category=Ingestion" --ignore-exit-code 8`; VSTest: `--filter "Category=Ingestion"` |
| Grafana/alert provisioning check | `build/lint.sh observability` (boots pinned Grafana with the repo provisioning; update expected rule count and add dashboard assertions) |

Both `.github/workflows/ci.yml:54` and `.github/workflows/release.yml:62` currently run `dotnet test Ledger.slnx --no-restore` and both change with the migration.

### Phase Requirements -> Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| INGEST-01 | link start returns URL and stores hashed state; callback exchanges code and stores protected session | integration (fake provider) | `dotnet test ... --filter-trait "Category=Callback"` | Wave 0 |
| INGEST-01 | scheduler runs once per Amsterdam day, catches up after restart, one retry | unit (`FakeTimeProvider`, DST dates) | `--filter-trait "Category=Scheduler"` | Wave 0 |
| INGEST-02 | re-running a sync adds nothing; pending -> booked keeps one row | integration + unit | `--filter-trait "Category=Reconciliation"` | Wave 0 |
| INGEST-02 | ambiguous match stays separate and flagged; vanished pending becomes dropped only after a complete fetch | unit | `--filter-trait "Category=Reconciliation"` | Wave 0 |
| INGEST-03 | exact decimal parse, sign from CRDT/DBIT, >4 dp rejected, dates kept as dates, payload retained and append-only | unit + integration (runtime role cannot UPDATE payloads) | `--filter-trait "Category=Ingestion"` / `DatabaseRoles` | Wave 0 |
| INGEST-04 | derived linked/expiring/expired; premature `EXPIRED_SESSION` flips state; renewal maps by identification hash and keeps history | unit + integration | `--filter-trait "Category=Consent"` | Wave 0 |
| INGEST-05 | first sync uses `strategy=longest`, follows continuation with identical params, empty page with key continues | unit (fake HTTP handler) | `--filter-trait "Category=EnableBanking"` | Wave 0 |
| INGEST-06 | full pipeline runs on the synthetic provider with no code outside ingestion changed | integration | `--filter-trait "Category=Ingestion"` | Wave 0 |
| INGEST-07 | 429 -> no same-day retry, quota ledger refuses sync-now at last call, failures recorded in `sync_runs` | unit + integration | `--filter-trait "Category=Sync"` | Wave 0 |
| SEC-01 | allow-listed requests only; `/payments` refused; consent request carries account-info access only; no payment method on the interface | unit (recording handler, reflection) | `--filter-trait "Category=ReadOnlyGuard"` | Wave 0 |
| OPS-01 | metrics seeded from DB at startup; opaque labels; pre-initialised counters; no IBAN/name in `/metrics` | integration | `--filter-trait "Category=Metrics"` | Wave 0 |
| OPS-02 | rules load in the pinned Grafana with expected uids; expressions reference existing metric names | lint (real Grafana) | `build/lint.sh observability` | extend existing |
| DASH-05 | generated JSON equals committed JSON; every translation key present in EN and NL | unit | `--filter-trait "Category=Dashboards"` | Wave 0 |
| DASH-07 | `grafana_reader` has SELECT only on every `reporting` object, no access to `public`; write attempts rejected with SQLSTATE 42501; every generated query runs as `grafana_reader` | integration | `--filter-trait "Category=DatabaseRoles"` | extend existing |
| API-01 | only the callback is anonymous; other bank endpoints reject missing/invalid key; callback rejects unknown/expired/reused state with an identical generic response | integration | `--filter-trait "Category=Callback"` | Wave 0 |
| SEC-06 (regression) | sentinels for `code`, `state`, session id never in logs, responses or metrics | integration | `--filter-trait "Category=LogRedaction"` | extend existing |

### Sampling Rate
- **Per task commit:** the quick unit run for the touched area (`--filter-trait` for its category).
- **Per wave merge:** full solution run (needs the local PostgreSQL container), plus `build/lint.sh observability` when alerts, dashboards or provisioning changed.
- **Phase gate:** full suite green, `build/lint.sh` green, one manual Grafana check (open EN and NL dashboards against synthetic data and confirm panels render) before `/gsd-verify-work`; live-bank verification recorded as a human checkpoint after the spike.

### Wave 0 Gaps
- [ ] Migrate to xunit.v3 4.x on Microsoft.Testing.Platform (`global.json`, both test csproj files, lock files, `ci.yml`, `release.yml`, README/docs command lines).
- [ ] `Ledger.Dashboards` project added to `Ledger.slnx`, with lock file, and referenced by `Ledger.UnitTests`.
- [ ] `Microsoft.Extensions.TimeProvider.Testing` in both test projects.
- [ ] Synthetic provider and scenario builder (shared by unit, integration and dev seeding).
- [ ] `LedgerWebApplicationFactory` already accepts `Action<IServiceCollection>? configureTestServices` [VERIFIED: LedgerWebApplicationFactory.cs:31], so the fake provider is injectable without touching the factory.
- [ ] Recording `HttpMessageHandler` test helper for the Enable Banking client.
- [ ] `build/lint/checks/60-observability.sh` rule count and dashboard assertions.

## Security Domain

`security_enforcement` is enabled (`security_asvs_level: 2`, `security_block_on: high` in `.planning/config.json`).

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes | Existing `X-Api-Key` scheme for every bank endpoint; the callback is the single anonymous endpoint and is protected by a one-time state |
| V3 Session Management | partial | Bank session id encrypted at rest via Data Protection; single-use, 15-minute state; no cookies introduced |
| V4 Access Control | yes | Fallback `RequireAuthenticatedUser()` stays; endpoint inventory test allow-lists exactly one anonymous route; Traefik LAN/VPN allowlist for `/api/` |
| V5 Input Validation | yes | Strict parsing of callback query parameters; provider payloads treated as untrusted (descriptions can contain arbitrary text; Grafana escapes table cells); decimal parsing rejects malformed amounts; validate the authorisation URL returned by the aggregator |
| V6 Cryptography | yes | RS256 JWT via `Microsoft.IdentityModel`; `RandomNumberGenerator` for state; `SHA256` for state hash; Data Protection for session; RSA key generated on the host with `openssl`, mode 640 `root:ledger` under `/etc/ledger`, excluded from backups |
| V7 Logging | yes | No secrets, codes, states, session ids or IBANs in logs, exceptions, metric labels; `System.Net.Http.HttpClient` logging lowered; log-redaction sentinel tests; `ledger-selfcheck` scans journal for full key/session shapes |
| V8 Data Protection | yes | Financial data never in alert text; reporting views expose no payloads or secrets; least-privilege roles unchanged |
| V9 Communications | yes | TLS to `api.enablebanking.com` with default certificate validation; never disable validation |
| V10 Malicious Code / supply chain | yes | One new runtime package (first-party Microsoft); lock files committed; CI locked-mode restore |
| V12 Files | yes | Private key file permissions and path validated at startup (name the key, never the value) |
| V13 API | yes | Documented `.http` file with placeholders; generic error bodies |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Forged or replayed consent callback (login CSRF, state fixation) | Spoofing / Tampering | 256-bit state, hash stored, TTL, atomic single use, purpose binding, generic failure response |
| Leak of `code`/`state` via proxy access logs, browser history, referrer | Information disclosure | Single-use short-lived values; `Cache-Control: no-store`, `Referrer-Policy: no-referrer`; do not log query strings; note that a reverse-proxy access log may record the query |
| Payment initiation reachable through the aggregator API | Elevation of privilege | AIS-only interface, outbound allow-list handler, recorded-request test, no `/payments` string in the codebase (asserted) |
| Quota exhaustion (self-DoS) | Denial of service | Quota ledger, no interactive triggers, no retry on 429 |
| SQL write from Grafana | Tampering | `grafana_reader` grants (SELECT on `reporting` only), owner-rights views, privilege enumeration test |
| Private key or session in backups or logs | Information disclosure | Key outside backups, selfcheck log scan, DP-protected session |
| Fake provider in production | Tampering | Validator refuses `Synthetic` in Production |
| Untrusted bank text reaching Claude later | Tampering | Out of scope now; keep descriptions out of alert text and out of any log line |
| Consent silently expiring | Availability | Derived state, distinct immediate alerts, 14/7-day rules |

## Sources

### Primary (HIGH confidence)
- `https://enablebanking.com/docs/api/reference/enablebanking-api.yaml` (official OpenAPI 3.1, fetched and parsed: paths, schemas, enums, error codes)
- `https://enablebanking.com/docs/faq/` (continuation keys, entry_reference, history depth, rate limits, PSU headers, consent validity, premature expiry, account matching, restricted mode)
- `https://enablebanking.com/docs/api/quick-start/` (JWT claims and header, flow)
- `https://enablebanking.com/docs/api/control-panel/` and `/linked-accounts/` (restricted mode, external key generation)
- `https://enablebanking.com/docs/api/sandbox/` (Mock ASPSP), `https://enablebanking.com/docs/markets/nl/` (ING authentication note; no savings statement)
- Repo files read this session: `.planning/phases/02-automatic-ing-sync/02-CONTEXT.md`, `.planning/REQUIREMENTS.md`, `.planning/STATE.md`, `.planning/config.json`, `.claude/CLAUDE.md`, `Ledger.Service/*`, `Ledger.Repository/*`, `Ledger.IntegrationTests/*`, `Ledger.UnitTests/*.csproj`, `deploy/*` (provisioning, installer, lint, traefik, nftables), `docs/monitoring.md`, `docs/rest-api.md`, the three folded todos
- Local executions: pinned `grafana/grafana:13.2.2` provisioning probe; `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 JWT probe; xunit.v3 4.0.1 + Microsoft.Testing.Platform trial in a scratch copy; Amsterdam DST conversion probe

### Secondary (MEDIUM confidence)
- `https://www.dnb.nl/en/sector-information/open-book-supervision/laws-and-eu-regulations/psd2/savings-accounts/` (savings accounts with fixed contra accounts outside PSD2; optional access by agreement)
- `https://grafana.com/docs/grafana/latest/alerting/set-up/provision-alerting-resources/file-provisioning/` and `.../datasources/postgres/query-editor/` (policy tree, rule format, macros)
- `https://xunit.net/docs/getting-started/v3/microsoft-testing-platform` and `/docs/query-filter-language` (`--filter-trait`, `--filter-query`)
- nuget.org search/flat-container APIs (versions, owners, downloads)

### Tertiary (LOW confidence, marked for validation)
- `https://www.moneymonk.nl/help/562-...` and `https://www.mijngeldzaken.nl/...` (Dutch budgeting tools stating banks block savings via PSD2; not ING-specific)
- `https://github.com/firefly-iii/firefly-iii/issues/11925` (one bank changing `entry_reference` and transaction code at booking; bank not identified, unlikely ING)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH. One new runtime package, verified; everything else already locked in the repo.
- Enable Banking API mechanics: HIGH. Read from the official OpenAPI file and FAQ.
- ING-specific behaviour (savings, pending, identifiers, real quota and consent length): LOW until the spike; the plan should branch on the decision table, not assume.
- Architecture and data model: MEDIUM-HIGH. Grounded in documented aggregator behaviour and existing repo patterns; the reconciliation window and fingerprint occurrence index are tuned by spike data.
- Grafana provisioning and generator approach: HIGH for load/provisioning (executed), MEDIUM for panel option schemas (validated only by loading in a real Grafana plus a manual render check).
- Pitfalls: HIGH for those verified by execution or cited; MEDIUM for the reconciliation false-positive guidance.

**Research date:** 2026-09-30
**Valid until:** 2026-10-30 for the Enable Banking API and tooling versions (the API file carries a build hash, `1.0.0-ef13dc17`); re-check sooner if the spike contradicts a LOW item.
