---
phase: 02-automatic-ing-sync
verified: 2026-10-07T12:00:00Z
status: human_needed
score: 4/5 must-haves verified
behavior_unverified: 1
overrides_applied: 1
overrides:
  - must_have: "After one guided consent flow, the joint account and every ING savings account appear in the ledger with the longest history the bank link offers"
    reason: "ING offers no savings account through the PSD2 link (restricted production application listed exactly two joint current accounts, type CACC). The fallback was agreed in the phase context before the spike: ship with the joint current accounts only. Transfers to and from savings stay visible on the current accounts; savings balances and interest are absent from the ledger. The provider-agnostic interface keeps a second provider for savings possible later."
    accepted_by: "household operator (fallback decision recorded in the phase context and the spike decisions)"
    accepted_at: "2026-10-05T00:00:00Z"
behavior_unverified_items:
  - truth: "Alerts reach the household when a sync fails (including rate-limit rejections) and 14 and 7 days before consent expires"
    test: "In Grafana (Alerting > Alert rules, folder Household) confirm all eight rules show Normal. Then provoke one firing alert end to end, for example by temporarily lowering a threshold in a throwaway copy or by waiting for a real failure, and confirm the email reaches every household recipient."
    expected: "Eight Household rules Normal while healthy; a firing rule produces one email to the configured recipients with no financial content; the 14-day and 7-day bands each send their own email as the consent days-left value crosses 14 and 7."
    why_human: "The rule expressions and metrics are present, wired and statically linted, and the metrics they read are covered by integration tests. No test makes Grafana evaluate the sync-failing, rate-limited or 14/7-day rules, and no real alert email for these has fired. Only the stale-sync expression was evaluated against live Prometheus (10212 s, correct) and a test notification from the contact point arrived."
human_verification:
  - test: "Open Grafana alerting and confirm the eight Household rules are Normal; check the recipient list in LEDGER_ALERT_EMAIL (server-side env) contains both partners if both should be warned."
    expected: "All eight rules Normal; recipients are the intended household addresses (the contact point accepts a list)."
    why_human: "Rule state and the server-side recipient value are not visible from the repository."
  - test: "Renew the consent through the guided flow when the 14-day alert fires (about 2027-03-21), or earlier on a spare window, and watch the run."
    expected: "Superseded connection, same account keys, names, selection and history; a longest-history sync adds no duplicates; the days-left metric jumps back to about 180; whether ING grants the full 180 days on renewal is recorded."
    why_human: "Renewal is covered by integration tests and the synthetic provider but has not run against the live bank (the consent is new)."
  - test: "Over the next days watch the two real pending transactions until they book."
    expected: "Each stays one row and becomes booked, with the booking date filled, no second row and no ambiguous flag; balance reconciliation stays true."
    why_human: "The pending-to-booked path is exercised by tests (same reference, new reference, ambiguous), but ING was believed booked-only and two pending items have now appeared; the live transition has not been observed."
  - test: "Confirm in the ING app that the old spike consent no longer appears under connected parties (the operator could not find the screen)."
    expected: "No spike access listed; the API revocation returned HTTP 200 and the spike application was deleted."
    why_human: "The ING app screen is not reachable from here; only the aggregator side was confirmed."
---

# Phase 2: Automatic ING Sync Verification Report

**Phase Goal:** After linking the ING accounts once, the household's real transactions arrive in the ledger every day. They are complete and never duplicated, bank-consent expiry never goes unnoticed, and both partners can see the data arriving.
**Verified:** 2026-10-07
**Status:** human_needed
**Re-verification:** No, initial verification
**Code state:** branch milestone/v1-household-ledger at 2834008 (identical to main and the deployed v0.2.3)

The code, the tests and the live observations support the goal. Nothing blocks the next phase. The remaining items are operator confirmations that cannot be made from the repository: alert delivery and rule state, a first live renewal, the first live pending-to-booked transition and the ING-side revocation screen.

## Goal Achievement

### Observable Truths (roadmap success criteria)

