---
phase: 03-claude-reads-the-ledger
verified: 2026-10-08T00:00:00Z
status: passed
score: 3/5 must-haves verified
behavior_unverified: 2
overrides_applied: 1
overrides:
  - truth: "SC1 hosted clients (claude.ai web, mobile, Desktop chats) and the allow side of SC5 observed live"
    decision: "Deferred by the operator on 2026-10-09 to .planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md: the household has no Claude subscription with custom connectors yet. Everything else verified in code, tests and on the live host; UAT 3/3 runnable tests passed."
    requirements_left_open: [ADV-10]
behavior_unverified_items:
  - truth: "SC1 - The user adds the ledger as a custom connector in claude.ai, signs in through OAuth 2.1, and gets answers on web and mobile (Desktop chats too)"
    test: "With a household Claude subscription that allows custom connectors, reopen the public MCP route (ledger-mcp-public on ledger-mcp-allow) and add the connector in claude.ai with client ID ledger-claude-hosted and an empty secret; sign in from home or VPN; ask a how-much question on web, then on the mobile app and in a Desktop chat"
    expected: "Each client lists the four tools and answers from the real transactions with period, count and exclusions stated; no separate setup for mobile or Desktop"
    why_human: "Never exercised with a hosted client. claude.ai could not add the connector (work-organisation account disables custom connectors). Hosted-client behaviour (redirect URI https://claude.ai/api/mcp/auth_callback, callback through Anthropic's range, token refresh from Anthropic's servers) is covered only by synthetic-client tests"
  - truth: "SC5 - From outside the network only /mcp and its OAuth endpoints respond, only to Anthropic's published ranges; OAuth discovery works from outside"
    test: "During the hosted connect above, read the reverse proxy access log: protected-resource document, authorization-server metadata, /connect/token and /mcp answered 200 to 160.79.104.0/21 addresses; /connect/authorize reached only from home/VPN. Run build/check-exposure.sh --from outside once more with the public router open"
    expected: "200 from the Anthropic range on the four public paths, 403 from any other outside address, 404 elsewhere"
    why_human: "The negative half was proven live (phone on mobile data: every ledger path 403, unknown path 404, route then closed). The positive half (Anthropic's servers actually reaching discovery, token and /mcp through the public router) was never observed; the route is closed again today"
human_verification:
  - test: "Connect claude.ai web, mobile and Desktop through the public route (see behavior_unverified_items; sequence is in .planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md)"
    expected: "Answers from the real ledger on all three; sudo ledger-grants revoke-all makes the next call fail and reconnecting from home/VPN restores access; new-grant alert mail carries no financial detail"
    why_human: "Needs a Claude subscription with custom connectors and Anthropic's real egress addresses"
  - test: "Add the 'In practice' part to docs/mcp.md after the hosted run"
    expected: "What the hosted clients, including mobile, actually showed"
    why_human: "Can only be written from observation"
---

# Phase 3: Claude Reads the Ledger Verification Report

**Phase Goal:** Claude can answer accurate "how much" and "what did we spend" questions from the household's real transactions, on desktop, in Claude Code and on claude.ai web and mobile, through a securely exposed MCP endpoint.
**Verified:** 2026-10-08
**Status:** passed with a recorded deferral (was human_needed on 2026-10-08)

> **Update 2026-10-09:** UAT passed 3/3 runnable tests (sign-in pages on desktop and phone, kill switch and re-authorisation with Claude Code, new-grant alert email). The hosted-client checks were moved to `.planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md` by operator decision and recorded as an override in the frontmatter; ADV-10 stays open until that todo is done. The phase security audit (03-SECURITY.md) closed all 64 threats.
**Re-verification:** No - initial verification

## Verdict in one paragraph

Everything that can be proven in the repository is implemented, wired and tested, and the Claude Code path was proven on the live v0.3.0 host from the home network (operator-confirmed against the bank's app). The goal as worded, however, includes claude.ai web and mobile through the public endpoint, and that was never observed: no hosted client has ever connected, and the public route is currently closed. This is not a missing implementation (no gap in code), so the status is `human_needed`, not `gaps_found`. The phase goal is NOT fully achieved until the hosted-client run in the todo is done; do not tick ADV-10 (and treat SEC-04's "restricted to Anthropic's ranges" as only half observed) in REQUIREMENTS.md before then.

## Goal Achievement

