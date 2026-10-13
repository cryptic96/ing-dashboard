---
slug: balance-drift-after-booking
status: resolved
created: 2026-10-09
updated: 2026-10-13
goal: find_and_fix
---

# Debug: balance reconciliation alert on one joint account

## Trigger

DATA_START
Balance reconciliation alert "Account balance does not reconcile" fired 2026-10-09 06:34 Europe/Amsterdam (just after the early-morning sync) on the live v0.3.0 host. Grafana Bank sync → Sync status: one joint account shows "Reconciles", the other "does not reconcile".
DATA_END

## Symptoms

- **Expected:** after every daily sync both joint accounts reconcile: the bank's expected balance equals the stored booked transactions with exposed pending payments treated as neutral (the Phase 2 design), so `ledger_balance_reconciliation_drift` stays 0 for both.
- **Actual:** after the 2026-10-09 early-morning sync, one account's latest balance snapshot does not reconcile; the alert fired at 06:34 Amsterdam time (04:34 UTC). The other account reconciles.
- **Errors:** only the alert email (summary text, no account named by design). No app error reported yet.
- **Timeline:** both accounts reconciled since the first link on 2026-10-06 (Phase 2 acceptance) and through 2026-10-08. First drift observed 2026-10-09 early morning. v0.3.0 was installed 2026-10-08 ~09:00 UTC (migrations AddOAuthAndLogins, TrackLastTotpTimeStep; no ingestion code change in the phase).
- **Reproduction:** the scheduled daily sync. Not reproducible locally (real bank data).
- **Leading hypothesis (unverified):** on first link the second joint account had two pending transactions without a booking date. If ING booked them overnight, this is the first real pending→booked transition. A mismatch could come from (a) a booked row inserted alongside the old pending row instead of replacing it (duplicate), (b) a booked amount differing from the pending amount, (c) the balance snapshot's treatment of pending items, or (d) persistence rule of drift (flag only when it persists) misbehaving. See `.planning/todos/pending/2026-10-07-confirm-pending-payments-book-as-single-rows.md`.

## Constraints

- Claude has NO access to the ledger host or the reverse proxy (revoked 2026-10-08). Every host-side check must be an exact command the operator runs and reports back as counts, yes/no, booleans or signs only — never amounts, names, IBANs, descriptions or tokens, in the conversation or in any file.
- Commands must be read-only (SELECT via the reporting reader or `runuser -u ledger_backup -- psql -d ledger -Atc ...` style as used before; never write to the database on the host).
- Public repository: no personal data in this file or any commit. Use account keys only if they are opaque; prefer "account A/B".
- Local reproduction: tests use the user's local PostgreSQL container via user-secrets; synthetic data only.
- Any fix ships as a normal release (branch, PR, tag, operator approval); the operator runs any host-side remediation.

## Current Focus

bug_class: Bohrbug (deterministic given the bank feed; the trigger is a one-off real-data transition)
known_pattern_candidate: none (no knowledge base yet)
status_note_2026_10_12: |
  Round 2 answered: none of H2/H2c/H2d/H7 fits; drift on A = -(p1+p2) from 10-08, constant through 10-12; B reconciles every day.
  NEW LEADING HYPOTHESIS H9 (release-boundary semantic change of the carried expectation): the 10-07 04:30 UTC snapshot of A was
  checked by v0.2.1 (v0.2.2 tagged 05:29 UTC, v0.2.3 tagged 08:55 UTC and deployed 10-07), whose CheckUndated recorded
  expected_amount = previous amount + booked since = the bank's XPCD figure INCLUDING the pending p1/p2. v0.2.3 (commit 9290ad4)
  redefined expected_amount as booked-only and carries the stored value forward without migrating stored rows (commit 244d857 only
  re-derived rows with a null expectation). The 10-08 check (v0.2.3) therefore started from bank_0 (still containing p1/p2 as
  pending), added p1/p2 again when they booked, and subtracted only p3 as pending: drift = -(p1+p2), carried forward forever.
  B had nothing pending at its 10-07 legacy check, so its legacy expectation equalled the booked-only one and B never drifted.
status_note_2026_10_13: |
  RESOLVED. Round 3 (host, 2026-10-12, before the release) matched the H9 prediction on every reported column. The fixed release
  v0.3.1 was installed 2026-10-12; the first scheduled snapshot on it (10-13) reconciles on both accounts with pending_amount
  recorded, and the Grafana Sync status reconciles for both. The operator confirmed the fix. No check is outstanding.
hypothesis: H9 CONFIRMED in code (integration test b) and on the host (round 3 + post-release check).
test: none outstanding.
expecting: n/a (resolved).
next_action: none. Session archived to .planning/debug/resolved/ and recorded in .planning/debug/knowledge-base.md. The separate doubtful-match defect (H1) stays tracked in .planning/todos/pending/2026-10-12-doubtful-booking-match-counts-twice-for-one-snapshot.md.
reasoning_checkpoint:
  hypothesis: "The undated check carries the previous snapshot's stored expected_amount forward. v0.2.3 changed what that column means (pending excluded) without migrating stored rows, so A's 10-07 row, checked under v0.2.1 while p1/p2 were pending, carried B0+P into v0.2.3's 10-08 check, which added p1/p2 again when they booked: drift = -(p1+p2), carried forever."
  confirming_evidence:
    - "Old CheckUndated (v0.2.0..v0.2.2) recorded expected = previous amount + booked since, i.e. pending included; commits 244d857/9290ad4 in v0.2.3 redefine it without a data migration; carry reads it back."
    - "v0.2.2 tagged 05:29 UTC 10-07, v0.2.3 tagged 08:55 UTC 10-07 and deployed that day: A's 10-07 04:30 check ran old rules, 10-08 04:30 ran new rules."
    - "Host: drift = exactly -(p1+p2) from 10-08, constant through 10-12; 10-07 reconciled; B (nothing pending at its old-rules check) never drifted; later transitions clean; all bank-side models false."
    - "Integration test (b) reproduces it exactly (+50 = -(p1+p2), constant, later card payment clean); test (a), same feed without the old-rules row, is green."
  falsification_test: "Operator read-only check: A's 10-07 row has expected_amount = amount (old identity) and NOT amount - pending_at_fetch; and an expectation recomputed from the 10-06 baseline (amount - pending at its fetch + booked since) gives drift 0 on every A and B snapshot 10-08..10-12. If A's 10-07 expected_amount already excluded p1/p2, or the recomputed drift is non-zero, H9 is wrong or incomplete."
  fix_rationale: "Derive the expectation on every check from the baseline (latest earlier snapshot with no expectation of its own): baseline amount - pending at the baseline fetch + booked since the baseline fetch. No stored expectation is read back, so a value recorded under other rules can never be carried. Algebraically identical to the carry when every stored value has the current meaning (all existing tests stay green), and it heals the host at the first snapshot after the release because the host's baseline (10-06) is clean. To avoid widening an existing weakness (reconstructing the baseline's pending sum from current row amounts is wrong if a baseline-pending item later books in place at another amount; the carry froze it at the first check), each snapshot now records the pending sum live at its fetch (new nullable pending_amount), used for every future baseline; the host's existing baselines fall back to reconstruction, which round 2 showed is exact (no amount changed on booking)."
  blind_spots: "Host timestamp of the v0.2.3 install is inferred from the phase records, not read from the host (the read-only check settles it via the 10-07 row's identity). Unexposed reservations at the baseline (H7 class) are still a permanent drift by design. The doubtful-match one-day drift (H1) is untouched by this fix."
  candidate_causes:
    - "code: carry of a stored derived value whose meaning changed between releases (confirmed)"
    - "config/deploy: release boundary between two daily checks with no data migration or re-anchor step (contributing condition)"
    - "data: bank figure semantics (H2/H2c/H2d/H7/H8/H4) — eliminated by round 2 models"
  and_gate: "yes — the drift needs BOTH the semantic change being deployed between two checks AND items pending at the last old-rules check that book under the new rules. B lacked the second condition and stayed clean; a fresh link lacks the first (test a green)."
history_note: the round-1/round-2 hypothesis list (H2, H2c, H2d, H7, H8, H4) and their tests are recorded in Evidence and Eliminated.

## Operator check round 3 (answered 2026-10-12 before the release, results in Evidence)

