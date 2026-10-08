# Phase 3: Claude Reads the Ledger - Research

**Researched:** 2026-10-07
**Domain:** Public MCP endpoint (Streamable HTTP) behind OAuth 2.1 for claude.ai / Claude Code, embedded authorization server, server-side ledger aggregation, Amsterdam-time periods, Traefik exposure
**Confidence:** HIGH on connector/OAuth behaviour and library choice (read from Anthropic's current docs and the library sources this session); MEDIUM on end-to-end behaviour that can only be proven against the real claude.ai (grant-expiry UX, mobile re-auth, `resource` canonical form)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
### Sign-in and identity
- **D-01:** **Only the operator connects Claude in v1.** The partner uses ChatGPT, not Claude. Supporting ChatGPT's MCP connectors would mean three things:
  - adding OpenAI's egress ranges to the allowlist
  - making OpenAI a second AI provider that receives the household's transactions, which is not an accepted data flow
  - testing a second connector implementation

  That is deferred. The login store must not hard-code a single user: adding a second login later is a data change, not a redesign.
- **D-02:** **The OAuth sign-in step (authorize and login pages) is reachable from the home network and VPN only.** The other endpoints accept Anthropic's published ranges plus LAN and VPN: the token endpoint, client registration and metadata documents, and `/mcp` itself. As a result, connecting the claude.ai connector, or signing in again, needs the operator's browser at home or on the VPN. On a phone that means the VPN is on. This resolves the open STATE.md concern: connecting from home or VPN is acceptable. Research must confirm that the claude.ai connector flow works this way, with the browser handling authorize and Anthropic's servers handling the token exchange. That includes the mobile app's re-auth path.
- **D-03:** **Access lasts a long time, so re-sign-in is rare:**
  - access tokens are short-lived (minutes)
  - refresh tokens rotate and expire after about **90 days without use** (sliding)
  - reusing an already-rotated refresh token revokes the whole grant

  Research verifies that claude.ai refreshes tokens reliably and how it behaves when a grant expires or is revoked.
- **D-04:** **Sign-in is a password plus a second factor:** TOTP or a passkey, whichever the chosen authorization server supports natively. The password comes from the password manager.
- **Still open, and decided by phase research as the roadmap requires:** a separate Authentik service vs a lightweight embedded authorization server. Research weighs D-01..D-07 and the low-power, single-LXC resource budget. An embedded server must still meet D-03 and D-04: rotation with reuse detection, and a second factor.

### Client access and exposure
- **D-05:** **Claude Code uses the same OAuth flow, and so does Claude Desktop if it connects directly.** There is no API-key path into `/mcp`. Named API keys stay REST-only. This gives one way in, one token validation and one place to revoke. Research confirms whether Claude Desktop's custom connectors are brokered through Anthropic's cloud, in which case they behave exactly like claude.ai, or connect from the workstation.
- **D-06:** **A new, dedicated public hostname carries only `/mcp`, the OAuth endpoints and the discovery documents.** The discovery documents include the protected-resource metadata and the authorization-server metadata.
  - Its Traefik router uses exact path matches. The allowlist is Anthropic's ranges plus LAN and VPN; the sign-in path is narrower, LAN and VPN only (D-02).
  - Local DNS points the name at Traefik at home. LAN clients and claude.ai therefore use the same name, which gives **one issuer and one resource/audience URL**.
  - The public DNS record must be **DNS-only, not proxied through the DNS provider**. Otherwise Traefik sees the proxy's addresses and the IP allowlist cannot work.
  - The existing internal REST hostname and the Grafana hostname keep **no** public-facing router and never carry `/mcp`.
  - If the authorization server needs its own hostname (e.g. a separate Authentik), the same split applies: sign-in from LAN and VPN only, token and registration endpoints from Anthropic plus LAN and VPN.
  - The real hostname lives only in server-side config. The repo uses `example.com` placeholders.
- **D-07:** **A kill switch and visibility:**
  - A CLI command on the LXC, following the API-key command's pattern, revokes every grant and refresh token at once, and can also revoke a single grant.
  - `/metrics` gains MCP call counts per tool and rejected-token counts by reason (expired, wrong audience, invalid). Labels are opaque only, with no financial data and no client identifiers that reveal anything.
  - An operator alert fires on a burst of rejected tokens, through the existing contact point, with no financial detail. Requests blocked at Traefik never reach the app. Watching those too is optional (see Claude's Discretion).

### What a total counts
- **D-08:** **Before categories exist, "groceries in August" is answered by a merchant filter.** Claude passes the counterparty and description filters it believes match. The total comes back with a **per-counterparty breakdown** (count and amounts), so the operator can see which merchants were included and spot misses. Tool descriptions make Claude say plainly that it grouped by merchant, not by category.
- **D-09:** **Totals count booked transactions only.** Pending items in the same period are reported next to the total (count and amount), never mixed in. Dropped transactions never appear. This settles the per-figure question that Phase 2 D-13 left open, for Claude's totals.
- **D-10:** **Transfers between the household's own synced accounts are excluded from spending and income** and reported separately as internal transfers (count and amount). The rule is exact: the counterparty IBAN, normalised, equals the IBAN of a synced ledger account. Other own-account flows still count until Phase 4's general transfer detection (CAT-02). Examples: transfers to the unsynced savings account, and to the partners' personal accounts. Phase 4 builds on this rule rather than redefining it. IBANs never leave the server for this.
- **D-11:** **The booking date decides the period.**
  - Bank dates are calendar dates and are used as they are, never converted through UTC.
  - Relative periods ("today", "this month", "last 30 days", "last year") resolve from the **current date in Europe/Amsterdam**, using the existing configured zone and `TimeProvider`.
  - A pending row with no booking date sits on the Amsterdam-local day it was first seen, the same rule `ILedgerStore` already uses.
  - Results echo the resolved absolute dates.
  - Tests cover a transaction on the last evening of a month and one on a daylight-saving changeover weekend (the last Sunday of March and of October). They also cover a "now" just after midnight Amsterdam time, while UTC is still on the previous day.
- **D-12:** **Every total returns money out, money in and net, separately.** Claude says which one it used, e.g. "€210 spent, €40 refunded, €170 net".
- **D-13:** **By default, totals cover all synced accounts combined**, with a per-account split on request. Accounts appear by display name and opaque key.

### Tool output and privacy
- **D-14:** **Counterparty IBANs reach Claude masked to the last four characters** (e.g. `NL••••1234`). Full IBANs never leave the server. The household's own accounts appear only by display name and opaque key, never by IBAN. Claude targets a specific payee by name, or by an opaque counterparty reference the tools return, never by a full IBAN.
- **D-15:** **No untrusted-data marking in this phase. This is the user's decision after advice.** The operator was told that a light version would cost little now:
  - bank-sourced text in labelled fields
  - stripping invisible and bidirectional control characters, and capping field lengths
  - one line in the server instructions

  They chose to wait. The planner must **not** add untrusted labels, text sanitising or injection instructions in this phase. The full ADV-07 set arrives with the first write tools in Phase 4: marking, an injection test transaction, bulk-write confirmation and write rate limits. Nothing in this phase may make that later envelope harder to add.

### Folded Todos
- **Remove temporary Claude SSH access before `/mcp` goes public** (`.planning/todos/pending/2026-09-28-remove-temporary-claude-ssh-access-before-real-bank-data.md`, major, `resolves_phase: 3`). The current mitigation is a passphrase-protected key loaded through `ssh-agent`, and this is the removal point Phase 2 D-04 fixed.
  - **Order:** first delete the logins on the ledger host and on the reverse proxy and delete the key on the workstation. Then `ledger-selfcheck` must pass. Only after that is the public `/mcp` router enabled.
  - Add the optional selfcheck that fails when any login other than the expected service accounts has sudo.
  - The exact accounts and commands are in the operator's local, uncommitted notes, never in the repo.
  - Planning consequence: Claude may use SSH for internal-only host setup before the removal step. Every host step after it is run by the operator: enabling the public router, the outside-in exposure check, and any later fixes on the host.

### Claude's Discretion
### Claude's Discretion
- **The tool set.** The user said "up to you", and the default is four core tools:
  1. Ledger overview: synced accounts by display name, today's balance and whether it reconciles, last successful sync, how far history reaches, pending count. Claude calls this first, for data freshness.
  2. Search transactions: period, account, counterparty or description text, amount range and direction filters. Paginated, with a hard cap and a truncation notice.
  3. Money totals: out, in and net, grouped by none, counterparty, month, week, day or account. Applies D-08..D-13.
  4. Find counterparties: look up merchant spellings, with count and total, so Claude can build filters.

  Names, parameters and grouping options are the planner's choice. This keeps the milestone inside ADV-01's roughly 8–15 tools. A period-comparison tool stays with Phase 4 (PLAN-09).
- **Paging.** About 50 rows per page by default, a hard maximum per call, and a "showing N of M, truncated" notice with how to narrow or fetch the next page (Pitfall 20).
- **Provenance on every result:** resolved absolute dates, the filters applied, the number of transactions included, the booked/pending handling and pending counts, the internal transfers excluded, and "data as of" the last successful sync per account.
- **How amounts are represented** (exact decimals with currency, never floats), and the sign convention inside out/in/net.
- **The MCP server instructions text.** Minimum: for any "how much", use the totals tool and never add up search pages (Pitfall 17). State the period and count. Say when results are grouped by merchant.
- **Stateless or stateful MCP HTTP mode** (the SDK 2.x default is stateless), and the SDK patch version.
- The revoke CLI's shape, metric names, alert thresholds, and whether requests blocked at Traefik get any visibility (Traefik runs in another container and isn't scraped today).
- The hostname label and whether the authorization server shares it, within D-06.
- How "Anthropic's published IP ranges" are kept current in the Traefik template and docs. Use the outbound range from Anthropic's official IP-address page, checked during research. Old phased-out addresses must not be carried over.

### Deferred Ideas (OUT OF SCOPE)
- **Partner access through ChatGPT's MCP connectors.** It would need OpenAI's egress ranges, accepting OpenAI as a second AI provider that receives transaction data, and testing ChatGPT's connector OAuth. The login model leaves room for a second user. Revisit after v1.
- **A period-comparison tool** (month over month, year over year). It belongs to Phase 4 with PLAN-09.
- **Untrusted-data marking for bank text, including the light version.** Phase 4 with ADV-07, per D-15.
- **General own-account transfer detection** (unsynced savings, personal accounts). Phase 4 with CAT-02, building on D-10.

### Reviewed Todos (not folded)
- **Confirm the first consent renewal keeps all history** (`.planning/todos/pending/2026-10-07-confirm-first-consent-renewal-keeps-history.md`). It can only run at the first renewal, around March 2027. It matched on keywords only and is not MCP work.
- **Confirm the first real pending payments book as single rows** (`.planning/todos/pending/2026-10-07-confirm-pending-payments-book-as-single-rows.md`). This verifies ingestion, independent of this phase.
- **Confirm the first real household alert email arrives without financial detail** (`.planning/todos/pending/2026-10-07-confirm-first-real-alert-email.md`). This verifies monitoring, independent of this phase.

</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ADV-01 | Small set of intent-shaped MCP tools (roughly 8-15 for the milestone), not one per table | Four read-only tools (`ledger_overview`, `search_transactions`, `money_totals`, `find_counterparties`); SDK 2.2.0 attribute model, stateless HTTP mode; see "Tool set" |
| ADV-02 | Every "how much" answer is server-side aggregation and states its provenance | `money_totals` computes in PostgreSQL/domain on `decimal`; provenance envelope on every result; see "Totals semantics" and Pitfalls 9-11 |
| ADV-03 | Search is paginated under a server-side cap and reports truncation | Keyset cursor, default 50 / hard max 100 rows, `matching_total` + `truncated` + `next_cursor`; see "Pagination" |
| ADV-10 | Desktop and Claude Code on home network or VPN; claude.ai web and mobile via public `/mcp` with OAuth 2.1 | Verified: every custom connector (web, Desktop, mobile) is brokered from Anthropic's cloud; only Claude Code and the browser sign-in step touch the LAN; see "Connector behaviour" |
| SEC-04 | Only `/mcp` and needed OAuth endpoints internet-facing, restricted to Anthropic ranges; audience-validated tokens, no passthrough | Dedicated hostname, exact-path Traefik routers, `160.79.104.0/21` outbound range (current page), OpenIddict audience validation + token-entry validation, separate `/mcp` auth policy; see "Network exposure" and "Security Domain" |
| OPS-06 | All period bucketing uses Europe/Amsterdam | `PeriodResolver` in the domain on `TimeProvider` + configured zone; booking dates stay `DateOnly`; tests for month-end, DST weekends, just-after-midnight; see "Amsterdam periods" |
</phase_requirements>

## Project Constraints (from CLAUDE.md)

Source: `/mnt/Data/repos/ing-dashboard/.claude/CLAUDE.md` (read this session). The planner must verify every task against these.

- No planning references outside `.planning/`: no requirement keys, decision IDs, phase/plan numbers or planning document names in code, `///` docs, string literals, log/exception messages, test names, Grafana titles/alert texts, MCP tool names/descriptions/server instructions, scripts, config, `docs/`. `build/lint/checks/10-repo-rules.sh` enforces this by regex (requirement keys such as `ADV-02`, `D-07`, `Phase 3`, `*.md` planning file names) [VERIFIED: build/lint/checks/10-repo-rules.sh:8-14].
- Comments: `///` XML doc only, never `//` (lint enforces; generated `Migrations/` are exempt) [VERIFIED: build/lint/checks/10-repo-rules.sh:21-26].
- Public repository: no personal data. No real hostnames, IPs, merchant names, IBANs. Tool descriptions and server instructions must use generic examples (for example "a supermarket"), never household merchants. All fixtures synthetic. Hostnames in templates/docs use `example.com`; IPs use RFC 5737 ranges.
- Never commit to `main`; work on the milestone branch.
- Local DB tests use the user's own PostgreSQL container (`postgres-dev`, `postgres:18`, running) with connection strings in `dotnet user-secrets`; no Testcontainers [VERIFIED: `docker ps` this session].
- The app never calls an LLM API.
- Security first: bank access read-only; only `/mcp` internet-facing; least-privilege DB roles (runtime role has no schema rights; new tables get runtime DML via default privileges); secrets never in logs, exceptions, metric labels or MCP tool output.
- Stack overrides win: PostgreSQL (Npgsql) replaces SQL Server; the CLAUDE.md stack section's SQL Server, Authentik-recommended and "OpenIddict has no DCR" statements predate this phase and are superseded by this research where they conflict.
- Project skill present: `senior-frontend` (server-rendered pages, strict CSP, no SPA, no bundler). The sign-in and consent pages are the only UI this phase adds; follow its constraints (no third-party JS, no inline script) when planning them.

## Summary

Decision resolved: **embed OpenIddict 7.7.1 in the existing ledger host as the authorization server, with a single pre-registered public client per Claude surface, ASP.NET Core Identity (core only, no default UI) holding the login, and TOTP as the second factor. Do not deploy Authentik.** Evidence that drives it: Anthropic's current connector documentation accepts a user-entered, pre-registered OAuth client ("Use your own OAuth client", secret optional) and Claude Code accepts `--client-id` / `--callback-port`, so neither Dynamic Client Registration nor Client ID Metadata Documents is needed. That removes the only feature Authentik had over an embedded server. Authentik's documented minimum is 2 CPU cores and 2 GB RAM for itself, on a container-only install, while the whole ledger LXC is 2 cores / 2048 MiB and already runs the app, PostgreSQL, Grafana and Prometheus. OpenIddict adds no process, no container, no database, and shares the existing PostgreSQL role model, Data Protection key ring, backups and provisioning. It implements authorization code + PKCE, rotating refresh tokens with reuse detection, RFC 8707 `resource` parameter handling, and serves both RFC 8414 discovery paths by default (all read from the 7.7.1 sources this session).

Connector behaviour (answers to the roadmap research flags), all from Anthropic's own documentation: (1) Every custom connector - claude.ai web, Claude Desktop, Cowork and the mobile apps - is connected **from Anthropic's cloud, not from the user's device**, using one shared OAuth client with callback `https://claude.ai/api/mcp/auth_callback`; so Claude Desktop is not a LAN client at all, it behaves exactly like claude.ai (D-05 resolved). Only Claude Code runs its own OAuth flow on the user's machine with a loopback redirect. (2) During sign-in, the **browser** goes to the authorize endpoint (so it must be on LAN/VPN per D-02), and **Anthropic's servers** exchange the code at the token endpoint and later refresh it (so token endpoint, discovery and `/mcp` must accept Anthropic's range). The D-02 split is therefore exactly right and works. (3) Anthropic's outbound range for MCP calls is IPv4 `160.79.104.0/21` only; the page also lists five phased-out `34.162.x.x/32` addresses that must not appear. (4) The connector hostname must resolve publicly to a globally routable IPv4 address (an `A` record); Anthropic rejects private, CGNAT, loopback or mixed answers and cannot use IPv6-only hosts.

