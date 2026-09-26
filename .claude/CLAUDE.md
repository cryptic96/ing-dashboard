# CLAUDE.md

## Hard rules

These apply to every change in this repository, without exception.

### NEVER use planning references outside `.planning/`

Planning and tracking references belong in `.planning/` only. **Never** put them in documentation or anywhere else in the repository. That includes:

- requirement keys (`ABC-01`), decision IDs, review-finding IDs
- phase, plan, wave or milestone numbers (`Phase 2`, `02-01`, `v1`)
- planning document names (`PROJECT.md`, `REQUIREMENTS.md`, `ROADMAP.md`, `STATE.md`, `RESEARCH.md`, `CONTEXT.md`, `PLAN.md`, `SPEC.md`)

This covers: `README.md`, everything under `docs/`, setup guides, `///` XML doc comments, code, string literals, log and exception messages, test names, Grafana dashboard titles/descriptions/alert texts, MCP tool names and descriptions, OpenAPI descriptions, scripts, config and workflow files.

These references go stale the moment a phase closes. Write documentation that explains the *what* and *why* in plain language that stays true regardless of which phase produced it. Git commit messages are the only place outside `.planning/` where phase/plan identifiers may appear, for traceability.

### Comments: `///` only

No `//` comments. Only `///` XML doc summaries on types and members. If a line needs a `//` comment to be understood, rename or extract it instead.

### Public repository: no personal data, anywhere

This repository is public. Never commit personal details in code, comments, docs, fixtures, dashboards, seed data or commit messages: names, IBANs or account numbers, addresses, email addresses, real domains or hostnames, homelab IP addresses, API keys, household-specific merchant or counterparty names, or real transaction data.

- Personal configuration lives only in the server-side env file; `.env.example` contains placeholders only.
- Use `example.com` / `example.org` style placeholders in docs.
- Categories and categorisation rules that name specific counterparties live in the database, never in code or seed data.
- All test data and fixtures are synthetic.

### Branching: never commit to `main`

`main` is protected: no direct pushes, no force-pushes, changes land only through pull requests. All work — including planning docs — goes on a branch.

- Milestone work: `milestone/v<N>-<name>` (e.g. `milestone/v1-household-ledger`)
- Feature or fix work: `feature/<short-description>`

Branch from the current working branch, not from `main`, unless the work genuinely has no dependency on what is in flight. If commits land on local `main` by mistake, move them to a branch and reset local `main` to `origin/main`.

### Local database

Tests and local runs that need SQL Server use the user's own local SQL Server Docker container on `localhost:1433`. Connection strings go in `dotnet user-secrets`, never in committed `appsettings*.json`. Do not provision a different database server, and do not tear the container down afterwards. CI uses a SQL Server service container.

### No LLM API calls from the app

The application never calls the Anthropic API or any other LLM API. All Claude usage runs on the household's Claude subscription through clients that connect to the MCP server.

### Security first

This application holds a household's complete financial history. Treat security as a first-class requirement and flag weaknesses proactively — in code, deployment, CI and infrastructure alike.

- Bank access is read-only. Nothing in this application may ever initiate a payment or move money.
- Only `/mcp` is internet-facing, behind OAuth 2.1. Dashboards, REST API and web pages are reachable on the home network and VPN only.
- Least-privilege SQL logins per purpose; never `sa`.
- Secrets never appear in logs, exceptions, metrics labels or MCP tool output.

<!-- GSD:project-start source:PROJECT.md -->

## Project

**Household Ledger**

A self-hosted personal-finance backend for a two-person household that banks with ING (Netherlands). It automatically syncs transactions from the household's ING joint account and savings accounts, categorises them against a Nibud-based category tree (rules plus Claude-assisted suggestions), and exposes the result three ways: **Grafana dashboards** (English and Dutch) so both partners can see where the money goes, an **MCP server** so Claude can serve as the household's financial advisor, and a **REST API** for anything MCP is not suited to. Written in .NET 10 and deployed to a Proxmox homelab LXC from a public GitHub repository.

**Core Value:** Claude can serve as a trustworthy financial advisor for the household — answering any question about our money accurately and giving grounded, useful advice — because it has complete, correctly categorised transaction data, budgets, goals, and a shared advisor memory to reason over.

### Constraints