| # | Truth | Status | Evidence |
| --- | --- | --- | --- |
| 1 | One guided consent links the joint account and every ING savings account with the longest history; new transactions then arrive daily unattended | PASSED (override) | Guided flow: `BankLinkService` (start, callback with one-shot hashed state, account selection, post-link sync with `HistoryDepth.Longest`), `BankEndpoints`, bank-link docs. Live: 2 joint current accounts, 3481 transactions back to 2024-10-06 (the 24-month window ING gives only right after authorisation), then an unattended scheduled sync on 2026-10-07 06:30 Amsterdam (`SyncScheduler`, covered by `The_scheduler_starts_exactly_one_scheduled_run_at_half_past_six_Amsterdam_time`). Savings: ING exposes none through this link (spike: two CACC accounts only), so the "every savings account" part is met by the agreed fallback, see override. |
| 2 | Re-running adds nothing; a pending transaction that books stays one; every field retained exactly | VERIFIED | `TransactionReconciler` (pure planner, entry reference primary key `er:`, fingerprint `fp:` only when no reference, certain-match merge, ambiguity flagging, drop rules), `LedgerStore.ApplyAsync` (one DB transaction per account, redirects references the window did not see to their owning row), primary key (account_id, ref) on `transaction_refs`. Columns: `numeric(19,4)` amount, `date` booking, value and transaction dates, counterparty name and IBAN, description, status, append-only raw payload in `jsonb` with SHA-256. Tests passing: `Running_the_same_sync_twice_adds_nothing_and_records_no_changes`, `Pending_item_arriving_booked_under_the_same_reference_stays_one_row_and_becomes_booked`, `Pending_payment_that_books_under_a_new_reference_stays_one_transaction_with_both_references`, `Every_field_is_stored_exactly_as_received_and_reporting_shows_the_effective_date`, `Changed_payload_appends_one_row_and_an_unchanged_payload_appends_none`. Reconciliation to the cent: daily balance snapshot compared with the ledger (`BalanceReconciler`); live both accounts reconciled=true, drift 0, second run inserted 0 with 0 duplicated references. |
| 3a | Metrics show consent state, days until expiry, last successful sync and sync errors | VERIFIED | `SyncMetrics`: `ledger_bank_consent_state{state}`, `ledger_bank_consent_days_until_expiry`, `ledger_sync_last_success_timestamp_seconds`, `ledger_sync_failing{reason}`, `ledger_sync_errors_total{reason}` (all reasons pre-created at zero), projected from the database by `SyncMetricsRefresher` so a restart loses nothing. Only active and provider-expired connections are read, so a superseded consent after renewal cannot keep an expiry alert alive. Live metrics confirmed (state linked, about 179.7 days left, last success advanced). |
| 3b | Alerts reach the household on sync failure (including rate limiting) and at 14 and 7 days before expiry; renewal keeps all history | PRESENT_BEHAVIOR_UNVERIFIED (alerts); VERIFIED (renewal) | Eight provisioned rules in `household-rules.yaml` (transient failure, rate limited, consent or credential rejected, stale sync, 14-day band, 7-day band, expired, balance drift) routed to the email contact point with a daily repeat. Rate-limit and quota outcomes map to `rate_limited` (`SyncHealth.ReasonForOutcome`). Renewal: `BankConnectionStore.ApplyRenewalAsync` re-attaches accounts by provider identification hash and supersedes the old connection in one database transaction; test `Renewal_keeps_account_keys_names_selection_and_history_and_syncs_the_longest_history_without_duplicates` passes. No test makes Grafana evaluate the failure, rate-limit, 14-day or 7-day rules and no real alert has fired; see human items. |
| 4 | Both partners open an EN or NL Grafana dashboard with sync status and recent transactions per account, from one source, via the SELECT-only reporting role, which the database rejects for writes | VERIFIED | `Ledger.Dashboards` generator with one definition and `translations.json` (EN and NL); `ledger-sync-en.json` and `ledger-sync-nl.json` committed, uids fixed, not editable; unit tests prove byte-identical regeneration, equal translation keys, languages differ only in text, and every query reads only `reporting` relations. Panels: sync status table and recent transactions table (variable per account). Datasource connects as `grafana_reader` over the Unix socket. `DatabaseRoleTests.Grafana_reader_role_can_only_read_reporting` and `Grafana_reader_has_only_select_on_every_reporting_object_and_nothing_in_public` pass. Live: DELETE as grafana_reader refused with SQLSTATE 42501, SELECT on reporting works, all three Grafana accounts see data in both languages. |
| 5 | Bank connection read-only, no code path can start a payment; a synthetic provider feeds the same pipeline with no changes outside ingestion | VERIFIED | `IBankDataProvider` has no payment operation. `AisOnlyGuardHandler` is the only handler on the only `HttpClient` in the solution and admits just the seven account-information routes on one host over HTTPS (GET application, aspsps, balances, transactions; POST auth, sessions; DELETE session); anything else throws before reaching the network. The production aggregator application has AIS only (spike). `SyntheticBankDataProvider` plus `SyntheticSyncPipelineTests` (10 tests) run link, select, sync, reconcile against real PostgreSQL through the same `SyncOrchestrator`; provider chosen by `Ingestion:Provider`. |