### Observable Truths (ROADMAP success criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | User adds the ledger as a custom connector in claude.ai, signs in via OAuth 2.1, gets answers on web and mobile; Desktop and Claude Code reach the same tools from home/VPN | PRESENT_BEHAVIOR_UNVERIFIED (partial) | Claude Code (desktop Code tab, `.mcp.json`, client id `ledger-claude-code`) verified on live host: overview, money_totals (3 calls), search; metrics show 1 grant, no rejected tokens; operator confirmed the three answers against the bank app (03-07-SUMMARY). Hosted client registration `ledger-claude-hosted` with redirect `https://claude.ai/api/mcp/auth_callback` exists and is tested (`EndpointBoundaryTests.The_hosted_client_receives_its_code_at_its_registered_callback_and_can_exchange_it`, `A_redirect_address_that_is_not_registered_...`). claude.ai web/mobile/Desktop chats never connected (03-08-SUMMARY: blocked by organisation policy; todo `2026-10-08-connect-hosted-claude-clients.md`) |
| 2 | Small intent-shaped tool set; "how much" answered from a server-side total stating range, filters and transaction count | VERIFIED | `Ledger.Service/Mcp/LedgerTools.cs`: exactly four tools (`ledger_overview`, `money_totals`, `search_transactions`, `find_counterparties`), all ReadOnly/Idempotent/non-destructive/closed-world, each a thin call into `LedgerQueryService`; `money_totals` description routes every how-much question to it; search rows carry no sums. Provenance (resolved dates, keyword, zone, filters, booked count, pending and own-transfer exclusions, per-account last sync) in `TotalsResult.cs`/`LedgerQueryService.TotalsAsync`. Tests: `TotalsQueryTests`, `TotalsAggregatorTests`, `ToolSurfaceTests`, `ToolCatalogTests` pass |
| 3 | Amsterdam days/months/years: month-end late evening and DST changeover land correctly | VERIFIED | `PeriodResolver` uses `SyncSchedule.LocalDate(utcNow, zone)`; `PeriodDate.Of` uses bank dates as-is and converts only first-seen into the zone, never via UTC. Tests: `PeriodResolverTests` (just-after-midnight with UTC still previous day; 2026-03-29, 2026-10-25, 2027-03-28 both sides of the change; pending row 23:30 vs 00:30 Amsterdam at the August/September boundary), integration seed rows for the clock-change days with day grouping 24/25/26 Oct |
| 4 | Full year of transactions searched: paginated, server-side cap, explicit truncation | VERIFIED | `LedgerQueryService.SearchAsync`: `MaxSearchLimit = 100`, default 50, `Math.Clamp`, `limitClamped` reported, `truncated`/`nextCursor`/`matchingTotal` returned, cursor bound to filter hash. `SearchPaginationTests.A_year_of_rows_is_walked_in_twelve_pages_that_show_each_row_once_in_a_stable_order` (1,200 rows), plus exact-fit, clamp, tie-order and bad-cursor tests pass |
| 5 | Outside the network only /mcp and OAuth endpoints respond, only to Anthropic's ranges; discovery works from outside; wrong-audience token rejected; token never forwarded | PRESENT_BEHAVIOR_UNVERIFIED (partial) | Implemented and tested: `deploy/traefik/ledger.yml.example` (exact-path public router with `ledger-mcp-allow` = 160.79.104.0/21 + LAN/VPN; `/connect/authorize` and `/account/` on LAN/VPN only; template unit test; network test against Anthropic's page, PASS on go-live day); in-app `PublicHostGuard` and sign-in network gate (`EndpointBoundaryTests`); audience via `validation.AddAudiences(options.ResourceUrl)` with `TokenRejectionTests.A_token_issued_for_another_resource_is_counted_as_wrong_audience` and `EndpointBoundaryTests.A_token_issued_by_another_instance_for_another_resource_is_rejected_at_the_mcp_endpoint`; `No_outgoing_request_leaves_the_app_while_it_serves_tool_calls`, no `HttpClient` use in Mcp/Queries/OAuth. Live: phone on mobile data, every ledger path 403, unknown 404 (03-08-SUMMARY); inside `check-exposure.sh` 12 PASS / 0 FAIL. NOT observed: Anthropic's servers reaching discovery/token//mcp through the public router (route closed again) |

**Score:** 3/5 truths verified (2 present and tested but not exercised live with a hosted client)

### Requirements Coverage

All six IDs appear in PLAN frontmatter (03-01: ADV-10, SEC-04, ADV-01; 03-02: SEC-04, ADV-10; 03-03: ADV-02, OPS-06, ADV-01; 03-04: ADV-03, ADV-01, ADV-02; 03-05: SEC-04, ADV-10; 03-06: SEC-04, ADV-10; 03-07: all six; 03-08: ADV-10, SEC-04). REQUIREMENTS.md maps exactly these six to Phase 3; no orphans.