- **Tech stack**: .NET 10 / C#; one ASP.NET Core host serving REST API, MCP endpoint and background sync — user's main language, one deployable unit
- **Solution**: a single `.slnx` solution file
- **Data access**: Entity Framework Core, code-first, with migrations; migrations are applied automatically during deployment using the migrator login (the runtime login has no schema rights) — keeps deployments hands-off
- **LLM usage**: none from the app — no Anthropic (or other LLM) API key or billing; all Claude usage runs on the household's Claude subscription via Claude Desktop, Claude Code, claude.ai and Claude-side scheduled tasks connecting to the MCP server
- **Local development**: tests that need SQL Server use the user's local SQL Server Docker container; connection strings live in `dotnet user-secrets`, never in committed config; CI uses a SQL Server service container
- **Database**: existing network MS SQL Server — new dedicated database with least-privilege logins; never `sa`
- **Code style**: no `//` comments — only `///` XML doc summaries
- **No planning references outside `.planning/`**: never put requirement keys, decision IDs, phase/plan numbers or planning document names in documentation, READMEs, code, comments, XML docs, test names, dashboards, MCP tool descriptions or config — they go stale the moment a phase closes
- **Public repository**: no personal details anywhere in code, commits, comments, docs, fixtures or dashboards — IBANs, names, domains, API keys and similar live only in the server-side env file; merchant/category rules live in the database; all test data is synthetic
- **CI/CD**: GitHub Actions free tier (requires public repo); the self-hosted runner only deploys
- **Hosting**: Proxmox LXC; app, Grafana and Prometheus in the same LXC; automated provisioning preferred, documented one-time setup acceptable
- **Network exposure**: only `/mcp` public (HTTPS via existing Traefik, OAuth 2.1); dashboards, REST and web page reachable on the home network and VPN only
- **Bank access**: read-only, fully automatic sync
- **Language**: application code and UI in English; dashboards (and web page) translatable English/Dutch
- **Security posture**: this handles household finances — treat security as a first-class requirement, and flag weaknesses proactively

<!-- GSD:project-end -->

<!-- GSD:stack-start source:research/STACK.md -->

## Technology Stack

## 1. Bank data route for ING NL (individual, joint account + savings)

- Enable Banking (Finland-based, eIDAS-licensed AISP, ~2,700 ASPSPs across 30 European countries) grants a **free production-mode application restricted to accounts the account holder links themself** — explicitly scoped by their Terms of Service to "evaluation purposes or... the personal use of private individuals," non-commercial. This is the only mainstream provider found that has a genuine no-cost, no-sales-call path for an individual in 2026; every other aggregator (Tink, Salt Edge business tier, Yapily, TrueLayer, Ponto/Isabel) is sales-led/business-oriented with no public individual pricing.
- **ING NL coverage:** Enable Banking's Netherlands market page lists ING among the covered ASPSPs (alongside ABN AMRO, Rabobank, De Volksbank, Triodos, Van Lanschot Kempen), authenticating via the ING Bankieren app (QR/app-based SCA). The docs do not explicitly break out savings vs. current accounts, but Enable Banking's account listing endpoint returns *all* accounts the PSU consents to during the `/auth` flow — a joint current account and its linked savings accounts (spaarrekening) are exposed the same way any bank's multi-account consent works under PSD2 AISP scope. **Verify this specific detail (savings account inclusion for ING NL) during Phase 1 implementation against a real consent, since no source gives an explicit yes/no for ING's spaarrekening specifically — confidence here is LOW pending that concrete check.**
- **Auth:** JWT signed with an RSA private key (4096-bit, self-signed cert registered via the Control Panel/API), `iss`/`aud`/`iat`/`exp` claims, max token TTL 24h, `Authorization: Bearer <jwt>`. This is a private-key-JWT client-credential pattern, not a shared secret — good practice, but means the app must manage a private key file (store it exactly like the DB connection string: server-side env/secret, never in the repo).
- **Consent duration:** ASPSP-advertised `maximum_consent_validity` can be up to 180 days (the EU raised the SCA reauthentication ceiling from 90 to 180 days), but current community reports (a GitHub issue against an open-source finance app using this API) show Enable Banking's own session handling **still effectively caps renewal at ~90 days in practice** regardless of what the bank advertises — treat 90 days as the real-world renewal cadence to design the "consent expiring soon" alert and guided-renewal flow around, with 180 days as a best case.
- **History depth on first sync:** use `strategy=longest` on the transactions endpoint — tells Enable Banking to walk back to the earliest transaction the ASPSP will return, which satisfies the "no manual backfill" requirement.
- **Rate limits:** not set by Enable Banking itself but inherited from each ASPSP; the most common constraint is background (PSU-not-present) data fetches capped at ~4 calls/day per account — comfortably supports one scheduled daily sync per account.
- **Data returned:** `booking_date`, `value_date`, `transaction_date`, credit/debit indicator and a booked/pending status, debtor/creditor name and account identification (IBAN), and `remittance_information` (array — this is where Tikkie/iDEAL/SEPA description text lands, feeding the categorisation rules).
- **Cost:** production (non-restricted, multi-user/commercial) pricing is volume-based with a minimum monthly charge and requires contacting sales — irrelevant here because the restricted personal-use tier is free and fits a single household's own accounts exactly.

## 2. Grafana data access

### (a) Grafana MSSQL datasource + SELECT-only `reporting` login on views

