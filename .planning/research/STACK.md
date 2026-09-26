# Stack Research

**Domain:** Self-hosted household personal-finance backend (.NET 10) — automatic ING NL bank sync, categorisation, MCP-based AI advisor, REST API, Grafana dashboards
**Researched:** 2026-09-26
**Confidence:** MEDIUM overall (HIGH on stable ecosystem facts — EF Core, MSSQL, Grafana/Prometheus core; LOW on a few fast-moving specifics called out inline — exact aggregator rate limits, exact IdP versions, plugin pricing minutiae)

This file answers each of the nine must-answer research questions directly, then rolls the decisions up into the standard stack tables for roadmap consumption.

---

## 1. Bank data route for ING NL (individual, joint account + savings)

**Confirmed: ING's own developer portal (`developer.ing.com`) PSD2 API is not usable by a private individual.** It requires the caller to be a regulated Third Party Provider (AISP/PISP) holding an eIDAS QWAC/QSEALC certificate issued under a national competent authority licence (in NL, DNB). A private household cannot obtain this licence; ING's docs are written for licensed fintechs, not consumers. **Confidence: MEDIUM** (consistent across ING's own developer portal description and general PSD2 TPP-licensing sources; no source contradicts it).

**GoCardless Bank Account Data (formerly Nordigen) — closed to new signups.** GoCardless stopped accepting new Bank Account Data accounts from **July 2025**; the dedicated page `bankaccountdata.gocardless.com/new-signups-disabled` confirms new signups remain disabled through the research date (2026-09-26), and the legacy docs site itself is scheduled to disappear on 24 August 2026. Existing accounts created before the cutoff keep working, but a greenfield project in September 2026 **cannot** register. This was the most commonly recommended free option historically (used by Actual Budget, Firefly III, etc.) — it is no longer viable for this project. **Confidence: MEDIUM (cross-checked across the vendor's own status page, GitHub issue threads, and third-party trackers).**

**Recommendation: Enable Banking, "Restricted Production" personal-use tier.**

- Enable Banking (Finland-based, eIDAS-licensed AISP, ~2,700 ASPSPs across 30 European countries) grants a **free production-mode application restricted to accounts the account holder links themself** — explicitly scoped by their Terms of Service to "evaluation purposes or... the personal use of private individuals," non-commercial. This is the only mainstream provider found that has a genuine no-cost, no-sales-call path for an individual in 2026; every other aggregator (Tink, Salt Edge business tier, Yapily, TrueLayer, Ponto/Isabel) is sales-led/business-oriented with no public individual pricing.
- **ING NL coverage:** Enable Banking's Netherlands market page lists ING among the covered ASPSPs (alongside ABN AMRO, Rabobank, De Volksbank, Triodos, Van Lanschot Kempen), authenticating via the ING Bankieren app (QR/app-based SCA). The docs do not explicitly break out savings vs. current accounts, but Enable Banking's account listing endpoint returns *all* accounts the PSU consents to during the `/auth` flow — a joint current account and its linked savings accounts (spaarrekening) are exposed the same way any bank's multi-account consent works under PSD2 AISP scope. **Verify this specific detail (savings account inclusion for ING NL) during Phase 1 implementation against a real consent, since no source gives an explicit yes/no for ING's spaarrekening specifically — confidence here is LOW pending that concrete check.**
- **Auth:** JWT signed with an RSA private key (4096-bit, self-signed cert registered via the Control Panel/API), `iss`/`aud`/`iat`/`exp` claims, max token TTL 24h, `Authorization: Bearer <jwt>`. This is a private-key-JWT client-credential pattern, not a shared secret — good practice, but means the app must manage a private key file (store it exactly like the DB connection string: server-side env/secret, never in the repo).
- **Consent duration:** ASPSP-advertised `maximum_consent_validity` can be up to 180 days (the EU raised the SCA reauthentication ceiling from 90 to 180 days), but current community reports (a GitHub issue against an open-source finance app using this API) show Enable Banking's own session handling **still effectively caps renewal at ~90 days in practice** regardless of what the bank advertises — treat 90 days as the real-world renewal cadence to design the "consent expiring soon" alert and guided-renewal flow around, with 180 days as a best case.
- **History depth on first sync:** use `strategy=longest` on the transactions endpoint — tells Enable Banking to walk back to the earliest transaction the ASPSP will return, which satisfies the "no manual backfill" requirement.
- **Rate limits:** not set by Enable Banking itself but inherited from each ASPSP; the most common constraint is background (PSU-not-present) data fetches capped at ~4 calls/day per account — comfortably supports one scheduled daily sync per account.
- **Data returned:** `booking_date`, `value_date`, `transaction_date`, credit/debit indicator and a booked/pending status, debtor/creditor name and account identification (IBAN), and `remittance_information` (array — this is where Tikkie/iDEAL/SEPA description text lands, feeding the categorisation rules).
- **Cost:** production (non-restricted, multi-user/commercial) pricing is volume-based with a minimum monthly charge and requires contacting sales — irrelevant here because the restricted personal-use tier is free and fits a single household's own accounts exactly.

**Fallback: Salt Edge.** Has a documented self-serve "Developer" role explicitly aimed at individuals evaluating the API firsthand, and a freemium model, but Salt Edge does not publish concrete personal-use pricing or ToS restrictions the way Enable Banking does — **this needs a direct signup/ToS check before relying on it (LOW confidence)**. Use it only if Enable Banking's restricted mode turns out not to cover ING's savings accounts or some other ING-specific limitation surfaces during Phase 1 spike.

**What NOT to use:** GoCardless Bank Account Data (signups closed), Tink/Yapily/TrueLayer/Ponto-Isabel (all enterprise sales-led, no individual self-serve tier found, aimed at fintechs building products on top, not individuals reading their own accounts).

---

## 2. Grafana data access

The project has already ruled out Prometheus as the *store of record* for financial data (see PROJECT.md) — this section confirms why precisely, and compares the remaining options as requested.

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

Answered precisely, because the "does history survive" question is really three separate mechanics:

- **Retention:** Prometheus retains samples for as long as configured (`--storage.tsdb.retention.time`), so retention itself is not the problem.
- **Timestamps:** a `/metrics` scrape records the value **at scrape time**, not at the transaction's booking date. A gauge like `household_transactions_total{category="groceries"}` reflects "what the count was when Prometheus last scraped," which has no relationship to *when* that spending happened in the household's calendar — you cannot ask Prometheus "what did we spend in March" after May, because March's scrapes only exist if you were already scraping in March with that exact label set.
- **Backfill:** `promtool tsdb create-blocks-from openmetrics` can backfill historical blocks, but only if you can construct OpenMetrics-formatted samples for every past instant — which requires already having the point-in-time truth (i.e., the very SQL data you're trying to avoid depending on) and does not handle re-categorisation after the fact.
- **Immutability / recategorisation:** Prometheus samples are **immutable once ingested** — you cannot "edit" a stored sample to say a transaction is now `category=entertainment` instead of `category=uncategorised`; you can only emit new samples going forward. Any historical recategorisation (the core "tell Claude to fix that Tikkie from January" workflow) becomes invisible in a Prometheus-backed view of the past, silently corrupting exactly the trend/history dashboards the household cares about most.
- **Cardinality:** per-merchant or per-transaction labels would explode cardinality (thousands of unique counterparty names/IBANs as label values) — Prometheus is not designed for record-level data at all, only aggregates.

**Conclusion: Prometheus is correct only for *operational* metrics (sync health, last-successful-sync timestamp, days-to-consent-expiry, error counters) — never for financial facts.** This matches the decision already recorded in PROJECT.md; this research confirms the reasoning is sound and current.

### (d) Other approaches considered

- **Separate reporting database / read replica:** overkill at household scale (single small SQL Server, no load concerns) — adds an operational burden (replication or ETL) with no benefit over option (a)'s view-schema approach on the same database.
- **Materialized/pre-aggregated tables refreshed by the BackgroundService:** worth doing *underneath* option (a) for the heavier aggregates (monthly category totals, forecast numbers) so Grafana queries stay fast, but this is an implementation detail of option (a), not a separate architecture.

### Recommendation

**Primary: (a) MSSQL datasource against a `reporting` schema of views, SELECT-only login.** It is the simplest, most auditable option that reuses infrastructure already in the constraints (existing MSSQL server, no new service), gives Grafana native SQL power for the exact kind of category/period/merchant drill-downs the dashboards need, and keeps the write path (categorisation, budgets, MCP tool calls) entirely inside the app/API where audit logging already lives.

**Secondary, additive: (b) Infinity against the REST API for any panel that needs logic the SQL layer shouldn't own** (e.g., the end-of-month forecast, which is genuinely a computed projection, not a stored fact) — use both datasources in the same Grafana instance, each for what it's naturally good at, rather than forcing everything through one.

### Can Grafana do writes in 2026?

**Yes, via the Business Forms panel** (`volkovlabs-form-panel`). Its original maintainer, Volkov Labs, ceased operations on 2025-09-26; Grafana Labs has since **republished and taken over maintenance of the plugin under the same plugin ID**, and it remains **Apache-2.0, free, works on Grafana OSS** (not enterprise-gated) — current release (6.x) targets Grafana ≥12.3. It lets a dashboard panel POST/PUT data back to a datasource that supports it (Infinity against the REST API is the natural pairing — the MSSQL datasource is read-only by design and not a write target). This is a legitimate option for a lightweight "recategorise this transaction" panel directly in a Grafana dashboard, but the project's own web page (already planned as a nice-to-have) is the more maintainable place for that kind of interactive editing; treat Business Forms as an optional enhancement, not the primary write UI. **Confidence: MEDIUM** (plugin page directly confirms license/maintainer/compatibility; the Volkov Labs shutdown and Grafana Labs takeover is corroborated by multiple independent sources).

---

## 3. MCP in .NET

- **Package:** `ModelContextProtocol` (core types/client), `ModelContextProtocol.AspNetCore` (ASP.NET Core hosting, Streamable HTTP transport), `ModelContextProtocol.Core` (low-level building blocks) — official SDK, maintained by the MCP org in collaboration with Microsoft.
- **Current version:** **v2.2.0** (published 2026-08-13 per the NuGet gallery page), marked stable. Sources disagree slightly on the exact minor prior to that (one summary mentioned "1.4.0 stable / 2.0 preview" as of July 2026) — the discrepancy is explained by the SDK's v2.0.0 stable release landing shortly before, so v2.x is the current stable line; **treat "latest 2.x, verify exact patch at implementation time" as the actionable takeaway rather than pinning to 2.2.0 in stone. Confidence: MEDIUM** (version number confirmed directly against the NuGet package page and the GitHub releases page; exact minor-version history has minor cross-source noise typical of a fast-moving pre-1.0-mindset SDK).
- **Spec revision implemented:** **2026-07-28** (the current MCP specification), with backward compatibility for the prior **2025-11-25** revision and earlier. The 2026-07-28 spec is a significant revision: it removes the stateful session model (`Mcp-Session-Id`) and the `initialize`/`initialized` handshake in favor of a stateless core, adds an extensions framework, and **formally deprecates Dynamic Client Registration in favor of OAuth Client ID Metadata Documents** while keeping DCR as a backward-compatible path (see §4).
- **Transport:** **Streamable HTTP** (HTTP POST + optional SSE event stream on one root URL) is the transport for remote/production servers — this is what `ModelContextProtocol.AspNetCore` implements, and it is the correct choice here (the project's single ASP.NET Core host serving `/mcp` publicly). The SDK supports both stateless (default in 2.x) and explicit stateful session modes via `HttpServerSessionMode`, matching the 2026-07-28 spec's stateless-by-default model.
- **Authorization support:** production-ready OAuth 2.0/2.1 **resource-server** role — `ModelContextProtocol.AspNetCore` includes `McpAuthenticationHandler`, which serves **OAuth 2.0 Protected Resource Metadata** (RFC 9728) at `/.well-known/oauth-protected-resource/mcp`, returns `401` + `WWW-Authenticate: Bearer` challenges pointing at that metadata document when a request lacks a valid token, and validates bearer tokens issued by whatever separate Authorization Server is configured (RFC 9207 issuer validation, PKCE S256 enforcement). Critically, **the C# SDK plays the resource-server role only — it does not implement an authorization server** (no token issuance, no client registration, no user login screens). That piece is a separate component (§4).

**Recommendation:** target the current 2.x line of `ModelContextProtocol` + `ModelContextProtocol.AspNetCore`, verify the exact patch version against NuGet at implementation time, and build the MCP endpoint as an OAuth 2.1 **resource server** per the SDK's built-in support — do not hand-roll RFC 9728 metadata serving or bearer validation. **Confidence: MEDIUM-HIGH** on the architecture (resource-server-only role, Streamable HTTP, PRM support) — this is corroborated by the SDK's own API docs, a Microsoft .NET blog post, and multiple independent implementation write-ups; MEDIUM on the exact version pin.

---

## 4. OAuth for a public MCP endpoint reachable by claude.ai (web + mobile)

**What claude.ai actually requires (2026-09):** when a user adds a custom connector, the claude.ai web/mobile client:
1. Fetches `/.well-known/oauth-protected-resource` from the MCP server (RFC 9728) to discover the Authorization Server.
2. Fetches that Authorization Server's `/.well-known/oauth-authorization-server` metadata (RFC 8414).
3. **Registers itself as an OAuth client**, using either:
   - **Dynamic Client Registration** (RFC 7591, `POST /register`) — the mechanism claude.ai currently exercises in practice for most self-hosted connectors, or
   - **Client ID Metadata Documents (CIMD)** — an HTTPS URL used directly as `client_id`, pointing at a static JSON document describing the client; the MCP 2026-07-28 spec now prefers this over DCR and formally deprecates DCR (though DCR remains supported for backward compatibility, and it's what most current authorization-server implementations actually ship first).
4. Runs the standard **OAuth 2.1 authorization-code flow with PKCE (S256)**, with a fixed claude.ai callback URL, and uses short-lived access tokens against the resource server.

In short: **the Authorization Server the household stands up must support Dynamic Client Registration** to work with claude.ai today without manual per-client pre-registration friction (CIMD support is newer/rarer and would also work, but DCR is the safer bet for compatibility right now).

**Which AS fits a single-household self-hosted .NET app — comparison:**

| Option | DCR support (2026-09) | Cost | Footprint | Verdict |
|---|---|---|---|---|
| **OpenIddict** (embedded in the .NET app) | **Not yet** — DCR (RFC 7591/7592) is an open GitHub issue targeting an `8.0.0-preview.5` milestone, not shipped as of this research date | Free (MIT) | Zero extra service (embedded) | Not viable today for the DCR requirement, despite being the most natural fit for "stay in .NET" |
| **Keycloak** (separate service) | Yes — Keycloak 26.5.0 (Jan 2026) added first-class MCP authorization-server support and documentation; DCR itself has existed for years | Free (Apache 2.0) | Heavy — JVM, needs its own DB, realistically 512MB–1GB+ RAM | Viable, but heavier than needed for one household |
| **Authentik** (separate service) | Yes — native DCR shipped in **2026.8**; OpenID Certified (Basic/Implicit/Hybrid/Config/Form-Post OP profiles) as of the same release | Free (core is open source) | Moderate — needs Postgres + Redis alongside it, but is the lighter of the two full IdPs and has a well-trodden single-`docker-compose.yml` deployment story | **Recommended** |
| **Authelia** (separate service) | **Not yet** — DCR is planned for a future 4.40.0 release (current is 4.39.x) | Free | Light | Not viable today for the DCR requirement |
| **Duende IdentityServer** (embedded) | Yes (full OAuth/OIDC AS) | **Ambiguous for this use case** — the free Community Edition is scoped to "qualifying startups and non-profits" under revenue/capital thresholds; a personal household project is arguably outside that framing, and the paid tiers start at $5,750/yr (Lite) | Embedded, no extra service | Licensing risk not worth taking when a fully free alternative (Authentik) exists |
| **External IdP** (e.g., a hosted Auth0/Okta free tier) | Varies | Free tiers exist but terms/limits change often and this adds a third-party dependency for household financial-advisor auth | N/A | Rejected — contradicts the project's self-hosted, no-unnecessary-third-party posture |

**Recommendation: Authentik, self-hosted as a separate service** (its own container/LXC, not embedded in the ASP.NET Core host — OAuth authorization-server concerns are cleanly separable from the app per the SDK's resource-server-only design in §3). It has native DCR (2026.8+), is fully open source with no licensing ambiguity, and there is a well-documented pattern for exactly this "Claude custom connector → self-hosted AS → resource server" chain. The MCP app itself only needs to be a **resource server** validating tokens Authentik issues — it never needs to implement DCR, token issuance, or user login screens itself. **Confidence: MEDIUM** (Authentik's 2026.8 DCR feature and OpenID certification are corroborated by the project's own release notes and its docs page; the OpenIddict/Authelia DCR gaps are corroborated by their own open GitHub issues, which is about as authoritative as a "not yet shipped" claim can get).

**Egress IP allowlisting for Traefik:** Anthropic **does publish stable IP ranges** at `platform.claude.com/docs/en/api/ip-addresses`:
- Outbound (used for MCP connector tool calls, i.e., calls *from* Anthropic to the household's `/mcp` endpoint): IPv4 `160.79.104.0/21`, IPv6 `2607:6bc0::/48`.
- Inbound (if the household ever calls the Anthropic API itself): IPv4 `160.79.104.0/23`, IPv6 `2607:6bc0::/48`.
- A short list of previously-used IPs (`34.162.x.x/32`) is explicitly marked phased out — don't carry those over from older blog posts.

Use the outbound range as a Traefik IP allowlist middleware on the `/mcp` route **in addition to** OAuth — defense in depth, not a replacement for authentication. Note one caveat found in community reports: Anthropic's GCP-hosted MCP path has occasionally been seen originating from internal GCP ranges instead of the documented public range, causing intermittent connectivity — if the allowlist causes unexplained connection failures, that's the first thing to check before assuming misconfiguration. **Confidence: MEDIUM-HIGH** on the IP ranges themselves (official first-party documentation page, fetched directly); LOW on the GCP-routing caveat (single community report, not corroborated elsewhere).

---

## 5. Scheduled proactive reviews

Two real mechanisms exist, and they are not mutually exclusive:

**(a) Claude-side scheduling (cloud Routines / Desktop scheduled tasks).** Anthropic shipped "Routines" (claude.ai/code/routines and `/schedule` in the CLI) in April 2026, in research preview across Pro/Max/Team/Enterprise plans — a saved configuration (prompt + connectors, i.e. the household's MCP server) that runs on **Anthropic-managed cloud infrastructure** on a schedule, with full access to the configured MCP server exactly as an interactive session would. Claude Desktop also supports a lighter local scheduled-task variant tied to a running Desktop app.

- **Pros:** zero app-side scheduling code, zero API cost accounting to build (it rides the subscription), the review literally *is* a Claude conversation and lands wherever Claude conversations land (claude.ai history, referenceable later), and it inherits the same MCP write tools (audit log, revert) already required.
- **Cons:** it is a **subscription-plan feature in research preview**, not a stable committed API — Anthropic can change or gate it; it depends on the household's Claude plan tier; "reliability" is whatever Anthropic's scheduler guarantees, which is not a documented SLA; the review only lands "in Claude" (claude.ai conversation history) unless the MCP write tools are used to also persist a copy into the app's own database (which the requirements already mandate — "stored in the app, visible in Grafana, and readable as a Claude conversation" — so this is required regardless of which scheduling mechanism is chosen).

**(b) The app itself calls the Anthropic Messages API on a schedule (BackgroundService), using the API's MCP connector (`mcp_servers` parameter) so Claude can call the household's own tools mid-conversation.**

- **Pros:** fully within the app's own control — a `BackgroundService` cron-style trigger (e.g. monthly) that calls `POST /v1/messages` with `mcp_servers` pointed at the household's own `/mcp` endpint (self-call, LAN-local, no public exposure needed for *this* path since it's the app calling itself/Anthropic, not Anthropic calling in), and the app fully owns retry/logging/reliability the same way it owns every other background job.
- **Cons:** metered API usage cost (pay-per-token, plus the MCP connector's own tool-call overhead) instead of riding the subscription; the app must hold and rotate an Anthropic API key as a new secret; **only tool calls are supported through the Messages API's MCP connector** — the full interactive feature set (resources, prompts) is not exercised, though for a scheduled "explain the past month" review, tool calls (read aggregates, read budgets/goals, write a review record) are exactly what's needed.

**Recommendation: (b), the app calling the Messages API on its own schedule.** It fits the project's existing architecture (a `BackgroundService` is already planned for sync — a second one for reviews is the same pattern), gives full control over reliability/retry/logging consistent with the rest of the app's operational posture, has a real (if metered) cost that's small and predictable for a monthly household review versus an undocumented preview feature that could change without notice, and doesn't require the review to depend on the household's specific Claude subscription tier remaining eligible for Routines. Keep the Claude-side Routine/Desktop-scheduled-task option available as a **user-triggered convenience** (a household member can always ask Claude Desktop/claude.ai to "review this month" on demand against the same MCP server) — that's complementary, not competing. **Confidence: MEDIUM** (the Messages API MCP connector's tool-call-only scope and pricing model are documented on Anthropic's own platform docs; Routines' preview status and plan-tier availability are corroborated by multiple third-party write-ups but not yet by a long-lived stable Anthropic doc page, since it's explicitly in research preview).

---

## 6. Observability

- **prometheus-net vs. OpenTelemetry .NET + Prometheus exporter:** OpenTelemetry's `OpenTelemetry.Exporter.Prometheus.AspNetCore` component is explicitly flagged by the OpenTelemetry project itself as **still evolving**, tracking an experimental Prometheus/OpenMetrics compatibility spec — official OpenTelemetry guidance for *production* metrics export currently favors the OTLP exporter over the Prometheus exporter. Given the project has **no other OpenTelemetry need** (no distributed tracing across services — it's a single deployable host) and Prometheus is already the fixed target (per constraints), pulling in the OTel SDK purely to re-derive a Prometheus scrape endpoint adds a layer of indirection and an experimental component for no benefit. **Recommendation: `prometheus-net` (`prometheus-net.AspNetCore`)** — mature, purpose-built, directly exposes `/metrics` with minimal setup, and is the simpler, more stable choice for this project's actual shape (one process, one metrics consumer). Revisit OpenTelemetry only if a future milestone adds distributed tracing needs across multiple services. **Confidence: MEDIUM** (the "OTel Prometheus exporter is still experimental" claim comes from the OpenTelemetry project's own blog/README, which is about as authoritative as it gets; the "no benefit here" conclusion is this research's own reasoning applied to the project's fixed architecture, not an external source).
- **Versions (as of 2026-09-26):** Grafana **13.2.2** (Grafana 13 launched at GrafanaCON 2026 in April); Prometheus **3.15.0** latest, with **3.13.3 marked LTS** — for a homelab single-instance deployment prioritising stability over bleeding-edge PromQL/service-discovery features, **track the 3.13.x LTS line** rather than the latest non-LTS minor. **Confidence: MEDIUM** (version numbers corroborated across the projects' own release pages and endoflife.date, a maintained cross-project EOL tracker).
- **Grafana Alerting vs. Prometheus Alertmanager:** Alertmanager is the more mature, config-as-code (single YAML) option specifically for *Prometheus-metric* alerts, and is the conventional choice at any real infrastructure scale. For this project specifically, however, the two alert conditions explicitly required ("sync failing", "consent expiring in N days") are **both already Prometheus metrics** (per the `/metrics` requirement), so either tool could serve them — but running a whole separate Alertmanager container/config for two alert rules, in a resource-constrained single LXC that already centralises on Grafana as "the one UI," adds an operational component (a second thing to provision-as-code, a second thing to keep alive) for no functional gain over Grafana's own **unified alerting**, which can evaluate Prometheus queries natively and already lives in the same provisioning-as-code story as the dashboards themselves. **Recommendation: Grafana unified alerting, provisioned from files, no separate Alertmanager.** Reassess only if the alert surface grows substantially (many more rules, need for Alertmanager-specific routing/inhibition logic) — not expected at household scale. **Confidence: MEDIUM.**

---

## 7. Grafana as code + localisation

- **Provisioning:** Grafana supports fully file-based provisioning for **datasources**, **dashboards**, and **alerting** (rules + contact points + notification policies), all under `/etc/grafana/provisioning/{datasources,dashboards,alerting}/` with YAML manifests — dashboard JSON files referenced from a `dashboards/` provider config, nested folder structure on disk mirrored into Grafana's folder tree. This is a mature, well-documented, first-party mechanism — exactly the "nothing clicked together by hand" requirement.
- **Dashboard generation tooling:** **Grafonnet (Jsonnet) is explicitly not officially supported by Grafana** — Grafana Labs' own current guidance directs users to the **Grafana Foundation SDK** instead: a strongly-typed, composable builder-pattern library available in **Go, TypeScript, Python, Java, and PHP** (no first-party C#/.NET binding as of this research). **Recommendation: generate dashboard JSON with the Foundation SDK in a small standalone Go or Python script/project inside the repo** (a one-off code-generation tool, not part of the runtime app — it doesn't need to be .NET, and forcing it into C# would mean hand-rolling what the SDK already provides in other languages), committing the generated JSON as the actual provisioned artifact so Grafana itself just loads static files. **Confidence: MEDIUM** (Foundation SDK's supported-languages list and Grafonnet's unsupported status are both stated directly on Grafana's own docs/blog).
- **English/Dutch dashboards:** Grafana's **application UI chrome** (menus, buttons, settings) does support Dutch (`nl-NL` is among its shipped locales, selectable per-user in profile preferences) — but that is Grafana's own interface, **not** a mechanism for translating user-authored dashboard content (panel titles, axis labels, text panels). There is no built-in "one dashboard, many languages" runtime switch. **Recommended approach:** keep a single source of truth per dashboard (the Foundation SDK builder script, parameterised with a translation dictionary for titles/labels/legends), and **generate two fully separate provisioned dashboards per view** (e.g. `spending-overview-en.json` / `spending-overview-nl.json`), placed in an `English/` and `Nederlands/` provisioning folder respectively, each with its own UID. This matches the must-answer hint exactly ("generated per-language dashboards from one template") and is the only approach that actually works given Grafana's lack of native dashboard-content i18n. **Confidence: MEDIUM.**
- **Install method (Ubuntu/Debian LXC):** both Grafana and Prometheus have official APT-style install paths appropriate for a Debian/Ubuntu LXC:
  - Grafana: official `apt.grafana.com` repository (GPG key → `sources.list.d` entry → `apt install grafana`) — this is Grafana Labs' own documented path for Debian/Ubuntu, not a third-party script.
  - Prometheus: no official APT repo maintained by the Prometheus project itself — the standard practice is downloading the official static binary tarball for the target architecture from the GitHub releases page and running it as a systemd unit (the same pattern already used for the .NET app in the reference deployment), rather than relying on distro-packaged Prometheus (which lags upstream and varies by distro). **Confidence: MEDIUM-HIGH** (Grafana's APT repo is corroborated across Grafana's own docs and several independent install guides; Prometheus's lack of an official APT repo and binary-tarball-as-systemd-unit convention is general Prometheus ecosystem knowledge, not contradicted by any source found).

---

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

**Test stack — align with the existing homelab reference project's layering exactly** (confirmed by reading its architecture doc and test `.csproj` files):
- **xUnit v3** (`xunit.v3` 3.2.2 + `xunit.runner.visualstudio` 3.1.5) as the test framework — not NUnit/MSTest, matching the household's established convention.
- **FluentAssertions** (8.10.0) for assertions.
- **NSubstitute** (5.3.0) for mocking in unit tests.
- **`Microsoft.EntityFrameworkCore.InMemory`** for fast unit tests of query/business logic that doesn't depend on SQL-Server-specific behaviour.
- **`Microsoft.AspNetCore.Mvc.Testing`** for integration tests hosting the app in-process.
- **Add `Testcontainers.MsSql`** on top of this existing stack (the reference project didn't need it since its integration tests didn't require SQL-Server-specific semantics; this project does, for the reasons above) — spin up a real SQL Server container per test run, apply migrations under the dedicated migration login, and verify the runtime login's restricted permissions actually behave as intended (e.g., assert a runtime-login connection *cannot* `DROP TABLE`).
- Follow the reference project's layering discipline: EF Core packages confined to the Repository/Data project, never referenced from the API/Service project directly.

**Migrations with a separate login:** apply the same pattern the project's own security review already calls for — one SQL login with DDL rights used **only** by the CI/deploy-time migration step (`dotnet ef database update` or a migration bundle executed under that login's connection string), and a second, narrower runtime login (DML only, plus SELECT on the `reporting` views for Grafana) used by the running app and by Grafana respectively. This is standard EF Core practice (`Database.Migrate()` is not the right call site here — prefer explicit migration bundles run by CI/CD against the migration login, keeping the app's own runtime connection string permanently unable to alter schema).

---

## 9. CI/CD supply-chain hardening (GitHub free tier, public repo)

All of the following are directly actionable on GitHub's free tier for a public repository (required reviewers on Environments are explicitly available on Free/Pro/Team for public repos, not gated to paid orgs):

1. **`actions/attest-build-provenance`** — add `id-token: write`, `contents: read`, `attestations: write` permissions to the release-build job, generate a signed provenance attestation (via Sigstore's **public-good instance**, since the repo is public) for the release artifact, uploaded to GitHub's attestations API tied to the repo.
2. **`gh attestation verify <artifact> -o <org-or-user>`** — run this on the self-hosted deploy runner **before** unpacking the downloaded release artifact, failing the deploy if verification fails. This directly closes the security-review finding "deploy script runs a downloaded artifact without integrity verification."
3. **SHA-pin all third-party GitHub Actions** to full 40-character commit SHAs (with a trailing `# vX.Y.Z` comment for human readability) rather than mutable tags — this is the only truly immutable reference; **Dependabot fully supports updating SHA-pinned actions**, proposing a PR that bumps both the SHA and the version comment, so pinning does not sacrifice automatic maintenance.
4. **GitHub Environments with required reviewers** gating the deploy job specifically (not the build job) — required reviewers need only read access and only one approval is needed to proceed; combine with **"require approval for all outside collaborators"** (not just first-time contributors, which is the GitHub default) on the Actions settings, since the project explicitly must prevent a fork PR from ever reaching the self-hosted runner. Note the important caveat surfaced in research: **workflows on self-hosted runners are not sandboxed by Environments** — the Environment/reviewer gate controls *whether the job runs at all*, not what it can do once it's running; the actual isolation still has to come from running the self-hosted runner as a separate, secrets-scoped OS user (already planned per the security review) rather than from GitHub's approval gate alone.
5. **Immutable releases:** GitHub's newer immutable-release support (tags that, once published as a release, cannot be silently force-moved) complements SHA-pinning — combine with a strict semver-tag-pattern validation in the workflow trigger (already noted in the project's constraints) so the release/deploy trigger only ever fires on a well-formed, immutable tag.

**Confidence: MEDIUM-HIGH** — all five points are corroborated by GitHub's own documentation pages plus multiple independent security-hardening write-ups from 2026, and none of them contradict each other; this is a mature, well-established area of GitHub Actions practice, not a fast-moving one.

---

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

```bash
# .NET packages (add to the appropriate project per the Service → Domain → Repository layering)
dotnet add package ModelContextProtocol.AspNetCore
dotnet add package Microsoft.EntityFrameworkCore.SqlServer
dotnet add package Microsoft.AspNetCore.DataProtection.EntityFrameworkCore
dotnet add package Microsoft.Extensions.Http.Resilience
dotnet add package MailKit
dotnet add package prometheus-net.AspNetCore

# Test projects
dotnet add package Testcontainers.MsSql
dotnet add package FluentAssertions
dotnet add package NSubstitute
dotnet add package Microsoft.EntityFrameworkCore.InMemory
dotnet add package Microsoft.AspNetCore.Mvc.Testing

# Grafana plugins (bundled/core: MSSQL datasource needs no install)
grafana-cli plugins install yesoreyeram-infinity-datasource
grafana-cli plugins install volkovlabs-form-panel   # optional, only if a write-back panel is wanted later
```

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

**If Enable Banking's ING NL coverage excludes savings accounts (spaarrekening) in practice:**
- Fall back to Salt Edge's Developer tier for the savings accounts specifically, keeping Enable Banking for the joint current account — the ingestion interface is already required to be provider-agnostic, so a two-provider split is architecturally cheap even if operationally slightly more complex.

**If the household's Claude plan doesn't have access to Routines / the feature is discontinued from preview:**
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

---
*Stack research for: Self-hosted household personal-finance backend (.NET 10, ING NL bank sync, MCP advisor, Grafana dashboards)*
*Researched: 2026-09-26*