**Score:** 4/5 truths verified (including 1 passed by override), 1 present but behavior-unverified (alert firing).

### Required Artifacts

| Artifact | Expected | Status | Details |
| --- | --- | --- | --- |
| `Ledger.Service/Ingestion/SyncOrchestrator.cs` | Per-account fetch, plan, apply, balance snapshot | VERIFIED | Substantive, wired through `SyncWorker` and `SyncScheduler`; stops at first failure; run always recorded with retry |
| `Ledger.Domain/Ingestion/TransactionReconciler.cs` | Dedupe and pending-to-booked planner | VERIFIED | Pure, 400+ lines, unit and integration tested |
| `Ledger.Repository/Stores/LedgerStore.cs` | Applies plan atomically | VERIFIED | Single DB transaction, reference redirect |
| `Ledger.Service/Ingestion/BankLinkService.cs` and `Endpoints/BankEndpoints.cs` | Guided link, renew, select, sync now | VERIFIED | Used by integration tests end to end |
| `Ledger.Service/Ingestion/EnableBanking/*` | Aggregator adapter with read-only guard | VERIFIED | Guard is the sole handler (`IngestionServiceCollectionExtensions`) |
| `Ledger.Service/Metrics/SyncMetrics.cs`, `SyncMetricsRefresher.cs` | Consent and sync metrics | VERIFIED | Live scrape confirmed |
| `deploy/provisioning/grafana/.../household-rules.yaml`, `contact-points.yaml`, `notification-policies.yaml` | Alert rules, email routing | VERIFIED (present, wired); firing unproven | See human items |
| `Ledger.Dashboards/*`, `ledger-sync-{en,nl}.json` | Generated dashboards | VERIFIED | Drift test passes |
| `Ledger.Repository/Migrations/*` (reporting views, role grants, call ledger, balance snapshots) | Reporting schema and least-privilege roles | VERIFIED | Applied live by the migrator role |

### Key Link Verification

| From | To | Via | Status |
| --- | --- | --- | --- |
| `SyncScheduler` | `SyncOrchestrator` | `ISyncDispatcher` queue and `SyncWorker` | WIRED (scheduled run observed live) |
| `SyncOrchestrator` | `TransactionReconciler`, `LedgerStore.ApplyAsync` | plan then apply | WIRED |
| `BankLinkService` callback | post-link sync (`SyncTrigger.PostLink`, longest history, call budget not enforced) | dispatcher | WIRED (`PostLinkSyncTests`) |
| `SyncMetricsRefresher` | `/metrics` | `IngestionStatusStore` to `SyncMetrics.Apply` | WIRED |
| Prometheus | Grafana alert rules | datasource uid `prometheus` | WIRED (stale-sync expression evaluated live) |
| Grafana dashboards | `reporting.account_status`, `reporting.transactions` | datasource `ledger-reporting` as `grafana_reader` | WIRED (data visible live) |
| `EnableBankingClient` | network | `AisOnlyGuardHandler` | WIRED |

### Data-Flow Trace (Level 4)

| Artifact | Data variable | Source | Real data | Status |
| --- | --- | --- | --- | --- |
| Recent transactions panel | `reporting.transactions` rows | `public.transactions` written by sync | Yes, 3481 live rows visible to three accounts | FLOWING |
| Sync status panel | `reporting.account_status` | sync_runs, bank_connections, balance_snapshots | Yes | FLOWING |
| Metrics | `IngestionStatus` | database via `IngestionStatusStore` | Yes, live values confirmed | FLOWING |

### Behavioral Spot-Checks (run by the verifier)

| Behavior | Command | Result | Status |
| --- | --- | --- | --- |
| Unit suite | `dotnet test --project Ledger.UnitTests` | 433 total, 432 passed, 1 skipped (real-capture replay needs an env var), 0 failed | PASS |
| Integration suite against the local PostgreSQL container | `dotnet test --project Ledger.IntegrationTests` | 201 total, 200 passed, 1 skipped (packaged bundle needs a built bundle), 0 failed | PASS |
| Debt markers (TBD, FIXME, XXX, TODO, HACK) in tracked non-planning files | grep | none | PASS |
| `//` comments in C# | grep | none | PASS |
| Planning references (requirement keys, phase or plan numbers, planning document names, finding IDs) outside `.planning/` | grep over tracked files | none | PASS |
| IBAN-like strings or private IP addresses in tracked files | grep | none | PASS |

