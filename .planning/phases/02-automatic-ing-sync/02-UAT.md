---
status: partial
phase: 02-automatic-ing-sync
source: [02-VERIFICATION.md]
started: 2026-10-07T10:00:50Z
updated: 2026-10-07T11:01:59Z
---

## Current Test

[testing complete]

## Tests

### 1. The eight Household alert rules are Normal and reach the right people
expected: In Grafana, under Alerting, Alert rules, the Household folder lists eight rules and all of them show Normal. The operator email contact point lists every address that should be warned. (The contact point's test notification already arrived on 2026-10-07.)
result: pass

### 2. The two real pending transactions book as one row each
expected: Once ING books the two pending transactions seen at the first link, each stays a single row (now booked), no reference sits on two rows, and the daily balance check stays reconciled on both accounts. Checked by Claude over SSH with counts only. Status checked on 2026-10-07: both are still pending (3479 booked, 2 pending, 0 references on two rows), so this cannot pass until ING books them. Reply `blocked` to wait for the bank, or `skip`.
result: blocked
blocked_by: third-party
reason: "blocked (waiting for ING to book the two pending transactions)"

### 3. The old spike consent no longer shows in the ING app
expected: Wherever the ING app or Mijn ING lists third parties with account access, only the server's own application is listed. (The spike consent was revoked through the API with HTTP 200 on 2026-10-05 and the spike application was deleted; this is an optional confirmation.)
result: skipped
reason: "Operator could not find the access overview in the ING app; covered by the API revocation (HTTP 200) and the deleted spike application"

### 4. A real alert email arrives when it should (long-running)
expected: The first real household alert (a failed sync, a rate-limit rejection, or the 14-day consent warning due around 2027-03-21) arrives by email with no financial detail.
result: blocked
blocked_by: other
reason: "blocked (no real alert has fired yet; waits for a real failure or the 14-day consent warning around 2027-03-21)"

### 5. The first renewal keeps all history (long-running, around March 2027)
expected: Renewing through the guided flow before the consent expires keeps both accounts' keys, names, selection and history; the days-left metric returns to about 180; no duplicates appear.
result: blocked
blocked_by: other
reason: "blocked (the consent is new and valid until 2027-04-04; renewal can only be exercised around March 2027)"

## Summary

total: 5
passed: 1
issues: 0
pending: 0
skipped: 1
blocked: 3

## Gaps