- Grafana ships the Microsoft SQL Server datasource **as a core, bundled plugin** — no separate install, works against SQL Server 2005+. Configure a dedicated SQL login (`reporting`) with `GRANT SELECT` on a `reporting` schema of views only (never the base tables), matching the project's least-privilege login requirement.
- **Pros:** zero extra moving parts (no new service/container), full SQL expressiveness for drill-downs (category trees, budget-vs-actual, recurring-cost detection can all be expressed as views), Grafana's native templating/variables work naturally against SQL, alerting can query the same views.
- **Cons:** dashboards become coupled to the DB schema through the view layer — every dashboard change that needs a new shape means a new/altered view (a migration), so "dashboards as code" now spans two repos worth of change (SQL migration + dashboard JSON) that must ship together.
- **Security:** straightforward — `reporting` login is SQL-Server-native, auditable, and trivially rotated; no additional network surface beyond the existing MSSQL port already open to the LXC.

### (b) Grafana → REST API via Infinity datasource

- **Infinity** (`yesoreyeram-infinity-datasource`), now maintained by Grafana Labs, is the modern successor to the older `marcusolsson-json-datasource` (JSON API plugin) — Infinity supports JSON/CSV/XML/GraphQL, backend query features (alerting, recorded queries, query caching, shared dashboards), and auth methods the older JSON API plugin lacks (OAuth2, JWT, digest auth). The JSON API plugin is still maintained but Grafana's own docs steer new deployments toward Infinity whenever those backend features matter.
- **Pros:** the app's REST API becomes the single source of truth for both the web page and dashboards — one query layer to test and version, business logic (forecast projection, recurring-cost detection) lives in C# rather than duplicated in SQL views, and it naturally supports the same authentication/authorization path already built for the REST API.
- **Cons:** an extra hop and format (JSON parsing, path expressions in Grafana panel config) instead of native SQL semantics; ad-hoc "let me just add a column" dashboard tweaks are slower because they require an API change and a deploy versus editing a view.
- **Security:** Infinity's auth options (bearer token, OAuth2 client credentials) fit cleanly with the REST API already needing internal service-to-service auth; still LAN/VPN-only per the project's network constraints, so this doesn't add public exposure.

### (c) Prometheus scraping the app