**Primary recommendation:** Build a tracer slice first: OpenIddict + login (password, TOTP) + `/mcp` (SDK 2.2.0, stateless, own bearer-only policy) + one tool (`ledger_overview`) on an internal-only hostname, proven by an automated in-process OAuth+MCP test and by Claude Code on the LAN; then add the three aggregation/search tools on the domain layer, then observability and the kill switch, then (operator-run, after SSH removal) enable the public router with Anthropic's range and connect claude.ai.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Public TLS termination, hostname + exact-path routing, IP allowlist (Anthropic + LAN + VPN), sign-in LAN/VPN-only | Reverse proxy (Traefik container) | App (defence-in-depth host/path/network checks) | First filter; a template mistake is the most likely failure, so the app repeats the host/path rule |
| OAuth authorization server (authorize, token, discovery, refresh rotation, revocation) | API / Backend (same ASP.NET Core host, OpenIddict) | Database (token/authorization/application tables) | One process, one DB, one key ring; no new service on the low-power host |
| Login (password + TOTP), lockout, consent page | API / Backend (server-rendered pages) | Database (Identity tables) | Credentials never leave the server; no SPA |
| Bearer-token validation, audience binding, `/mcp` auth policy | API / Backend (OpenIddict validation + MCP auth handler) | Database (token-entry/authorization-entry validation for instant revoke) | Audience check on every request; separate policy from the API-key scheme |
| MCP protocol, tool dispatch, server instructions | API / Backend (`ModelContextProtocol.AspNetCore`) | - | Thin adapters over the application layer |
| Period resolution (Amsterdam), totals, search, counterparty lookup, own-account exclusion, IBAN masking | Domain (rules) + Repository (EF/SQL aggregation) | - | "Never let Claude do arithmetic"; one layer under REST and MCP |
| Revoke-all / revoke-one, login enrolment | Host CLI (dispatched in `Program.cs`, wrapped by a `deploy/bin` script) | Database | Mirrors the API-key command; works when the web app is down |
| MCP/token metrics, rejected-token alert | App (`prometheus-net`) -> Prometheus -> Grafana alerting | - | Existing loopback scrape and email contact point |
| Dashboards, REST API, Grafana | Not touched; must remain unreachable on the public hostname | - | Only `/mcp` and OAuth paths are public |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `ModelContextProtocol.AspNetCore` | 2.2.0 (published 2026-08-13) | MCP server, Streamable HTTP, protected-resource metadata handler | Official SDK; targets net10.0; depends on `ModelContextProtocol` 2.2.0 and Microsoft.Extensions.* 10.0.10 [VERIFIED: NuGet registration + nuspec] |
| `OpenIddict.AspNetCore` | 7.7.1 (published 2026-09-17) | Embedded OAuth 2.1/OIDC authorization server + local token validation | Apache-2.0, 22.5M downloads, owner `openiddict`, net10.0 target; 7.7.0/7.7.1 fix a critical client-assertion audience-validation bug, so do not go below 7.7.1 [VERIFIED: NuGet; CITED: github.com/openiddict/openiddict-core/releases] |
| `OpenIddict.EntityFrameworkCore` | 7.7.1 | EF Core stores for applications, authorizations, scopes, tokens | Depends on `Microsoft.EntityFrameworkCore.Relational` 10.0.11 for net10.0; repo already uses 10.0.12 [VERIFIED: nuspec] |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | 10.0.12 (matches repo EF packages) | Login store, password hashing, lockout, TOTP token provider (`AddIdentityCore`, no UI package) | First-party; do not hand-roll credential storage [VERIFIED: NuGet, Microsoft-owned, 293M downloads] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `prometheus-net.AspNetCore` | 8.2.1 (already referenced) | New counters for tool calls, token rejections, grants | Alongside `SyncMetrics` / `LedgerMetrics` |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.10.0 (already in both test projects) | `FakeTimeProvider` for period tests | Midnight / month-end / DST tests |
| `xunit.v3` 4.0.1, FluentAssertions 8.11.0, `Microsoft.AspNetCore.Mvc.Testing` 10.0.12 | already referenced | Unit and in-process integration tests; the `ModelContextProtocol` client types arrive transitively and drive the end-to-end test | Existing conventions |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Embedded OpenIddict | Authentik 2026.8 (separate service) | Has DCR (shipped in 2026.8) and a polished admin UI, but needs >= 2 CPU / 2 GB by itself on a container-only install, adds a worker + its own PostgreSQL database/role/socket story, documents refresh-token rotation as optional with no reuse detection, and no RFC 8707 `resource` handling in the OAuth provider docs (audience becomes the client id). Loses on every axis the phase cares about. |
| Pre-registered clients | DCR / CIMD | OpenIddict 7.7.1 contains no `registration_endpoint` or `client_id_metadata_document` support (grep of the 7.7.1 source) [VERIFIED]. Not needed: Anthropic accepts own client ids. Not building them also removes an unauthenticated registration endpoint and an SSRF-prone metadata fetcher from a public surface. |
| Identity core + TOTP | Passkeys (ASP.NET Core 10 Identity has them) | Passkeys need schema version 3 tables and client-side WebAuthn JS; TOTP satisfies the decision and needs no JS. Passkeys are a later, additive option. |
| DataProtection token format + reference tokens | Persistent signing/encryption certificates | Certificates add a new secret to provision, custody, rotate and back up. See Pitfall 14 for the required verification before relying on the DataProtection route. |