Run on the ledger host as root. Read-only (ledger_backup, BEGIN READ ONLY ... ROLLBACK). Output: account letters, MM-DD dates,
kind names, booleans, signs, counts and UTC baseline times only. Validated locally on 2026-10-12 as ledger_backup against a
migrated throwaway database driven through the real SyncOrchestrator (A-like account: day-2 row rewritten to the old rules and
later rows poisoned like the host; B-like account: clean, one pending item that books); the rows came out exactly as predicted
below. It reads no column the fix adds, so it runs on the current release too.

```bash
runuser -u ledger_backup -- psql -d ledger -X -v ON_ERROR_STOP=1 -P pager=off -P footer=off <<'SQL'
BEGIN READ ONLY;
\echo == checked undated snapshots: how each recorded its expectation, and the verdict the corrected check gives
WITH acc AS (
  SELECT a.id, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
)
SELECT acc.account,
       to_char(sn.snapshot_date, 'MM-DD') AS snapshot_date,
       sn.balance_kind,
       sn.reconciled,
       sign(sn.drift_amount)::int AS drift_sign,
       to_char(b.created_at AT TIME ZONE 'UTC', 'MM-DD HH24:MI') AS baseline_utc,
       pc.n AS pending_at_fetch,
       pb.n AS pending_at_baseline,
       sn.expected_amount + sn.drift_amount = sn.amount AS drift_left_pending_in,
       sn.expected_amount + sn.drift_amount = sn.amount - pc.total AS drift_took_pending_out,
       sn.expected_amount = b.amount + bs.total AS expectation_kept_baseline_pending,
       sn.expected_amount = b.amount - pb.total + bs.total AS expectation_took_baseline_pending_out,
       sign(fx.drift)::int AS fixed_drift_sign,
       fx.drift = 0 AS fixed_reconciles,
       fx.drift = sn.drift_amount AS fixed_equals_recorded
FROM public.balance_snapshots sn
JOIN acc ON acc.id = sn.account_id
JOIN LATERAL (
  SELECT x.created_at, x.amount
  FROM public.balance_snapshots x
  WHERE x.account_id = sn.account_id AND x.balance_kind = sn.balance_kind
    AND x.snapshot_date < sn.snapshot_date AND x.expected_amount IS NULL
  ORDER BY x.snapshot_date DESC, x.created_at DESC
  LIMIT 1
) b ON true
CROSS JOIN LATERAL (
  SELECT count(*) AS n, coalesce(sum(t.amount), 0) AS total
  FROM public.transactions t
  WHERE t.account_id = sn.account_id AND t.first_seen_at <= sn.created_at
    AND (t.status <> 'booked' OR (t.booked_at IS NOT NULL AND t.booked_at > sn.created_at))
    AND (t.status <> 'dropped' OR (t.dropped_at IS NOT NULL AND t.dropped_at > sn.created_at))
) pc
CROSS JOIN LATERAL (
  SELECT count(*) AS n, coalesce(sum(t.amount), 0) AS total
  FROM public.transactions t
  WHERE t.account_id = sn.account_id AND t.first_seen_at <= b.created_at
    AND (t.status <> 'booked' OR (t.booked_at IS NOT NULL AND t.booked_at > b.created_at))
    AND (t.status <> 'dropped' OR (t.dropped_at IS NOT NULL AND t.dropped_at > b.created_at))
) pb
CROSS JOIN LATERAL (
  SELECT coalesce(sum(t.amount), 0) AS total
  FROM public.transactions t
  WHERE t.account_id = sn.account_id AND t.status = 'booked'
    AND t.booked_at > b.created_at AND t.booked_at <= sn.created_at
) bs
CROSS JOIN LATERAL (
  SELECT sn.amount - pc.total - (b.amount - pb.total + bs.total) AS drift
) fx
WHERE sn.reference_date IS NULL AND sn.expected_amount IS NOT NULL
ORDER BY acc.account, sn.snapshot_date, sn.balance_kind;
ROLLBACK;
SQL
```

Predicted answer if H9 is right:
- A 10-07: drift_left_pending_in=t, drift_took_pending_out=f (checked by the old rules while p1/p2 were pending),
  expectation_kept_baseline_pending=t, expectation_took_baseline_pending_out=f, fixed_reconciles=t.
- A 10-08..10-12: drift_took_pending_out=t (new rules), expectation_kept_baseline_pending=t (the carried old figure),
  expectation_took_baseline_pending_out=f, fixed_drift_sign=0, fixed_reconciles=t, fixed_equals_recorded=f.
- B every day: fixed_reconciles=t, fixed_equals_recorded=t.
Any fixed_reconciles=f means the corrected check would still mismatch on that day: do not expect the alert to clear, report it.

## Post-release check (answered 2026-10-13 after the first scheduled sync on v0.3.1, results in Evidence)

```bash
runuser -u ledger_backup -- psql -d ledger -X -v ON_ERROR_STOP=1 -P pager=off -P footer=off <<'SQL'
BEGIN READ ONLY;
WITH acc AS (
  SELECT a.id, a.account_key, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
)
SELECT acc.account, to_char(s.snapshot_date, 'MM-DD') AS snapshot_date, s.balance_kind, s.reconciled,
       sign(s.drift_amount)::int AS drift_sign, s.pending_amount IS NOT NULL AS pending_recorded,
       v.balance_reconciled AS dashboard_verdict
FROM public.balance_snapshots s
JOIN acc ON acc.id = s.account_id
LEFT JOIN reporting.account_status v ON v.account_key = acc.account_key
WHERE s.snapshot_date >= current_date - 2
ORDER BY acc.account, s.snapshot_date, s.balance_kind;
ROLLBACK;
SQL
```

Validated locally on 2026-10-12 as ledger_backup (read-only) against a migrated synthetic database on the fixed code.
Expected: the newest row of each account reconciled=t, drift_sign=0, pending_recorded=t; dashboard_verdict=yes for both; the alert
resolves at its next evaluation. No write on the host is needed: snapshots stay append-only and the 10-08..release-day rows keep
their recorded drift as history.

## Operator check round 2 (answered 2026-10-12, results in Evidence)

Run on the ledger host as root. Read-only: ledger_backup has pg_read_all_data only and the session is BEGIN READ ONLY ... ROLLBACK.
The raw payloads are read inside the database only; the output contains account letters, item labels (p1.., n1..), signs, counts,
day offsets, booleans, payload key names, bank transaction type codes (letters only, otherwise "?") and UTC observation times.
Validated locally on 2026-10-09 as ledger_backup against a migrated throwaway database driven through the real SyncOrchestrator with
Enable Banking-shaped payloads, five synthetic accounts: A = H7 (hidden baseline reservation that books later), B = H2c (XPCD carries
a pending item at its final amount), C = H2d (baseline pending credits not in XPCD), D = H2 (no pending item in XPCD), E = H4 (missed
credit). Each scenario lit exactly its own column and nothing else (E: everything false).

