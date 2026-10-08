# Phase 3: Claude Reads the Ledger - Context

**Gathered:** 2026-10-07
**Status:** Ready for planning

<domain>
## Phase Boundary

The operator connects the ledger to Claude: as a custom connector in claude.ai (web and mobile), and from Claude Code (and Claude Desktop) at home or on the VPN. Claude then gets a small set of read-only, intent-shaped MCP tools. With them it answers "how much" and "what did we spend" questions from the real transactions. Every total is computed on the server and states its date range, filters and number of transactions included. Search is paginated under a server-side cap and says explicitly when it was truncated. Days, months and years follow Europe/Amsterdam.

`/mcp` and its OAuth endpoints are the host's first internet-facing surface:
- From outside, only they respond, and only to Anthropic's published IP ranges.
- OAuth discovery works from outside.
- Tokens are audience-validated on every request and never forwarded to anything else.
- Claude's temporary SSH access is removed before the public route goes live.

Not in this phase:
- categories, rules, the review queue, general transfer detection (Phase 4)
- comparisons with the household's own history (Phase 4)
- any write tool, the audit log, and untrusted-data marking (Phase 4)
- budgets, goals, recurring costs, forecasts, affordability (Phase 5)
- advisor memory, reviews, review prompts (Phase 6)

</domain>

<decisions>
## Implementation Decisions

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

### Folded Todos
- **Remove temporary Claude SSH access before `/mcp` goes public** (`.planning/todos/pending/2026-09-28-remove-temporary-claude-ssh-access-before-real-bank-data.md`, major, `resolves_phase: 3`). The current mitigation is a passphrase-protected key loaded through `ssh-agent`, and this is the removal point Phase 2 D-04 fixed.
  - **Order:** first delete the logins on the ledger host and on the reverse proxy and delete the key on the workstation. Then `ledger-selfcheck` must pass. Only after that is the public `/mcp` router enabled.
  - Add the optional selfcheck that fails when any login other than the expected service accounts has sudo.
  - The exact accounts and commands are in the operator's local, uncommitted notes, never in the repo.
  - Planning consequence: Claude may use SSH for internal-only host setup before the removal step. Every host step after it is run by the operator: enabling the public router, the outside-in exposure check, and any later fixes on the host.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Scope and requirements
