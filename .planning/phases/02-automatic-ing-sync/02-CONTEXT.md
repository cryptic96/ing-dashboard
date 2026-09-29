# Phase 2: Automatic ING Sync - Context

**Gathered:** 2026-09-29
**Status:** Ready for planning

<domain>
## Phase Boundary

The household links its ING accounts once through Enable Banking's consent flow. After that, the joint account and the savings account sync every day with no manual steps:
- the first sync requests the longest history the bank link offers
- re-running a sync adds nothing
- pending transactions become booked in place
- every transaction keeps exact decimals, both dates, counterparty, description and the raw provider payload
- a daily balance snapshot proves each account reconciles to the cent

Consent state (linked, expiring, expired), days until expiry, last successful sync and sync errors are exposed as operational metrics and trigger alerts. A REST-driven guided flow handles renewal and keeps all history.

A first Grafana dashboard shows sync status and recent transactions per account, in English and Dutch, generated from one C# source. It reads through the SELECT-only reporting role.

Bank access is read-only, and ingestion goes through a provider interface that a synthetic test provider also feeds.

Not in this phase:
- categorisation, internal-transfer detection and totals by category (Phase 4)
- MCP and the public `/mcp` route (Phase 3)
- period bucketing rules for Claude's answers (Phase 3)
- budgets, goals and forecasts (Phase 5)
- a web page for linking (v2)

</domain>

<decisions>
## Implementation Decisions

### Spike & fallback
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

### Temporary Claude SSH access (folded todo, reshaped)
- **D-04:** Claude's temporary SSH access (sudo on the ledger LXC, the dynamic-config login on the reverse proxy) **stays during this phase**, including once real bank data is on the host. The user's reasoning: Claude reads the data through MCP later anyway, and the access speeds up LXC development. Two agreed mitigations:
  1. **In this phase:** the key gets a passphrase and is loaded into `ssh-agent` for sessions, so the key file on the workstation is useless on its own.
  2. **Hard removal point:** before `/mcp` goes public in Phase 3, when the host gets its first internet-facing surface. The todo moves to Phase 3.

  Claude still never reads the env file, the Data Protection certificate, the Enable Banking private key or any credential on the host.

### Linking & renewal
- **D-05:** **Link and renewal start with an authenticated REST call only.** No CLI and no web page, because it happens about four times a year after an alert email. A committed `.http` file and documented `curl` snippets use the operator API key (the header scheme from Phase 1, D-22). The call returns the Enable Banking/ING authorisation URL. The operator opens it and approves in the ING app. ING then redirects the browser to the callback on the internal REST hostname.
- **D-06:** **The callback is the only REST endpoint without an API key**, because a bank redirect can't carry one. It is protected instead by a one-time `state` value that the authenticated start call creates:
  - the state is unguessable, single-use and short-lived, and bound to that start request
  - the callback rejects anything else
  - its response reveals nothing beyond the outcome (e.g. "linked 2 accounts")

  The planner must treat this as a deliberate, tested exception to the fallback "require authenticated user" policy. — **Reversibility:** reversible — a single endpoint; replacing it later (e.g. with OIDC-authenticated linking) touches only the linking flow.
- **D-07:** **The callback is reachable from the home network or VPN only**, through the existing internal Traefik route. Linking or renewing works only while the browser handling the ING redirect is at home or on the VPN. Nothing new becomes internet-facing.
- **D-08:** **One consent, one expiry clock.** The operator's ING login shows the joint account and the savings account, and not the partner's personal accounts. The data model may support several bank connections at low cost, but the flow, alerts and dashboard are designed around one.
- **D-09:** **Accounts are chosen at link time.** The start or finish step lists what the consent exposes: masked IBAN, account type and ING's name. The operator marks which accounts to sync and gives each a display name (e.g. "Joint", "Savings"). Unselected accounts, such as the operator's own personal account, are recorded but **never fetched**, so their transactions never enter the database. A renewal keeps the selection and display names, and maps the renewed session's accounts back to the existing ledger accounts, so history continues unbroken. Account identity and names live in the database, never in the repository.