```bash
runuser -u ledger_backup -- psql -d ledger -X -v ON_ERROR_STOP=1 -P pager=off -P footer=off <<'SQL'
BEGIN READ ONLY;
\echo == 1 rows first seen as pending, pending-time payload versus booked row
WITH acc AS (
  SELECT a.id, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
),
pay AS (
  SELECT p.transaction_id, p.payload,
         CASE WHEN p.payload->'transaction_amount'->>'amount' ~ '^[0-9]{1,15}([.][0-9]{1,4})?$'
                   AND p.payload->>'credit_debit_indicator' IN ('DBIT', 'CRDT')
              THEN (p.payload->'transaction_amount'->>'amount')::numeric
                   * CASE p.payload->>'credit_debit_indicator' WHEN 'DBIT' THEN -1 ELSE 1 END
         END AS amount,
         row_number() OVER (PARTITION BY p.transaction_id ORDER BY p.observed_at, p.id) AS seq,
         row_number() OVER (PARTITION BY p.transaction_id ORDER BY p.observed_at DESC, p.id DESC) AS rseq
  FROM public.transaction_payloads p
),
origin AS (
  SELECT t.*, acc.account,
         'p' || row_number() OVER (PARTITION BY t.account_id ORDER BY t.first_seen_at, t.id) AS item
  FROM public.transactions t
  JOIN acc ON acc.id = t.account_id
  WHERE EXISTS (SELECT 1 FROM public.transaction_refs r WHERE r.transaction_id = t.id AND r.first_status = 'pending')
)
SELECT o.account, o.item, o.status,
       f.payload->>'status' AS first_payload_status,
       sign(f.amount)::int AS pending_sign,
       (SELECT count(*) FROM pay x WHERE x.transaction_id = o.id) AS payloads,
       (SELECT count(DISTINCT x.amount) FROM pay x WHERE x.transaction_id = o.id AND x.payload->>'status' = 'PDNG') AS distinct_pending_amounts,
       l.amount = o.amount AS latest_payload_parses_to_row_amount,
       f.amount <> o.amount AS amount_changed_on_booking,
       sign(o.amount - f.amount)::int AS booked_minus_pending_sign,
       o.transaction_date - (o.first_seen_at AT TIME ZONE 'Europe/Amsterdam')::date AS txn_minus_first_seen_days,
       o.booking_date - o.transaction_date AS booking_minus_txn_days,
       o.booking_date - (o.booked_at AT TIME ZONE 'Europe/Amsterdam')::date AS booking_minus_booked_seen_days,
       CASE WHEN f.payload->'bank_transaction_code'->>'code' ~ '^[A-Z]{1,8}$' THEN f.payload->'bank_transaction_code'->>'code'
            WHEN f.payload->'bank_transaction_code'->>'code' IS NULL THEN '' ELSE '?' END AS pending_code,
       CASE WHEN l.payload->'bank_transaction_code'->>'code' ~ '^[A-Z]{1,8}$' THEN l.payload->'bank_transaction_code'->>'code'
            WHEN l.payload->'bank_transaction_code'->>'code' IS NULL THEN '' ELSE '?' END AS booked_code,
       (SELECT string_agg(k, ',' ORDER BY k) FROM jsonb_object_keys(f.payload) k) AS pending_payload_keys,
       (SELECT string_agg(k, ',' ORDER BY k) FROM jsonb_object_keys(l.payload) k
         WHERE NOT f.payload ? k) AS keys_added_on_booking,
       (SELECT string_agg(k, ',' ORDER BY k) FROM jsonb_object_keys(f.payload) k
         WHERE NOT l.payload ? k) AS keys_removed_on_booking
FROM origin o
JOIN pay f ON f.transaction_id = o.id AND f.seq = 1
JOIN pay l ON l.transaction_id = o.id AND l.rseq = 1
ORDER BY o.account, o.item;
\echo == 2 which balance model reproduces each stored drift exactly
WITH acc AS (
  SELECT a.id, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
),
pay AS (
  SELECT p.transaction_id,
         CASE WHEN p.payload->'transaction_amount'->>'amount' ~ '^[0-9]{1,15}([.][0-9]{1,4})?$'
                   AND p.payload->>'credit_debit_indicator' IN ('DBIT', 'CRDT')
              THEN (p.payload->'transaction_amount'->>'amount')::numeric
                   * CASE p.payload->>'credit_debit_indicator' WHEN 'DBIT' THEN -1 ELSE 1 END
         END AS amount,
         row_number() OVER (PARTITION BY p.transaction_id ORDER BY p.observed_at, p.id) AS seq
  FROM public.transaction_payloads p
),
item AS (
  SELECT t.id, t.account_id, t.status, t.amount AS b, f.amount AS p, t.first_seen_at, t.booked_at, t.dropped_at,
         row_number() OVER (PARTITION BY t.account_id ORDER BY t.first_seen_at, t.id) AS n
  FROM public.transactions t
  JOIN pay f ON f.transaction_id = t.id AND f.seq = 1
  WHERE EXISTS (SELECT 1 FROM public.transaction_refs r WHERE r.transaction_id = t.id AND r.first_status = 'pending')
),
snap AS (
  SELECT s.account_id, acc.account, s.snapshot_date, s.created_at, s.reconciled, s.drift_amount AS drift,
         first_value(s.created_at) OVER (PARTITION BY s.account_id, s.balance_kind ORDER BY s.snapshot_date, s.created_at) AS bf
  FROM public.balance_snapshots s
  JOIN acc ON acc.id = s.account_id
)
SELECT sn.account, sn.snapshot_date, sn.reconciled, sign(sn.drift)::int AS drift_sign,
       m.base_n AS pending_at_baseline, m.cur_n AS pending_at_fetch,
       sn.drift = m.base_p - m.cur_p AS no_pending_in_balance,
       sn.drift = (m.base_p - m.base_b) - (m.cur_p - m.cur_b) AS final_amount_in_balance,
       sn.drift = m.base_p - m.cur_base_p AS baseline_pending_not_in_balance,
       coalesce(lab.labels, '') AS pending_item_sums_equal_to_drift,
       nb.new_booked AS new_booked_since_baseline,
       nb.minus_drift_subsets,
       nb.minus_drift_min_size,
       nb.plus_drift_subsets
FROM snap sn
CROSS JOIN LATERAL (
  SELECT count(*) FILTER (WHERE x.pb) AS base_n,
         count(*) FILTER (WHERE x.pc) AS cur_n,
         coalesce(sum(x.p) FILTER (WHERE x.pb), 0) AS base_p,
         coalesce(sum(x.b) FILTER (WHERE x.pb), 0) AS base_b,
         coalesce(sum(x.p) FILTER (WHERE x.pc), 0) AS cur_p,
         coalesce(sum(x.b) FILTER (WHERE x.pc), 0) AS cur_b,
         coalesce(sum(x.p) FILTER (WHERE x.pc AND x.pb), 0) AS cur_base_p
  FROM (
    SELECT i.p, i.b,
           (i.first_seen_at <= sn.bf
             AND (i.status <> 'booked' OR (i.booked_at IS NOT NULL AND i.booked_at > sn.bf))
             AND (i.status <> 'dropped' OR (i.dropped_at IS NOT NULL AND i.dropped_at > sn.bf))) AS pb,
           (i.first_seen_at <= sn.created_at
             AND (i.status <> 'booked' OR (i.booked_at IS NOT NULL AND i.booked_at > sn.created_at))
             AND (i.status <> 'dropped' OR (i.dropped_at IS NOT NULL AND i.dropped_at > sn.created_at))) AS pc
    FROM item i
    WHERE i.account_id = sn.account_id
  ) x
) m
LEFT JOIN LATERAL (
  SELECT string_agg(c.label, ' ' ORDER BY c.label) AS labels
  FROM (
    SELECT sg.s || k.kind || '{' || string_agg(i.n::text, ',' ORDER BY i.n) || '}' AS label,
           sg.v * sum(CASE k.kind WHEN 'P' THEN i.p WHEN 'B' THEN i.b ELSE i.b - i.p END) AS total
    FROM item i
    JOIN generate_series(1, CASE WHEN (SELECT count(*) FROM item j WHERE j.account_id = sn.account_id) BETWEEN 1 AND 10
                                 THEN (1 << (SELECT count(*) FROM item j WHERE j.account_id = sn.account_id)::int) - 1
                                 ELSE 0 END) mask
      ON (mask >> (i.n::int - 1)) & 1 = 1
    CROSS JOIN (VALUES ('P'), ('B'), ('D')) k(kind)
    CROSS JOIN (VALUES ('+', 1), ('-', -1)) sg(s, v)
    WHERE i.account_id = sn.account_id
    GROUP BY mask, k.kind, sg.s, sg.v
    HAVING bool_and(CASE k.kind WHEN 'P' THEN i.p WHEN 'B' THEN i.b ELSE i.b - i.p END <> 0)
  ) c
  WHERE sn.drift <> 0 AND c.total = sn.drift
) lab ON true
CROSS JOIN LATERAL (
  WITH nb AS (
    SELECT t.amount, row_number() OVER (ORDER BY t.first_seen_at, t.id) AS n
    FROM public.transactions t
    WHERE t.account_id = sn.account_id AND t.status = 'booked' AND t.booked_at = t.first_seen_at
      AND t.first_seen_at > sn.bf AND t.first_seen_at <= sn.created_at
  ),
  sums AS (
    SELECT mask, sum(nb.amount) AS total, count(*) AS size
    FROM nb
    JOIN generate_series(1, CASE WHEN (SELECT count(*) FROM nb) BETWEEN 1 AND 16
                                 THEN (1 << (SELECT count(*) FROM nb)::int) - 1 ELSE 0 END) mask
      ON (mask >> (nb.n::int - 1)) & 1 = 1
    GROUP BY mask
  )
  SELECT (SELECT count(*) FROM nb) AS new_booked,
         (SELECT count(*) FROM sums WHERE sn.drift <> 0 AND sums.total = -sn.drift) AS minus_drift_subsets,
         (SELECT min(size) FROM sums WHERE sn.drift <> 0 AND sums.total = -sn.drift) AS minus_drift_min_size,
         (SELECT count(*) FROM sums WHERE sn.drift <> 0 AND sums.total = sn.drift) AS plus_drift_subsets
) nb
WHERE sn.reconciled IS NOT NULL
ORDER BY sn.account, sn.snapshot_date;
\echo == 3 rows first seen as booked after the baseline
WITH acc AS (
  SELECT a.id, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
),
base AS (
  SELECT DISTINCT ON (s.account_id) s.account_id, s.created_at AS bf
  FROM public.balance_snapshots s
  ORDER BY s.account_id, s.snapshot_date, s.created_at
),
latest AS (
  SELECT DISTINCT ON (s.account_id) s.account_id, s.drift_amount AS drift
  FROM public.balance_snapshots s
  WHERE s.reconciled IS NOT NULL
  ORDER BY s.account_id, s.snapshot_date DESC, s.created_at DESC
)
SELECT acc.account,
       'n' || row_number() OVER (PARTITION BY t.account_id ORDER BY t.first_seen_at, t.id) AS item,
       to_char(t.first_seen_at AT TIME ZONE 'UTC', 'MM-DD HH24:MI') AS first_seen_utc,
       sign(t.amount)::int AS sign,
       t.booking_date - (t.first_seen_at AT TIME ZONE 'Europe/Amsterdam')::date AS booking_minus_first_seen_days,
       t.transaction_date - t.booking_date AS txn_minus_booking_days,
       t.value_date IS NOT NULL AS has_value_date,
       t.counterparty_name IS NOT NULL AS has_counterparty,
       coalesce(t.transaction_date, t.booking_date, t.value_date) <= (b.bf AT TIME ZONE 'Europe/Amsterdam')::date AS dated_on_or_before_baseline_day,
       ls.drift <> 0 AND t.amount = -ls.drift AS amount_equals_minus_latest_drift,
       ls.drift <> 0 AND t.amount = ls.drift AS amount_equals_latest_drift
FROM public.transactions t
JOIN acc ON acc.id = t.account_id
JOIN base b ON b.account_id = t.account_id
LEFT JOIN latest ls ON ls.account_id = t.account_id
WHERE t.status = 'booked' AND t.booked_at = t.first_seen_at AND t.first_seen_at > b.bf
ORDER BY acc.account, item;
\echo == 4 payload fields that could locate the bank figure
WITH acc AS (
  SELECT a.id, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
)
SELECT acc.account,
       count(*) AS payloads,
       count(*) FILTER (WHERE jsonb_typeof(p.payload->'balance_after_transaction') = 'object') AS with_balance_after_transaction,
       count(*) FILTER (WHERE p.payload->>'status' = 'PDNG') AS pending_payloads,
       count(*) FILTER (WHERE p.payload->>'status' = 'PDNG'
                          AND jsonb_typeof(p.payload->'balance_after_transaction') = 'object') AS pending_with_balance_after_transaction
FROM public.transaction_payloads p
JOIN public.transactions t ON t.id = p.transaction_id
JOIN acc ON acc.id = t.account_id
GROUP BY acc.account
ORDER BY acc.account;
\echo == 5 booked rows per booking date since 10-04
WITH acc AS (
  SELECT a.id, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
)
SELECT acc.account, to_char(t.booking_date, 'MM-DD') AS booking_date,
       count(*) AS booked_rows,
       count(*) FILTER (WHERE t.amount > 0) AS credits,
       count(*) FILTER (WHERE t.amount < 0) AS debits,
       count(*) FILTER (WHERE t.booked_at = t.first_seen_at) AS first_seen_as_booked
FROM public.transactions t
JOIN acc ON acc.id = t.account_id
WHERE t.status = 'booked' AND t.booking_date >= date '2026-10-04'
GROUP BY acc.account, t.booking_date
ORDER BY acc.account, t.booking_date;
ROLLBACK;
SQL
```

