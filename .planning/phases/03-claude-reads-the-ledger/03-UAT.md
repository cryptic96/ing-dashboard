---
status: complete
phase: 03-claude-reads-the-ledger
source: [03-VERIFICATION.md, 03-02-SUMMARY.md]
started: 2026-10-08T12:30:00Z
updated: 2026-10-09T09:30:00Z
---

## Current Test

[testing complete]

## Tests

### 1. Sign-in and consent pages look right on desktop and phone
expected: on the home network, /account/login is plain and readable on desktop and phone with no third-party resources and no blocked script or style in the console; the consent page names the client and the redirect host and offers Approve and Deny
result: pass
note: "Desktop sign-in and consent seen while connecting Claude Code; login page checked in the phone's browser on home Wi-Fi"

### 2. Kill switch cuts Claude Code off and re-authorising restores it
expected: after `sudo ledger-grants revoke-all` on the ledger host, the next ledger question in the ledger-chat Code session fails or asks to authenticate; authenticating again through /mcp (password and code) restores answers; `sudo ledger-grants list` then shows the old grant revoked and one new valid grant
result: pass

### 3. "A new Claude connection was approved" alert email
expected: connecting Claude Code fired the "A new Claude connection was approved" alert and an email arrived at the alert address; it says a connection was approved and how to review or revoke it, and contains no amount, merchant, category, balance or token
result: pass

## Summary

total: 3
passed: 3
issues: 0
pending: 0
skipped: 0
blocked: 0

## Deferred Follow-Ups

Moved out of this test list into a todo at the operator's request (2026-10-09), so the phase can close with the hosted-client run tracked separately:


- moved_test: "Connect claude.ai web, mobile and Desktop through the public route"
  idea: "Connect claude.ai web, mobile and Desktop chats once a household Claude subscription with custom connectors exists (reopen route, outside/inside checks, connector, kill switch, access-log counts, approval email)"
  deferred_at: 2026-10-09
  todo: .planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md
- moved_test: "Add the 'In practice' part to docs/mcp.md"
  idea: "Write the 'In practice' part of docs/mcp.md from the hosted run"
  deferred_at: 2026-10-09
  todo: .planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md

## Gaps
