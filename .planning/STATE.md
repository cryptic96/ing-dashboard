---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
current_phase: 4
current_phase_name: Trustworthy Categorisation
status: planning
stopped_at: Phase 3 complete; Phase 4 ready to plan
last_updated: "2026-10-09T07:38:09.307Z"
last_activity: 2026-10-09
last_activity_desc: Phase 3 complete, transitioned to Phase 4
progress:
  total_phases: 3
  completed_phases: 3
  total_plans: 36
  completed_plans: 36
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-09-26)

**Core value:** Claude can serve as a trustworthy financial advisor for the household, because it has complete, correctly categorised transaction data, budgets, goals and a shared advisor memory to reason over.
**Current focus:** Phase 04 — trustworthy-categorisation (ready to plan)

## Current Position

Phase: 4 — Trustworthy Categorisation
Plan: Not started
Status: Ready to plan
Last activity: 2026-10-09 — Phase 3 complete, transitioned to Phase 4

Progress: [░░░░░░░░░░] 0%

## Performance Metrics

**Velocity:**

- Total plans completed: 36
- Average duration: -
- Total execution time: 0.0 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01 | 12 | - | - |
| 2 | 16 | - | - |
| 3 | 8 | - | - |

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

- [major] Connect the hosted Claude clients (claude.ai web, mobile, Desktop chats) once a household subscription allows custom connectors — `.planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md`
- [minor] Encrypt the reverse-proxy-to-Grafana hop like the application hop — `.planning/todos/pending/2026-10-08-encrypt-the-proxy-to-grafana-hop.md`
- [minor] Confirm the first real pending payments book as single rows — `.planning/todos/pending/2026-10-07-confirm-pending-payments-book-as-single-rows.md` (a balance-reconciliation alert fired 2026-10-09 06:34 Amsterdam time; investigate)
- [minor] Confirm the first real household alert email — `.planning/todos/pending/2026-10-07-confirm-first-real-alert-email.md`
- [minor] Confirm the first consent renewal keeps history — `.planning/todos/pending/2026-10-07-confirm-first-consent-renewal-keeps-history.md`

### Blockers/Concerns

- Phase 3 follow-up: claude.ai web/mobile/Desktop chats need a household Claude subscription with custom connectors (work-organisation account disables them); public route stays closed until then (see hosted-clients todo).
- Phase 6: The research summary favoured the app calling the Messages API for reviews. The household decided on Claude-side scheduling (PROJECT.md), and that decision stands. Phase research should only pick the Claude-side scheduler.

### Quick Tasks Completed

| # | Description | Date | Commit | Directory |
|---|-------------|------|--------|-----------|
| 261007-vrj | Encrypt TOTP authenticator secrets at rest with the Data Protection key ring; operator commands share the real key ring | 2026-10-07 | 3eb9648 | [261007-vrj-encrypt-totp-authenticator-secrets-at-re](./quick/261007-vrj-encrypt-totp-authenticator-secrets-at-re/) |
| 261008-0av | Encrypt the proxy-to-application hop: HTTPS on 5080 with a host-generated certificate pinned by Traefik | 2026-10-08 | 6b9bf5e | [261008-0av-encrypt-the-proxy-to-app-hop-with-a-pinn](./quick/261008-0av-encrypt-the-proxy-to-app-hop-with-a-pinn/) |
| 261008-evv | Remove the integration-test free-port race: test hosts keep their bound sockets until Kestrel takes them | 2026-10-08 | df423d6 | [261008-evv-bind-test-hosts-to-port-zero-to-remove-t](./quick/261008-evv-bind-test-hosts-to-port-zero-to-remove-t/) |

## Deferred Items

Items acknowledged and carried forward from previous milestone close:

| Category | Item | Status | Deferred At |
|----------|------|--------|-------------|
| *(none)* | | | |

## Session Continuity

Last session: 2026-10-07T13:17:52.776Z
Stopped at: Phase 3 complete (UAT 3/3, security 64/64 closed); Phase 4 ready to plan
Resume file: None