What each output answers:
- 1: per pending-origin row: the sign of the pending amount; whether the amount changed on booking (and the sign of booked minus
  pending); whether the pending amount changed while pending; transaction date relative to the day first seen (positive = future
  dated, for example a scheduled payment); booking date relative to transaction date; the payload's type code and key names before and
  after booking; latest_payload_parses_to_row_amount must be t (sanity check of the in-SQL amount parse).
- 2: per checked snapshot: no_pending_in_balance (H2), final_amount_in_balance (H2c), baseline_pending_not_in_balance (H2d) — a model
  is confirmed only if it is t on 10-07, 10-08 and 10-09; pending_item_sums_equal_to_drift lists +/- sums of pending (P), booked (B)
  or booked-minus-pending (D) amounts of the items that equal the drift (empty = none); minus_drift_subsets > 0 means a set of rows
  first seen as booked after the baseline sums to minus the drift (H7).
- 3: each row first seen as booked after the baseline: run time, sign, booking date relative to the day first seen, whether it is
  dated on or before the baseline day, and whether its amount equals minus (or plus) the latest drift (H7 points at one row).
- 4: whether ING sends balance_after_transaction (if so, a later round can compare the bank's own booked balance with XPCD directly).
- 5: booked rows per booking date since 10-04 (counts only) for an optional comparison with the ING app for the account Grafana shows
  as "does not reconcile" (a differing count points at H4; equal counts with every model false points at H8).

## Operator check round 1 (answered 2026-10-09, results in Evidence)

Run on the ledger host as root (read-only: ledger_backup has pg_read_all_data only, and the session is BEGIN READ ONLY ... ROLLBACK). Output contains only account letters, counts, booleans, signs, reference kinds (er/fp) and UTC observation times. The exact command text is in the checkpoint returned on 2026-10-09; it was validated locally against a migrated schema as ledger_backup.

```bash
runuser -u ledger_backup -- psql -d ledger -X -v ON_ERROR_STOP=1 -P pager=off -P footer=off <<'SQL'
BEGIN READ ONLY;
\echo == 1 accounts
WITH acc AS (
  SELECT a.id, a.account_key, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
)
SELECT acc.account,
       v.balance_reconciled AS dashboard_verdict,
       v.flagged_count AS flagged_pending_now,
       (SELECT count(*) FROM public.transactions t WHERE t.account_id = acc.id AND t.status = 'booked') AS booked,
       (SELECT count(*) FROM public.transactions t WHERE t.account_id = acc.id AND t.status = 'pending') AS pending,
       (SELECT count(*) FROM public.transactions t WHERE t.account_id = acc.id AND t.status = 'dropped') AS dropped,
       (SELECT count(*) FROM (SELECT r.transaction_id FROM public.transaction_refs r WHERE r.account_id = acc.id
                              GROUP BY r.transaction_id HAVING count(*) > 1) x) AS rows_with_two_refs
FROM acc
LEFT JOIN reporting.account_status v ON v.account_key = acc.account_key
ORDER BY acc.account;
\echo == 2 rows first seen as pending
WITH acc AS (
  SELECT a.id, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
),
origin AS (
  SELECT t.*, acc.account,
         'p' || row_number() OVER (PARTITION BY t.account_id ORDER BY t.first_seen_at, t.id) AS item,
         coalesce(t.transaction_date, t.booking_date, t.value_date, (t.first_seen_at AT TIME ZONE 'Europe/Amsterdam')::date) AS effective_date
  FROM public.transactions t
  JOIN acc ON acc.id = t.account_id
  WHERE EXISTS (SELECT 1 FROM public.transaction_refs r WHERE r.transaction_id = t.id AND r.first_status = 'pending')
)
SELECT o.account, o.item, o.status,
       o.match_flag IS NOT NULL AS flagged,
       o.counterparty_name IS NOT NULL AS has_counterparty,
       o.transaction_date IS NOT NULL AS has_txn_date,
       o.value_date IS NOT NULL AS has_value_date,
       o.booking_date IS NOT NULL AS has_booking_date,
       (SELECT string_agg(left(r.ref, 2) || '/' || r.first_status, ',' ORDER BY r.first_seen_at)
          FROM public.transaction_refs r WHERE r.transaction_id = o.id) AS refs,
       to_char(o.first_seen_at AT TIME ZONE 'UTC', 'MM-DD HH24:MI') AS first_seen_utc,
       to_char(o.booked_at AT TIME ZONE 'UTC', 'MM-DD HH24:MI') AS booked_utc,
       to_char(o.dropped_at AT TIME ZONE 'UTC', 'MM-DD HH24:MI') AS dropped_utc,
       tw.twins,
       tw.first_twin_booked_utc
FROM origin o
LEFT JOIN LATERAL (
  SELECT count(*) AS twins,
         to_char(min(w.booked_at) AT TIME ZONE 'UTC', 'MM-DD HH24:MI') AS first_twin_booked_utc
  FROM public.transactions w
  WHERE w.account_id = o.account_id AND w.id <> o.id AND w.status = 'booked'
    AND w.amount = o.amount AND w.currency = o.currency
    AND w.booked_at >= o.first_seen_at
    AND abs(coalesce(w.transaction_date, w.booking_date, w.value_date,
                     (w.first_seen_at AT TIME ZONE 'Europe/Amsterdam')::date) - o.effective_date) <= 5
) tw ON true
ORDER BY o.account, o.item;
\echo == 3 balance snapshots
WITH acc AS (
  SELECT a.id, chr(64 + (row_number() OVER (ORDER BY a.created_at, a.id))::int) AS account
  FROM public.accounts a
  WHERE a.sync_enabled
),
snap AS (
  SELECT s.*, acc.account,
         lag(s.created_at) OVER w AS prev_fetch,
         lag(s.drift_amount) OVER w AS prev_drift,
         first_value(s.created_at) OVER w AS baseline_fetch
  FROM public.balance_snapshots s
  JOIN acc ON acc.id = s.account_id
  WINDOW w AS (PARTITION BY s.account_id, s.balance_kind ORDER BY s.snapshot_date, s.created_at)
),
pend AS (
  SELECT t.id, t.account_id, t.amount, t.currency, t.status, t.first_seen_at, t.booked_at, t.dropped_at,
         coalesce(t.transaction_date, t.booking_date, t.value_date, (t.first_seen_at AT TIME ZONE 'Europe/Amsterdam')::date) AS effective_date
  FROM public.transactions t
)
SELECT sn.account, sn.snapshot_date, sn.balance_kind,
       to_char(sn.created_at AT TIME ZONE 'UTC', 'MM-DD HH24:MI') AS fetched_utc,
       sn.reconciled,
       sign(sn.drift_amount)::int AS drift_sign,
       sn.drift_amount = sn.prev_drift AS same_drift_as_previous,
       pa.pending_rows_at_fetch,
       pa.with_booked_twin_in_window,
       (sn.drift_amount <> 0 AND sn.drift_amount = -pa.twin_sum) AS double_count_explains,
       (sn.drift_amount <> 0 AND sn.drift_amount = pb.base_sum - pa.pending_sum) AS pending_not_in_bank_balance_explains
FROM snap sn
LEFT JOIN LATERAL (
  SELECT count(*) AS pending_rows_at_fetch,
         coalesce(sum(p.amount), 0) AS pending_sum,
         count(*) FILTER (WHERE tw.has_twin) AS with_booked_twin_in_window,
         coalesce(sum(p.amount) FILTER (WHERE tw.has_twin), 0) AS twin_sum
  FROM pend p
  CROSS JOIN LATERAL (
    SELECT EXISTS (
      SELECT 1 FROM pend w
      WHERE w.account_id = p.account_id AND w.id <> p.id AND w.status = 'booked'
        AND w.amount = p.amount AND w.currency = p.currency
        AND w.booked_at > coalesce(sn.prev_fetch, '-infinity'::timestamptz) AND w.booked_at <= sn.created_at
        AND abs(w.effective_date - p.effective_date) <= 5
    ) AS has_twin
  ) tw
  WHERE p.account_id = sn.account_id
    AND p.first_seen_at <= sn.created_at
    AND (p.status <> 'booked' OR (p.booked_at IS NOT NULL AND p.booked_at > sn.created_at))
    AND (p.status <> 'dropped' OR (p.dropped_at IS NOT NULL AND p.dropped_at > sn.created_at))
) pa ON true
LEFT JOIN LATERAL (
  SELECT coalesce(sum(p.amount), 0) AS base_sum
  FROM pend p
  WHERE p.account_id = sn.account_id
    AND p.first_seen_at <= sn.baseline_fetch
    AND (p.status <> 'booked' OR (p.booked_at IS NOT NULL AND p.booked_at > sn.baseline_fetch))
    AND (p.status <> 'dropped' OR (p.dropped_at IS NOT NULL AND p.dropped_at > sn.baseline_fetch))
) pb ON true
WHERE sn.balance_kind = 'expected' OR sn.reconciled IS NOT NULL
ORDER BY sn.account, sn.snapshot_date, sn.balance_kind;
\echo == 4 sync runs since 2026-10-05
SELECT to_char(r.started_at AT TIME ZONE 'UTC', 'MM-DD HH24:MI') AS started_utc,
       r.trigger, r.outcome, r.calls_made, r.inserted, r.updated, r.dropped, r.flagged
FROM public.sync_runs r
WHERE r.started_at >= timestamptz '2026-10-05 00:00+00'
ORDER BY r.started_at;
ROLLBACK;
SQL
```


## Evidence

- timestamp: 2026-10-09
  checked: Ledger.Service/Metrics/SyncMetrics.cs, Ledger.Repository/Stores/IngestionStatusStore.cs (ReadFlaggedReconciliationAsync), reporting.account_status view (CheckVerdictsAcrossRecentSnapshots migration)
  found: ledger_balance_reconciliation_drift is 1 only when the latest snapshot with a verdict mismatched AND the previous verdict of the same kind, at most 3 days earlier, also mismatched. A single mismatch is reported as null (metric 0, dashboard "unknown").
  implication: the alert at 04:34 UTC on 2026-10-09 requires at least two mismatching snapshots (2026-10-09 and the previous verdict, almost certainly 2026-10-08). "Reconciled through 2026-10-08" was probably never observed directly; a first mismatch on 2026-10-08 would have shown as "unknown" and kept the metric at 0.

- timestamp: 2026-10-09
  checked: SyncOrchestrator.SnapshotBalancesAsync / CheckBalanceAsync, BalanceReconciler.CheckUndated, BalanceStore sums, 02-SPIKE.md balance types
  found: ING returns only XPCD (expected) without a reference date, so the undated path is used. drift = bank - pendingAt(fetch) - (previous.ExpectedAmount + booked first seen as booked since the previous fetch). previous.ExpectedAmount is carried forward, not re-derived from the bank figure. Only the first sync of a local day reads balances (HasSnapshotForDateAsync).
  implication: a manual sync later on 2026-10-09 cannot change today's verdict or the dashboard row; it only changes transactions. The next verdict is the 2026-10-10 scheduled snapshot.

- timestamp: 2026-10-09
  checked: algebra of CheckUndated across a pending→booked transition (X = signed amount of the pending item, design assumption: XPCD contains exposed pending items)
  found: clean upgrade under the same reference, certain merge under a new reference, and drop-plus-insert when nothing matches all give drift 0. While items stay pending the pending terms cancel on both sides whether or not XPCD contains them, so 2026-10-07/08 verdicts could not have tested that assumption.
  implication: drift on the transition needs either a double count (pending row still pending at the fetch while its booked twin is counted, drift = -X) or a wrong design assumption (XPCD never contained the item, drift = +X permanently because the expectation is carried forward).

- timestamp: 2026-10-09
  checked: TransactionReconciler.MatchUnresolvedBookedItems / IsCertain / AddDropsForAbsentPendingRows
  found: a booked item under a new reference that matches a pending row on amount, currency and date window but not with certainty (counterparty missing on either side, or more than one candidate/claim) flags the pending row and inserts the booked item separately. Rows flagged in this run are in the "unclear" set and are never dropped in the same run, even when the bank no longer lists them; they are dropped on the next run.
  implication: on the booking run the flagged pending row is still pending when the balance is fetched, so SumPendingAt subtracts X from the bank figure while SumBookedSince also adds X to the expectation: drift = -X for that day, self-healing the next day. Two pending items that book on consecutive days produce two consecutive one-day mismatches, which the two-snapshot rule reports as persisting drift. No existing test covers a flagged booking followed by a balance check (A_flagged_pending_twin... checks only reporting rows; the balance tests only book under the same reference).

- timestamp: 2026-10-09
  checked: new integration test (synthetic, local PostgreSQL container) — two pending items without counterparty in the baseline, booking on consecutive days under new references with a counterparty, XPCD constant (it contains the pending items)
  found: both booking runs flag one row (Flagged=1); snapshot day 2 reconciled=false drift=+20 expected=980, day 3 reconciled=false drift=+30 expected=950, day 4 reconciled=true. The status store therefore reports false after day 3 (alert condition).
  implication: H1 is a real, deterministic defect in the code: a doubtful match on the booking day double-counts the payment for one snapshot, and two such days in a row raise the persisting-drift alert. Whether it is what happened on the host is still unproven.

- timestamp: 2026-10-09
  checked: the four diagnostic queries, run locally as ledger_backup against (a) the H1 synthetic scenario and (b) an H2 synthetic scenario (XPCD excludes the pending items, both book under the same reference), and the assembled psql script against a migrated empty schema
  found: (a) double_count_explains=t, pending_not_in_bank_balance_explains=f, drift_sign=+1, origin rows flagged with twins=1, runs flagged=1; (b) the opposite: pending_not_in_bank_balance_explains=t, double_count_explains=f, drift_sign=-1, no flags, no twins. The full script executes inside BEGIN READ ONLY / ROLLBACK.
  implication: one operator round discriminates H1/H3 vs H2 vs unexplained (H4/H5) vs a persistence-rule fault (H6).

- timestamp: 2026-10-09 (operator round 1, run after one manual sync at 08:08 UTC)
  checked: the four read-only round-1 queries on the host
  found: |
    Accounts: A dashboard "no" (the drifting account), B "yes". Now 0 pending, 0 dropped, 0 flagged, 0 rows with two references on both.
    Pending-origin rows: only on A, four items, all booked in place under their original entry reference (refs er/pending only), flagged=f,
    twins=0, with counterparty and transaction date, no value date. p1 and p2 first seen 10-06 21:12 (post-link), booked 10-08 04:30;
    p3 first seen 10-08 04:30, booked 10-09 04:30; p4 first seen 10-09 04:30, booked 10-09 08:08 (manual run).
    Snapshots A: baseline 10-06 21:12 (2 pending at fetch); 10-07 reconciled, drift 0 (2 pending); 10-08 reconciled=f, drift sign +1
    (1 pending: p3); 10-09 reconciled=f, drift sign +1, identical drift to 10-08 (1 pending: p4). double_count_explains=f and
    pending_not_in_bank_balance_explains=f on both mismatching days. B: all reconciled, never a pending row.
    Runs: 10-06 post_link inserted 3481; 10-07 scheduled inserted 0, updated 0; 10-08 inserted 4, updated 2; 10-09 04:30 inserted 3,
    updated 1; 10-09 08:08 manual (no balance call) updated 1. No run flagged or dropped anything.
  implication: |
    H1 and H3 did not happen (no flag, no twin, one reference per row). H6 did not happen (two real consecutive mismatches). The drift is a
    constant positive offset that appeared exactly with the run in which p1 and p2 booked, and did not move when p3 booked and p4 appeared,
    so the p3/p4 transition behaved as designed and something specific to the 10-07 -> 10-08 interval (or to p1/p2) is off.
    10-07 reconciled with nothing inserted or updated on either account, so the bank figure for A did not change between the baseline fetch
    and the 10-07 fetch.

- timestamp: 2026-10-09
  checked: SyncOrchestrator.SyncAccountAsync ordering (orchestrator question about the "at fetch" window)
  found: ApplyAsync writes the run's rows with observedAt taken before the apply; SnapshotBalancesAsync runs afterwards, takes fetchedAt after
    the balance call, and saves the snapshot with created_at = fetchedAt. SumPendingAtAsync(fetchedAt) therefore counts rows first seen in
    the same run (p3 on 10-08, p4 on 10-09) and excludes rows booked in the same run (p1/p2 on 10-08, p3 on 10-09).
  implication: the round-1 query's pending_rows_at_fetch window (first_seen <= created_at, booked_at > created_at) is exactly what the code
    subtracted. No ordering artefact.

- timestamp: 2026-10-09
  checked: general algebra of CheckUndated with the carried expectation
  found: |
    expected_k = (bank_0 - P_0) + sum of BookedSince windows, so drift_k = R_k - R_0 with R_k = bank_k - L_k - P_k (bank figure minus
    ledger booked total placed by BookedAt minus ledger pending at fetch, each pending amount as stored at check time). Anything in the bank
    figure that the ledger cannot see at the baseline (an unexposed reservation, a booking not yet listed, a pending item carried at a
    different amount, or a pending item not in XPCD at all) becomes part of R_0 and turns into a permanent drift once it resolves.
  implication: the round-1 H2 formula used current (booked) amounts; if p1/p2/p3 changed amount on booking it could not see H2 or H2c.
    The pending-time amounts are recoverable: LedgerStore appends a transaction_payloads row (jsonb, raw provider payload) whenever the
    payload hash changes, so the first payload of each origin row holds the amount carried while pending.

- timestamp: 2026-10-09
  checked: round-2 diagnostic script, run locally as ledger_backup (BEGIN READ ONLY / ROLLBACK) against a migrated throwaway database
    filled by the real SyncOrchestrator with Enable Banking-shaped payloads (five synthetic accounts, four days each, one per hypothesis)
  found: A (H7) -> models f, minus_drift_subsets=1 (size 1), section 3 flags the hidden row (dated before the baseline day, amount =
    -drift); B (H2c) -> final_amount_in_balance=t on all three days, label -D{1}; C (H2d) -> baseline_pending_not_in_balance=t, labels
    +P{1,2} +B{1,2}; D (H2) -> no_pending_in_balance=t; E (H4) -> every column false/empty. Sanity column latest_payload_parses_to_row_amount=t
    everywhere. The throwaway database was dropped by the fixture and the scratch test file deleted.
  implication: one operator round separates H7, H2c, H2d, H2 and {H4, H8}; the optional app count comparison then separates H4 from H8.

- timestamp: 2026-10-12 (operator round 2)
  checked: the five read-only round-2 sections on the host
  found: |
    Origin rows: A p1..p7 and B p1/p2, every pending payload PDNG, one distinct pending amount, amount unchanged on booking, no keys
    added or removed on booking, no bank transaction codes. A p1/p2 are the only future-dated items (transaction date one day after
    the Amsterdam first-seen day) and the only items pending at A's baseline; booked with booking date = transaction date, one day
    before the day seen booked. B p1/p2 first appeared well after B's baseline.
    Models on A from 10-08 to 10-12: H2, H2c, H2d all f; no subset of rows first seen as booked sums to minus the drift (H7 f);
    pending_item_sums_equal_to_drift = -B{1,2} -P{1,2} on all five days: the drift is exactly -(p1+p2), constant, positive (debits).
    A's later pending items p3..p6 booked without moving the drift. B reconciles every day, including its own p1/p2 transitions.
    No payload carries balance_after_transaction.
  implication: the drift is tied to the items pending at A's first checked days, not to pending-to-booked transitions in general
    (B's and A's later transitions are clean). None of the bank-side models explains it, so the cause is on the ledger side.

- timestamp: 2026-10-12
  checked: git history of BalanceReconciler.cs / SyncOrchestrator.cs / BalanceStore.cs and release tags
  found: |
    v0.2.0..v0.2.2 CheckUndated: expected = (previous.ExpectedAmount ?? previous.Amount) + bookedSince; drift = amount - expected.
    The stored expected_amount was therefore an XPCD-style figure that INCLUDED the pending items live at the fetch.
    v0.2.3 (tagged 2026-10-07 08:55 UTC, the release deployed on 10-07 per the phase security/verification records) changed it:
    commit 244d857 subtracts the pending rows live at a baseline (only when the previous row has NO expectation), commit 9290ad4
    subtracts the pending rows live at the current fetch and redefines expected_amount as "booked balance without pending items"
    (entity XML doc changed) — with no data migration, while the carry `start = previous.ExpectedAmount ?? ...` keeps reading the
    stored value. Commit 244d857's message says it "corrects baselines that are already stored"; it did not consider stored CHECKED
    rows whose expectation had been recorded under the old meaning. No ingestion code changed after v0.2.3 (v0.3.0 and later).
    Host timeline: A's 10-07 04:30 UTC snapshot ran under v0.2.1 (v0.2.2 was tagged 05:29 UTC), A's 10-08 04:30 UTC snapshot ran
    under v0.2.3.
  implication: |
    Algebra (A, P = p1+p2 < 0, B0 = A's true booked balance at the baseline, XPCD = booked + exposed pending):
      10-06 baseline: amount_0 = B0 + P, no expectation.
      10-07 (old rules): expected_1 = amount_0 + 0 = B0 + P (includes P); drift_1 = amount_1 - expected_1 = 0 -> reconciled.
      10-08 (new rules): start = expected_1 = B0 + P (carried; no pending subtraction because the row HAS an expectation);
        expected_2 = B0 + P + (p1+p2 booked in the window) = B0 + 2P; drift_2 = (B0 + P + p3) - p3 - (B0 + 2P) = -P > 0.
      Every later day: expected_k = expected_{k-1} + booked since, so the -P offset is carried forever (10-09..10-12 same drift).
    B: nothing pending at its 10-07 old-rules check, so its old-rules expectation equalled the booked-only one -> no drift ever.
    This matches every observed fact: 10-07 reconciled, drift = exactly -(p1+p2) from 10-08, constant, positive, B clean,
    later transitions clean, all bank-side models false. The orchestrator's reading (current code double counts any item pending
    at a baseline) predicts the same numbers on the host but contradicts commit 244d857's tested baseline handling; it is tested
    below with a fresh-link test on current code (prediction: green).

- timestamp: 2026-10-12
  checked: two new integration tests (synthetic, local PostgreSQL container, real SyncOrchestrator), current code (v0.3.x)
    Scenario: booked 1000.00, two debits 20.00/30.00 pending at the link and dated one day after it (bank XPCD 950.00);
    day 2 still pending; day 3 both booked in place under their references with booking date day 2, a new 15.00 card payment
    pending (XPCD 935.00); day 4 the card payment booked; day 5 nothing.
    (a) Future_dated_debits_pending_at_the_baseline_that_book_a_day_later_never_cause_drift — fresh link, current code throughout.
    (b) A_stored_expectation_that_still_contains_the_pending_items_live_at_its_fetch_never_counts_them_twice_once_they_book —
        identical, but the day-2 row's expected_amount is rewritten (admin connection) to the bank amount, i.e. what v0.2.0..v0.2.2
        recorded.
  found: (a) GREEN: all four snapshots reconciled, drift 0. (b) RED: day 3 drift +50.00 (expected 900 = 950 - 50), day 4 and day 5
    drift +50.00 (expected 885); the 15.00 card payment's own transition adds nothing. Exactly the host's shape: drift = -(p1+p2),
    constant, positive, later transitions clean.
  implication: H9 CONFIRMED in code. Current code does NOT double count items pending at a fresh baseline (orchestrator's reading
    refuted for current code); the defect is that the undated check carries a stored expected_amount forward, so a value recorded
    under a different meaning (here: by the pre-fix release, pending included) is double counted and never heals.

- timestamp: 2026-10-12
  checked: fix verification (fix-acceptance guardrail), local PostgreSQL container
  found: |
    target test (b) green after the fix; (a) still green; new test
    A_pending_item_inside_the_baseline_balance_that_books_in_place_at_another_amount_reconciles_on_the_booked_amount green.
    Manual mutation (no Stryker configured): ignoring the recorded pending_amount (always reconstructing) -> the in-place amount
    test fails with drift -2.00 on two days (mutant killed); dropping the baseline filter (start from the latest snapshot, i.e.
    re-anchor daily on the bank) -> 3 existing balance tests fail (gap persistence, reservation resolution, second-snapshot flag).
    Revert-and-reconfirm: production code stashed and migration moved aside -> test (b) red again (drift +50 on days 3-5); reapplied
    -> green. Full suites on the final tree: unit 757 (756 passed, 1 pre-existing env-gated skip: captured-replay), integration 385
    (384 passed, 1 pre-existing env-gated skip: packaged migration bundle, runs in CI). The H1 red test was removed from the suite
    and preserved verbatim in .planning/todos/pending/2026-10-12-doubtful-booking-match-counts-twice-for-one-snapshot.md.
    Round-3 operator query validated locally (see section above).
  implication: fix accepted by the guardrail; awaiting the operator's read-only round 3 and the post-release check.

- timestamp: 2026-10-12 (operator round 3, run on the host before the release)
  checked: the round-3 read-only query (how each checked undated snapshot recorded its expectation, and the verdict the corrected
    check gives), on the release that was live before v0.3.1
  found: |
    The operator reported an exact match with the prediction. Reported columns:
    A 10-07: reconciled t, drift_left_pending_in t, drift_took_pending_out f, fixed_reconciles t, fixed_equals_recorded t.
    A 10-08..10-12: reconciled f, drift_sign +1, expectation_kept_baseline_pending t, fixed_drift_sign 0, fixed_reconciles t,
      fixed_equals_recorded f.
    B 10-07..10-12: reconciled t, fixed_reconciles t, fixed_equals_recorded t.
  implication: |
    The falsification test for H9 passed on real data. A's 10-07 row recorded its expectation under the old identity (pending left
    in the drift, i.e. expected = bank figure with p1/p2 included), and every A row from 10-08 carried that figure from the baseline
    without taking the baseline's pending out. The corrected (baseline-derived) check gives drift 0 on every stored A and B snapshot;
    it differs from the recorded verdict only on A 10-08..10-12 (the drifting days) and agrees everywhere else. H9 confirmed on the
    host; the fix predicts that the alert clears at the first snapshot after the release with no write on the host.

