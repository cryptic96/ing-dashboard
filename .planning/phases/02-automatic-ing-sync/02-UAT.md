---
status: complete
phase: 02-automatic-ing-sync
source: [02-VERIFICATION.md]
started: 2026-10-07T10:00:50Z
updated: 2026-10-07T11:16:31Z
---

## Current Test

[testing complete]

## Tests

### 1. The eight Household alert rules are Normal and reach the right people
expected: In Grafana, under Alerting, Alert rules, the Household folder lists eight rules and all of them show Normal. The operator email contact point lists every address that should be warned. (The contact point's test notification already arrived on 2026-10-07.)
result: pass

### 2. The two real pending transactions book as one row each
expected: Once ING books the two pending transactions seen at the first link, each stays a single row (now booked), no reference sits on two rows, and the daily balance check stays reconciled on both accounts. Checked by Claude over SSH with counts only. Status checked on 2026-10-07: both are still pending (3479 booked, 2 pending, 0 references on two rows), so this cannot pass until ING books them. Reply `blocked` to wait for the bank, or `skip`.
result: skipped
reason: "Deferred follow-up: tracked in .planning/todos/pending/2026-10-07-confirm-pending-payments-book-as-single-rows.md"

### 3. The old spike consent no longer shows in the ING app
expected: Wherever the ING app or Mijn ING lists third parties with account access, only the server's own application is listed. (The spike consent was revoked through the API with HTTP 200 on 2026-10-05 and the spike application was deleted; this is an optional confirmation.)
result: skipped
reason: "Operator could not find the access overview in the ING app; covered by the API revocation (HTTP 200) and the deleted spike application"

### 4. A real alert email arrives when it should (long-running)
expected: The first real household alert (a failed sync, a rate-limit rejection, or the 14-day consent warning due around 2027-03-21) arrives by email with no financial detail.
result: skipped
reason: "Deferred follow-up: tracked in .planning/todos/pending/2026-10-07-confirm-first-real-alert-email.md"

### 5. The first renewal keeps all history (long-running, around March 2027)
expected: Renewing through the guided flow before the consent expires keeps both accounts' keys, names, selection and history; the days-left metric returns to about 180; no duplicates appear.
result: skipped
reason: "Deferred follow-up: tracked in .planning/todos/pending/2026-10-07-confirm-first-consent-renewal-keeps-history.md"

## Summary

total: 5
passed: 1
issues: 0
pending: 0
skipped: 4
blocked: 0

## Deferred Follow-Ups

- test: 2
  idea: "confirm pending payments book as single rows"
  todo: .planning/todos/pending/2026-10-07-confirm-pending-payments-book-as-single-rows.md
  deferred_at: 2026-10-07
- test: 4
  idea: "confirm first real alert email"
  todo: .planning/todos/pending/2026-10-07-confirm-first-real-alert-email.md
  deferred_at: 2026-10-07
- test: 5
  idea: "confirm first consent renewal keeps history"
  todo: .planning/todos/pending/2026-10-07-confirm-first-consent-renewal-keeps-history.md
  deferred_at: 2026-10-07

## Gaps
