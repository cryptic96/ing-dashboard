---
created: 2026-10-07T11:16:22.000Z
title: Confirm the first real pending payments book as single rows
area: ingestion
severity: minor
files:
  - Ledger.Domain/Ingestion/TransactionReconciler.cs
  - Ledger.Domain/Ingestion/BalanceReconciler.cs
---

## Problem

The first real link on 2026-10-06 brought two pending transactions on the second joint account (no booking date yet). The pending-to-booked handling and the pending-neutral balance check are covered by tests, but the transition has not been seen with real data yet. The spike had concluded that ING sends booked items only; these two prove it does send pending items.

## What to check

Once ING books them (after a scheduled sync), check over SSH with counts only:

- the two rows are booked, and the pending count dropped by two without new duplicate rows;
- no reference sits on two rows and no row has two references from different items;
- the next balance snapshots stay reconciled on both accounts and the drift metric stays 0.

## Origin

Deferred follow-up from the phase 2 acceptance test (test 2), so the phase could close while the bank had not booked the payments yet.

## Resolution

Confirmed on the host during the balance-drift investigation (`.planning/debug/resolved/balance-drift-after-booking.md`), with read-only, counts-only checks run by the operator:

- Every pending item (nine pending-to-booked transitions across both accounts) booked in place under its original reference: no duplicate rows, no row with two references, nothing flagged or dropped.
- No amount changed on booking.
- The balance snapshots did not stay reconciled on one account from 10-08 to 10-12, but that was a separate defect (a stored expectation carried across a release that changed its meaning), fixed in v0.3.1; from the first v0.3.1 snapshot on 2026-10-13 both accounts reconcile and the drift metric is 0.