- timestamp: 2026-10-13 (post-release check, after the first scheduled sync on v0.3.1)
  checked: the post-release read-only query on the host (snapshots of the last three days per account), after v0.3.1 (installed
    2026-10-12) ran the 2026-10-13 morning scheduled sync; plus the operator's look at Grafana Bank sync -> Sync status
  found: |
    A 10-11 expected: reconciled f, drift_sign +1, pending_recorded f.
    A 10-12 expected: reconciled f, drift_sign +1, pending_recorded f.
    A 10-13 expected: reconciled t, drift_sign 0, pending_recorded t.
    B 10-11 expected: reconciled t, drift_sign 0, pending_recorded f.
    B 10-12 expected: reconciled t, drift_sign 0, pending_recorded f.
    B 10-13 expected: reconciled t, drift_sign 0, pending_recorded t.
    dashboard_verdict = yes on every row. Note: dashboard_verdict comes from a LEFT JOIN to reporting.account_status, so it is the
    account's CURRENT verdict repeated on each of its rows, not a per-day verdict (it does not mean A reconciled on 10-11/10-12).
    The operator reports the Grafana Sync status now shows both accounts as reconciling.
  implication: |
    The fix works on the host. The 10-13 rows are the first written by v0.3.1 (pending_recorded t; the 10-12 rows have it f, so they
    were written before the install). A's first new-release snapshot derived its expectation from the clean 10-06 baseline and
    reconciled without any host-side write; B stayed clean. Historical A rows keep their recorded drift as history (append-only
    snapshots), as designed. The account status flipped to reconciled at the first post-release snapshot, so the persisting-drift
    alert condition is gone.

