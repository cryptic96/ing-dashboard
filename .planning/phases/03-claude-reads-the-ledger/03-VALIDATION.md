---
phase: 3
slug: claude-reads-the-ledger
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-07
---

# Phase 3 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 on Microsoft.Testing.Platform, FluentAssertions, NSubstitute (unit); `Microsoft.AspNetCore.Mvc.Testing` + the user's local PostgreSQL container (integration); bash logic tests under `deploy/tests/` |
| **Config file** | `global.json` (`"runner": "Microsoft.Testing.Platform"`), `Ledger.IntegrationTests/xunit.runner.json` |
| **Quick run command** | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj` |
| **Full suite command** | `dotnet test --solution Ledger.slnx --no-restore` (needs `ConnectionStrings__TestAdmin` and `LEDGER_EFBUNDLE`, as CI sets them) and `build/lint.sh` |
| **Trait filter** | `dotnet test --solution Ledger.slnx --filter-trait "Category=<X>" --ignore-exit-code 8` |
| **Estimated runtime** | ~30 s unit, ~180 s full suite |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj` plus the relevant `--filter-trait` integration category once its task lands
- **After every plan wave:** Run `dotnet test --solution Ledger.slnx --no-restore` and `build/lint.sh`
- **Before `/gsd-verify-work`:** Full suite green, `deploy/tests/*-test.sh` green, selfcheck 0 failures on the host
- **Max feedback latency:** 180 seconds

---

## Per-Task Verification Map

Filled in by the planner/executor per task. Requirement → test coverage from research:

| Requirement | Behavior | Test Type | Automated Command | File Exists | Status |
|-------------|----------|-----------|-------------------|-------------|--------|
| OPS-06 | Month-end, last evening, just after midnight, DST changeover weekends (2026-03-29, 2026-10-25, 2027-03-28, 2027-10-31), ISO weeks | unit | `--filter-trait "Category=Periods"` | ❌ W0 `Ledger.UnitTests/Mcp/PeriodResolverTests.cs` | ⬜ pending |
| OPS-06 | Pending row first seen 23:30 local on the last day of a month lands on that day | integration | `--filter-trait "Category=Totals"` | ❌ W0 `Ledger.IntegrationTests/Mcp/TotalsQueryTests.cs` | ⬜ pending |
| ADV-02 | Totals: booked only, pending/dropped/own-account transfers handled, out/in/net, per-counterparty breakdown with remainder, currency grouping, provenance (range, filters, count) | integration | `--filter-trait "Category=Totals"` | ❌ W0 | ⬜ pending |
| ADV-03 | Year of >1,000 synthetic rows: page cap, `truncated` flag, cursor continuity, filter-bound cursor | integration | `--filter-trait "Category=Search"` | ❌ W0 `Ledger.IntegrationTests/Mcp/SearchPaginationTests.cs` | ⬜ pending |
| ADV-01 | Tool catalogue: intent-shaped names, read-only annotations, descriptions free of planning references and personal names | unit | `--filter-trait "Category=McpTools"` | ❌ W0 `Ledger.UnitTests/Mcp/ToolCatalogTests.cs` | ⬜ pending |
| ADV-10 / SEC-04 | In-process OAuth: authorize (password + TOTP) → code → token → `/mcp` tool call; discovery documents; protected-resource metadata; S256; `offline_access` | integration | `--filter-trait "Category=OAuth"` | ❌ W0 `Ledger.IntegrationTests/Mcp/OAuthFlowTests.cs` | ⬜ pending |
| SEC-04 | Wrong-audience token → 401; expired → 401; API key on `/mcp` → 401; bearer on `/api/*` → 401; no outbound HTTP during a tool call | integration | `--filter-trait "Category=OAuth"` | ❌ W0 | ⬜ pending |
| SEC-04 | Refresh rotation; reuse outside leeway revokes the grant; revoke-all CLI kills the next call; tokens survive restart | integration | `--filter-trait "Category=OAuth"` | ❌ W0 | ⬜ pending |
| SEC-04 | Host/path guard: MCP hostname answers only MCP/OAuth paths; sign-in paths reject non-LAN/VPN clients; metadata ignores spoofed Host headers | integration | `--filter-trait "Category=OAuth"` | ❌ W0 | ⬜ pending |
| SEC-04 | Traefik template: exact paths only, Anthropic range present, phased-out addresses absent, placeholders only | unit | `--filter-trait "Category=Configuration"` | extend `CommittedConfigurationTests` | ⬜ pending |
| SEC-06 | No bearer token, tool argument or counterparty text in captured logs | integration | `--filter-trait "Category=Security"` | extend `LogRedactionTests.cs` | ⬜ pending |
| Metrics/alert | Metrics present at zero from startup; opaque labels; alert rule parses | unit + config | `--filter-trait "Category=Metrics"` | ❌ W0 | ⬜ pending |
| Selfcheck | Fails when an unexpected login has sudo | shell | `bash deploy/tests/selfcheck-logic-test.sh` | extend existing | ⬜ pending |
| Exposure | Outside-in check expected-status matrix | shell | `bash deploy/tests/exposure-check-logic-test.sh` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `Ledger.UnitTests/Mcp/PeriodResolverTests.cs`, `ToolCatalogTests.cs` — OPS-06, ADV-01
- [ ] `Ledger.IntegrationTests/Mcp/TotalsQueryTests.cs`, `SearchPaginationTests.cs`, `OAuthFlowTests.cs` plus a synthetic-ledger seeding helper (a large year of rows, own-account transfers, pending/dropped rows) — ADV-02, ADV-03, ADV-10, SEC-04
- [ ] Test OAuth driver: signs in a synthetic user with a computed TOTP code, runs the PKCE code flow against the in-process host, returns a bearer for the MCP SDK client
- [ ] No new framework install; lock files regenerate when packages are added

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| claude.ai custom connector connects via OAuth and answers from real transactions on web and mobile | ADV-10 | Needs the real claude.ai and Anthropic's network | Add connector from a LAN/VPN desktop browser, sign in, ask a totals question on web, then on mobile |
| Claude Desktop connector and Claude Code reach the same tools | ADV-10 | Real clients | Desktop: same connector; Claude Code on LAN/VPN with `--client-id` |
| Only `/mcp` and OAuth endpoints answer, only to Anthropic's range; discovery reachable from Anthropic | SEC-04 | Allowlist can only be proven from Anthropic's range | Outside-in script from mobile data (expect 403/404), plus Traefik log lines showing 200 from `160.79.104.0/21` during connect |
| Behaviour on revoked grant and mobile re-auth | SEC-04 | claude.ai runtime behaviour | Run kill switch mid-session, observe claude.ai prompting re-auth |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 180s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
