---
created: 2026-10-08T12:00:00.000Z
title: Connect the hosted Claude clients (web, mobile, Desktop chats) once a household subscription allows custom connectors
area: integration
severity: major
files:
  - docs/mcp.md
---

## Problem

The public route was opened and verified from outside on 2026-10-08, but claude.ai could not add the connector: the operator's paid Claude account belongs to a work organisation that disables custom connectors, and the free personal account is unlikely to support them. The household's finances should not live under an employer's Claude account anyway. The operator closed the public route again (MCP routers back on the home/VPN list) so nothing internet-facing sits unused. Claude Code (desktop Code tab, project `.mcp.json`) works from home and VPN.

## Solution

When a household Claude subscription with custom connectors exists:

1. Reopen the route: re-run the Anthropic range check, then switch `ledger-mcp-public` to `ledger-mcp-allow` (the go-live steps in the plan and `docs/mcp.md`).
2. Outside check from mobile data (script or the phone-browser table) and inside check: 0 FAIL each.
3. claude.ai: custom connector with "Use your own OAuth client", client ID `ledger-claude-hosted`, empty secret; sign in and approve.
4. Verify web, mobile and Desktop; test `ledger-grants revoke-all` and reconnect; count Anthropic-range 200s on the four public paths in the proxy access log and confirm `/connect/authorize` only from home/VPN; confirm the approval email has no financial detail.
5. Add the "In practice" part to `docs/mcp.md` with what the real hosted clients did.
