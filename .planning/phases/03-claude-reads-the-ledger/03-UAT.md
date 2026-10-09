---
status: partial
phase: 03-claude-reads-the-ledger
source: [03-VERIFICATION.md, 03-02-SUMMARY.md]
started: 2026-10-08T12:30:00Z
updated: 2026-10-09T09:00:00Z
---

## Current Test

[testing complete — tests 1 and 2 blocked until a household Claude subscription exists]

## Tests

### 1. Connect claude.ai web, mobile and Desktop through the public route
expected: answers from the real ledger on web, mobile and a Desktop chat; kill switch and reconnect behave as designed; access log shows 200s from Anthropic's range on the four public paths and /connect/authorize only from home/VPN; the new-grant email carries no financial detail. Sequence: .planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md
result: blocked
blocked_by: third-party
reason: "Needs a household Claude subscription that allows custom connectors; the operator's paid account belongs to a work organisation that disables them, and the public route is closed again until a hosted client exists"

### 2. Add the "In practice" part to docs/mcp.md after the hosted run
expected: the guide records what the hosted clients, including mobile, actually showed (generic, no personal data)
result: blocked
blocked_by: prior-phase
reason: "Depends on test 1"

### 3. Sign-in and consent pages look right on desktop and phone
expected: on the home network, /account/login is plain and readable on desktop and phone with no third-party resources and no blocked script or style in the console; the consent page names the client and the redirect host and offers Approve and Deny
result: pass
note: "Desktop sign-in and consent seen while connecting Claude Code; login page checked in the phone's browser on home Wi-Fi"

### 4. Kill switch cuts Claude Code off and re-authorising restores it
expected: after `sudo ledger-grants revoke-all` on the ledger host, the next ledger question in the ledger-chat Code session fails or asks to authenticate; authenticating again through /mcp (password and code) restores answers; `sudo ledger-grants list` then shows the old grant revoked and one new valid grant
result: pass

### 5. "A new Claude connection was approved" alert email
expected: connecting Claude Code fired the "A new Claude connection was approved" alert and an email arrived at the alert address; it says a connection was approved and how to review or revoke it, and contains no amount, merchant, category, balance or token
result: pass

## Summary

total: 5
passed: 3
issues: 0
pending: 0
skipped: 0
blocked: 2

## Gaps