## Eliminated

- hypothesis: (b) from the trigger — a booked amount different from the pending amount causes the drift
  evidence: algebra of CheckUndated plus LedgerStore: same-reference upgrade overwrites the amount and counts the booked amount on both sides; a different amount never merges (IsCandidate needs equal amounts) so the pending row is dropped and the booked row inserted; both give drift 0 when XPCD contains the pending item.
  timestamp: 2026-10-09

- hypothesis: v0.3.0 install (OAuth/TOTP migrations) touched ingestion or snapshot state
  evidence: the two migrations add identity/OAuth tables and a TOTP column only; no ingestion code changed; the 10-08 snapshot was taken at the 06:30 local sync before the ~09:00 UTC install, and a later same-day sync cannot write another snapshot.
  timestamp: 2026-10-09

- hypothesis: H1 — a doubtful pending-to-booked match left a flagged pending twin that was double counted at the fetch
  evidence: host round 1: all four origin rows flagged=f, twins=0, refs er/pending only; no run flagged anything. (The code defect is real
    and reproduced by the uncommitted red test, but it is not what happened on the host.)
  timestamp: 2026-10-09

- hypothesis: H3 — the bank still listed the old pending item next to the booked one
  evidence: host round 1: one reference per row, no booked twin, nothing pending or dropped now.
  timestamp: 2026-10-09

