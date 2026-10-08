---
created: 2026-10-07T11:16:22.000Z
title: Confirm the first consent renewal keeps all history (around March 2027)
area: ingestion
severity: major
files:
  - Ledger.Service/Ingestion/BankLinkService.cs
  - docs/bank-link.md
---

## Problem

The bank consent is valid until 2027-04-04. Renewing through the guided flow is covered by tests (account keys, names, selection and history kept, longest-history sync without duplicates), but it has never run against the real bank. Whether ING grants the full 180 days again on renewal is also unknown.

## What to check

When the 14-day warning arrives (around 2027-03-21), renew through the guided flow (docs/bank-link.md), then check with counts only:

- both accounts keep their keys, display names and selection;
- the transaction count per account does not double and no reference sits on two rows;
- the old connection is superseded and the days-left metric returns to about 180 (record the granted validity);
- the consent alerts clear.

## Origin

Deferred follow-up from the phase 2 acceptance test (test 5); it can only happen months after go-live.
