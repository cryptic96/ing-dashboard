---
phase: 02-automatic-ing-sync
plan: 12
subsystem: infra
tags: [enable-banking, spike, ing, quota, pending, reconciliation, consent]
requires: [02-02]
provides:
  - "Completed 02-SPIKE.md: rate-limit, pending-to-booked, consent validity, filled decision table, Decisions and cleanup state"
  - "Operator-approved adapter values: 12 background calls per day, rolling 24 hours, PSU headers on operator syncs, match window 5 days, entry reference primary"
  - "Required adapter-plan change: XPCD balance reconciliation with a fetch-time reference date and a two-consecutive-snapshots drift rule"
affects: [enable-banking-adapter, balance-reconciliation, categorisation, go-live]
tech-stack:
  added: []
  patterns:
    - "Decrypted captures only in memory; analyze and probe-quota print counts and enum codes only"
key-files:
  created:
    - .planning/phases/02-automatic-ing-sync/02-12-SUMMARY.md
  modified:
    - .planning/phases/02-automatic-ing-sync/02-SPIKE.md
key-decisions:
  - "Ingestion:BackgroundCallsPerDay = 12 (was 4): no limit seen up to 44 calls in a day, a daily sync costs 2 calls per account"
  - "Ingestion:QuotaWindow stays Rolling24Hours and PsuHeadersOnOperatorSyncs stays true (reset behaviour unobserved; ING lists psu-ip-address as required)"
  - "ING balance reconciliation uses the expected balance (XPCD) dated at fetch time and flags drift only when it persists across two consecutive daily snapshots; this needs a reconciler change in the adapter plan, not only config"
  - "Entry reference stays the primary dedup key (100% present, unique, stable); fingerprint fallback only when absent, because 37 identical same-day groups prove fingerprints collide"
  - "Treat ING as booked-only (no pending ever seen, weak-evidence caveat); keep MatchWindowDays = 5 and the pending-to-booked reconciliation as a safety net"
  - "Post-link and post-renewal sync runs immediately with PSU headers and the longest strategy and is never skipped or deferred: full history exists only right after authorisation"
  - "Joint current accounts only; savings not offered (already-agreed fallback)"
requirements-completed: []
requirements-progressed: [INGEST-01, INGEST-04, INGEST-05, INGEST-07]
status: complete
duration: spread across 2026-09-30 to 2026-10-05 (waiting on real captures)
completed: 2026-10-05
actuals:
  tokens: 7000
  tasks: 3
  commits: 11
---

# Phase 2 Plan 12: Real-consent spike, second half and decisions Summary

The spike is closed with evidence: ING enforced no background-call limit up to 44 calls in a day, exposes booked transactions only, supplies a unique and stable entry reference on every item, and grants the full 180-day consent; the operator then fixed the adapter's values, including a required reconciler change for ING's expected-balance-only reporting.

## Tasks

| Task | Name | Commit |
| --- | --- | --- |
| 1 (tracer) | Analyze and probe-quota in the spike tool, proven offline | none: the tool lives outside the repository by design; its selftest and shell lint passed |
| 2 (checkpoint:human-action) | Captures, quota probe, analyze, revoke | docs(02-12) commits recording each finding (8ee5f38 through ac8ebdc and earlier), made by the orchestrator |
| 3 (checkpoint:decision) | Choose the adapter's values and branches | e1cb215 (Decisions, decision table, cleanup in 02-SPIKE.md) |

The tracer gate: the tool's offline selftest and shell lint passed, and analyze and probe-quota then ran end to end against the real captures and the real consent.

## Findings

Full detail is in `02-SPIKE.md`.

- **Rate limit:** 42 probe calls plus 2 capture calls (44 header-less calls on one account in one day) all returned HTTP 200, no 429. The probe was run by the orchestrator at the operator's request. Reset behaviour is unobserved because no limit was hit, so the after-reset capture was not needed.
- **Pending to booked:** zero pending items in any snapshot, 27 new items arrived directly booked, so zero pairs. Weak-evidence caveat: no payment shown as pending by the bank app at capture time was caught, and day-3 was skipped.
- **Deduplication:** entry reference present on 100% of booked items, unique and stable (3507 distinct across both accounts); 37 identical same-day groups (75 items) show fingerprints would wrongly merge distinct payments.
- **History depth:** 24 months right after authorisation, 90 days about 12 hours later; header-less calls are admitted.
- **Consent validity:** confirmed, 180 days granted.
- **Savings:** not offered; joint accounts only.
- **Revocation:** done by the orchestrator on 2026-10-05 after the operator's go (revoke HTTP 200, session state deleted); no plaintext capture file exists.

## Decisions

The operator chose the measured values with specific choices; the list is recorded in `02-SPIKE.md` under "Decisions" for the adapter plan to apply. The one item that is not just configuration is ING balance reconciliation: the current reconciler returns unknown without a reference date and has no persistence rule, so the adapter plan must add an XPCD balance kind mapping, a fetch-time reference date, and the two-consecutive-snapshots drift rule.

## Deviations from Plan

- **Orchestrator-run steps.** The quota probe and the session revocation were run by the orchestrator at the operator's request and go, instead of by the operator. The operator still ran the captures and the analyze step with the identity at a hidden prompt.
- **Day-3 capture skipped and after-reset capture dropped.** Day-3 was skipped by the operator (weak-evidence caveat recorded); the after-reset capture lost its purpose because no limit was hit. Fewer than three daily captures were taken, which the plan's flagged assumption anticipated: the reconciler's certain-match rules stay as built, and the replay against the real adapter is the final check.
- **Stale paragraph removed.** A leftover first-half paragraph in 02-SPIKE.md saying the daily captures were still to show whether pending items exist was removed; the findings above supersede it.

## Spike cleanup status

- Session revoked and state file gone: done.
- Plaintext captures: none (only `.age` files remain, kept until the adapter replay).
- Deleting the spike and sandbox private keys: pending (operator). Both key files were still present at the time of the check and were not deleted by the executor.
- ING app check that the aggregator's access is gone: pending (operator).

## Known Stubs

None.

## Threat Flags

None. No code or repository files other than planning notes changed.

## Requirements

INGEST-01, INGEST-04, INGEST-05 and INGEST-07 progressed: the provider-behaviour evidence is now complete, but the adapter is not built yet, so none is marked complete.

## Self-Check: PASSED

- 02-SPIKE.md contains "Decisions", "Decision table" and "Spike cleanup"; commit e1cb215 exists.
- `! grep -niP '\bNL[0-9]{2} ?[A-Z]{4}'` on 02-SPIKE.md finds nothing; no addresses, URLs, identifiers or key material found.
- State file absent; `find` for non-age capture files prints nothing.
- Key-file absence is not met and is recorded as pending (operator), by design of the instructions.