- hypothesis: H2, H2c, H2d — the bank's expected balance treats the pending items differently than the design assumes
  evidence: host round 2: no_pending_in_balance, final_amount_in_balance and baseline_pending_not_in_balance all f on 10-08..10-12.
  timestamp: 2026-10-12

- hypothesis: H7 — a hidden booking in the baseline figure that later arrived as a new booked row
  evidence: host round 2: no subset of rows first seen as booked sums to minus the drift (minus_drift_subsets=0 on every day).
  timestamp: 2026-10-12

- hypothesis: H8 / H4 — an unexposed reservation released, or a booked transaction missing on one side
  evidence: the drift equals exactly -(p1+p2) on all five days and is fully reproduced by the release-boundary mechanism (H9) in
    code; neither would produce that exact figure, and B (same bank, same feed) stays clean.
  timestamp: 2026-10-12

- hypothesis: orchestrator reading — current code counts any item pending at a fresh baseline twice once it books
  evidence: integration test Future_dated_debits_pending_at_the_baseline_that_book_a_day_later_never_cause_drift (the host's exact
    shape on a fresh link, current code) is green before the fix; the double count needs a stored expectation recorded under the
    pre-v0.2.3 rules (test with that row rewritten is red).
  timestamp: 2026-10-12

- hypothesis: H6 — the persistence rule fired on a single mismatch
  evidence: host round 1: 10-08 and 10-09 both reconciled=f with identical drift; the rule behaved as designed.
  timestamp: 2026-10-09

