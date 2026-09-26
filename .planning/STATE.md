---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
current_phase: 1
current_phase_name: Secure Platform & Release Pipeline
status: planning
stopped_at: Phase 1 context gathered
last_updated: "2026-09-26T22:57:34.796Z"
last_activity: 2026-09-26
last_activity_desc: Roadmap created (6 phases, 64/64 v1 requirements mapped)
progress:
  total_phases: 1
  completed_phases: 0
  total_plans: 0
  completed_plans: 0
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-09-26)

**Core value:** Claude can serve as a trustworthy financial advisor for the household, because it has complete, correctly categorised transaction data, budgets, goals and a shared advisor memory to reason over.
**Current focus:** Phase 1: Secure Platform & Release Pipeline

## Current Position

Phase: 1 of 6 (Secure Platform & Release Pipeline)
Plan: 0 of TBD in current phase
Status: Ready to plan
Last activity: 2026-09-26 — Roadmap created (6 phases, 64/64 v1 requirements mapped)

Progress: [░░░░░░░░░░] 0%

## Performance Metrics

**Velocity:**

- Total plans completed: 0
- Average duration: -
- Total execution time: 0.0 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| - | - | - | - |

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

None yet.

### Blockers/Concerns

- Phase 2: It is unconfirmed whether Enable Banking's ING NL consent covers the savings accounts. Run the spike first; Salt Edge is the fallback.
- Phase 3: The OAuth server choice is open (separate Authentik vs embedded lightweight server). Settle it in phase research; it depends on claude.ai client-registration requirements and the single-LXC resource budget.
- Phase 3: If the Anthropic IP allowlist also covers the browser-facing authorize/login step, connecting claude.ai only works from the home network or VPN. Confirm that is acceptable and that refresh-token lifetimes keep re-authorisation rare.
- Phase 6: The research summary favoured the app calling the Messages API for reviews. The household decided on Claude-side scheduling (PROJECT.md), and that decision stands. Phase research should only pick the Claude-side scheduler.

## Deferred Items

Items acknowledged and carried forward from previous milestone close:

| Category | Item | Status | Deferred At |
|----------|------|--------|-------------|
| *(none)* | | | |

## Session Continuity

Last session: 2026-09-26T22:57:34.786Z
Stopped at: Phase 1 context gathered
Resume file: .planning/phases/01-secure-platform-release-pipeline/01-CONTEXT.md
