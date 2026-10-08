---
phase: 03-claude-reads-the-ledger
plan: 08
subsystem: go-live
tags: [ssh-removal, exposure, traefik, claude-ai, docs]
status: partial
requires: [03-07]
provides:
  - Claude's temporary SSH access removed and verified gone (hard gate before any internet-facing surface)
  - Public route proven safe from outside, then deliberately closed again (no hosted client can use it yet)
  - Connection guide covers the desktop Code tab (.mcp.json) and organisation-managed accounts
affects: [phase-03-verification]
tech-stack:
  added: []
  patterns:
    - "Open the public MCP route only when a hosted Claude client will use it"
key-files:
  created:
    - .planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md
  modified:
    - docs/mcp.md
    - .planning/todos/completed/2026-09-28-remove-temporary-claude-ssh-access-before-real-bank-data.md
key-decisions:
  - "The operator keeps using the paid work-organisation Claude account with Claude Code from home/VPN; hosted clients (claude.ai web, mobile, Desktop chats) are deferred until a household subscription with custom connectors exists"
  - "Because nothing can use it yet, the operator closed the public route again (MCP routers back on the home/VPN list)"
requirements-completed: []
duration: "~1.5h"
completed: 2026-10-08
actuals:
  tasks: 3
  tasks_completed: 1
  tasks_partial: 2
---

# Plan 03-08: Go live — partial

Claude's temporary access is gone and the public route was proven safe from outside, but claude.ai could not add the connector because the operator's Claude account is managed by a work organisation that disables custom connectors. The route was closed again; hosted clients are deferred to a todo.

## Accomplishments

1. **Task 1 — access removed (complete).** The operator deleted Claude's login and sudoers drop-in on the ledger host, handed the two route files the login had written to root, removed its ACLs and deleted the login on the reverse proxy (`userdel -r`, because `deluser --remove-home` needs Perl, which the proxy container lacks), and unloaded and deleted the key on the workstation. Claude confirmed "Permission denied" on both hosts, no key files and no agent entry. `ledger-selfcheck --grafana-admin`: 0 FAIL.
2. **Task 2 — public route (partial).**
   - Anthropic range check (`LEDGER_LINT_NETWORK=1 build/tests/anthropic-ranges-network-test.sh`): PASS, the template's `/21` is still published.
   - Public DNS checked over DNS-over-HTTPS (the home router intercepts plain DNS): the MCP hostname resolves to the home's public IPv4 address, not CGNAT, not proxied, no AAAA.
   - The operator applied the route with `ledger-mcp-public` on `ledger-mcp-allow` (Anthropic range plus home/VPN); sign-in, REST and Grafana unchanged; the previous file was backed up outside the dynamic directory.
   - Outside check from a phone on mobile data with VPN off (browser table instead of the script): every MCP, OAuth and sign-in path and both other hostnames answered 403; an unknown path on the MCP hostname answered 404. This also proves the router passes the real client address (no source NAT).
   - Inside check: 0 FAIL, before and after.
   - claude.ai: blocked — the organisation managing the operator's account disables custom connectors / own OAuth clients. Not attempted further; household finances should not sit under an employer's Claude account.
   - The operator closed the route again (restored the home/VPN-only file); inside check 0 FAIL afterwards.
3. **Task 3 — docs and todo (partial).** The SSH-removal todo is closed with its evidence. `docs/mcp.md` gains a note that organisation-managed accounts can have custom connectors disabled (then only Claude Code works and the public router should stay on the home/VPN list) and the desktop Code tab `.mcp.json` alternative to `claude mcp add`. The "In practice" part about real hosted-client behaviour cannot be written yet.

## Task Commits

Docs, todo moves and this summary are committed together with the plan's tracking update.

## Deviations from Plan

1. Outside check done with the phone-browser table rather than `build/check-exposure.sh --from outside` (no laptop at hand); it covers the same expectations for every path that matters.
2. Hosted-client steps 4–9 (connector, web/mobile/Desktop, revoke-all with a hosted client, access-log counts, approval email) not done — blocked by the organisation's connector setting; deferred to `.planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md`.
3. Route closed again after verification, by operator decision, so no unused internet-facing surface remains.
4. The 03-07 summary's DNS note was corrected (the earlier lookup was answered by the home router).

## Issues Encountered

- `deluser --remove-home` fails on the proxy container (no Perl); `userdel -r` works.
- The Claude Code test session from the home-network proof ran under the work account; the operator was advised (optional) to delete the session and its local transcript and to keep future ledger chats in a household account.

## Known Stubs

None.

## Threat Flags

None open: no Claude access to any host, no internet-facing route.

## Next Phase Readiness

Phase 3's Claude Code path is live and verified from home/VPN. Its hosted-client goal (claude.ai web and mobile) is open until a household subscription exists; the reopen-and-connect sequence is in the todo.

## Self-Check: PARTIAL