- hypothesis: H5 — two unrelated hidden reservations on consecutive mornings
  evidence: host round 1: the drift on 10-09 is identical to 10-08 (same_drift_as_previous=t), one constant offset rather than two different ones.
  timestamp: 2026-10-09

## Resolution

root_cause: |
  H9 (AND-gate: two contributing conditions). (1) The undated balance check carried the previous snapshot's stored
  expected_amount forward (start = previous.ExpectedAmount ?? ...). (2) v0.2.3 (deployed 2026-10-07) redefined that column as the
  booked-only expectation (pending excluded) without migrating stored rows, while v0.2.0..v0.2.2 had recorded it as the bank
  figure with exposed pending items included. Account A's 10-07 snapshot was checked by the old rules while p1/p2 were pending, so
  its expectation contained them; the first new-rules check (10-08) carried it and added p1/p2 again when they booked:
  drift = -(p1+p2), carried forward forever. Account B had nothing pending at its old-rules check and never drifted.
latent_defect_found_on_the_way: H1 — a pending row flagged as a doubtful match in the same run is still subtracted as pending at that run's fetch while its booked twin is counted, giving a one-day drift of minus its amount; two such bookings on consecutive days raise the persisting-drift flag. Separate mechanism and trigger, not the host's cause, effect self-heals after one snapshot; NOT fixed now (fix needs an ingestion design decision). Red test preserved in .planning/todos/pending/2026-10-12-doubtful-booking-match-counts-twice-for-one-snapshot.md (removed from the suite so CI stays green).
fix: |
  Every undated check derives its expectation from the baseline (latest earlier snapshot of the kind with no expectation of its
  own): baseline amount - pending at the baseline fetch + booked since the baseline fetch. No stored expectation is read back
  (BalanceSnapshotState no longer exposes one), so a figure recorded under other rules can never be carried; identical to the
  carry under consistent rules. Each snapshot now records pending_amount (signed pending sum live at its fetch, new nullable
  column, migration RecordPendingSumOnBalanceSnapshots) and later checks use it for the baseline, so a baseline-pending item that
  books in place at another amount still reconciles; older baselines fall back to reconstruction (exact on the host: no amount
  changed on booking). Commit a838daa on milestone/v1-household-ledger.
release: |
  v0.3.1 — PR cryptic96/ing-dashboard#19 merged to main (merge commit 7dd4a51), tag v0.3.1 on that commit; built, attested,
  approved by the operator and installed on the host on 2026-10-12. First scheduled snapshot on it: 2026-10-13.
oracle_type: specified (the bank's expected balance = booked + exposed pending; the drift must be 0 when the ledger has every booking)
verification:
  target_test: { result: pass }
  mutation_check: { result: pass, reason_if_skipped: "Stryker not configured; manual mutants at the fix sites", mutant_killed: true }
  no_op_deletion: { result: pass, deletion_justified_by_rca: true }
  adjacent_tests: { result: pass, suites_run: [Ledger.UnitTests 757, Ledger.IntegrationTests 385] }
  revert_and_reconfirm: { result: pass, bug_returned_on_revert: true, fixed_on_reapply: true }
  guardrail_verdict: accepted
  host_round_3: { result: pass, date: 2026-10-12, detail: "exact match with the H9 prediction; fixed_reconciles=t on every A and B row" }
  host_post_release: { result: pass, date: 2026-10-13, detail: "first v0.3.1 snapshot reconciled on A and B, drift_sign 0, pending_recorded t" }
  human_verify: { result: confirmed, date: 2026-10-13, detail: "operator: confirmed fixed; Grafana Sync status reconciles for both accounts" }
host_remediation: |
  None written, none needed (confirmed 2026-10-13). The migration added pending_amount as NULL to existing rows; the first
  scheduled snapshot on v0.3.1 derived A's and B's expectation from their 10-06 baselines (pending reconstructed, exact) and
  reconciled on its own, and the account status flipped to reconciled. Historical rows keep their recorded drift as history.
prevention: |
  Blameless postmortem (branches from the diagnosis candidate_causes and the AND-gate):
  - code branch: why could the drift happen? The undated check read back a stored derived value (expected_amount) and built on it.
    Why was that dangerous? The value's meaning was defined only by the code that wrote it, so any change to that code silently
    changed the meaning of every future row but not of the rows already stored. Why was the change not paired with a migration?
    The change was reviewed as a pure logic correction; its commit corrected stored baselines (rows with no expectation) but nobody
    asked whether stored CHECKED rows, recorded under the old meaning, would be read back by the new code.
  - deploy/config branch: why did it bite on the host? A release with the semantic change landed between two daily checks, and the
    release process has no step that re-anchors or migrates stored derived values; tests always start from a fresh link on one
    code version, so a mixed-version history was never exercised.
  - data branch: why only account A? A had items pending at its last old-rules check that booked under the new rules; B had nothing
    pending then, so its old and new meanings coincided. The bank figure semantics were eliminated (round 2).
  why_not_caught: none (no gate existed for this class) — every test drove a single code version from a fresh link, so no test,
    type check or review step covered a stored value written by one release and read by the next; code review of the v0.2.3
    change looked at the formula, not at previously stored rows.
  recurrence_guard: |
    1. The expectation is no longer stored-and-carried: every undated check re-derives it from the baseline snapshot (amount,
       pending at its fetch, booked since), so a value recorded under other rules can never be read back
       (Ledger.Domain/Ingestion/BalanceReconciler.cs; BalanceSnapshotState no longer exposes a stored expectation).
    2. Each snapshot records pending_amount (the signed pending sum live at its fetch), so a future baseline does not depend on
       reconstructing pending from current row amounts.
    3. Regression test Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs:
       A_stored_expectation_that_still_contains_the_pending_items_live_at_its_fetch_never_counts_them_twice_once_they_book
       (a stored row written under the old meaning; red before the fix, green after), plus
       Future_dated_debits_pending_at_the_baseline_that_book_a_day_later_never_cause_drift and
       A_pending_item_inside_the_baseline_balance_that_books_in_place_at_another_amount_reconciles_on_the_booked_amount.
    4. Knowledge-base pattern: carrying a stored derived value across a release that changed its meaning, without migrating or
       re-anchoring the stored rows.
files_changed:
  - Ledger.Domain/Ingestion/BalanceReconciler.cs
  - Ledger.Domain/Ingestion/IBalanceStore.cs
  - Ledger.Repository/Entities/BalanceSnapshotEntity.cs
  - Ledger.Repository/LedgerDbContext.cs
  - Ledger.Repository/Migrations/20261012200324_RecordPendingSumOnBalanceSnapshots.cs (+ Designer, model snapshot)
  - Ledger.Repository/Stores/BalanceStore.cs
  - Ledger.Service/Ingestion/SyncOrchestrator.cs
  - Ledger.UnitTests/Ingestion/BalanceReconcilerTests.cs
  - Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs
