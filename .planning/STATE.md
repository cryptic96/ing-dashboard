---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
current_phase: 03
current_phase_name: claude-reads-the-ledger
status: executing
stopped_at: Phase 3 context gathered
last_updated: "2026-10-07T21:29:12.007Z"
last_activity: 2026-10-07
last_activity_desc: Phase 2 complete, transitioned to Phase 3
progress:
  total_phases: 3
  completed_phases: 2
  total_plans: 36
  completed_plans: 34
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-09-26)

**Core value:** Claude can serve as a trustworthy financial advisor for the household, because it has complete, correctly categorised transaction data, budgets, goals and a shared advisor memory to reason over.
**Current focus:** Phase 03 — claude-reads-the-ledger

## Current Position

Phase: 03 (claude-reads-the-ledger) — EXECUTING
Plan: 7 of 8
Status: Ready to execute
Last activity: 2026-10-07 — Phase 03 execution started

Progress: [░░░░░░░░░░] 0%

## Performance Metrics

**Velocity:**

- Total plans completed: 28
- Average duration: -
- Total execution time: 0.0 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01 | 12 | - | - |
| 2 | 16 | - | - |

**Recent Trend:**

- Last 5 plans: -
- Trend: -

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- Roadmap: 6 vertical MVP phases; each one delivers something the household can see in Grafana or ask Claude about.
- Roadmap: each security control ships in the phase that first needs it (platform hardening before real bank data; OAuth, audience validation and IP allowlist together with the public MCP endpoint; audit and undo together with the first write tools). Phase 6 adds only a final verification pass.
- Roadmap: Grafana grows with each slice (lockdown in Phase 1, first data dashboard and reporting-view contract in Phase 2) instead of getting a separate dashboards phase.
- Roadmap: categorisation (Phase 4) comes before any planning feature (Phase 5), so budgets and forecasts never rest on dirty data.
- Phase 1 context: PostgreSQL inside the app LXC (Unix socket only, peer auth, runtime/migrator/reader roles) replaces the shared MS SQL Server; local tests use the user's own Postgres container.
- Phase 1 context: pull-based deploys, no self-hosted runner. Approval publishes the release; a timer on the LXC pulls it and a root-owned installer verifies, migrates and restarts. Tags only on `main`.
- Phase 1 context: backups local inside the LXC, encrypted to a public key (private key off-server); losing the SSD loses data and backups (accepted risk, offsite deferred).

### Pending Todos

- [minor] Harden privileged units and scan logs for secrets — `.planning/todos/pending/2026-09-29-harden-privileged-units-and-scan-logs-for-secrets.md`
- [major] Remove temporary Claude SSH access to the ledger host before /mcp goes public (kept through bank sync by operator decision; key is passphrase-protected) — `.planning/todos/pending/2026-09-28-remove-temporary-claude-ssh-access-before-real-bank-data.md`
- [minor] Migrate tests to Microsoft.Testing.Platform for xunit v4 (after go-live) — `.planning/todos/pending/2026-09-28-migrate-tests-to-microsoft-testing-platform-for-xunit-v4.md`

### Blockers/Concerns

- Phase 2: It is unconfirmed whether Enable Banking's ING NL consent covers the savings accounts. Run the spike first; Salt Edge is the fallback.
- Phase 3: The OAuth server choice is open (separate Authentik vs embedded lightweight server). Settle it in phase research; it depends on claude.ai client-registration requirements and the single-LXC resource budget.
- Phase 3: If the Anthropic IP allowlist also covers the browser-facing authorize/login step, connecting claude.ai only works from the home network or VPN. Confirm that is acceptable and that refresh-token lifetimes keep re-authorisation rare.
- Phase 6: The research summary favoured the app calling the Messages API for reviews. The household decided on Claude-side scheduling (PROJECT.md), and that decision stands. Phase research should only pick the Claude-side scheduler.

### Quick Tasks Completed

| # | Description | Date | Commit | Directory |
|---|-------------|------|--------|-----------|
| 261007-vrj | Encrypt TOTP authenticator secrets at rest with the Data Protection key ring; operator commands share the real key ring | 2026-10-07 | 3eb9648 | [261007-vrj-encrypt-totp-authenticator-secrets-at-re](./quick/261007-vrj-encrypt-totp-authenticator-secrets-at-re/) |
| 261008-0av | Encrypt the proxy-to-application hop: HTTPS on 5080 with a host-generated certificate pinned by Traefik | 2026-10-08 | 6b9bf5e | [261008-0av-encrypt-the-proxy-to-app-hop-with-a-pinn](./quick/261008-0av-encrypt-the-proxy-to-app-hop-with-a-pinn/) |

## Deferred Items

Items acknowledged and carried forward from previous milestone close:

| Category | Item | Status | Deferred At |
|----------|------|--------|-------------|
| *(none)* | | | |

## Session Continuity

Last session: 2026-10-07T13:17:52.776Z
Stopped at: Phase 3 context gathered
Resume file: .planning/phases/03-claude-reads-the-ledger/03-CONTEXT.md