- `.planning/ROADMAP.md` §Phase 3: goal, five success criteria, the open OAuth-server decision, and the research flags (claude.ai connector OAuth requirements; Anthropic egress ranges; sign-in from home or VPN; refresh lifetimes)
- `.planning/REQUIREMENTS.md`: ADV-01, ADV-02, ADV-03, ADV-10, SEC-04 and OPS-06 for this phase. For boundaries: ADV-07 (Phase 4, per D-15), CAT-02 (Phase 4 builds on D-10), PLAN-09 (comparison stays in Phase 4).
- `.planning/PROJECT.md` §Constraints, §Key Decisions (OAuth server pending; only `/mcp` public; dashboards LAN and VPN only), §Context "Accepted data flows" (Anthropic only, the basis for D-01)
- `.planning/STATE.md` §Blockers/Concerns: the Phase 3 OAuth-server and sign-in-location concerns. D-02 and D-03 answer the second one.
- `.claude/CLAUDE.md` §Hard rules (no planning references outside `.planning/`, `///` comments only, no personal data, security first, no LLM API calls from the app), §Technology Stack §3 (MCP C# SDK, Streamable HTTP, resource-server role, spec revision), §4 (OAuth server options, DCR vs CIMD, Anthropic IP ranges; verify all of it again, it predates this phase)

### Prior phase decisions that constrain this phase
- `.planning/phases/01-secure-platform-release-pipeline/01-CONTEXT.md`:
  - D-07: secrets only in the env file
  - D-19: email contact point, no financial detail
  - D-20: internal Traefik hostnames with the LAN/VPN allowlist
  - D-22: named API keys ("can move to OIDC once the OAuth server exists"; D-05 keeps them REST-only)
  - D-23: `/metrics` on loopback
- `.planning/phases/02-automatic-ing-sync/02-CONTEXT.md`:
  - D-04: SSH removal point
  - D-09: account display names and opaque keys
  - D-13: pending totals per figure, settled here by D-09
  - D-14: immutable ledger identity
  - D-17: alerts go to the operator only

### Research (historical; decisions above take precedence)
- `.planning/research/PITFALLS.md`:
  - Pitfall 4: internal transfers double-counted (D-10)
  - Pitfall 7: prompt injection (deferred by D-15)
  - Pitfall 8: token passthrough, audience validation, confused deputy
  - Pitfall 9: OAuth metadata behind a reverse proxy, forwarded headers, issuer URL
  - Pitfall 17: Claude doing arithmetic over raw rows
  - Pitfall 19: Europe/Amsterdam period boundaries
  - Pitfall 20: tool-result size and truncation
- `.planning/research/ARCHITECTURE.md` Pattern 1 (one application layer under REST and MCP), §MCP request flow, §MCP Surface Design, §Security Architecture (trust boundaries), Anti-Pattern 2 (MCP tools must not bypass domain services). Its SQL Server details are superseded.
- `.planning/research/SUMMARY.md`: MCP/OAuth stack summary (Authentik vs embedded, DCR vs CIMD)

### Todo folded into this phase
- `.planning/todos/pending/2026-09-28-remove-temporary-claude-ssh-access-before-real-bank-data.md`

### Existing code, config and docs to extend
- `Ledger.Service/Program.cs`: authentication setup (API-key scheme), fallback authenticated policy, forwarded headers with `ReverseProxy:KnownProxies`, ops endpoint. The MCP endpoint and the bearer-token scheme get wired in here.
- `deploy/traefik/ledger.yml.example`: the router, allowlist and security-header template. Add the public MCP router and its Anthropic-range allowlist with placeholders.
- `deploy/nftables/ledger.nft.in`: the LXC firewall. The app port accepts traffic only from Traefik.
- `deploy/provisioning/grafana/provisioning/alerting/*.yaml`: contact point, notification policy and rule format for the rejected-token alert
- `docs/rest-api.md`, `docs/monitoring.md`: extend with MCP connection steps and the new metrics and alert
- `deploy/bin/ledger-selfcheck`: the SSH-removal check, plus any new MCP or exposure checks

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `Ledger.Service/Auth/ApiKeyAuthenticationHandler.cs` and the fallback "require authenticated user" policy: `/mcp` adds a bearer-token scheme beside the API-key scheme. The API-key scheme must not be accepted on `/mcp` (D-05).
- `Ledger.Service/Cli/ApiKeyCommand.cs` with `deploy/bin/ledger-apikey`: the pattern for the revoke-all CLI (D-07). It's dispatched from `Program.cs` before the host starts.
- `Ledger.Service/Metrics/LedgerMetrics.cs` and `SyncMetrics.cs` (prometheus-net, static): add the MCP call and rejected-token metrics alongside them.
- `Ledger.Service/Ingestion/IngestionOptions.cs` (`TimeZone = "Europe/Amsterdam"`) and the `TimeProvider` used by the sync services: reuse them for relative-period resolution (D-11).
- `Ledger.Domain/Ingestion/ILedgerStore.cs`: already places an undated pending row on the local day it was first seen, in a given zone. Totals and search use the same rule.
- `Ledger.Domain/Ingestion/TextNormalizer.cs` (`ForMatching`): a candidate for normalising counterparty names and filters in the find-counterparties and totals tools.
- `Ledger.Repository/Entities/LedgerAccountEntity.cs` (`Iban`, `DisplayName`, `AccountKey`, `SyncEnabled`) and `LedgerTransactionEntity.cs` (`BookingDate`, `Amount`, `CounterpartyIban`, `Status`): enough to compute D-10's exclusion and D-14's masking on the server.
- `reporting.transactions` view (migration `20260930181840_AddLedgerIngestion`): its effective date is booking date first. It is Grafana's contract, though. MCP reads go through the app's own application layer and stores as the runtime role, not through the reporting views.

### Established Patterns
- Layering: `Ledger.Domain` holds query and aggregation rules (no EF or HTTP), `Ledger.Repository` the EF queries, `Ledger.Service` the MCP tools as thin adapters over application services (ARCHITECTURE Pattern 1 and Anti-Pattern 2). REST and MCP share the same application layer.
- Money stays `decimal` / `numeric` end to end, and aggregation happens in SQL or the domain, never in Claude.
- Opaque keys in metrics and views. No IBAN, amount or counterparty ever goes into labels, logs or exceptions (SEC-06).
- `packages.lock.json` in every project with locked-mode restore. Adding `ModelContextProtocol.AspNetCore`, and any OAuth or JWT packages, needs lock-file updates.
- Tests: xUnit on Microsoft.Testing.Platform, FluentAssertions, NSubstitute, trait categories as CI filters. Real-database tests run against the user's local PostgreSQL container via user-secrets, and a service container in CI.
- Host-side scripts have offline logic tests in `deploy/tests/`. `build/lint.sh` runs actionlint, zizmor, shellcheck and gitleaks.

### Integration Points
- Traefik (another container): a new public router for the dedicated MCP hostname (D-06). The app trusts forwarded headers only from the configured known proxy. The OAuth issuer and resource metadata must show the public HTTPS URL, not the internal one (Pitfall 9).
- The env file (`deploy/ledger.env.example` holds placeholders only): the public MCP base URL and issuer, the token-signing or authorization-server settings, and any client-registration settings.
- If research chooses Authentik: it becomes a new service in the LXC that provisioning has to install, plus a backup and secret-custody story. Its memory footprint matters on the low-power host.
- Prometheus scrapes the loopback ops endpoint. Grafana alerting adds the rejected-token alert to the operator contact point.

</code_context>

<specifics>
## Specific Ideas

- The user's DNS provider already has public records for the dashboard and internal API names, because the certificates needed them. Traefik's LAN/VPN allowlist answers outside requests to those names with 403. The user expected those names to be restricted from outside, and they are. This phase's outside-in check should prove it again for the existing names as well as the new MCP name.
- The user leaned towards reusing the existing API hostname and asked for advice. After hearing the trade-off (path-scoped routers on a shared name vs separation at the hostname level), they chose a new dedicated name.
- On untrusted marking, the user's first question was whether marking is even the right defence. The honest answer: it is a partial measure, and the real defences are structural and arrive with the write tools. With that, they chose to wait.
- Usage tip, not a build item: until Phase 4's injection defences land, keep ledger conversations in claude.ai separate from mail and calendar connectors.
- An example answer shape for D-12: "€210 spent, €40 refunded, €170 net, at 3 merchants, 14 booked transactions, 1 Aug–31 Aug; 1 pending item of €12.50 not included; data as of this morning's sync".

</specifics>

<deferred>
## Deferred Ideas

- **Partner access through ChatGPT's MCP connectors.** It would need OpenAI's egress ranges, accepting OpenAI as a second AI provider that receives transaction data, and testing ChatGPT's connector OAuth. The login model leaves room for a second user. Revisit after v1.
- **A period-comparison tool** (month over month, year over year). It belongs to Phase 4 with PLAN-09.
- **Untrusted-data marking for bank text, including the light version.** Phase 4 with ADV-07, per D-15.
- **General own-account transfer detection** (unsynced savings, personal accounts). Phase 4 with CAT-02, building on D-10.

### Reviewed Todos (not folded)
- **Confirm the first consent renewal keeps all history** (`.planning/todos/pending/2026-10-07-confirm-first-consent-renewal-keeps-history.md`). It can only run at the first renewal, around March 2027. It matched on keywords only and is not MCP work.
- **Confirm the first real pending payments book as single rows** (`.planning/todos/pending/2026-10-07-confirm-pending-payments-book-as-single-rows.md`). This verifies ingestion, independent of this phase.
- **Confirm the first real household alert email arrives without financial detail** (`.planning/todos/pending/2026-10-07-confirm-first-real-alert-email.md`). This verifies monitoring, independent of this phase.

</deferred>

---

*Phase: 03-claude-reads-the-ledger*
*Context gathered: 2026-10-07*
