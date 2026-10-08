---
status: testing
phase: 03-claude-reads-the-ledger
source: [03-VERIFICATION.md]
started: 2026-10-08T12:30:00Z
updated: 2026-10-08T12:30:00Z
---

## Current Test

number: 1
name: Connect claude.ai web, mobile and Desktop through the public route
expected: |
  With a household Claude subscription that allows custom connectors: reopen the public MCP route, pass the outside and
  inside exposure checks, add the connector (client ID ledger-claude-hosted, empty secret), sign in from home or VPN.
  Web, mobile and a Desktop chat each list the four tools and answer from the real transactions with period, count and
  exclusions stated. `sudo ledger-grants revoke-all` makes the next hosted call fail; reconnecting from home/VPN restores
  access; the new-grant email carries no financial detail. The proxy access log shows 200s from Anthropic's range on the
  four public paths and /connect/authorize only from home/VPN. Full sequence:
  .planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md
awaiting: household Claude subscription with custom connectors

## Tests

### 1. Connect claude.ai web, mobile and Desktop through the public route
expected: answers from the real ledger on all three surfaces; kill switch and reconnect behave as designed; access-log and email checks pass (see Current Test)
result: [pending]

### 2. Add the "In practice" part to docs/mcp.md after the hosted run
expected: the guide records what the hosted clients, including mobile, actually showed (generic, no personal data)
result: [pending]

## Summary

total: 2
passed: 0
issues: 0
pending: 2
skipped: 0
blocked: 2

## Gaps