### Probe Execution

No `probe-*.sh` scripts are declared by the plans or present under `scripts/`. SKIPPED.

### Requirements Coverage

| Requirement | Source plans | Description | Status | Evidence |
| --- | --- | --- | --- | --- |
| INGEST-01 | 02-01, 02, 03, 07, 08, 13, 14, 15 | Link once, then daily sync unattended | SATISFIED (joint accounts; savings not offered by ING, fallback agreed) | Live link and unattended scheduled sync |
| INGEST-02 | 02-04, 05, 09, 12, 14, 15, 16 | No duplicates; pending that books stays one | SATISFIED | Reconciler and store tests; live rerun inserted 0. Live pending-to-booked still to be observed (human item) |
| INGEST-03 | 02-04, 09, 16 | Exact decimals, both dates, counterparty, description, status, raw payload retained | SATISFIED | Schema `numeric(19,4)`, `date` columns, append-only `jsonb` payload; field-fidelity test |
| INGEST-04 | 02-02, 07, 10, 11, 12, 14, 15 | Consent state tracked, warned at 14 days, guided renewal keeps history | SATISFIED in code and tests; live renewal and alert email pending (human items) | `ConsentState`, metrics, 14/7/expired rules, `ApplyRenewalAsync` |
| INGEST-05 | 02-02, 07, 08, 13, 15 | First sync requests longest history | SATISFIED | 3481 transactions back to the 24-month limit; `HistoryDepth.Longest` |
| INGEST-06 | 02-04, 13 | Provider interface | SATISFIED | `IBankDataProvider`; Enable Banking, synthetic and disabled implementations |
| INGEST-07 | 02-02, 08, 10, 12, 13 | Rate limits respected; failed syncs visible | SATISFIED | `CallBudget`, `ProviderCallMeter`, rate-limit mapping, failure metrics and alert |
| SEC-01 | 02-13, 14, 15 | Read-only bank access, no payment path | SATISFIED | `AisOnlyGuardHandler`, interface shape, AIS-only application |
| OPS-01 | 02-10, 15 | `/metrics` with last success, errors, days to expiry | SATISFIED for the sync, consent and error metrics. The review-queue size named in the requirement text belongs to the later categorisation work and is not part of this phase's criteria | Live scrape |
| OPS-02 | 02-10, 15 | Alerts on sync failure and at 14 and 7 days | SATISFIED in configuration; delivery of the failure and expiry alerts not yet seen live (human item) | `household-rules.yaml` |
| DASH-05 | 02-06, 11, 15 | EN and NL generated from one source | SATISFIED | Generator and drift tests |
| DASH-07 | 02-04, 06, 11, 15 | Read-only reporting role, cannot write | SATISFIED | Role tests; live 42501 |

Orphaned requirements: none. Every ID mapped to this phase in the requirements traceability is claimed by at least one plan. API-01 appears in plans 02-07 and 02-08 but is mapped to a later phase; the bank-link endpoints here are a partial head start, not a claim of completion, and it stays pending.

### Anti-Patterns Found

None blocking. No debt markers, no `//` comments, no planning references or personal data in tracked non-planning files. Informational: review notes record that a pending item that stays pending for two or more days can still raise the drift flag on two consecutive daily snapshots (existing design; the follow-up made exposed pending items neutral in the undated check), and the alert documentation phrases the 14-day band as "in 14 days or less, but not yet within 7" while the rule is strictly under 14 and at least 7 (immaterial).

### Gaps Summary

No unmet must-haves in code or behavior. The savings-account wording of the first criterion is met through the agreed fallback (override above): ING does not expose savings accounts through this link, so savings balances and interest stay out of the ledger, while transfers to and from savings are visible on the joint current accounts. The roadmap and requirement wording could be reworded to "joint account and any ING savings account the bank link offers" so it stays true; that is a documentation edit for the planning owner.

Human verification is needed for the four items in the frontmatter: alert rule state and real alert delivery (including recipients), the first live renewal, the first live pending-to-booked transition, and the ING-app check that the spike consent is gone.

---

_Verified: 2026-10-07_
_Verifier: Claude (gsd-verifier)_