- **Retention:** Prometheus retains samples for as long as configured (`--storage.tsdb.retention.time`), so retention itself is not the problem.
- **Timestamps:** a `/metrics` scrape records the value **at scrape time**, not at the transaction's booking date. A gauge like `household_transactions_total{category="groceries"}` reflects "what the count was when Prometheus last scraped," which has no relationship to *when* that spending happened in the household's calendar — you cannot ask Prometheus "what did we spend in March" after May, because March's scrapes only exist if you were already scraping in March with that exact label set.
- **Backfill:** `promtool tsdb create-blocks-from openmetrics` can backfill historical blocks, but only if you can construct OpenMetrics-formatted samples for every past instant — which requires already having the point-in-time truth (i.e., the very SQL data you're trying to avoid depending on) and does not handle re-categorisation after the fact.
- **Immutability / recategorisation:** Prometheus samples are **immutable once ingested** — you cannot "edit" a stored sample to say a transaction is now `category=entertainment` instead of `category=uncategorised`; you can only emit new samples going forward. Any historical recategorisation (the core "tell Claude to fix that Tikkie from January" workflow) becomes invisible in a Prometheus-backed view of the past, silently corrupting exactly the trend/history dashboards the household cares about most.
- **Cardinality:** per-merchant or per-transaction labels would explode cardinality (thousands of unique counterparty names/IBANs as label values) — Prometheus is not designed for record-level data at all, only aggregates.

### (d) Other approaches considered

- **Separate reporting database / read replica:** overkill at household scale (single small SQL Server, no load concerns) — adds an operational burden (replication or ETL) with no benefit over option (a)'s view-schema approach on the same database.
- **Materialized/pre-aggregated tables refreshed by the BackgroundService:** worth doing *underneath* option (a) for the heavier aggregates (monthly category totals, forecast numbers) so Grafana queries stay fast, but this is an implementation detail of option (a), not a separate architecture.

### Recommendation

### Can Grafana do writes in 2026?

## 3. MCP in .NET

- **Package:** `ModelContextProtocol` (core types/client), `ModelContextProtocol.AspNetCore` (ASP.NET Core hosting, Streamable HTTP transport), `ModelContextProtocol.Core` (low-level building blocks) — official SDK, maintained by the MCP org in collaboration with Microsoft.
- **Current version:** **v2.2.0** (published 2026-08-13 per the NuGet gallery page), marked stable. Sources disagree slightly on the exact minor prior to that (one summary mentioned "1.4.0 stable / 2.0 preview" as of July 2026) — the discrepancy is explained by the SDK's v2.0.0 stable release landing shortly before, so v2.x is the current stable line; **treat "latest 2.x, verify exact patch at implementation time" as the actionable takeaway rather than pinning to 2.2.0 in stone. Confidence: MEDIUM** (version number confirmed directly against the NuGet package page and the GitHub releases page; exact minor-version history has minor cross-source noise typical of a fast-moving pre-1.0-mindset SDK).
- **Spec revision implemented:** **2026-07-28** (the current MCP specification), with backward compatibility for the prior **2025-11-25** revision and earlier. The 2026-07-28 spec is a significant revision: it removes the stateful session model (`Mcp-Session-Id`) and the `initialize`/`initialized` handshake in favor of a stateless core, adds an extensions framework, and **formally deprecates Dynamic Client Registration in favor of OAuth Client ID Metadata Documents** while keeping DCR as a backward-compatible path (see §4).
- **Transport:** **Streamable HTTP** (HTTP POST + optional SSE event stream on one root URL) is the transport for remote/production servers — this is what `ModelContextProtocol.AspNetCore` implements, and it is the correct choice here (the project's single ASP.NET Core host serving `/mcp` publicly). The SDK supports both stateless (default in 2.x) and explicit stateful session modes via `HttpServerSessionMode`, matching the 2026-07-28 spec's stateless-by-default model.
- **Authorization support:** production-ready OAuth 2.0/2.1 **resource-server** role — `ModelContextProtocol.AspNetCore` includes `McpAuthenticationHandler`, which serves **OAuth 2.0 Protected Resource Metadata** (RFC 9728) at `/.well-known/oauth-protected-resource/mcp`, returns `401` + `WWW-Authenticate: Bearer` challenges pointing at that metadata document when a request lacks a valid token, and validates bearer tokens issued by whatever separate Authorization Server is configured (RFC 9207 issuer validation, PKCE S256 enforcement). Critically, **the C# SDK plays the resource-server role only — it does not implement an authorization server** (no token issuance, no client registration, no user login screens). That piece is a separate component (§4).

## 4. OAuth for a public MCP endpoint reachable by claude.ai (web + mobile)

| Option | DCR support (2026-09) | Cost | Footprint | Verdict |
|---|---|---|---|---|
| **OpenIddict** (embedded in the .NET app) | **Not yet** — DCR (RFC 7591/7592) is an open GitHub issue targeting an `8.0.0-preview.5` milestone, not shipped as of this research date | Free (MIT) | Zero extra service (embedded) | Not viable today for the DCR requirement, despite being the most natural fit for "stay in .NET" |
| **Keycloak** (separate service) | Yes — Keycloak 26.5.0 (Jan 2026) added first-class MCP authorization-server support and documentation; DCR itself has existed for years | Free (Apache 2.0) | Heavy — JVM, needs its own DB, realistically 512MB–1GB+ RAM | Viable, but heavier than needed for one household |
| **Authentik** (separate service) | Yes — native DCR shipped in **2026.8**; OpenID Certified (Basic/Implicit/Hybrid/Config/Form-Post OP profiles) as of the same release | Free (core is open source) | Moderate — needs Postgres + Redis alongside it, but is the lighter of the two full IdPs and has a well-trodden single-`docker-compose.yml` deployment story | **Recommended** |
| **Authelia** (separate service) | **Not yet** — DCR is planned for a future 4.40.0 release (current is 4.39.x) | Free | Light | Not viable today for the DCR requirement |
| **Duende IdentityServer** (embedded) | Yes (full OAuth/OIDC AS) | **Ambiguous for this use case** — the free Community Edition is scoped to "qualifying startups and non-profits" under revenue/capital thresholds; a personal household project is arguably outside that framing, and the paid tiers start at $5,750/yr (Lite) | Embedded, no extra service | Licensing risk not worth taking when a fully free alternative (Authentik) exists |
| **External IdP** (e.g., a hosted Auth0/Okta free tier) | Varies | Free tiers exist but terms/limits change often and this adds a third-party dependency for household financial-advisor auth | N/A | Rejected — contradicts the project's self-hosted, no-unnecessary-third-party posture |

- Outbound (used for MCP connector tool calls, i.e., calls *from* Anthropic to the household's `/mcp` endpoint): IPv4 `160.79.104.0/21`, IPv6 `2607:6bc0::/48`.
- Inbound (if the household ever calls the Anthropic API itself): IPv4 `160.79.104.0/23`, IPv6 `2607:6bc0::/48`.
- A short list of previously-used IPs (`34.162.x.x/32`) is explicitly marked phased out — don't carry those over from older blog posts.

## 5. Scheduled proactive reviews

- **Pros:** zero app-side scheduling code, zero API cost accounting to build (it rides the subscription), the review literally *is* a Claude conversation and lands wherever Claude conversations land (claude.ai history, referenceable later), and it inherits the same MCP write tools (audit log, revert) already required.
- **Cons:** it is a **subscription-plan feature in research preview**, not a stable committed API — Anthropic can change or gate it; it depends on the household's Claude plan tier; "reliability" is whatever Anthropic's scheduler guarantees, which is not a documented SLA; the review only lands "in Claude" (claude.ai conversation history) unless the MCP write tools are used to also persist a copy into the app's own database (which the requirements already mandate — "stored in the app, visible in Grafana, and readable as a Claude conversation" — so this is required regardless of which scheduling mechanism is chosen).
- **Pros:** fully within the app's own control — a `BackgroundService` cron-style trigger (e.g. monthly) that calls `POST /v1/messages` with `mcp_servers` pointed at the household's own `/mcp` endpint (self-call, LAN-local, no public exposure needed for *this* path since it's the app calling itself/Anthropic, not Anthropic calling in), and the app fully owns retry/logging/reliability the same way it owns every other background job.
- **Cons:** metered API usage cost (pay-per-token, plus the MCP connector's own tool-call overhead) instead of riding the subscription; the app must hold and rotate an Anthropic API key as a new secret; **only tool calls are supported through the Messages API's MCP connector** — the full interactive feature set (resources, prompts) is not exercised, though for a scheduled "explain the past month" review, tool calls (read aggregates, read budgets/goals, write a review record) are exactly what's needed.

## 6. Observability

- **prometheus-net vs. OpenTelemetry .NET + Prometheus exporter:** OpenTelemetry's `OpenTelemetry.Exporter.Prometheus.AspNetCore` component is explicitly flagged by the OpenTelemetry project itself as **still evolving**, tracking an experimental Prometheus/OpenMetrics compatibility spec — official OpenTelemetry guidance for *production* metrics export currently favors the OTLP exporter over the Prometheus exporter. Given the project has **no other OpenTelemetry need** (no distributed tracing across services — it's a single deployable host) and Prometheus is already the fixed target (per constraints), pulling in the OTel SDK purely to re-derive a Prometheus scrape endpoint adds a layer of indirection and an experimental component for no benefit. **Recommendation: `prometheus-net` (`prometheus-net.AspNetCore`)** — mature, purpose-built, directly exposes `/metrics` with minimal setup, and is the simpler, more stable choice for this project's actual shape (one process, one metrics consumer). Revisit OpenTelemetry only if a future milestone adds distributed tracing needs across multiple services. **Confidence: MEDIUM** (the "OTel Prometheus exporter is still experimental" claim comes from the OpenTelemetry project's own blog/README, which is about as authoritative as it gets; the "no benefit here" conclusion is this research's own reasoning applied to the project's fixed architecture, not an external source).
- **Versions (as of 2026-09-26):** Grafana **13.2.2** (Grafana 13 launched at GrafanaCON 2026 in April); Prometheus **3.15.0** latest, with **3.13.3 marked LTS** — for a homelab single-instance deployment prioritising stability over bleeding-edge PromQL/service-discovery features, **track the 3.13.x LTS line** rather than the latest non-LTS minor. **Confidence: MEDIUM** (version numbers corroborated across the projects' own release pages and endoflife.date, a maintained cross-project EOL tracker).
- **Grafana Alerting vs. Prometheus Alertmanager:** Alertmanager is the more mature, config-as-code (single YAML) option specifically for *Prometheus-metric* alerts, and is the conventional choice at any real infrastructure scale. For this project specifically, however, the two alert conditions explicitly required ("sync failing", "consent expiring in N days") are **both already Prometheus metrics** (per the `/metrics` requirement), so either tool could serve them — but running a whole separate Alertmanager container/config for two alert rules, in a resource-constrained single LXC that already centralises on Grafana as "the one UI," adds an operational component (a second thing to provision-as-code, a second thing to keep alive) for no functional gain over Grafana's own **unified alerting**, which can evaluate Prometheus queries natively and already lives in the same provisioning-as-code story as the dashboards themselves. **Recommendation: Grafana unified alerting, provisioned from files, no separate Alertmanager.** Reassess only if the alert surface grows substantially (many more rules, need for Alertmanager-specific routing/inhibition logic) — not expected at household scale. **Confidence: MEDIUM.**

## 7. Grafana as code + localisation

- **Provisioning:** Grafana supports fully file-based provisioning for **datasources**, **dashboards**, and **alerting** (rules + contact points + notification policies), all under `/etc/grafana/provisioning/{datasources,dashboards,alerting}/` with YAML manifests — dashboard JSON files referenced from a `dashboards/` provider config, nested folder structure on disk mirrored into Grafana's folder tree. This is a mature, well-documented, first-party mechanism — exactly the "nothing clicked together by hand" requirement.
- **Dashboard generation tooling:** **Grafonnet (Jsonnet) is explicitly not officially supported by Grafana** — Grafana Labs' own current guidance directs users to the **Grafana Foundation SDK** instead: a strongly-typed, composable builder-pattern library available in **Go, TypeScript, Python, Java, and PHP** (no first-party C#/.NET binding as of this research). **Recommendation: generate dashboard JSON with the Foundation SDK in a small standalone Go or Python script/project inside the repo** (a one-off code-generation tool, not part of the runtime app — it doesn't need to be .NET, and forcing it into C# would mean hand-rolling what the SDK already provides in other languages), committing the generated JSON as the actual provisioned artifact so Grafana itself just loads static files. **Confidence: MEDIUM** (Foundation SDK's supported-languages list and Grafonnet's unsupported status are both stated directly on Grafana's own docs/blog).
- **English/Dutch dashboards:** Grafana's **application UI chrome** (menus, buttons, settings) does support Dutch (`nl-NL` is among its shipped locales, selectable per-user in profile preferences) — but that is Grafana's own interface, **not** a mechanism for translating user-authored dashboard content (panel titles, axis labels, text panels). There is no built-in "one dashboard, many languages" runtime switch. **Recommended approach:** keep a single source of truth per dashboard (the Foundation SDK builder script, parameterised with a translation dictionary for titles/labels/legends), and **generate two fully separate provisioned dashboards per view** (e.g. `spending-overview-en.json` / `spending-overview-nl.json`), placed in an `English/` and `Nederlands/` provisioning folder respectively, each with its own UID. This matches the must-answer hint exactly ("generated per-language dashboards from one template") and is the only approach that actually works given Grafana's lack of native dashboard-content i18n. **Confidence: MEDIUM.**
- **Install method (Ubuntu/Debian LXC):** both Grafana and Prometheus have official APT-style install paths appropriate for a Debian/Ubuntu LXC:

## 8. Core .NET libraries

| Library | Version (verify at implementation time) | Purpose |
|---|---|---|
| `Microsoft.EntityFrameworkCore.SqlServer` | **10.x** (EF Core 10, released 2025-11-11, aligned with .NET 10) | ORM against the dedicated household database |
| `Microsoft.Data.SqlClient` | **6.1.x** (pulled transitively by the EF Core provider; EF Core 10's own release train bumped it 6.1.1→6.1.6) | Low-level SQL Server driver |
| `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` | matches ASP.NET Core 10 | Persists Data Protection keys to the database via `PersistKeysToDbContext<T>()` on a context implementing `IDataProtectionKeyContext` — used to encrypt bank consent tokens (Enable Banking session/JWT material) at rest |
| `Microsoft.Extensions.Http.Resilience` | **10.x** (10.10.0 confirmed current on NuGet) | `AddStandardResilienceHandler()` on the `HttpClient` used for Enable Banking and Anthropic API calls — retry-with-jitter, circuit breaker, hedging, layered timeouts, built on Polly v8, wired into `IHttpClientFactory` with logging/metrics for free |
| `MailKit` | **4.18.x** current | Sends the "your review is ready" notification email through the existing Postfix relay (SMTP submission with STARTTLS) — deliberately used only for the no-financial-detail notification email, never for report content, per the project's constraints |
| `Testcontainers.MsSql` | **4.11.x** current | Integration tests against a real (throwaway, Dockerised) SQL Server instance — needed because `Microsoft.EntityFrameworkCore.InMemory` (used for fast unit tests, matching the reference project's pattern) cannot validate real SQL Server behaviours this project actually depends on: least-privilege login enforcement, view-based `reporting` schema correctness, and EF migrations themselves |
| ASP.NET Core built-in localization (`IStringLocalizer`, resource files) | ships with ASP.NET Core 10 | Localises the small review/edit web page (English/Dutch) — no third-party package needed; this is a well-trodden, low-risk built-in feature and did not need deep research |

- **xUnit v3** (`xunit.v3` 3.2.2 + `xunit.runner.visualstudio` 3.1.5) as the test framework — not NUnit/MSTest, matching the household's established convention.
- **FluentAssertions** (8.10.0) for assertions.
- **NSubstitute** (5.3.0) for mocking in unit tests.
- **`Microsoft.EntityFrameworkCore.InMemory`** for fast unit tests of query/business logic that doesn't depend on SQL-Server-specific behaviour.
- **`Microsoft.AspNetCore.Mvc.Testing`** for integration tests hosting the app in-process.
- **Add `Testcontainers.MsSql`** on top of this existing stack (the reference project didn't need it since its integration tests didn't require SQL-Server-specific semantics; this project does, for the reasons above) — spin up a real SQL Server container per test run, apply migrations under the dedicated migration login, and verify the runtime login's restricted permissions actually behave as intended (e.g., assert a runtime-login connection *cannot* `DROP TABLE`).
- Follow the reference project's layering discipline: EF Core packages confined to the Repository/Data project, never referenced from the API/Service project directly.

## 9. CI/CD supply-chain hardening (GitHub free tier, public repo)

## Recommended Stack

### Core Technologies

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| .NET / ASP.NET Core | 10 (GA, Nov 2025) | Single host: REST + MCP + BackgroundService | Fixed by project constraints; current LTS-track release, EF Core 10 and the MCP C# SDK both target it |
| `ModelContextProtocol.AspNetCore` | current 2.x (verify exact patch, e.g. ≥2.2.0) | MCP server, Streamable HTTP, OAuth resource-server role | Official SDK, implements current MCP spec (2026-07-28), production-ready OAuth/PRM support |
| Enable Banking API (Restricted Production) | current API, no versioned releases | ING NL bank sync (AISP) | Only individual-eligible, free, non-sales-led aggregator found that covers ING NL in 2026; GoCardless (the prior default) is closed to new signups |
| Authentik | current (≥2026.8 for native DCR) | OAuth 2.1 Authorization Server for the public `/mcp` endpoint | Free, self-hosted, native DCR (what claude.ai needs today), lighter than Keycloak, no licensing ambiguity unlike Duende |
| Grafana | 13.2.x | Primary UI, dashboards, alerting | Fixed by project constraints; current stable, native MSSQL + Infinity datasources, file-based provisioning, unified alerting |
| Prometheus | 3.13.x LTS | Operational metrics only (sync health, consent expiry) | Fixed by project constraints; explicitly NOT for financial data (immutability + scrape-time timestamps break recategorisation) |

### Supporting Libraries

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `Microsoft.EntityFrameworkCore.SqlServer` | 10.x | ORM | All data access; confined to the Repository/Data project per the reference layering |
| `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` | matches ASP.NET Core 10 | Encrypt bank consent tokens at rest | Any place a bank session token, JWT private key reference, or similar secret is persisted to the DB |
| `Microsoft.Extensions.Http.Resilience` | 10.x | HTTP retry/circuit-breaker/timeout | `HttpClient`s calling Enable Banking and the Anthropic Messages API |
| `MailKit` | 4.18.x | SMTP via existing Postfix relay | Review-ready notification email only — no financial content |
| `prometheus-net.AspNetCore` | current | `/metrics` endpoint | Sync health, last-success timestamp, consent-expiry countdown, error counters |
| `yesoreyeram-infinity-datasource` (Grafana plugin) | current | Grafana → REST API | Panels needing computed/business-logic data (forecast) rather than raw SQL aggregates |
| `volkovlabs-form-panel` (Business Forms, Grafana plugin) | current 6.x | Optional in-dashboard write panel | Only if a "recategorise from Grafana" panel is wanted later; the web page is the primary write UI |
| Grafana Foundation SDK | Go or Python | Generate dashboard JSON (EN + NL) | Build-time code-generation tool, not part of the runtime app |

### Development/Test Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| xUnit v3 | Test framework | Matches the household's existing homelab reference project convention |
| FluentAssertions | Assertions | Same |
| NSubstitute | Mocking | Same |
| `Microsoft.EntityFrameworkCore.InMemory` | Fast unit tests | Same |
| `Microsoft.AspNetCore.Mvc.Testing` | In-process integration tests | Same |
| `Testcontainers.MsSql` | Real-SQL-Server integration tests | New addition versus the reference project — needed to validate least-privilege logins, view-based reporting schema, and migrations against real SQL Server semantics that `InMemory` cannot exercise |

## Installation

# .NET packages (add to the appropriate project per the Service → Domain → Repository layering)

# Test projects

# Grafana plugins (bundled/core: MSSQL datasource needs no install)

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|--------------------------|
| Enable Banking (Restricted Production) | Salt Edge (self-serve Developer role) | If Enable Banking's ING NL coverage turns out to exclude savings accounts, or restricted-mode terms prove too limiting once tested against a real ING consent |
| Grafana MSSQL datasource (views) | Grafana Infinity → REST API for everything | If the team decides all dashboard logic should live in C# rather than SQL views, accepting the extra deploy coupling |
| Authentik | Keycloak | If the household later needs enterprise-grade IdP features (SAML, complex federation) that Authentik doesn't cover as well |
| Authentik | Embedded OpenIddict | Once OpenIddict ships DCR (tracked issue, targeting an 8.0.0-preview milestone) — revisit then, since embedding avoids running a second service entirely |
| The app calling the Messages API on a schedule | Claude-side cloud Routines | If Anthropic graduates Routines out of research preview with a documented SLA/versioning guarantee, and the household is fine with the review living primarily in claude.ai's own history |
| prometheus-net | OpenTelemetry .NET + Prometheus exporter | If a future milestone adds distributed tracing needs across multiple services, making full OTel instrumentation worth the current experimental-exporter risk |
| Grafana unified alerting | Prometheus Alertmanager | If the alert surface grows well beyond "sync failing" / "consent expiring" into something needing Alertmanager-specific routing/inhibition |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|--------------|
| GoCardless Bank Account Data for a new integration | New signups closed since July 2025; docs site sunsetting August 2026 | Enable Banking (Restricted Production) |
| ING's own developer portal / PSD2 API | Requires a licensed TPP (AISP) with eIDAS certificates — not obtainable by a private individual | A licensed aggregator (Enable Banking) |
| Prometheus as the store for financial transaction data | Scrape-time timestamps (not booking dates), no true backfill, immutable samples break recategorisation, cardinality explosion on per-transaction labels | MS SQL Server `reporting` views (Grafana MSSQL datasource) |
| Duende IdentityServer for this project | Free-tier eligibility ("qualifying startups and non-profits") is ambiguous for a personal household project; paid tiers start at $5,750/yr for no clear benefit over a free alternative | Authentik |
| OpenIddict as the sole Authorization Server today | No shipped Dynamic Client Registration yet (open issue, targeting a future preview) — claude.ai needs DCR (or CIMD, which is even less commonly implemented yet) to onboard as a client without manual pre-registration | Authentik (or Keycloak, if heavier footprint is acceptable) |
| Grafonnet (Jsonnet) for dashboard-as-code | Not officially supported by Grafana Labs; guidance explicitly points elsewhere | Grafana Foundation SDK (Go/Python/TypeScript/Java/PHP) |
| `Database.Migrate()` at app startup for this project | Runs schema changes under the app's own runtime connection — conflicts with the least-privilege "runtime login has no DDL rights" requirement | Explicit migration bundle/command run by CI/CD under a separate migration-only login |

## Stack Patterns by Variant

- Fall back to Salt Edge's Developer tier for the savings accounts specifically, keeping Enable Banking for the joint current account — the ingestion interface is already required to be provider-agnostic, so a two-provider split is architecturally cheap even if operationally slightly more complex.
- No impact — the recommended path (§5) already uses the app's own scheduled Messages API call, independent of plan-tier Routine availability.

## Version Compatibility

| Package A | Compatible With | Notes |
|-----------|------------------|-------|
| `ModelContextProtocol.AspNetCore` 2.x | ASP.NET Core 10 | Verify exact SDK patch against NuGet at implementation time — this SDK ships frequently |
| `Microsoft.EntityFrameworkCore.SqlServer` 10.x | `Microsoft.Data.SqlClient` 6.1.x | Pulled transitively; don't pin an older SqlClient manually unless a specific bug requires it |
| Grafana 13.2.x | MSSQL datasource (bundled, core) | No separate plugin install/version to track |
| Grafana 13.2.x | Infinity datasource | Install via `grafana-cli`; track its own release versioning separately from Grafana core |
| Grafana 13.2.x | Business Forms (`volkovlabs-form-panel`) 6.x | 6.x requires Grafana ≥12.3 — satisfied by 13.2.x |
| Authentik ≥2026.8 | Native DCR | Earlier Authentik versions lack native DCR — do not deploy an older pinned version expecting this feature |

## Sources

- `enablebanking.com` — API reference, FAQ, NL market page, terms of service (WebFetch, official docs — restricted-production terms, JWT auth, consent duration, `strategy=longest`, rate-limit behaviour)
- `bankaccountdata.gocardless.com/new-signups-disabled`, `developer.gocardless.com` — GoCardless signup closure and data-field documentation (WebFetch + WebSearch, official)
- `developer.ing.com` and general PSD2/TPP-licensing sources — confirms individual ineligibility for ING's own API (WebSearch, cross-checked)
- `grafana.com/docs/...` (MSSQL datasource, Infinity plugin, Business Forms plugin, provisioning, Foundation SDK, internationalization, Debian/Ubuntu install), `grafana.com/grafana/plugins/volkovlabs-form-panel/` (WebFetch, official — license/maintainer confirmation)
- `github.com/modelcontextprotocol/csharp-sdk` releases, `nuget.org/packages/ModelContextProtocol.AspNetCore`, `modelcontextprotocol.io/specification/...` (WebFetch — SDK version, spec revision, resource-server architecture)
- `platform.claude.com/docs/en/api/ip-addresses` (WebFetch, official — egress/ingress IP ranges, phased-out IPs)
- `docs.goauthentik.io`, `keycloak.org/securing-apps/mcp-authz-server`, `duendesoftware.com/pricing`, GitHub issues on `openiddict/openiddict-core` and `authelia/authelia` (WebSearch, cross-checked — DCR support matrix and IdP licensing)
- `learn.microsoft.com` (EF Core 10 what's-new, Data Protection key storage, `Microsoft.Extensions.Http.Resilience`), `nuget.org` (MailKit, Testcontainers.MsSql versions) (WebSearch, official Microsoft docs + NuGet gallery)
- `opentelemetry.io` blog on dual-exporting .NET metrics (WebSearch, official — OTel Prometheus exporter experimental status)
- `github.com/actions/attest-build-provenance`, `docs.github.com` (deployments/environments, Actions settings) (WebSearch, official — attestation workflow, required reviewers, fork-PR approval)
- Reference read: the user's existing homelab app's architecture doc and test-project files (direct file read, not web research — establishes the xUnit v3/FluentAssertions/NSubstitute/InMemory/Mvc.Testing baseline this project should match)

<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->

## Conventions

Conventions not yet established. Will populate as patterns emerge during development.
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:ARCHITECTURE.md -->

## Architecture

Architecture not yet mapped. Follow existing patterns found in the codebase.
<!-- GSD:architecture-end -->

<!-- GSD:skills-start source:skills/ -->

## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, `.github/skills/`, or `.codex/skills/` with a `SKILL.md` index file.
<!-- GSD:skills-end -->

<!-- GSD:workflow-start source:GSD defaults -->

## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:

- `/gsd-quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd-debug` for investigation and bug fixing
- `/gsd-execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->

<!-- GSD:profile-start -->

## Developer Profile

> Profile not yet configured. Run `/gsd-profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->
