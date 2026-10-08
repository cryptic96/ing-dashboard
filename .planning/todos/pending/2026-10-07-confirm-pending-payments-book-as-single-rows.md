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