| Requirement | Status | Evidence |
|-------------|--------|----------|
| ADV-01 small set of intent-shaped tools (roughly 8-15) | SATISFIED for this phase | Four tools, intent-shaped, no per-table tools. The "8-15" figure is a milestone-wide band (03-CONTEXT keeps the milestone inside it; comparison, categorisation, budgets tools come in Phases 4-6). Note the number only reaches the band after later phases; do not read four as 8-15 |
| ADV-02 server-side aggregation with provenance | SATISFIED | Truth 2 |
| ADV-03 paginated search, cap, explicit truncation | SATISFIED | Truth 4 |
| OPS-06 Europe/Amsterdam bucketing | SATISFIED | Truth 3 (note: Phase 3 covers the query/MCP bucketing; Grafana/ingestion bucketing from earlier phases is out of scope here) |
| SEC-04 only /mcp + OAuth public, Anthropic ranges, audience-validated, no passthrough | SATISFIED in code/config/tests; live positive path unobserved | Truth 5. Audience, no-passthrough, exact-path routers, range check and outside 403/404 proven; Anthropic-range 200s never seen |
| ADV-10 Desktop/Claude Code on home/VPN; claude.ai web and mobile via public /mcp with OAuth 2.1 | PARTIAL | Claude Code from home proven live. claude.ai web and mobile through the public endpoint never connected. Needs human step |

### Required Artifacts / Key Links

| Artifact | Status | Details |
|----------|--------|---------|
| `Ledger.Service/Mcp/LedgerTools.cs`, `McpEndpoint.cs`, `McpMetrics.cs`, `ServerInstructions.cs` | VERIFIED | Substantive, wired to `LedgerQueryService`, tool call metrics |
| `Ledger.Service/Queries/*`, `Ledger.Domain/Queries/*` (PeriodResolver, PeriodDate, TotalsAggregator, SearchCursor), `Ledger.Repository/Stores/LedgerQueryStore.cs` | VERIFIED | Real DB-backed queries, tested against local PostgreSQL |
| `Ledger.Service/OAuth/*`, `Pages/Account`, `Pages/Connect`, `Hosting/PublicHostGuard.cs`, `RequestLimits.cs`, `ProductionConfigurationValidator.cs` | VERIFIED | OpenIddict embedded, TOTP with replay guard, consent, host/path allow-list, limits |
| `deploy/traefik/ledger.yml.example`, `build/check-exposure.sh`, `deploy/bin` (ledger-login, ledger-grants, selfcheck), `docs/mcp.md` | VERIFIED | Present, lint and script tests pass; docs honestly state hosted clients are not yet proven (no "In practice" claims) |
| Level 4 data flow | FLOWING | Totals and search come from EF queries over real ledger tables; no static returns found |

### Behavioral Spot-Checks and Probes

| Check | Command | Result | Status |
|-------|---------|--------|--------|
| MCP namespace tests (unit + integration, real local PostgreSQL) | `dotnet test --solution Ledger.slnx --filter-namespace "*Mcp*"` | 356 passed, 0 failed, 0 skipped | PASS |
| Repo lint | `bash build/lint.sh` | repo-rules, workflows, shell, secrets, script-tests, observability all PASS | PASS |
| Full suite | not re-run by the verifier | Orchestrator reports 1139 tests, 0 failed, 2 skipped at HEAD | accepted, not independently re-run |
| Live host / hosted client | n/a | No host access by design | SKIP, human item |

### Review follow-up

03-REVIEW.md criticals (TOTP replay by spelling; Deny-button approve) and all warnings except A-WR-06 were fixed with named regression tests (03-REVIEW-FIX.md, 15 of 15 fixed); A-WR-06 (plain-HTTP proxy hop) was fixed before release in quick task 261008-0av. Remaining info items are explicitly out of the operator's chosen fix scope.

### Anti-Patterns

- No TBD/FIXME/XXX in tracked non-planning files; no `//` comments in tracked source; no planning identifiers or planning document names outside `.planning/` (CLAUDE.md hard rules respected; the only hits are CLAUDE.md's own rule text).
- Minor known cosmetic issue (not a blocker): protected-resource document lists `"header"` twice in `bearer_methods_supported` (03-07-SUMMARY).

### Gaps

No code gaps. No FAILED truths.

### Human Verification Required

1. **Connect claude.ai web, mobile and Desktop chats.** Needs a household Claude subscription that allows custom connectors. Follow `.planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md`: re-check Anthropic's range, switch `ledger-mcp-public` to `ledger-mcp-allow`, run the outside and inside exposure checks (0 FAIL each), add the connector with client ID `ledger-claude-hosted` and an empty secret, sign in from home/VPN, ask a how-much question on each client. Expected: tool list of four, answer states period, count and exclusions, correct against the bank app.
2. **Observe the public path from Anthropic's addresses.** In the proxy access log, confirm 200s from 160.79.104.0/21 on the protected-resource document, authorization-server metadata, `/connect/token` and `/mcp`, and `/connect/authorize` only from home/VPN addresses.
3. **Revoke and reconnect with a hosted client.** `sudo ledger-grants revoke-all`, confirm the next hosted call fails, reconnect from home/VPN, note what the mobile app shows; confirm the new-grant alert email carries no financial detail.
4. **Record the outcome** in `docs/mcp.md` ("In practice") and then tick ADV-10 (and SEC-04's live evidence) in REQUIREMENTS.md.

Until then, closing the phase as "complete" overstates it: Claude Code on home/VPN is the only proven client.

---

_Verified: 2026-10-08_
_Verifier: Claude (gsd-verifier)_