### Sync schedule & quota
- **D-10:** **One scheduled sync early each morning, Amsterdam time, plus at most one automatic retry a few hours later on a transient failure.** A rate-limit rejection is never retried the same day. This stays well inside the bank's ~4 unattended calls per account per day.
- **D-11:** **The first sync runs automatically right after a successful link or renewal.** There is also an **authenticated REST "sync now"** call. It refuses when it would use the last remaining call of the day's per-account quota. Nothing interactive (dashboard load, MCP later) ever triggers a sync.
- **D-12:** **Balances are fetched once per day**, on the first successful sync of the day, and stored as a daily balance snapshot per account. The ledger checks **previous balance + that day's booked transactions = new balance** per account. Any drift is flagged as a metric and on the dashboard. Balance snapshots also give the budgets and goals phase real savings balances.

### Pending transactions & reconciliation
- **D-13:** **Pending transactions are stored and shown**, clearly marked pending, and turned into booked in place when the booked version arrives. This applies if the spike shows ING reports them. Totals default to booked only; later phases decide per figure whether pending counts.
- **D-14:** **Pending and booked merge only when the match is certain:** a unique match on a stable provider identifier, or a unique match on amount + counterparty within a short date window. Ambiguous cases are **never guessed**: they stay separate and are flagged, with a metric count and a dashboard marker. A pending transaction that disappears from the bank feed without booking is marked **dropped**: kept for audit, hidden from the recent-transactions view and excluded from totals. Every transaction has an immutable internal ID separate from any provider ID. — **Reversibility:** costly — the ledger identity and reconciliation model becomes the base every later phase (categorisation, audit, MCP) keys on; changing it after real data lands needs a data migration.

### Dashboard
- **D-15:** **The EN and NL dashboards are generated by a C# console tool inside the solution.** It uses a small typed model of the Grafana panel types actually used, plus one EN/NL translation file. The generated JSON is committed and provisioned into the existing `Household Ledger` folder. CI fails when the committed JSON is out of date with the generator, and when a translation key is missing in either language. There is no official Grafana SDK for .NET, so the model is hand-written against the Grafana 13 dashboard schema and validated by loading it in a real Grafana. — **Reversibility:** costly — every later phase's dashboards are written in this generator; switching tools means porting all of them.
- **D-16:** **One dashboard, with status on top.**
  - Top row, per account: last successful sync, consent state with days left, today's balance and whether it reconciles, and the count of flagged or unclear matches.
  - Below: a recent-transactions table with an account selector (default last 30 days). Pending transactions are marked, dropped ones are hidden.
  - Financial data (balances, transactions) is read through `reporting` views as `grafana_reader`. Operational state (sync, consent) may come from Prometheus or from reporting views, at Claude's discretion.
  - Both partners use the same dashboard through their Viewer logins.

### Alerts
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

### Folded Todos
- **Remove temporary Claude SSH access before real bank data** (`.planning/todos/pending/2026-09-28-remove-temporary-claude-ssh-access-before-real-bank-data.md`), **reshaped by D-04**. This phase passphrase-protects the key and loads it via `ssh-agent`. Removal moves to before `/mcp` goes public in Phase 3. The todo file is updated to match.
- **Harden privileged units and scan logs for secrets** (`.planning/todos/pending/2026-09-29-harden-privileged-units-and-scan-logs-for-secrets.md`). It fits here because this phase brings the first high-value secrets onto the host: the Enable Banking private key and the bank session. It covers:
  - sandboxing `ledger-deploy-poll.service` and the `ledger-apikey` `systemd-run` call
  - having `ledger-selfcheck` scan the journal and `/var/log` for complete secret shapes, which should include the new Enable Banking key and session material, without ever printing a match