**Installation:**
```bash
dotnet add Ledger.Service/Ledger.Service.csproj package ModelContextProtocol.AspNetCore --version 2.2.0
dotnet add Ledger.Service/Ledger.Service.csproj package OpenIddict.AspNetCore --version 7.7.1
dotnet add Ledger.Repository/Ledger.Repository.csproj package OpenIddict.EntityFrameworkCore --version 7.7.1
dotnet add Ledger.Repository/Ledger.Repository.csproj package Microsoft.AspNetCore.Identity.EntityFrameworkCore --version 10.0.12
dotnet restore --force-evaluate   # regenerate packages.lock.json in every project; CI restores in locked mode
```
Keep EF packages confined to `Ledger.Repository` (existing layering); `Ledger.Service` references OpenIddict's ASP.NET Core packages only.

**Version verification:** `gsd-tools package-legitimacy check` supports only npm/pypi/crates, so NuGet packages were verified directly against the NuGet registration and search APIs this session (versions, dates, owners, download counts, nuspec dependencies above).

## Package Legitimacy Audit

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| ModelContextProtocol.AspNetCore | NuGet | 18 months (first preview 2025-03-31) | 15.96M total (verified owner `ModelContextProtocol`) | github.com/modelcontextprotocol/csharp-sdk | OK (manual) | Approved; already in CLAUDE.md research |
| OpenIddict.AspNetCore | NuGet | 6 yrs (3.0 betas 2020) | 22.5M (verified owner `openiddict`) | github.com/openiddict/openiddict-core | OK (manual) | Approved |
| OpenIddict.EntityFrameworkCore | NuGet | 9 yrs (2017) | 22.7M (verified owner `openiddict`) | github.com/openiddict/openiddict-core | OK (manual) | Approved |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | NuGet | Microsoft first-party | 293.7M (owners `aspnet`, `Microsoft`) | github.com/dotnet/aspnetcore | OK (manual) | Approved |