- **Migrate tests to Microsoft.Testing.Platform for xunit v4** (`.planning/todos/pending/2026-09-28-migrate-tests-to-microsoft-testing-platform-for-xunit-v4.md`). The "after go-live" condition is now met: Phase 1 is live. Doing it early in this phase means the many new ingestion tests are written on the new platform. The categories/trait filters in CI and the release workflow must keep working.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Scope and requirements
- `.planning/ROADMAP.md` §Phase 2: goal, success criteria and research flags (spike first; confirm savings coverage, rate limits and consent duration; validate pending→booked against real pairs kept outside the repo)
- `.planning/REQUIREMENTS.md`: INGEST-01..INGEST-07, SEC-01, OPS-01, OPS-02, DASH-05, DASH-07. Also API-01 for the consent-link and renewal REST endpoints this phase builds.
- `.planning/PROJECT.md` §Constraints, §Key Decisions (Enable Banking with the Salt Edge fallback, now superseded by D-02; Prometheus not for financial data; reporting views), §Context "Banking domain"
- `.claude/CLAUDE.md` §Hard rules: no planning references outside `.planning/`, `///` comments only, no personal data (IBANs, names and real transactions never in the repo; fixtures synthetic), local database, security first. §Technology Stack §1: Enable Banking details (JWT with RSA key, `strategy=longest`, ~90-day practical consent, ~4 calls/day per account). Ignore its SQL Server specifics, which the stack-override section supersedes.

### Prior phase decisions that constrain this phase
- `.planning/phases/01-secure-platform-release-pipeline/01-CONTEXT.md`:
  - D-07: secrets only in the env file
  - D-08..D-13: PostgreSQL, Unix socket, runtime/migrator/`grafana_reader` roles, local test database
  - D-16: key custody outside backups
  - D-19: email alert contact point, no financial detail
  - D-22: named API keys
  - D-23: `/metrics` on loopback

### Research (historical; decisions above take precedence)
- `.planning/research/PITFALLS.md` Pitfalls 1 (silent consent expiry), 2 (pending→booked duplicates and ghosts), 3 (rate limits), 10 (Grafana read-only is only a UI convention: prove the role can't write), 14 (personal data in a public repo), 15 (Data Protection keys), 16 (money precision), 19 (Europe/Amsterdam vs UTC)
- `.planning/research/ARCHITECTURE.md` Pattern 2: ingestion behind a provider interface with a consent state machine; idempotent upsert and raw payload retention. §Ingestion → categorisation → advisor flow. Its SQL Server details are superseded.
- `.planning/research/STACK.md` §1: bank data route (Enable Banking); §7: Grafana as code and localisation. Its Foundation SDK recommendation is superseded by D-15.

### Existing code, config and docs to extend
- `docs/rest-api.md`: API-key usage the link/renew/sync-now calls follow
- `docs/monitoring.md`: existing metrics and alerts; extend with the sync and consent alerts and what to do for each
- `deploy/provisioning/grafana/provisioning/alerting/`: contact point, notification policy (mail-volume cap), platform rule format
- `deploy/provisioning/grafana/provisioning/dashboards/ledger.yaml`: dashboard provider; its comment already requires EN+NL generated dashboards
- `deploy/provisioning/grafana/provisioning/datasources/ledger.yaml`: `Ledger Reporting` (postgres, `grafana_reader` via peer auth) and `Prometheus` datasources
- `Ledger.Repository/Migrations/20260927183611_InitialCreate.cs`: `reporting` schema and default SELECT grants for `grafana_reader`

### Todos folded into this phase
- `.planning/todos/pending/2026-09-28-remove-temporary-claude-ssh-access-before-real-bank-data.md` (reshaped by D-04)
- `.planning/todos/pending/2026-09-29-harden-privileged-units-and-scan-logs-for-secrets.md`
- `.planning/todos/pending/2026-09-28-migrate-tests-to-microsoft-testing-platform-for-xunit-v4.md`

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `Ledger.Domain/Security/ISecretProtector.cs` with `Ledger.Service/Security/DataProtectionSecretProtector.cs`: encrypt the Enable Banking session and any consent material at rest.
- `Ledger.Service/Auth/ApiKeyAuthenticationHandler.cs`, with the fallback "require authenticated user" policy in `Program.cs`: every new REST endpoint is authenticated by default. Only the consent callback opts out (D-06).
- `Ledger.Service/Metrics/LedgerMetrics.cs` (prometheus-net, static gauges): add the sync, consent, balance-reconciliation and flagged-match metrics here, or alongside it.
- `Ledger.Service/Endpoints/StatusEndpoints.cs`: minimal-API endpoint pattern (`/api/v1/...`, extension method on `IEndpointRouteBuilder`) for the link, renew, callback and sync-now endpoints.
- `Ledger.Repository/LedgerDbContext.cs` plus `Conventions/SnakeCaseNaming.cs`: add the account, bank-connection, transaction, raw-payload and balance-snapshot entities. Migrations add the `reporting` views, which get the default SELECT grant.
- `Ledger.IntegrationTests/Infrastructure/` (`DatabaseFixture`, `LedgerWebApplicationFactory`): real-PostgreSQL integration tests. `Database/DatabaseRoleTests.cs` is the place to prove `grafana_reader` can't write.

### Established Patterns
- Layering: `Ledger.Domain` (no EF/HTTP: provider interface, consent state machine, reconciliation logic), `Ledger.Repository` (EF Core, migrations, stores), `Ledger.Service` (ASP.NET Core host, endpoints, background services). EF Core stays out of Domain and Service.
- `packages.lock.json` in every project with locked-mode restore in CI. New packages (e.g. a JWT library for the Enable Banking client assertion, `Microsoft.Extensions.Http.Resilience`) need lock-file updates.
- Tests use xUnit (moving to v4 on Microsoft.Testing.Platform, per the folded todo), FluentAssertions and NSubstitute, with trait categories used as CI filters. Real-database tests run against the user's local PostgreSQL container via user-secrets, and a service container in CI. No Testcontainers.
- Host scripts under `deploy/` have offline logic tests in `deploy/tests/`, and `build/lint.sh` runs actionlint, zizmor, shellcheck and gitleaks. Any new host-side step follows that pattern.
- Grafana provisioning is file-based from the repo. Alert rules are YAML with a threshold expression node, and titles and summaries never carry query results.

### Integration Points
- Internal Traefik route to the REST API (LAN/VPN allowlist): the consent callback URL registered with Enable Banking is on this hostname.
- The env file (`deploy/ledger.env.example` gets placeholders only) holds the Enable Banking application ID and the private key path.
- Prometheus scrapes the loopback ops endpoint, and Grafana reads Prometheus plus `Ledger Reporting`.
- `ledger-selfcheck` gains checks for the new secrets (folded todo), and the root installer provisions the new dashboard JSON and alert rules.

</code_context>

<specifics>
## Specific Ideas

- The user asked how often linking would really happen (about four renewals a year) before choosing the flow. Frequency drove the choice of the simplest REST-only path over a CLI or page.
- The operator's ING app shows the joint account and the savings account, and not the partner's personal accounts. The user's own personal account may be exposed by the consent and must be deselectable at link time.
- The user kept the SSH access deliberately and accepted the mitigations. They see MCP read access and SSH as comparable, and were told they aren't: SSH reaches the env file, the Data Protection certificate and the aggregator key. The removal point is fixed at "before `/mcp` goes public".
- Alerts are for the operator; the partner's view of "is the data arriving" is the dashboard status row.

</specifics>

<deferred>
## Deferred Ideas

- **Savings-account coverage through a second provider** (e.g. Salt Edge). Only needed if the spike shows Enable Banking's ING consent misses the savings account. Revisit behind the provider interface after v1.
- **CLI or web page for linking/renewal.** Not needed at ~4 uses a year. The v2 review/edit web page could host it later.
- **Public consent callback.** Rejected; linking stays home/VPN-only.
- **Removal of Claude's SSH access.** Moved to before `/mcp` goes public (Phase 3), per D-04.

</deferred>

---

*Phase: 02-automatic-ing-sync*
*Context gathered: 2026-09-29*