**Packages removed due to [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none
*The seam does not cover NuGet, so verdicts are manual. All four are tagged verified-publisher on NuGet and are named by their official documentation (the SDK docs and OpenIddict site). No install scripts exist in NuGet packages of this kind.*

## Architecture Patterns

### Connector behaviour: what talks to what (answers the roadmap research flags)

| Step | Actor | Source network | Endpoint | Allowed by |
|------|-------|----------------|----------|-----------|
| 1. Operator adds connector in claude.ai (URL `https://<mcp-host>/mcp`, own OAuth client id in Advanced settings) | Anthropic cloud | `160.79.104.0/21` | `POST /mcp` without token -> `401` + `WWW-Authenticate: Bearer resource_metadata="..."` | public router |
| 2. Discovery | Anthropic cloud | `160.79.104.0/21` | `GET /.well-known/oauth-protected-resource/mcp`, then `GET /.well-known/oauth-authorization-server` (falls back to `openid-configuration`) | public router |
| 3. Sign-in and consent | **Operator's browser** | LAN / VPN | `GET /connect/authorize` -> login (password, TOTP) -> consent -> redirect to `https://claude.ai/api/mcp/auth_callback` | LAN/VPN-only router (D-02) |
| 4. Code exchange | Anthropic cloud | `160.79.104.0/21` | `POST /connect/token` (`application/x-www-form-urlencoded`) | public router |
| 5. Tool calls | Anthropic cloud | `160.79.104.0/21` | `POST /mcp` with bearer | public router |
| 6. Refresh | Anthropic cloud | `160.79.104.0/21` | `POST /connect/token` (30 s timeout; reactive on 401 and proactively up to 5 min before expiry) | public router |
| Claude Code | Operator's workstation | LAN / VPN | all of the above, loopback redirect `http://localhost:PORT/callback` | LAN/VPN (also matches the public router) |

Facts and sources:
- "When you add a custom connector, Claude connects to your remote MCP server from Anthropic's cloud infrastructure, rather than from your local device. This is true across every Claude client, including claude.ai, Claude Desktop, Cowork, and the mobile apps." [CITED: support.claude.com/en/articles/11175166, fetched this session]. Consequence: Desktop needs the public endpoint and works from anywhere; it does not use the LAN. Success criterion 1's "Claude Desktop ... from the home network or VPN" is satisfied by the cloud path, and the Desktop acceptance test is "connector added in Desktop works", not "reaches over LAN".
- The hosted apps (web, Desktop, mobile, Cowork) share one OAuth client and one callback `https://claude.ai/api/mcp/auth_callback`; Anthropic notes it "may change to https://claude.com/api/mcp/auth_callback" - register both exact URIs [CITED: claude.com/docs/connectors/building/authentication; search result for the future URL].
- Connectors added on claude.ai web sync to the mobile apps (same account-level cloud connection); adding connectors on mobile is beta [CITED: search results on support.claude.com; MEDIUM]. Practical rule: **do the first connect and any re-authorisation from a desktop browser on the home network**; mobile then simply uses the stored connection. If re-auth is ever forced on the phone, the phone's browser must be on VPN. Whether the mobile app can start re-auth by itself is unverified (see Open Questions).
- Dialog offers three client identities; choose **"Use your own OAuth client"** and enter the client id, leaving the secret blank for a public client [CITED: claude.com/docs/connectors/custom/add-unlisted]. "You can't change authentication settings after you add a connector"; changing means remove + re-add.
- Claude requires: `401` (not `200`) with `resource_metadata` to start sign-in; uses only the **first** entry of `authorization_servers`; PRM `resource` must equal the connector URL exactly; S256 PKCE on every request; `application/x-www-form-urlencoded` token requests; RFC 6749 error codes (`invalid_grant` for a dead refresh token); rotate refresh tokens for public clients and return the new one in the same response; 10 s for discovery/registration/token and 30 s for refresh; appends `offline_access` when the AS metadata lists it in `scopes_supported`; sends the RFC 8707 `resource` parameter set to the canonical MCP URL (lowercase scheme/host, no trailing slash, no fragment, no default port); does not follow redirects with the `Authorization` header; resolves the hostname first and refuses private/CGNAT/loopback/mixed or IPv6-only answers [CITED: claude.com/docs/connectors/building/authentication and /troubleshooting, fetched this session].
- Claude Code: `claude mcp add --transport http --client-id ID --client-secret --callback-port PORT NAME URL`; redirect `http://localhost:PORT/callback` (v2.1.229 sent `127.0.0.1`, restored to `localhost` in v2.1.231 - register both); refreshes on 401; only sends credentials to an HTTPS token endpoint; fails sign-in on an unexpected issuer; discovery via PRM then RFC 8414 `oauth-authorization-server` (so serve that path) [CITED: code.claude.com/docs/en/mcp]. Claude Code also has its own published CIMD (`https://claude.ai/oauth/claude-code-client-metadata`), unused here.
- Anthropic egress: outbound `160.79.104.0/21` IPv4 only; phased-out `34.162.46.92/32`, `34.162.102.82/32`, `34.162.136.91/32`, `34.162.142.92/32`, `34.162.183.95/32` "should be removed". The current page no longer lists an IPv6 outbound range (CLAUDE.md's older `2607:6bc0::/48` is the *inbound* range). "These addresses will not change without notice." [VERIFIED: platform.claude.com/docs/en/api/ip-addresses fetched this session]. The IP allowlist is defence in depth only: any claude.ai user can add *our* URL as a connector and probe from the same range, so OAuth must stand on its own.
- MCP tunnels (outbound-only tunnel) exist but are Enterprise-plan research preview via Cloudflare; not applicable, noted only to close the question [CITED: claude.com/docs/connectors/mcp-tunnels/overview].

### System architecture diagram

```
Anthropic cloud (160.79.104.0/21)                Operator browser (LAN/VPN)         Claude Code (LAN/VPN)
  | POST /mcp, GET /.well-known/*, POST /connect/token     | GET /connect/authorize, /account/*        |
  v                                                        v                                           v
+--------------------- Traefik container (dedicated public hostname, TLS) ---------------------------+
| router "public":  Host && Path(/mcp | /.well-known/... | /connect/token)   allowlist: Anthropic+LAN+VPN |
| router "sign-in": Host && Path(/connect/authorize) | PathPrefix(/account/)   allowlist: LAN+VPN only    |
| anything else on this hostname -> 404 (no router).  REST and Grafana hostnames: no public router         |
+---------------------------------------------+---------------------------------------------------------+
                                              | HTTP, only from the Traefik address (nftables)
                                              v
+----------------------------- Ledger.Service (one ASP.NET Core process, :5080) -----------------------+
| ForwardedHeaders (known proxy) -> host guard (MCP host serves only MCP/OAuth paths, else 404)          |
|  /.well-known/oauth-protected-resource[/mcp]  <- McpAuthenticationHandler (config-fixed resource)       |
|  /.well-known/oauth-authorization-server, /openid-configuration, /connect/token <- OpenIddict           |
|  /connect/authorize (passthrough) -> cookie sign-in -> /account/login -> /account/totp -> /account/consent |
|  /mcp  [policy: scheme McpAuth, authenticate forwarded to OpenIddict validation, audience = MCP URL]    |
|     -> tools (thin) -> application services -> Ledger.Domain rules -> Ledger.Repository (EF/SQL)         |
|  /api/*  [fallback policy: ApiKey scheme only; never accepts bearer]                                    |
|  loopback ops :5081 /metrics /health -> Prometheus -> Grafana alerting (email, no financial detail)     |
+---------------------------------------------+---------------------------------------------------------+
                                              | Unix socket, peer auth, runtime role
                                              v
                                   PostgreSQL: ledger tables + OpenIddict tables + Identity tables
```

### Recommended project structure (extends existing layering)
```
Ledger.Domain/
├── Ledger/                 # query-side rules: PeriodResolver, LedgerQuery models, TotalsResult, CounterpartyRef, IbanMask, ILedgerQueryStore
Ledger.Repository/
├── Entities/               # LedgerUserEntity (Identity user), plus OpenIddict entities via UseOpenIddict()
├── Stores/                 # LedgerQueryStore (EF/SQL aggregation + keyset search)
├── Migrations/             # one migration: OpenIddict tables + Identity tables (runtime DML via default privileges)
Ledger.Service/
├── OAuth/                  # OpenIddict wiring, client seeding hosted service, endpoints (authorize, account/login, totp, consent), grant revocation service
├── Mcp/                    # tool classes (thin), server instructions, MCP auth wiring, metrics, host/path guard
├── Cli/                    # LoginCommand, GrantsCommand (dispatched from Program.cs like ApiKeyCommand)
deploy/bin/                 # ledger-login, ledger-grants (copy of the ledger-apikey wrapper pattern)
deploy/traefik/             # ledger.yml.example gains the MCP routers (placeholders only)
```

### Pattern 1: Two policies, two schemes, no cross-acceptance
**What:** Keep `ApiKey` as the default scheme and fallback policy for REST (unchanged). Add an explicit `/mcp` policy whose only scheme is the MCP scheme, with `ForwardAuthenticate` set to the OpenIddict validation scheme. REST never sees bearer tokens; `/mcp` never sees API keys.
**Why this wiring:** the SDK's `McpAuthenticationHandler` issues the `Bearer resource_metadata="..."` challenge and serves the PRM; its constructor defaults `ForwardAuthenticate = "Bearer"` (the JwtBearer scheme name), which does not exist here, so it must be overridden. A global `DefaultChallengeScheme` change would break REST challenges, so use a named policy on `MapMcp()` only.
**Verified API surface (verbatim):**
- `McpAuthenticationDefaults.AuthenticationScheme = "McpAuth"` [VERIFIED: csharp-sdk v2.2.0 src/ModelContextProtocol.AspNetCore/Authentication/McpAuthenticationDefaults.cs:11]
- `ForwardAuthenticate = "Bearer";` in the `McpAuthenticationOptions` constructor; properties `ResourceMetadataUri` (Uri?) and `ResourceMetadata` (ProtectedResourceMetadata?) with `Resource`, `AuthorizationServers`, `ScopesSupported`, `BearerMethodsSupported` [VERIFIED: McpAuthenticationOptions.cs:11-17, ProtectedResourceMetadata.cs]
- `public const string AuthenticationScheme = "OpenIddict.Validation.AspNetCore";` [VERIFIED: openiddict-core 7.7.1 src/OpenIddict.Validation.AspNetCore/OpenIddictValidationAspNetCoreDefaults.cs:17]
- `public OpenIddictValidationBuilder AddAudiences(params string[] audiences)`, `EnableAuthorizationEntryValidation()`, `EnableTokenEntryValidation()` [VERIFIED: OpenIddictValidationBuilder.cs:589,608,618]
- `UseLocalServer(this OpenIddictValidationBuilder builder)` [VERIFIED: OpenIddictValidationServerIntegrationExtensions.cs:26]
**Set the resource explicitly from configuration, never derive it from the request** (the handler's default derives scheme/host from `Request`, which is the Pitfall-9 failure mode behind a proxy).

### Pattern 2: Authorization server configuration (illustrative skeleton)
Names marked verified were read in the 7.7.1 source this session; values (URLs, lifetimes) come from configuration and the decisions, not hard-coded.

```csharp
services.AddOpenIddict()
    .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<LedgerDbContext>())
    .AddServer(server =>
    {
        server.SetIssuer(publicBaseUri)
              .SetAuthorizationEndpointUris("/connect/authorize")
              .SetTokenEndpointUris("/connect/token")
              .AllowAuthorizationCodeFlow()
              .AllowRefreshTokenFlow()
              .RequireProofKeyForCodeExchange()
              .RegisterScopes("ledger.read", Scopes.OfflineAccess)
              .SetAccessTokenLifetime(TimeSpan.FromMinutes(15))
              .SetRefreshTokenLifetime(TimeSpan.FromDays(90))
              .UseReferenceAccessTokens()
              .UseReferenceRefreshTokens()
              .UseDataProtection()
              .AddEphemeralSigningKey()
              .AddEphemeralEncryptionKey();

        server.UseAspNetCore()
              .EnableAuthorizationEndpointPassthrough()
              .EnableTokenEndpointPassthrough();
    })
    .AddValidation(validation =>
    {
        validation.UseLocalServer();
        validation.UseAspNetCore();
        validation.AddAudiences(mcpResourceUrl);
        validation.EnableTokenEntryValidation();
        validation.EnableAuthorizationEntryValidation();
    });
```
Verified in 7.7.1 source: `SetIssuer(Uri)`, `SetAuthorizationEndpointUris`, `SetTokenEndpointUris`, `AllowAuthorizationCodeFlow()`, `AllowRefreshTokenFlow()`, `RequireProofKeyForCodeExchange()`, `RegisterScopes(params string[])`, `SetAccessTokenLifetime(TimeSpan?)`, `SetRefreshTokenLifetime(TimeSpan?)`, `SetRefreshTokenReuseLeeway(TimeSpan?)`, `UseReferenceAccessTokens()`, `UseReferenceRefreshTokens()`, `AddEphemeralSigningKey()`, `AddEphemeralEncryptionKey()`, `UseDataProtection()`, `EnableAuthorizationEndpointPassthrough()`, `EnableTokenEndpointPassthrough()`, `Scopes.OfflineAccess = "offline_access"` [VERIFIED: OpenIddictServerBuilder.cs lines 901, 1012, 1742, 1724, 1763, 1817, 1826, 2251, 2262, 666, 289; OpenIddictServerAspNetCoreBuilder.cs:61,105; OpenIddictConstants.cs:545]. `UseEntityFrameworkCore().UseDbContext<T>()` and `UseOpenIddict()` on the DbContext options are standard OpenIddict EF setup [ASSUMED: not opened this session; confirm against OpenIddict docs when implementing]. The combination "DataProtection format + reference tokens + ephemeral keys" is [ASSUMED] and must pass the survive-a-restart test in Pitfall 14 before it is kept; the fallback is persistent certificates generated by provisioning exactly like the existing Data Protection certificate.

Token behaviour read from source (so the planner can rely on it):
- Refresh rotation is on by default; a refresh token already marked redeemed is reusable only within `RefreshTokenReuseLeeway` (default 30 s); outside it, **every token of that authorization is revoked** ("Note: the authorization itself is not revoked to allow the legitimate client to start a new flow") [VERIFIED: OpenIddictServerHandlers.Protection.cs:1233-1250, OpenIddictServerOptions.cs:302]. This meets the "reuse revokes the grant" intent in effect (all tokens dead, stolen token useless); the attacker cannot mint new ones, the operator simply signs in again. Verbatim quote of the revoke call: `count = await _tokenManager.RevokeByAuthorizationIdAsync(context.AuthorizationId);`.
- Sliding expiry is the default: each refresh issues a new refresh token with a fresh `RefreshTokenLifetime`, so `SetRefreshTokenLifetime(90 days)` means "90 days without use" (D-03). `DisableSlidingRefreshTokenExpiration` is false by default [VERIFIED: OpenIddictServerHandlers.cs:4477-4515, OpenIddictServerOptions.cs]. Defaults to replace: refresh 14 days, access 1 hour, authorization code 5 minutes.
- By default the validation side "doesn't check the status of a token entry when receiving an API request"; `EnableTokenEntryValidation()` and `EnableAuthorizationEntryValidation()` make revocation take effect on the very next `/mcp` request [CITED: documentation.openiddict.com token storage page; VERIFIED builder methods]. Required for the kill switch (D-07).
- Audience: the validation handler rejects with `invalid_token` and distinct descriptions for "no audience" (ID2093) and "no registered audience" (ID2094) [VERIFIED: OpenIddictValidationHandlers.Protection.cs:751-790]. The server only validates the `resource` parameter syntactically (absolute URI, no fragment) [VERIFIED: OpenIddictServerHandlers.Authentication.cs:1019-1065]; **the application must compare each requested `resource` with the configured canonical MCP URL, reject anything else, and attach exactly that value as the token's resource/audience** in the authorize passthrough. Without this step `AddAudiences` would reject every token (or, worse, tokens would carry a caller-chosen audience).
- Redirect URIs are exact, case-sensitive matches; a client registered with `ApplicationTypes.Native = "native"` additionally gets RFC 8252 loopback port relaxation (a registered `http://localhost/callback` matches `http://localhost:PORT/callback`) [VERIFIED: OpenIddictApplicationManager.cs:1570-1615, OpenIddictConstants.cs:29].
- Discovery: `ConfigurationEndpointUris` defaults to both `.well-known/openid-configuration` and `.well-known/oauth-authorization-server`; metadata includes `code_challenge_methods_supported` and `token_endpoint_auth_methods_supported` [VERIFIED: OpenIddictServerOptions.cs:69-73, OpenIddictServerHandlers.Discovery.cs:247-250]. The AS configuration validator still demands at least one asymmetric signing credential and one encryption credential at startup (ID0085/ID0086) [VERIFIED: OpenIddictServerConfiguration.cs:250-257]; ephemeral keys satisfy it.

### Pattern 3: Pre-registered clients (seeded, idempotent, from configuration)
Two applications, created or reconciled at startup by a hosted service through `IOpenIddictApplicationManager` (the runtime role already has DML on new tables via default privileges):
- `claude-hosted`: public client, no secret, redirect URIs `https://claude.ai/api/mcp/auth_callback` and `https://claude.com/api/mcp/auth_callback`, grant types authorization_code + refresh_token, scopes `ledger.read` + `offline_access`, PKCE required, explicit consent.
- `claude-code`: public, native application type, redirect URIs `http://localhost/callback` and `http://127.0.0.1/callback` (port-agnostic), same scopes.
Client ids are public constants (not secrets) and may live in `docs/` as placeholders-free literal names such as `ledger-claude-hosted`. Never add a registration endpoint.

### Pattern 4: The `/mcp` endpoint
- `AddMcpServer(options => options.ServerInstructions = ...)`, `.WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)`, `.WithTools<...>()`, then `app.MapMcp("/mcp").RequireAuthorization("McpPolicy")`. Stateless is the SDK default and its recommended setting for tools-only servers; stateless servers still answer clients on `2025-11-25` and earlier through `initialize` for the lifetime of one POST; GET/DELETE are unmapped (a `405` for GET is expected) [VERIFIED: csharp-sdk docs/concepts/stateless/stateless.md, HttpServerTransportOptions.cs, McpEndpointRouteBuilderExtensions.cs]. Fallback if claude.ai misbehaves against stateless: `HttpServerSessionMode.StatefulForInitializeClients` (enum members verified: `Stateless`, `Stateful`, `StatefulForInitializeClients`).
- Tool attribute properties that exist: `Name`, `Title`, `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld`, `UseStructuredContent` [VERIFIED: McpServerToolAttribute.cs]. Set `ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false` on every tool; the defaults are the opposite (`Destructive` true, `OpenWorld` true).
- Only an `McpException` message is returned to the client; any other exception becomes a generic "An error occurred invoking '<tool>'." [VERIFIED: McpServerImpl.cs:1722-1733]. Throw `McpException` only with messages that are safe to show (validation errors) and never include user text, counterparty names or amounts in exception messages (SEC-06).
- `ClaimsPrincipal` can be taken as a tool parameter and is excluded from the schema [CITED: SDK identity doc]; use it to read the subject and grant id for metrics and (later) audit, never the token.
- `ServerInstructions` is the place for the "use the totals tool for any 'how much'" guidance.

### Pattern 5: One application layer under MCP (and later REST)
Tools only translate parameters and render results. Rules live in `Ledger.Domain`, queries in `Ledger.Repository`, as the existing `ILedgerStore` / `IBalanceStore` do. Tools must not touch `LedgerDbContext` (ARCHITECTURE anti-pattern 2). New abstraction: `ILedgerQueryStore` (overview, totals, search, counterparties) returning plain records.

### Tool set (recommended; names and parameters are the planner's call)

| Tool | Purpose | Key parameters | Result must include |
|------|---------|----------------|---------------------|
| `ledger_overview` | Call first. Freshness and scope | none | per synced account: display name + opaque `account_key`, balance and whether it reconciles, last successful sync ("data as of"), history start date, pending count; today's date in Amsterdam |
| `money_totals` | Every "how much" | period (explicit `from`/`to` ISO dates, or one relative keyword), `direction`, `counterparty_contains[]`, `description_contains[]`, `account_keys[]`, `amount_min/max`, `group_by` (`none`,`counterparty`,`month`,`week`,`day`,`account`) | resolved absolute dates, filters echoed, `money_out`, `money_in`, `net`, `transaction_count`, per-currency, per-counterparty breakdown (top N + "other" remainder), internal transfers excluded (count + amount), pending not included (count + amount), "data as of", plain-language `summary` sentence |
| `search_transactions` | Supporting detail rows | same filters, `limit` (default 50, max 100), `cursor`, `sort` | rows (date, amount, currency, counterparty name, masked counterparty IBAN, description, status, account key), `matching_total`, `returned`, `truncated`, `next_cursor`, resolved period + filters |
| `find_counterparties` | Spell-check merchants before filtering | `text`, period, `limit` | distinct counterparty names (as stored) with count, total out/in, opaque `counterparty_ref`, so Claude can build filters |

Roughly 4 of the milestone's 8-15 tools; PLAN-09 comparison, affordability, writes, memory and reviews stay in later phases.

### Totals semantics (decisions D-08..D-13 turned into rules)
- Count `status = 'booked'` only. Report `pending` (count + amount) beside, never inside. `dropped` never appears [CONTEXT D-09; LedgerTransactionStatus values `Pending`, `Booked`, `Dropped` verified in LedgerModels.cs].
- Period date: for booked rows use `BookingDate`, falling back to `ValueDate` then `TransactionDate` only if null. **Do not reuse `TransactionReconciler.EffectiveDate`**: it prefers the transaction date (`transactionDate ?? bookingDate ?? valueDate ?? local first-seen day`) [VERIFIED: Ledger.Domain/Ingestion/TransactionReconciler.cs:259-267] and would disagree with D-11. The Grafana view prefers booking date first [VERIFIED: migration 20260930181840_AddLedgerIngestion.cs:28-46, `COALESCE(t.booking_date, t.transaction_date, t.value_date)`]. Pending rows with no booking date sit on the Amsterdam-local day first seen via the existing `SyncSchedule.LocalDate(firstSeenAt, zone)` helper (referenced by `TransactionReconciler.EffectiveDate`).
- Own-account exclusion (D-10): `upper(replace(counterparty_iban, ' ', ''))` equals the same normalisation of `accounts.iban` for an account with `sync_enabled = true`; excluded rows are reported as `internal_transfers` (count, amount out, amount in). IBANs never leave the server.
- Money out, money in, net reported separately; amounts are exact decimals serialised as strings with currency (`"210.00"`), never JSON floats. Sign: `money_out` and `money_in` are non-negative magnitudes, `net = money_in - money_out`. Never sum across currencies: group by currency and say so if more than one appears.
- Group in SQL by day (booked rows, `DateOnly` column, no timestamp involved), then roll up to week (ISO, Monday start) and month in C#. One place for bucketing, trivially testable. Pending rows are few: load them and bucket in C#.
- Counterparty breakdown is capped (for example 25 rows) with an explicit remainder row so the parts still sum to the total.
- Text filters: use `EF.Functions.ILike` with an escaped pattern (escape `%`, `_`, `\`); the filter text comes from an LLM and is hostile input. Compare normalised names with `TextNormalizer.ForMatching` where an exact comparison is needed [VERIFIED: Ledger.Domain/Ingestion/TextNormalizer.cs]. Do not add extensions (`pg_trgm`, `unaccent`) this phase.
- Opaque `counterparty_ref` (D-14): derive it deterministically from the normalised counterparty name (for example a truncated keyed hash) and resolve it by re-reading distinct names; do **not** add a counterparty table now (the later categorisation work introduces one).

### Amsterdam periods (OPS-06)
- One `PeriodResolver` in `Ledger.Domain`, constructed from `TimeProvider` and the configured `TimeZoneInfo` (`IngestionOptions.TimeZone = "Europe/Amsterdam"`, `ResolveTimeZone()` [VERIFIED: IngestionOptions.cs]). "Today" = the date of `TimeProvider.GetUtcNow()` converted to the zone. Relative keywords (`today`, `yesterday`, `this_month`, `last_month`, `last_30_days`, `this_year`, `last_year`) resolve to inclusive `DateOnly` ranges and the tool echoes the absolute dates.
- Bank dates are calendar dates: never pass them through `DateTime`/UTC. The only instant-to-date conversions are `FirstSeenAt` (pending rows) and "now".
- DST changeover days in 2026-2027: 2026-03-29 (23-hour day), 2026-10-25 (25-hour day), 2027-03-28, 2027-10-31 [VERIFIED: computed with Python zoneinfo this session]. Offsets: 2026-08-31 23:30 Amsterdam = 2026-08-31 21:30Z; 2026-10-01 00:05 Amsterdam = 2026-09-30 22:05Z (the "just after midnight while UTC is still the previous day" case); 2026-03-31 00:30 Amsterdam = 2026-03-30 22:30Z.
- The existing reporting view hard-codes `'Europe/Amsterdam'` in SQL; MCP code must take the zone from configuration, not from SQL.

### Pagination (ADV-03)
Keyset cursor over `(period_date DESC, first_seen_at DESC, id DESC)` (matches the existing index on `(account_id, booking_date)` well enough at household scale); opaque base64url cursor containing the last sort key and a hash of the filter set so a cursor cannot be replayed against different filters. Result fields: `returned`, `matching_total` (a `COUNT(*)` with the same filters), `truncated` (`matching_total > rows delivered so far`), `next_cursor`, and a sentence telling Claude to narrow the period or filters rather than page through everything. Default 50, hard max 100 (about 10k tokens at 100 tokens per row); a larger `limit` is clamped and reported, not rejected silently.

### Anti-Patterns to Avoid
- **Deriving issuer, resource or PRM URLs from the request** behind Traefik. Fix them in configuration and validate in `ProductionConfigurationValidator` (https, no trailing slash, no path other than `/mcp` for the resource).
- **Letting the API-key scheme or the fallback policy touch `/mcp`**, or a bearer token touch `/api/*`.
- **Per-table or per-endpoint tools**; any tool returning unbounded rows; any tool that lets Claude sum rows.
- **Adding a registration endpoint "just in case"**, enabling `AcceptAnonymousClients`, or wildcard redirect URIs.
- **Forwarding the caller's token anywhere.** The app needs no outbound call during a tool request; assert it with a test that the handler pipeline makes no `HttpClient` call.
- **Untrusted-data marking, text sanitising or injection instructions** (user decision; the planner must not add them). Keep result shapes extensible: put bank-sourced strings in clearly named fields so a later envelope is additive.
- **Reusing the reporting views for MCP reads** (they are Grafana's contract).

## Network exposure (Traefik, DNS, firewall)

Template additions to `deploy/traefik/ledger.yml.example` (placeholders only; syntax follows the existing file and Traefik v3 `ipAllowList` with `rejectStatusCode` option verified in Traefik docs):

```yaml
http:
  middlewares:
    ledger-mcp-allow:
      ipAllowList:
        sourceRange:
          - "160.79.104.0/21"   # Anthropic outbound range; re-check the IP address page before each release
          - "192.0.2.0/24"      # LAN placeholder
          - "198.51.100.0/24"   # VPN placeholder
  routers:
    ledger-mcp-public:
      rule: "Host(`mcp.example.com`) && (Path(`/mcp`) || Path(`/.well-known/oauth-protected-resource`) || Path(`/.well-known/oauth-protected-resource/mcp`) || Path(`/.well-known/oauth-authorization-server`) || Path(`/.well-known/openid-configuration`) || Path(`/connect/token`))"
      priority: 100
      middlewares: [ledger-mcp-allow, ledger-security-headers]
      service: ledger-api
      entryPoints: [websecure]
      tls: { certResolver: letsencrypt }
    ledger-mcp-signin:
      rule: "Host(`mcp.example.com`) && (Path(`/connect/authorize`) || PathPrefix(`/account/`))"
      priority: 110
      middlewares: [ledger-lan-vpn-only, ledger-security-headers]
      service: ledger-api
      entryPoints: [websecure]
      tls: { certResolver: letsencrypt }
```
Semantics verified in Traefik docs: `Path` matches the exact path only; `PathPrefix` matches everything beneath; default priority is rule length, explicit `priority` overrides; `rejectStatusCode` defaults to 403 and may be set (for example 404) [CITED: doc.traefik.io reference pages fetched this session]. Rules:
- Exact `Path()` matches only, except the single `PathPrefix('/account/')` sign-in group on the LAN/VPN-only router. No `PathPrefix('/')`, no `Host`-only router for the MCP hostname. Anything unmatched gets Traefik's default 404.
- Do not set `ipStrategy.depth` or `excludedIPs` unless Traefik itself sits behind another proxy; with them unset the allowlist uses the TCP peer address and a client-supplied `X-Forwarded-For` cannot spoof it. Traefik passes the real peer only if the router/NAT in front forwards without source NAT (operator to confirm).
- Do not add Traefik's `buffering` middleware on `/mcp` (it buffers responses and breaks streamed replies). Limit request bodies in the app (`KestrelServerOptions.Limits.MaxRequestBodySize`, a few hundred KiB is plenty) and rate-limit with the built-in ASP.NET Core rate limiter (per forwarded client address) on `/connect/token`, `/account/*` and `/mcp`.
- The existing Grafana and `ledger-api` hostnames keep their LAN/VPN routers and gain no public router. Outside requests to them already answer 403 (user confirmed); the exposure check must re-prove that.
- Keep `ReverseProxy:KnownProxies` as is. Add a defence-in-depth guard in the app: the MCP hostname answers only the MCP/OAuth path set (404 otherwise), and `/connect/authorize` + `/account/*` additionally require a client address inside configured LAN/VPN CIDRs, so a Traefik template mistake does not expose the login.
- DNS: the MCP hostname needs a public `A` record (IPv4, DNS-only, not proxied) to the home WAN address; do **not** publish an `AAAA` (connectors are IPv4-only and the outbound range has no IPv6). LAN and VPN clients resolve the same name to Traefik through local DNS (split horizon), which gives one issuer and one resource URL. If the WAN address sits behind CGNAT, claude.ai cannot reach it at all (operator check).
- TLS: the new hostname gets its certificate through the existing `letsencrypt` resolver. Traefik answers ACME challenges before router rules (resolver type to be confirmed by the operator).
- Firewall (`deploy/nftables/ledger.nft.in`): no change. Port 5080 is already reachable from the Traefik address only [VERIFIED: ledger.nft.in `tcp dport { 5080, 3000 } ip saddr @LEDGER_TRAEFIK_IP@ accept`].
- Because the allowlist covers everything on the public hostname, a request from an arbitrary internet host (for example the operator's phone on mobile data) gets 403 for *every* path, including discovery. "OAuth discovery works from outside the network" can therefore only be proven from Anthropic's range: by a successful claude.ai connect, plus the Traefik access log showing `200` for `/mcp`, the discovery documents and `/connect/token` from `160.79.104.x`. The outside-in script proves the negative cases (403/404 everywhere else).

### Host-side order of operations (planning consequence of the SSH todo)
1. While Claude still has SSH: provisioning/env additions, deploy the release, create the operator login and grants CLI wrapper, install the MCP Traefik file with **both routers restricted to LAN/VPN only** (no Anthropic range yet), local DNS entry. Prove OAuth + tools with the in-process test and with Claude Code on the LAN.
2. Extend `ledger-selfcheck` (see Validation) and docs.
3. Operator removes both SSH logins and the key (commands live only in the operator's local notes); `ledger-selfcheck --grafana-admin` must report 0 failures including the new "no unexpected sudo login" check.
4. Operator, from here on: add the Anthropic range to `ledger-mcp-allow`, create the public `A` record, run the outside-in exposure script from a mobile-data phone, add the connector in claude.ai from a desktop browser on the LAN, then confirm Traefik logs show Anthropic-origin `200`s. Any fix after step 3 is operator-run.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| OAuth authorization code + PKCE, token endpoint, discovery, rotation, reuse detection, revocation | Custom token endpoint or JWT minting | OpenIddict 7.7.1 | Reuse detection, leeway, token/authorization stores and metadata are already implemented and tested |
| Password hashing, lockout, TOTP secret/validation | Own hash, own TOTP, own lockout counter | ASP.NET Core Identity core (`UserManager`, `SignInManager`, authenticator token provider) | Versioned hasher, lockout, RFC 6238 provider |
| MCP protocol, protected-resource metadata, 401 challenge | Hand-written JSON-RPC over HTTP | `ModelContextProtocol.AspNetCore` 2.2.0 | Version negotiation, stateless handling, schema generation |
| Token signing/encryption key custody | New certificate files | Existing Data Protection key ring (verify per Pitfall 14) | Already persisted, certificate-protected, backed up |
| Rate limiting, antiforgery, CSRF | Custom middleware | `AddRateLimiter`, `AddAntiforgery` | Built in |
| Money arithmetic in tools or in Claude | Float math, row summing by the model | PostgreSQL `numeric` sums mapped to `decimal`, rolled up in the domain | Exactness; Pitfall 17 of the earlier research |
| Query-string filters | String-concatenated SQL | EF Core LINQ with parameters; `EF.Functions.ILike` with escaped patterns | Filter text is LLM-supplied |
| Period maths with `DateTime.Now`/UTC | Ad hoc conversions | `PeriodResolver` over `TimeProvider` + `TimeZoneInfo` | One tested place |

**Key insight:** every security property this phase needs (audience binding, rotation with reuse detection, revoke-all, S256 PKCE) is already a switch in OpenIddict; the work is wiring, a small login UI and tests, not protocol code.

## Common Pitfalls

### Pitfall 1: Resource/audience mismatch makes every token rejected (or accepts any audience)
**What goes wrong:** Claude sends `resource=https://<host>/mcp`; OpenIddict only checks the syntax, so tokens carry no audience unless the authorize handler sets it; `AddAudiences` then rejects all, and a "fix" that drops `AddAudiences` silently disables SEC-04.
**How to avoid:** In the authorize passthrough compare each requested resource (lowercased scheme/host, trailing slash trimmed) with the configured canonical URL, reject others with `invalid_target`, and set the token resources to exactly that. Accept the canonical form rather than a byte-for-byte match with what the user typed (Anthropic's guidance). Test: a token minted for another audience gets 401 and increments the `wrong_audience` metric.
**Warning signs:** `401` right after a successful token exchange; log id 6266/6267 (no audience / not an accepted audience).

### Pitfall 2: Wrong challenge on `/mcp` (ApiKey header instead of Bearer + resource_metadata)
**What goes wrong:** the repo's default challenge scheme is `ApiKey`; an unauthenticated `/mcp` request would answer `WWW-Authenticate: ApiKey ...` and Claude never starts OAuth ("Couldn't reach the MCP server").
**How to avoid:** the named `/mcp` policy lists only the MCP scheme; MCP options `ForwardAuthenticate` to the OpenIddict validation scheme; integration test asserts the exact `401` header shape. Also test that `Authorization: Bearer` on `/api/v1/status` is rejected and `X-Api-Key` on `/mcp` is rejected.

### Pitfall 3: Fallback policy blocks the OAuth pages
`FallbackPolicy` requires an authenticated user, so `/connect/authorize` passthrough, `/connect/token` passthrough, `/account/login`, `/account/totp` and the consent post need `.AllowAnonymous()` (the interactive pages then authenticate themselves with the sign-in cookie scheme). Forgetting it gives redirect loops or 401 on the login page. Use `AddIdentityCore` (not `AddIdentity`) plus an explicit `AddCookie`, so the default authentication scheme stays `ApiKey`.

### Pitfall 4: Issuer / PRM string mismatch
Claude Code fails sign-in on an unexpected issuer, and Claude uses only the first `authorization_servers` entry. A trailing-slash difference between `SetIssuer(...)`, the metadata `issuer`, and the PRM entry is the classic break. Build all three from one configured value and add a test comparing the strings character for character. Verify against both Claude Code and claude.ai during the tracer.

### Pitfall 5: Public hostname resolves privately or over IPv6 only
Anthropic refuses before any HTTP request if any answer is private, CGNAT, loopback, mixed, or only `AAAA`. Split-horizon DNS is fine for LAN clients but the **public** record must be a single public `A`. A grey-cloud/proxied record or dynamic-DNS hostname behind NAT fails silently ("Couldn't reach", empty server logs). Operator checks `dig +short <host>` from outside before connecting.

### Pitfall 6: Allowlist template drift
Anthropic can change the range without notice and the phased-out addresses must not be carried over. Add a unit test over `ledger.yml.example` asserting it contains `160.79.104.0/21` and none of the five phased-out `/32`s, uses only exact paths on the MCP hostname, and uses `example.*`/RFC 5737 values only. Add a docs step "compare with the IP address page before each release".

### Pitfall 7: Token and filter data leaking into logs, metrics, exceptions
The SDK and OpenIddict log at Information/Debug (token ids, tool names; tool arguments can be traced). Set `ModelContextProtocol` and `OpenIddict` log categories to Warning in `appsettings`, never log `Authorization`, never put counterparty/amount/IBAN/filter text in exception messages (only `McpException` text reaches Claude), metric labels are tool names and fixed reasons only. Extend `LogRedactionTests` (captures every level) with a tool call carrying a synthetic counterparty and a bearer token and assert neither appears.

### Pitfall 8: TOTP replay and brute force
Identity's TOTP validation is stateless: "The user can enter the code multiple times and authenticate successfully before it expires" [CITED: learn.microsoft.com identity-enable-qrcodes warning]. Persist the last accepted time-step per user and reject a repeat; keep Identity lockout (for example 5 failures -> 15 minutes) and add ASP.NET Core rate limiting on the sign-in posts. Server clock must be NTP-accurate (30 s codes).

### Pitfall 9: Claude summing pages / answering from search rows
Mitigate in the tool design (totals tool, server instructions, `summary` sentence, search result text telling Claude not to total rows) and test the instructions string contains the "use the totals tool" guidance. Never let `search_transactions` return a per-page sum.

### Pitfall 10: Silent truncation
Always return `matching_total`, `returned`, `truncated`; clamp `limit` and say so; cap the counterparty breakdown with a remainder row. Test with a synthetic year (>1,000 rows) that `truncated` is true on page 1 and false on the last page, with no duplicates or gaps across pages.

### Pitfall 11: Transfer exclusion applied unevenly
Apply the same own-account exclusion in totals, in the breakdown and in search (search flags such rows `internal_transfer: true` instead of hiding them, so the numbers can be reconciled). Compare normalised IBANs (spaces, case); only accounts with `sync_enabled`.

### Pitfall 12: Three different "effective date" definitions
Reconciler = transaction date first; Grafana view = booking date first; MCP = booking date (D-11). Name the new function distinctly (`PeriodDate`), cover it with tests, and comment (as `///`) why it differs. Booked rows missing a booking date fall back deterministically.

### Pitfall 13: Refresh race revokes the whole grant
A rotated refresh token reused after the 30 s leeway revokes every token of the authorization. If claude.ai retries a refresh whose response was lost, or refreshes from two surfaces at once outside 30 s, the grant dies and the operator must sign in again. Keep the default leeway initially, log (without values) every reuse-triggered revocation, count it, and revisit the leeway only if it happens in practice. [Behaviour on the Claude side after `invalid_grant` is documented only as "Re-authenticate" in Claude Code; the claude.ai UX is [ASSUMED] to show a reconnect prompt.]

### Pitfall 14: Token protection key custody (verify before keeping)
With `UseDataProtection()` tokens are protected by the existing key ring, which rotates (new key about every 90 days) but retains old keys for decryption. Ephemeral signing/encryption keys only satisfy OpenIddict's startup validation. This is [ASSUMED] to work without persisting those keys; prove it with an integration test: issue tokens, dispose the host, start a second host on the same database and key ring, refresh and call `/mcp` successfully. If it fails, generate persistent certificates in provisioning like the Data Protection certificate (`openssl req -x509 ... -days 3650`, mode 640 root:ledger, password in the env file) and load them with `AddSigningCertificate`/`AddEncryptionCertificate` [certificate pattern VERIFIED: deploy/provision.d/20-accounts.sh:81-96].

### Pitfall 15: Consent phishing / connector hijack
An attacker can add the public MCP URL as a connector in their own Claude account and send the operator the resulting authorize link; if the operator signs in and approves, the code goes to Anthropic's callback bound to the attacker's session. Reachability is limited by D-02 (the link only works from the operator's LAN/VPN browser) but the social-engineering path remains. Mitigations: always show an explicit consent page naming the client and the exact redirect host, tell the operator in docs to approve only a connect they just started, create the grant row with a creation time and client id, expose `ledger_oauth_grants_created_total`, and raise an operator email alert on any new grant (rare event, no financial detail). Revoke via the kill switch.

### Pitfall 16: Sign-in page hardening
`returnUrl` must be local-only (`Url.IsLocalUrl`), forms need antiforgery tokens, pages send `Cache-Control: no-store`, `Content-Security-Policy` with `default-src 'none'`/`form-action 'self'`/`frame-ancestors 'none'`, no inline script and no third-party JS (QR code libraries excluded: enrolment prints the `otpauth://` URI and the Base32 secret through the CLI instead). Cookie: `Secure`, `HttpOnly`, `SameSite=Lax` (the authorize redirect is a top-level GET), short lifetime, scoped path. Login errors identical for unknown user and wrong password.

### Pitfall 17: Dependency and lock-file churn
Adding the packages changes `packages.lock.json` in every project that references them (locked-mode restore in CI); `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 is already referenced in `Ledger.Service` and OpenIddict brings its own IdentityModel dependencies - resolve any version conflict by letting restore pick one version and re-locking. `TreatWarningsAsErrors` is on: public OpenIddict/SDK APIs marked experimental/obsolete (for example SDK `MCP9004`, `MCP9006` diagnostics) fail the build if touched; avoid the obsolete stateful knobs.

### Pitfall 18: Prometheus series that appear only after the first event
Pre-create every `reason` / `outcome` label value at zero at startup (the existing sync metrics do this) so the alert rule and dashboard see the series.

## Code Examples

Skeletons only; every value that is not in a verified quote above is configuration-driven.

### Per-endpoint policy
```csharp
services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { })
    .AddMcp(options =>
    {
        options.ForwardAuthenticate = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
        options.ResourceMetadataUri = new Uri("/.well-known/oauth-protected-resource/mcp", UriKind.Relative);
        options.ResourceMetadata = new ProtectedResourceMetadata
        {
            Resource = mcpResourceUrl,
            AuthorizationServers = { issuerUrl },
            ScopesSupported = { "ledger.read" }
        };
    });

services.AddAuthorization(options =>
{
    options.AddPolicy("McpPolicy", policy => policy
        .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireClaim("scope", "ledger.read"));
});

app.MapMcp("/mcp").RequireAuthorization("McpPolicy");
```
[VERIFIED names: McpAuthenticationDefaults.AuthenticationScheme, McpAuthenticationOptions.ForwardAuthenticate/ResourceMetadataUri/ResourceMetadata, ProtectedResourceMetadata.Resource/AuthorizationServers/ScopesSupported, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, MapMcp(pattern) signature. The scope claim type name used by OpenIddict principals and the `"ledger.read"` literal are [ASSUMED] until the tracer test runs.] When `ResourceMetadataUri` is set, `ProtectedResourceMetadata.Resource` must be set explicitly (documented in `ProtectedResourceMetadata.Resource` remarks) - done above.

### Tool shape
```csharp
[McpServerToolType]
public class LedgerTools(ILedgerQueryService ledger)
{
    [McpServerTool(Name = "money_totals", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Total money out, money in and net for a period and filters, computed on the server. Use this for every how-much question.")]
    public async Task<MoneyTotalsResult> MoneyTotals(MoneyTotalsRequest request, CancellationToken cancellationToken)
        => await ledger.TotalsAsync(request, cancellationToken);
}
```
[VERIFIED attribute property names; the DI-by-primary-constructor tool class and `[Description]` usage follow the SDK docs (tools.md).]

### Period resolution test skeleton
```csharp
var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T22:05:00Z"));
var resolver = new PeriodResolver(clock, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"));
resolver.Resolve(RelativePeriod.ThisMonth).Should().Be(new DateRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
```
(Instants verified with Python zoneinfo above; type names are the planner's.)

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| MCP stateful sessions + `initialize` | Spec 2026-07-28: stateless core; SDK 2.x defaults to `Stateless`, keeps `initialize` for older clients | SDK 2.0 / spec 2026-07-28 | Use stateless; fall back to `StatefulForInitializeClients` only if a client needs sessions |
| DCR as the way for claude.ai to onboard | CIMD preferred by the spec; Anthropic also accepts a user-entered pre-registered client | 2025-11-25 / 2026-07-28 spec; Anthropic docs | Pre-registration needs no DCR/CIMD server code |
| Authentik without DCR | Authentik 2026.8 ships DCR, OpenID certification, no Redis dependency since 2025.10 | 2026.8 | CLAUDE.md note is now true, but irrelevant to the choice |
| Anthropic IPs `34.162.x.x/32` | `160.79.104.0/21` outbound | per IP page | Remove old ranges |
| Claude Code `127.0.0.1` redirect in v2.1.229 | `localhost` restored in v2.1.231 | Claude Code release | Register both |

**Deprecated/outdated (do not carry over):** `34.162.*` addresses; the earlier research's "OpenIddict has no DCR so it is not viable" (true that it has no DCR, but not required); SQL Server specifics in ARCHITECTURE.md.

## Runtime State Inventory

Not a rename/refactor/migration phase. Omitted. (Greenfield additions: new tables arrive by migration; no stored string is renamed.)

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | DataProtection token format + reference tokens + ephemeral signing/encryption keys starts and survives restart in OpenIddict 7.7.1 | Pattern 2, Pitfall 14 | Tokens invalid after restart; fallback is persistent certificates (small provisioning change) |
| A2 | `UseEntityFrameworkCore().UseDbContext<T>()` / `options.UseOpenIddict()` setup works with the repo's snake-case naming convention and generated migration under the migrator role | Pattern 2 | Table names or grants differ; adjust convention exclusion or add explicit grants |
| A3 | claude.ai shows a reconnect prompt when a grant is revoked/expired and the mobile app can use the stored connection but cannot start re-auth by itself | Connector behaviour, Pitfall 13 | Operator surprise; mitigated by doing re-auth from desktop on the LAN |
| A4 | claude.ai sends the canonical `resource` value and works with a public client with no secret entered | Pitfall 1 | Connect fails in tracer; fallback is a confidential client with a secret entered once in the dialog |
| A5 | The scope claim on OpenIddict principals is readable as `scope` and the policy `RequireClaim("scope", ...)` matches | Code Examples | Policy rejects valid tokens; use OpenIddict's scope helpers instead |
| A6 | claude.ai and Claude Code do not send an `Origin` header to `/mcp` | Pitfall 16 / Security | If Origin rejection is added and clients send it, connects break; verify before enforcing |
| A7 | Traefik receives the true client address (no source NAT in front) and the home WAN address is not behind CGNAT | Network exposure | Allowlist rejects Anthropic or accepts spoofed sources; operator to confirm before go-live |
| A8 | Authentik provider docs omit RFC 8707 resource handling and reuse detection because they are absent (absence of evidence in doc excerpts) | Alternatives | Low impact: Authentik is rejected mainly on footprint and install model, which are documented |
| A9 | Identity's TOTP provider does not block replays and the repo's single-operator scale makes "last accepted step" storage sufficient | Pitfall 8 | Small hardening gap |
| A10 | Stateless mode answers claude.ai's initialize flow without a session | Pattern 4 | Use `StatefulForInitializeClients` |
| A11 | Operator's Traefik is directly internet-facing and uses an ACME resolver that answers challenges before router rules | Network exposure | Certificate issuance for the new hostname needs a different resolver path |
| A12 | `IOpenIddictAuthorizationManager` offers a revoke operation usable by the CLI (`RevokeByAuthorizationIdAsync` on the token manager is verified) | Kill switch | Implement via status update on authorization rows |

## Open Questions

1. **How does claude.ai behave when a refresh token is dead (revoked or expired)?**
   - Known: Anthropic requires `invalid_grant`; Claude Code offers "Re-authenticate".
   - Unclear: the claude.ai/mobile UX and whether tool calls fail with a clear message.
   - Recommendation: test in the tracer by running the kill-switch CLI mid-session and document the observed behaviour in `docs/`.
2. **Can the mobile app trigger re-auth on its own, and does its browser need the VPN?**
   - Recommendation: document "re-connect from desktop on the LAN" as the supported path; test mobile once and record the result.
3. **Does the operator's router forward without source NAT, and is the WAN address public (not CGNAT)?** Operator confirms before the public router is enabled.
4. **Hostname label** (Claude's discretion): use a dedicated label such as `mcp.<domain>`; the authorization server shares it (single issuer and resource, as designed).
5. **Alert thresholds and Traefik-side visibility:** start with an alert on more than 10 rejected tokens in 10 minutes and on any new grant; Traefik is not scraped, so blocked requests are visible only in its access log. Optional follow-up, not required.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.112 | - |
| PostgreSQL (user's container `postgres-dev`) | integration tests | yes | postgres:18 | CI service container |
| Traefik (separate container, dynamic config dir) | public + sign-in routers | yes (exists; SSH write access until removal) | v3 syntax per existing template | - |
| Public DNS `A` record + router port forward | claude.ai connect | operator task | - | none; blocks claude.ai, not Claude Code on LAN |
| Claude Code CLI | LAN tracer proof | not on PATH in the research shell; operator has it | v2.1.231+ needed for `localhost` redirect form | - |
| claude.ai account with custom connectors | final connect | operator | Free/Pro/Max can add one | - |
| Docker | not needed (Authentik rejected) | - | - | - |

**Missing dependencies with no fallback:** public `A` record / port forward (operator).
**Missing dependencies with fallback:** none.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 4.0.1 on Microsoft.Testing.Platform, FluentAssertions 8.11.0, NSubstitute 6.2.0 (unit), `Microsoft.AspNetCore.Mvc.Testing` 10.0.12 + real PostgreSQL (integration) |
| Config file | `global.json` (`"runner": "Microsoft.Testing.Platform"`), `Ledger.IntegrationTests/xunit.runner.json` |
| Quick run command | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj` |
| Full suite command | `dotnet test --solution Ledger.slnx --no-restore` (needs `ConnectionStrings__TestAdmin` and `LEDGER_EFBUNDLE`, as CI sets them) |
| Trait filter | `dotnet test --solution Ledger.slnx --filter-trait "Category=<X>" --ignore-exit-code 8` [VERIFIED: 02-VALIDATION.md] |
| Lint / host scripts | `build/lint.sh`; `deploy/tests/*-test.sh` offline logic tests |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| OPS-06 | Month-end, last evening, just-after-midnight, 2026-03-29 / 2026-10-25 / 2027-03-28 / 2027-10-31 periods, ISO weeks | unit | `dotnet test --project Ledger.UnitTests --filter-trait "Category=Periods"` | Wave 0: `Ledger.UnitTests/Mcp/PeriodResolverTests.cs` |
| OPS-06 | Pending row first seen 23:30 local on the last day of a month lands on that day | integration | `--filter-trait "Category=Totals"` | Wave 0: `Ledger.IntegrationTests/Mcp/TotalsQueryTests.cs` |
| ADV-02 | Totals: booked only, pending/dropped/internal transfers handled, out/in/net, per-counterparty breakdown with remainder, currency grouping, provenance fields | integration | `--filter-trait "Category=Totals"` | Wave 0 |
| ADV-02 | Own-account exclusion matches normalised IBAN of synced accounts only | integration | `--filter-trait "Category=Totals"` | Wave 0 |
| ADV-03 | Year of >1,000 synthetic rows: page cap, `truncated`, cursor continuity, filter-bound cursor | integration | `--filter-trait "Category=Search"` | Wave 0: `Ledger.IntegrationTests/Mcp/SearchPaginationTests.cs` |
| ADV-01 | Tool list: names, read-only annotations, descriptions free of planning references and household names; instructions mention totals tool | unit | `--filter-trait "Category=McpTools"` | Wave 0: `Ledger.UnitTests/Mcp/ToolCatalogTests.cs` |
| ADV-10 / SEC-04 | In-process OAuth: authorize (password + TOTP) -> code -> token -> `/mcp` tool call through the SDK client; discovery documents; PRM resource = configured URL; S256 advertised; `offline_access` listed | integration | `--filter-trait "Category=OAuth"` | Wave 0: `Ledger.IntegrationTests/Mcp/OAuthFlowTests.cs` |
| SEC-04 | Wrong-audience token -> 401 + `wrong_audience` metric; expired -> 401; API key on `/mcp` -> 401; bearer on `/api/*` -> 401; token never forwarded (no outbound HTTP during a tool call) | integration | `--filter-trait "Category=OAuth"` | Wave 0 |
| SEC-04 / D-03 | Refresh rotation; reuse outside leeway revokes all tokens; revoke-all CLI kills the next `/mcp` call; restart keeps tokens valid | integration | `--filter-trait "Category=OAuth"` | Wave 0 |
| SEC-04 | Host guard: MCP hostname answers only MCP/OAuth paths; sign-in paths reject non-LAN/VPN client addresses; metadata ignores spoofed Host/X-Forwarded-Host | integration | `--filter-trait "Category=OAuth"` | Wave 0 |
| SEC-04 | Traefik template: exact paths only on the MCP host, Anthropic range present, phased-out `/32`s absent, sign-in router uses the LAN/VPN middleware, placeholders only | unit | `--filter-trait "Category=Configuration"` | Wave 0: extend `CommittedConfigurationTests` |
| SEC-06 | No bearer token, tool argument or counterparty text in captured logs at any level | integration | `--filter-trait "Category=Security"` | Extend `Ledger.IntegrationTests/Security/LogRedactionTests.cs` |
| D-07 | Metrics exist at zero from startup; labels opaque; rejected-token alert rule parses and contains no financial text | unit + config | `--filter-trait "Category=Metrics"` | Wave 0 |
| SSH todo | `ledger-selfcheck` fails when an unexpected login has sudo; offline logic test | shell | `bash deploy/tests/selfcheck-logic-test.sh` | extend existing |
| Exposure | Outside-in script logic (expected status matrix) | shell | `bash deploy/tests/exposure-check-logic-test.sh` | Wave 0 |
| Success criterion 1 | claude.ai web and mobile answer from real transactions; Desktop connector works; Claude Code works on LAN/VPN | manual UAT | operator checklist | n/a |

### Sampling Rate
- **Per task commit:** `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj` plus the relevant `--filter-trait` integration category once its task lands.
- **Per wave merge:** `dotnet test --solution Ledger.slnx --no-restore` and `build/lint.sh`.
- **Phase gate:** full suite green, `deploy/tests` scripts green, selfcheck 0 failures on the host, then operator UAT (claude.ai connect, outside-in check, Traefik log evidence).

### Wave 0 Gaps
- [ ] `Ledger.UnitTests/Mcp/PeriodResolverTests.cs`, `ToolCatalogTests.cs` - covers OPS-06, ADV-01
- [ ] `Ledger.IntegrationTests/Mcp/TotalsQueryTests.cs`, `SearchPaginationTests.cs`, `OAuthFlowTests.cs` plus a synthetic-ledger seeding helper (large year of rows, own-account transfers, pending/dropped rows) - covers ADV-02, ADV-03, ADV-10, SEC-04
- [ ] A test OAuth driver: signs in as a synthetic user with a computed TOTP code, runs the PKCE code flow against the in-process host, returns a bearer for the SDK client (the factory in `Infrastructure/LedgerWebApplicationFactory.cs` already boots real Kestrel sockets)
- [ ] Framework install: none new; lock files regenerate when the packages are added

## Security Domain

### Applicable ASVS Categories (level 2, block on high)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes | Identity password hashing, lockout, TOTP with replay guard; rate limiting; sign-in only from LAN/VPN |
| V3 Session Management | yes | Sign-in cookie `Secure`/`HttpOnly`/`SameSite=Lax`, short lifetime; OAuth access tokens 15 min, rotating 90-day sliding refresh tokens, reuse revokes tokens, revoke-all CLI |
| V4 Access Control | yes | Separate schemes/policies for `/mcp`, REST and sign-in pages; single read scope; host/path guard; read-only tools |
| V5 Input Validation | yes | Typed tool parameters, enum/range validation, escaped `ILIKE`, clamped limits, exact redirect URIs, local-only `returnUrl` |
| V6 Cryptography | yes | PKCE S256, OpenIddict/Data Protection key handling, no hand-rolled crypto; TLS at Traefik |
| V7 Error Handling and Logging | yes | Generic tool errors; log categories at Warning; redaction test |
| V8 Data Protection | yes | IBAN masking to last four, own accounts by name/key only, no personal data in repo/fixtures |
| V9 Communications | yes | HTTPS only, HSTS (existing header middleware), no tokens in query strings |
| V13 API and Web Service | yes | Stateless MCP, request size limit, rate limits, no CORS |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Token for another audience accepted / token passthrough | Spoofing, Elevation | `AddAudiences`, authorize-time `resource` check, token-entry validation, no outbound calls in tool path (test) |
| Open or wildcard redirect URI; code interception | Spoofing | Exact registered URIs, native loopback relaxation only for the Claude Code client, PKCE S256 required |
| Public registration endpoint abuse / SSRF via CIMD fetch | DoS, Tampering | Not built: pre-registered clients only |
| Refresh token theft | Spoofing | Rotation, reuse detection (revokes tokens), 15-minute access tokens, instant revoke-all |
| Consent phishing (attacker's connector, operator's approval) | Spoofing | Explicit consent page naming client and redirect host, new-grant alert, kill switch, LAN/VPN-only sign-in |
| Brute force / credential stuffing on sign-in | Spoofing | Lockout, rate limiting, TOTP replay guard, sign-in unreachable from the internet |
| IP-allowlist bypass via forged `X-Forwarded-For` | Spoofing | Traefik uses the TCP peer (no `ipStrategy.depth`); app trusts forwarded headers only from the known proxy |
| Shared Anthropic egress range lets any claude.ai user probe the public paths | Information disclosure, DoS | OAuth is the control, not the IP list; exact-path routing; rate limits; discovery docs hold no secrets |
| Template mistake exposes REST/Grafana/sign-in on the public host | Information disclosure | Exact paths, app-side host/path and network guards, outside-in script, template unit test |
| Login CSRF / clickjacking / open redirect on sign-in | Tampering | Antiforgery, frame-ancestors none, local-only returnUrl |
| Sensitive data in logs/metrics/errors | Information disclosure | Log levels, redaction test, opaque labels |
| Prompt injection from bank-sourced text | Tampering | Accepted and deferred by the operator; read-only tools limit impact; structural defences arrive with write tools |
| Root SSH login with a key on a workstation while the host is internet-adjacent | Elevation | Remove logins/key first; selfcheck sudo check; public router last |

## Sources

### Primary (HIGH confidence)
- Anthropic connector authentication reference (claude.com/docs/connectors/building/authentication) - auth types, CIMD/DCR/pre-registered client, callbacks, token refresh rules, timeouts, latency
- Anthropic connector troubleshooting (claude.com/docs/connectors/building/troubleshooting) - public-DNS/IPv4 requirement, WAF/allowlist, redirects, audience/`resource`, discovery
- Anthropic "Add a connector that isn't in the directory" (claude.com/docs/connectors/custom/add-unlisted) - "Use your own OAuth client", can't change auth afterwards, request headers
- Anthropic custom connectors on mobile and desktop (support.claude.com/en/articles/11175166) - cloud-brokered connections across all clients
- Anthropic IP address reference (platform.claude.com/docs/en/api/ip-addresses) - outbound `160.79.104.0/21`, phased-out list (fetched 2026-10-07)
- Claude Code MCP documentation (code.claude.com/docs/en/mcp) - `--client-id`, `--callback-port`, redirect form, discovery, refresh, issuer check
- MCP authorization specification 2025-11-25 (modelcontextprotocol.io) - audience binding, resource indicators, refresh rotation for public clients, PKCE, redirect validation
- OpenIddict 7.7.1 source (github.com/openiddict/openiddict-core tag 7.7.1, cloned and read: options, handlers, builders, validation, application manager)
- ModelContextProtocol C# SDK 2.2.0 source and docs (github.com/modelcontextprotocol/csharp-sdk tag v2.2.0, cloned and read: HttpServerTransportOptions, session modes, auth handler, tool attributes, identity and stateless docs)
- NuGet registration/search APIs and nuspecs for the four packages (queried 2026-10-07)
- Repository files read: Program.cs, ApiKeyAuthenticationHandler, ApiKeyCommand, `deploy/bin/ledger-apikey`, ledger.yml.example, ledger.nft.in, ledger.env.example, ledger.service, migrations (reporting views), LedgerModels, ILedgerStore, TransactionReconciler, TextNormalizer, IngestionOptions, provisioning scripts, selfcheck, alerting rules, docs

### Secondary (MEDIUM confidence)
- Traefik reference pages for router rules/priority and ipAllowList (doc.traefik.io), Microsoft Learn TOTP page (replay warning), authentik 2026.8 release highlights and OAuth provider docs, authentik install docs (system requirements), search summaries on mobile connector behaviour and Authentik refresh-token handling

### Tertiary (LOW confidence)
- Web-search summaries for mobile add/re-auth behaviour and Authentik rotation semantics; marked as assumptions A3, A8

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - versions, publishers and dependencies verified; behaviours read from source
- Connector/OAuth behaviour: HIGH for documented requirements; MEDIUM for claude.ai runtime behaviours listed as assumptions
- Architecture: HIGH - follows existing layering; auth wiring risk concentrated in Pattern 1 and Pitfalls 1-4, to be retired by the tracer test
- Pitfalls: HIGH for items traced to source/docs; MEDIUM for A1-A6

**Research date:** 2026-10-07
**Valid until:** 2026-10-21 for Anthropic connector and IP-range facts (fast-moving; re-fetch the IP page before enabling the public router); 2026-11-07 for library versions
