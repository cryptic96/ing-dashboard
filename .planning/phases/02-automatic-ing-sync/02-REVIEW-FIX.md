---
phase: 02-automatic-ing-sync
fixed_at: 2026-10-07T00:00:00Z
review_path: .planning/phases/02-automatic-ing-sync/02-REVIEW.md
iteration: 1
fix_scope: critical_warning
findings_in_scope: 34
fixed: 34
skipped: 0
status: all_fixed
---

# Phase 2 Code Review Fix Report

Critical and warning findings from 02-REVIEW.md, fixed in four parts (deploy/CI in its own worktree, the C# parts sequentially on the milestone branch). Info findings were left for later.

## Orchestrator verification

- The rewritten stale-sync alert expression was evaluated read-only against the live Prometheus on 2026-10-07: both metrics carry the same label set, so the `or` fallback only applies to accounts without a success series. The live result was 10212 seconds (about 2.8 hours since the last success), not the never-succeeded fallback.
- After the tests fixer's local history rewrite, every finding ID in scope was confirmed present in the commit log and the deploy fixer's merge commit was intact.
- Follow-up to the domain part, requested by the orchestrator: pending items the bank exposes are neutral in the undated reconciliation (both sides exclude live pending rows), so a payment pending over a weekend cannot raise the drift flag.

## Part A: Domain and Repository (9 of 9 fixed, 0 skipped)

**Fixed at:** 2026-10-07
**Source review:** 02-REVIEW.md, Part A (CR-101, WR-101 to WR-108; IN-1xx not in scope)
**Iteration:** 1

**Summary:**
- Findings in scope: 9
- Fixed: 9
- Skipped: 0

Verification ran in the main checkout (no worktree) against the local postgres-dev container:
`dotnet test --project Ledger.UnitTests` 388 passed, 1 skipped (real-capture replay needs an env var);
`dotnet test --project Ledger.IntegrationTests` 165 passed, 1 skipped (packaged bundle needs LEDGER_EFBUNDLE), no cleanup failures;
`bash build/lint.sh repo-rules` PASS. Working tree clean, every fix committed.

## Fixed Issues

### CR-101: Baseline balance snapshot includes pending items
**Commit:** 244d857 (fixed: requires human verification, logic change)
**Files:** `Ledger.Domain/Ingestion/BalanceReconciler.cs`, `IBalanceStore.cs`, `Ledger.Repository/Stores/BalanceStore.cs`, `Ledger.Service/Ingestion/SyncOrchestrator.cs`, unit and integration tests.
**Applied fix and the choice made:** `CheckUndated` takes a `pendingSumAtPrevious`. When the previous snapshot has no
expectation of its own (a baseline), the start is `previous.Amount - pendingSumAtPrevious`; otherwise the carried
expectation is used unchanged. The pending sum is a new `IBalanceStore.SumPendingAtAsync(account, instant)` that
reconstructs "pending at that fetch" from timestamps that are already stored and write-once: first seen at or before the
instant, and not yet booked (`booked_at`) or dropped (`dropped_at`) at that instant. A row that has since booked or been
dropped still counts, a row inserted directly as booked never counts.
I chose recompute-on-read over recording an adjustment at save time because it needs no schema change and therefore
corrects the two baseline snapshots already stored on the live host without any manual DB edit (append-only snapshots
cannot be updated, and a recorded adjustment would have left the old rows wrong). No migration, so no role or REVOKE changes.
Effect on the live case: baseline expectation becomes bank amount minus the two pending rows; once they book (booked_at
inside the next window) the next check matches and stays matching; if they are dropped the next check matches against the
bank balance that no longer contains them; if still pending the first check shows their sum as drift, as any new pending
item does (existing two-snapshot rule).
**Tests:** unit (booked, dropped, still pending, own expectation wins); integration (pending that books, pending that is
dropped, pending that stays pending shows drift) plus the existing reservation-then-book and persistent 0.01 drift tests
unchanged. One existing integration test (`Pending_and_dropped_transactions_never_enter_the_undated_window`) seeded a
baseline balance that ignored its own pending item; its baseline balance was changed from 1000.00 to 930.00 so the data is
realistic (bank balance includes the pending 70.00); its assertions are unchanged.

### WR-103: Dateless pending rows never widen the fetch window and never drop
**Commit:** 12645a8
**Files:** `ILedgerStore.cs`, `LedgerStore.cs`, `TransactionReconciler.cs`, `SyncOrchestrator.cs`, integration test.
**Applied fix:** one public `TransactionReconciler.EffectiveDate(...)` (transaction, booking, value date, else local first-seen
day in the sync zone) is used by matching, the drop rule and now the store's `OldestPendingDate`. `GetFetchWindowAsync` takes the zone.
**Tests:** `A_pending_item_without_any_date_is_dropped_once_the_bank_stops_listing_it_even_weeks_later` (verified to fail without the fix).

### WR-108: Hard-coded Europe/Amsterdam in the reconciler
**Commit:** 167643e
**Applied fix:** `ReconcilerOptions(int MatchWindowDays, TimeZoneInfo Zone)`; the static zone is gone; the orchestrator passes the configured sync zone (production validation already checks that zone at startup). Unit test added for zone-dependent first-seen day.
**Not changed:** the zone literal inside the old view migrations; it is the display zone and migrations are append-only.

### WR-101: Flagged pending row never dropped
**Commit:** a17bbaa
**Applied fix:** removed the ambiguous-flag exemption from the drop rule. A flagged row the bank still lists stays pending and flagged. Unit tests (one renamed, one added) and integration tests (new one; `Flagged_pending_payment_that_books_under_its_own_reference_loses_its_flag` now expects the unlisted flagged twin to be dropped, 2 reported rows instead of 3).

### WR-102: Reference outside the loaded window causes PK violation
**Commit:** 47e7902
**Applied fix:** `LedgerStore.ApplyAsync` first resolves planned insert and merge references against the database. An insert for a known reference becomes an update of the owning row (restore enabled; a pending observation never touches a booked owner); a merge onto a known reference is left out. Reproduced with a booked item without any date, which the bank returns on every fetch and the date-windowed load misses; the integration test fails with `pk_transaction_refs` without the fix.

### WR-105: Re-link leaves the old connection active
**Commit:** 00b1414
**Applied fix:** `AddConnectionAsync` runs in a transaction, de-duplicates session accounts by identification hash, and when it re-attached any known account supersedes every other live connection of the provider that no longer owns an account. `Connections_show_the_consent_state_and_whole_days_left_and_never_a_session_id` relinked the same account to keep two active connections (it encoded the defect); it now checks the second link separately.

### WR-107: Unconditional status and run completion overwrites
**Commit:** 7b612c5
**Applied fix:** `MarkStatusAsync` applies only from allowed source states (active only from provider-expired; provider-expired and superseded only from active or provider-expired; revoked from active, provider-expired or superseded; revoked never leaves) and keeps the first closing time. `FinishAsync` is guarded by `finished_at IS NULL` and now returns whether it applied (`Task<bool>`; existing callers unaffected). Integration tests added.

### WR-104: NUL characters poison the sync
**Commit:** 80c6c59
**Applied fix:** `Validate` rejects an item with a NUL in reference, counterparty, account number, description, or any JSON string/key of the payload (token walk, so an escaped backslash followed by `u0000` text is not a false positive) as `MalformedData` with code `text_nul`. The run is now `FailedMalformed` instead of an endless transient retry. Trade-off: one such item blocks its account until the bank stops sending it, which is the loud behaviour the review preferred (no per-item quarantine, which would need a schema change).

### WR-106: View versus metric disagreement, consecutive rule, hard-coded 14 days
**Commit:** ece1017 (fixed: requires human verification, rule change)
**Applied fix:** new migration `CheckVerdictsAcrossRecentSnapshots` (`CREATE OR REPLACE VIEW`, so the reader's SELECT-only grant is kept; Down restores the previous definition; generator headers stripped from migration, designer and snapshot). The view now takes the verdict from the latest snapshot that has one, like the metric, and both require the previous mismatch to be at most 3 days older (`BalanceReconciler.MaxDaysBetweenFlaggedMismatches`; the same literal in the view). The unused `warnDays` parameter of `ConsentState.Derive` is removed in favour of one `WarnDays = 14` constant (alert rules, docs and the view already use 14); its single test of the parameter was removed. Tests: view (verdict hidden by a verdict-less latest row; 3, 4 and 21 day gaps) and metric (mismatches three weeks apart).
Judgement call: the 3-day limit is my choice for "immediately preceding or at most N days back"; change both the constant and the view literal together if a different tolerance is wanted.

## Skipped Issues

None.

## Notes for the following fixers

- `ILedgerStore.GetFetchWindowAsync` gained a `TimeZoneInfo` parameter, `IBalanceStore` gained `SumPendingAtAsync`, `ISyncRunStore.FinishAsync` returns `Task<bool>`, `ReconcilerOptions` requires a zone, `ConsentState.DefaultWarnDays` was renamed `WarnDays` and lost its parameter. Service callers were adapted (SyncOrchestrator only); no Service-layer behaviour beyond those call sites changed.
- Out of scope but observed: because `SyncOrchestrator` only reconciles once per day and a pending item that stays pending for two or more days still shows as drift on two consecutive snapshots, a slow card payment can still raise the flag; that is the existing design, not changed here.
- Test files touched beyond new ones: `BalanceReconcilerTests`, `TransactionReconcilerTests`, `ConsentStateTests`, `CapturedPairReplayTests` (options constructor), `BalanceSnapshotTests`, `ReconciliationPipelineTests`, `BankLinkEndpointTests`, `AccountStatusViewTests`, `SyncMetricsTests`. New: `ReconciliationRobustnessTests`, `ConnectionLifecycleTests`.

## Follow-up: exposed pending payments are neutral (CR-101, second commit)

**Commit:** 9290ad4 `fix(02): CR-101 treat exposed pending payments as neutral in undated reconciliation` (fixed: requires human verification, logic change). Replaces the limitation noted above that a payment pending for two or more days could raise the flag.

**Change:** `CheckUndated` now compares booked with booked on both sides. Bank side: `current.Amount - pendingSumAtCurrent`, where the pending sum is `SumPendingAtAsync(account, currentFetchedAt)` evaluated after the run's apply. Expectation side: previous snapshot's own expectation (or, for a baseline, its amount minus the pending live at its fetch) plus the rows first seen as booked in the window. Drift is the bank booked balance minus that expectation, tested with exact decimal equality. When the current check is itself a baseline nothing changes. The orchestrator computes the current pending sum whenever a previous snapshot exists.

**Stored values:** `expected_amount` stays a booked-only expectation (the same meaning as under the previous commit, so a checked snapshot stored by the earlier release stays compatible) and `drift_amount` is the bank's booked balance minus it. With pending items live, `amount - expected` therefore no longer equals the drift; the entity and status-store doc comments say so. No schema change, no migration. The view and metric rule (flag only on two mismatches at most three days apart) is untouched.

**Behaviour:** a pending item the bank exposes causes no drift however long it stays pending, counts once when it books, and leaves no trace when dropped. Only a reservation deducted from the expected balance without a pending row gives one unflagged mismatch and matches after it books; an unexplained 0.01 still mismatches and flags on the second snapshot.

**Tests:** unit (pending for three snapshots then books; pending that appears after the previous fetch; unexposed reservation; unexplained cent with pending taken out on both sides) and integration (pending stays for three snapshots then books with the status store flag true, never false/null-flagged; pending appearing after the baseline; unexplained 0.01 with an exposed pending still flags on the second snapshot). The earlier book-next-day and dropped integration tests pass unchanged; the earlier "has not booked yet shows drift" test was replaced by the neutral-pending test, one existing test's second-day bank balance changed from 990.00 to 960.00 so it includes its exposed pending 30.00, and the reservation test was renamed to say the bank deducts it without exposing it.

**Verification:** unit 391 passed, 1 skipped; integration 167 passed, 1 skipped; `build/lint.sh repo-rules` PASS. Tree clean.

---

_Fixed: 2026-10-07_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_

## Part B: Service and Dashboards (6 of 6 fixed, 0 skipped)

**Fixed at:** 2026-10-07
**Source review:** 02-REVIEW.md, Part B (CR-201, WR-201 to WR-205; IN-2xx not in scope)
**Iteration:** 1

**Summary:**
- Findings in scope: 6
- Fixed: 6 (CR-201 mitigated within the design, see below)
- Skipped: 0

**Verification** ran in the main checkout (no worktree), against the local postgres-dev container:
`dotnet test --project Ledger.UnitTests` 404 total, 403 passed, 1 skipped (real-capture replay needs an env var);
`dotnet test --project Ledger.IntegrationTests` 187 total, 186 passed, 1 skipped (packaged bundle needs LEDGER_EFBUNDLE), none failed;
`bash build/lint.sh` all six checks PASS (repo-rules, workflows, shell, secrets, script-tests, observability).
Working tree clean, every fix committed (6 commits after 9290ad4).

## Fixed Issues

### WR-205: Production validation does not require the reverse-proxy trust list
**Commit:** d82b0ce
**Files:** `Ledger.Service/Hosting/ProductionConfigurationValidator.cs`, `Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs`
**Applied fix:** with the EnableBanking provider, production startup now requires at least one entry in `ReverseProxy:KnownProxies` and rejects the whole list when any entry is not an IP address. Only the key name is reported. Provisioning already writes `ReverseProxy__KnownProxies__0`, so a provisioned host is unaffected. Docs: noted in the bank-link setup step.
**Tests:** missing, empty and hostname values name only the key; no provider needs no proxy; the complete-configuration tests now carry a proxy.

### WR-201: Post-link sync without PSU treated as metered background
**Commit:** d168685
**Files:** `ProviderCallMeter.cs`, `SyncOrchestrator.cs`, new `PostLinkSyncTests.cs`
**Applied fix:** the meter gets an `enforceBudget` flag. The orchestrator passes false for `SyncTrigger.PostLink`, so that fetch is never refused or cut by the background allowance. Its calls are still written to the call ledger, and recorded as background when no operator presence was sent (honest to what the bank saw; this does count against the rolling window for the next scheduled run). Operator-started syncs already carried PSU context when available: the callback, the account selection and sync-now all pass `PsuContextFactory.FromRequest`; the post-link recovery path (see WR-204) has no request and runs unattended, now without the cap.
**Tests:** 8-page post-link sync with an allowance of 4 and no presence succeeds and stores everything; manual and scheduled syncs in the same setup still stop with quota exhausted and apply nothing; an attended post-link run is recorded as attended.

### WR-202: Failed completion leaves a live consent at the bank
**Commit:** 995f38e
**Files:** `BankLinkService.cs`, `EnableBanking/EnableBankingClient.cs`, tests (`CompletionFailureTests.cs`, `EnableBankingClientTests.cs`)
**Applied fix:** best-effort `RevokeSessionAsync` (cancellation token none) when, after a successful code exchange, the account list is empty, `AddConnectionAsync` fails, `ApplyRenewalAsync` fails (both the "no longer renewable" rejection and unexpected errors), or the adapter cannot read the session (validity or account mapping). A failed revoke logs only the failure kind and a fixed sentence; the session id is never logged and never replaces the original failure. Recording a connection or renewal after the exchange no longer uses the request's cancellation token, so a browser that disconnects cannot strand a consent.
**Tests:** unit (unusable session answer is followed by a DELETE of that session; a failing DELETE does not hide the original failure); integration with a store that fails on demand and the fake provider (no accounts, link persistence failure, renewal persistence failure keeps the old connection active, failing revoke leaks nothing to logs or response).

### WR-203: Orphan cleanup gated on the scheduler; FinishAsync failure; startup DB blip
**Commit:** fc9d190 (logic change, requires human verification of the startup ordering)
**Files:** new `OrphanedRunRecovery.cs`, `SyncScheduler.cs`, `SyncOrchestrator.cs`, `IngestionServiceCollectionExtensions.cs`, `ISyncRunStore.cs`, `Repository/Stores/SyncRunStore.cs`, tests
**Applied fix:**
- The cleanup moved out of the scheduler into a hosted service registered before the worker and scheduler. It runs on every start, with or without the scheduler or a provider. The first attempt is made in `StartAsync` and never throws; on failure it logs the exception type and retries in the background (5 s, 15 s, 30 s, then every minute) until it succeeds, so a database blip at startup no longer faults the host.
- `ISyncRunStore.AbandonUnfinishedAsync` gained a `startedAtOrBefore` argument and the cleanup passes the process start time, so a late retry can never abandon a run that this process started.
- The final `FinishAsync` of a run is retried (250 ms, 1 s, 3 s) and is no longer tied to the caller's cancellation. If all attempts fail, the orchestrator logs that the run stays open and returns the real outcome instead of throwing, so the scheduler no longer logs a generic failure. Note: the run row itself then stays open until the next restart (documented in the log message); a run-level recovery for that case was not added.
**Tests:** scheduler-off host abandons the orphan at start; cleanup that fails at start keeps the host up and succeeds after the retry delay; a finish that fails twice still ends the run; a finish that never works is logged without a misleading failure. `FinishAsync` returning `Task<bool>` is handled.

### WR-204: Dispatcher dedupe, in-memory queue, renewal sync skipped when full
**Commit:** 8509b4f
**Files:** `SyncDispatcher.cs`, `BankLinkService.cs`, `BankEndpoints.cs`, `SyncScheduler.cs`, `IngestionServiceCollectionExtensions.cs` (no change needed), tests (`SyncDispatcherTests.cs`, `SyncQueueTests.cs`, `RecordingSyncDispatcher` updated)
**Applied fix:**
- `ISyncDispatcher.TryEnqueue` now returns `EnqueueResult` (`Queued`, `AlreadyQueued`, `Full`). Requests are collapsed per (connection, trigger) from queueing until the worker has finished them. A post-link request is never collapsed into a manual one. `POST /sync` answers 409 "already queued" for a duplicate.
- A post-link sync that finds the connection busy (for example a scheduled run started in the same minute) waits 10 s and retries up to 30 times instead of being skipped; exhausting that is logged as a warning.
- A full queue after a renewal (or a relink with already selected accounts) is logged as an error and the callback page tells the operator to start the sync with a sync request. The renewal itself is already recorded, so the callback cannot be failed at that point.
- The in-memory queue is mitigated rather than persisted: the scheduler starts a `PostLink` sync for an active connection with selected accounts that was approved between 5 minutes and 2 hours ago and has no run at all, so a restart that loses the queued request does not lose the first sync. Beyond two hours the daily schedule takes over (it also reads the longest history for an account without transactions).
**Tests:** dispatcher collapse, independence of triggers and connections, completion re-opens a key, full queue leaves no stale key; duplicate `POST /sync` is refused; busy post-link waits and then runs; renewal with a full queue still renews and reports it; scheduler recovery for a never-synced connection (no allowance cap) and the two-hour limit.

### CR-201: Full-history sync after a first link is deferred behind a manual account selection
**Commit:** 759cd24
**Status:** mitigated by design (not changed to fetch at link time).
**Why:** choosing the accounts before anything is read is a deliberate privacy decision (unselected accounts, such as a partner's or personal account, are recorded but never fetched, so their transactions never enter the database). Fetching every offered account right after approval, as the review suggested, would break that, so the flow keeps its shape and the risk of losing the window is reduced instead.
**Applied mitigation:**
- The callback page for a first link now says, in plain language, to select the accounts now, before anything else, because the bank returns the full history only for about an hour after approval and an account that is not selected is never read. A renewal page says "renewed" and only asks for a selection when new accounts appeared.
- `GET /api/v1/bank/connections` entries gained `selectionPending` and a `hint` (null when not pending) for a connection that is active, has no selected account and no run.
- The scheduler's early warning moved from 30 to 5 minutes after approval, with a second one after 45 minutes saying the window has probably closed and the connection should be renewed (a renewal is approved right then and syncs the longest history again).
- Linking again with accounts that were selected before now queues the first sync from the callback, since no selection is needed.
- `docs/bank-link.md`, `docs/rest-api.md` and `docs/bank-link.http` state all of this clearly, including that renewing gives another full-history window if the hour is missed. No planning references.
**Tests:** callback text, `selectionPending`/`hint` before and after selection, relink queues a post-link request without asking for a selection, warning cadence (none before 5 minutes, one at 5, still one at 40, a second after 45, none repeated).

## Skipped Issues

None.

## Notes for the tests fixer and reviewers

- Interface changes: `ISyncDispatcher.TryEnqueue` returns `EnqueueResult`; `ISyncRunStore.AbandonUnfinishedAsync(now, startedAtOrBefore, ct)`; `SyncScheduler.AbandonOrphanedRunsAsync` is gone (now `OrphanedRunRecovery.AbandonAsync(startedAtOrBefore, ct)`); `ProviderCallMeter` constructor gained `enforceBudget`; `SyncWorker` takes a `TimeProvider`; `CallbackOutcome` and `ConnectionOverview` gained optional members.
- The test factory still builds two complete hosts per instance, so start-up recovery and its failure switch run twice; `RunRecoveryTests` is written to hold either way (the failure switch stays on until the first assertion is done).
- New test files: `PostLinkSyncTests`, `CompletionFailureTests`, `RunRecoveryTests`, `SyncQueueTests`, `SelectionReminderTests` (integration), `SyncDispatcherTests` (unit). `SchedulerAndQuotaTests` was edited: the restart test is renamed and runs with the scheduler off, the unselected-warning test follows the new cadence, `RecordingSyncDispatcher` returns `EnqueueResult`; `ConnectionLifecycleTests` uses the new abandon signature.
- Judgement calls worth a look: the 5-minute and 2-hour limits of the scheduler's first-sync recovery, the 5 and 45-minute warning ages, and the retry counts (post-link busy wait 30 x 10 s, run finish 3 retries).

---

_Fixed: 2026-10-07_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_

## Part C: Deploy, provisioning, CI and docs (6 of 6 fixed, 0 skipped)

**Fixed at:** 2026-10-07
**Source review:** .planning/phases/02-automatic-ing-sync/02-REVIEW.md (Part C)
**Iteration:** 1

**Summary:**
- Findings in scope: 6 (WR-301 to WR-306; no critical findings in Part C; IN-3xx out of scope)
- Fixed: 6
- Skipped: 0

Verification ran in the isolated worktree (branch worktree-agent-a20781f472df8ae80, base 3ffdea0). The final `timeout 900 bash build/lint.sh` passed all six checks (repo-rules, workflows, shell, secrets, script-tests, observability). The observability check boots the pinned Grafana image with the provisioning, so the changed alert rule loads.

## Fixed Issues

### WR-301: stale-sync alert fires on a successful same-day retry
**Files modified:** `deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml`, `docs/monitoring.md`
**Commit:** fdc0eea (shared with WR-302)
**Applied fix:** Threshold raised from 93600 s (26 h) to 129600 s (36 h), summary and docs text updated.

### WR-302: stale-sync alert silent when nothing has ever succeeded
**Files modified:** same as WR-301
**Commit:** fdc0eea
**Applied fix:** The rule now takes the per-account maximum of `time() - last_success`, and for accounts that have no last-success series it substitutes a very large value derived from `ledger_sync_calls_remaining` (which exists for every selected account, joined on the account key). `noDataState` stays OK so a host with no selected accounts stays quiet; no C# change needed. `for` raised from 10m to 2h so a freshly selected account has time to finish its first sync. `docs/monitoring.md` explains both conditions and that the alert starts watching an account once it is selected. Status: fixed, requires human verification (the PromQL was reasoned about and loads in Grafana, but not executed against a Prometheus with data; promtool is not available locally).

### WR-303: `ledger-bank-key generate` strands a key and leaks its work dir
**Files modified:** `deploy/bin/ledger-bank-key`, `deploy/tests/bank-key-logic-test.sh`
**Commits:** 79675e7, 375d668 (shellcheck follow-up)
**Applied fix:** Key, certificate and public key are staged as `*.new` files, the password and key path are written to the env file, and only then renamed into place, so the final key never exists without its password. Every step is checked explicitly (the function may run where errexit is ignored). A cleanup function removes the work dir, staged files, exported password and restores the umask on success, failure and INT/TERM/HUP. `bank_key_set_env_value` now returns non-zero on any write failure instead of continuing with an empty temp name. New tests: env file not writable leaves no key, no work dir, no exported password, umask restored, and a retry succeeds without manual cleanup.

### WR-304: apt keyrings and sources only written on first install
**Files modified:** `deploy/provision.d/10-packages.sh`, `deploy/tests/provision-logic-test.sh`
**Commit:** f543ee8
**Applied fix:** New `apt_keyring_matches_pin` compares the installed keyring's fingerprint with the pin on every run; mismatching, missing, empty or truncated keyrings are reinstalled (dearmor goes to a temp file and is installed, so no half-written keyring is left). New `apt_source_line` and `install_apt_source` rewrite the three `*.list` files whenever their content differs. Library-level tests use throwaway gpg keys in a temp GNUPGHOME.

### WR-305: msmtp log not creatable under ProtectSystem=strict
**Files modified:** `deploy/provision.d/40-services.sh`, `deploy/tests/render-templates-test.sh`
**Commit:** 0aa6f7b
**Applied fix:** New `services_ensure_msmtp_log` creates `/var/log/msmtp.log` (root:adm, 640) when absent, called next to the msmtprc install; an existing file is never touched. The `-` prefix in the unit's ReadWritePaths is kept deliberately so the unit still starts if it ever runs before provisioning; the sandboxing test passes unchanged. Tests cover creation, mode and idempotence.

### WR-306: attestation re-verification not bound to the tag's commit
**Files modified:** `.github/workflows/release.yml`, `docs/releasing.md`
**Commit:** 5400746
**Applied fix:** Added `--source-digest "$GITHUB_SHA"` to the publish-time `gh attestation verify` (gh 2.102 help confirms the flag, alongside `--bundle`). The self-verification example in the docs shows the flag. Status: fixed, requires human verification on the first real release run (the workflow is only exercised by a tag push).

## Skipped Issues

None.

---

_Fixed: 2026-10-07_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_

## Part D: Tests (13 of 13 fixed, 0 skipped)

**Fixed at:** 2026-10-07
**Source review:** 02-REVIEW.md, Part D (WR-401 to WR-413; IN-4xx not in scope)
**Iteration:** 1

**Summary:**
- Findings in scope: 13
- Fixed: 13 (12 commits; WR-403 and WR-404 share one commit, as WR-301/302 did)
- Skipped: 0
- Only test code, test infrastructure and test-only helpers changed. No production code was modified (every mutation used for proof was reverted).

Verification ran in the main checkout (no worktree) against the local postgres-dev container:
- `dotnet test --project Ledger.UnitTests`: 433 total, 432 passed, 1 skipped (real-capture replay needs an env var).
- `dotnet test --project Ledger.IntegrationTests`: 201 total, 200 passed, 1 skipped (packaged bundle needs LEDGER_EFBUNDLE). Run three times after the last functional change (60 s, 58 s, 72 s), no flake, none failed. Baseline before the fixes was 187 tests, 74 s.
- `bash build/lint.sh`: all six checks PASS (repo-rules, workflows, shell, secrets, script-tests, observability).
- After the runs, `pg_database` holds no `ledger_%` database; the container was never stopped or restarted and nothing but own test databases was dropped.

History note: the first version of WR-412 put a PEM header literal and a base64 blob into test inputs, which the secrets lint (full-history gitleaks) flagged. Because those commits were local and not yet pushed (branch 35 ahead of origin at the time), the fix was folded into the WR-412 commit with a fixup and autosquash. Commit hashes below are the final ones.

## Fixed Issues

### WR-402: Replay order nondeterministic for equal timestamp and account
**Commit:** 5be4333
**Files:** `Ledger.UnitTests/Ingestion/EnableBanking/CapturedPairReplayTests.cs`
**Applied fix:** added `.ThenBy(label, Ordinal)` to the capture ordering. New test `Captures_with_the_same_second_and_account_replay_in_label_order` writes a booked capture under label `b-second` first and a pending capture of the same reference under `a-first` second, and expects the pending-then-booked upgrade (`Upgraded == 1`).
**Proof it can fail:** temporarily changed to `ThenByDescending`; the new test failed ("Expected result.Upgraded to be 1 ... found 0"); reverted.

### WR-401: Reference-on-two-rows invariant could never fail
**Commit:** 58ed505
**Files:** `Ledger.UnitTests/Ingestion/InMemoryLedger.cs`, `CapturedPairReplayTests.cs`
**Applied fix:** the in-memory ledger no longer throws when a reference would map to a second row; it records it, so `ReferencesOnMoreThanOneRow` (computed from the rows) and `ReplayInvariants.Verify`'s first branch are live. The unused reference dictionary was removed. New test `A_reference_that_maps_to_two_rows_is_counted_and_fails_the_replay_naming_the_invariant` applies two plans that reuse one reference, expects the count 1 and the exact `ReplayFailedException` message "Invariant broken: 1 references map to more than one row." with no captured value. The real-capture test's two assertions are now meaningful.
**Proof it can fail:** temporarily changed `duplicated > 0` to `> 99` in `Verify`; the new test failed; reverted.

### WR-411: Assertions that cannot fail
**Commit:** a3005bb
**Files:** `Ledger.UnitTests/Ingestion/SyncScheduleTests.cs`, `Ledger.IntegrationTests/Ingestion/EnableBankingPipelineTests.cs`
**Applied fix:** the second `Decide` call of the next-local-day test now receives the evening failure (`runs`), as the review suggested. The pipeline test now takes the second fetch as a list and asserts exactly three requests (the same three pages again) before `OnlyContain(date_from != null)`.
**Proof it can fail:** pipeline test with `.Skip(30)` (empty second fetch) failed with "Expected secondFetch to contain 3 item(s) ... found 0"; the schedule test failed (3 requests returned `Scheduled`/`Retry`) when the `now < ScheduledInstant` guard was disabled; both reverted.
**Honest limitation:** `SyncSchedule.Decide` checks the scheduled time before the retry moment and its `LocalDate(retryAt) == today` clause is redundant for any run started today (a retry moment on the next day is always after `now`), so no test can isolate that clause. The strengthened assertion pins the observable behaviour (no retry from yesterday's failure at 01:00) but not that dead clause.

### WR-412: Committed-secret scan matched key names only
**Commit:** 5b56ace
**Files:** `Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs`
**Applied fix:** new `CommittedSecretScanner` (in the test file). JSON: walks every key and string value; key markers widened (password, passwd, pwd, secret, token, apikey, privatekey, credential, accesskey, signingkey); every string value is checked for an embedded credential assignment (`Password=`, `Pwd=`, `Secret=`, `Token=`, `ApiKey=` ... with a non-empty value, so `Password=;` and plain hosts pass), PEM headers, credentials inside URLs and 40+ character base64-like blobs. Findings name the path and kind and never echo the value. Locations scanned: `Ledger.Service/appsettings*.json`, all `deploy/provisioning/**/*.json`, `Ledger.Dashboards/translations.json`, and the `deploy/*.example` environment files (value-shaped checks only, so placeholders such as `generated-by-provisioning` stay legal). Added scanner theories: 10 secret-shaped JSON inputs, 5 clean JSON inputs, 5 secret-shaped environment lines, 5 placeholder lines. PEM header and blob test inputs are built from split constants so the repository's own secret lint does not trip on them.
**Proof it can fail:** appended `;Password=<synthetic>` to the connection string in `appsettings.Production.json`; the appsettings test failed naming `ConnectionStrings:Ledger` without the value; reverted.

### WR-408: Process-global environment mutation never restored
**Commit:** 8c5460d
**Files:** new `Infrastructure/EnvironmentOverride.cs`, new `Infrastructure/HostIsolationTests.cs`, `LedgerWebApplicationFactory.cs`, `BankLinkEndpointTests.cs`, `EnableBankingPipelineTests.cs`, `LogRedactionTests.cs`
**Applied fix:** `EnvironmentOverride` captures the previous value (or absence) and restores it in reverse order. The factory owns it: constructor takes `startupEnvironment`, always clears `Ingestion__Provider` and sets the two `DataProtection__Certificate*` variables from its arguments, applies all of them only around `EnsureHostStarted()` in a `using`, so values are restored on success and on a failed start, and an ambient developer value can no longer leak into a test. The three copy-pasted try/finally blocks are gone (`startupEnvironment: { Ingestion__Provider = EnableBanking }`). Tests: override restores value and absence; a started host ignores ambient `Ingestion__Provider=EnableBanking` (which would break startup) and restores ambient values afterwards; a failed startup restores them too.
**Proof it can fail:** made `Dispose` a no-op; all three tests failed; reverted.
**Not done:** making the production composition read these two values from supplied configuration instead of the environment (the review's "better still"); that would be a production change, and the startup-only override is now safe.

### WR-409: Two complete hosts per factory instance
**Commit:** a4769c6
**Files:** `LedgerWebApplicationFactory.cs`, `HostIsolationTests.cs`
**Applied fix:** the base class builds the app once per `Build()` (each build runs the program) and insists on an in-memory test server host, so two builds remain, but the dormant one now has every application `IHostedService` removed (only the web server host service is kept), via a flag-guarded `ConfigureServices` that is switched off before the Kestrel build. `Services` is overridden to return the running Kestrel host's services, so the 23 test call sites that resolve `factory.Services` now use the same singletons as the real endpoints (previously a second container). New test registers a counting hosted service and expects exactly 1 start and that `factory.Services` returns that same instance.
**Proof it can fail:** before the fix the test failed with "Expected counter.Starts to be 1, but found 2"; after the fix it passes. All 201 integration tests pass with the single running host, including the scheduler, restart, queue and recovery tests.

### WR-406: Log capture limited to the configured level
**Commit:** 9a865f4
**Files:** `LedgerWebApplicationFactory.cs`, `HostIsolationTests.cs`
**Applied fix:** the capture provider gets a provider-specific Trace rule for every category, then re-applies each category level pinned in the committed `Logging:LogLevel` (for example `System.Net.Http.HttpClient`, `Microsoft.AspNetCore.Hosting.Diagnostics`, EF commands: Warning), so Debug and Trace are captured everywhere the committed configuration does not pin a level on purpose. Forcing Trace for those pinned categories would make the redaction tests fail on request URLs that production configuration deliberately suppresses; the committed-configuration test keeps verifying the pin itself, as the review asked. New test logs at Debug and Trace through the host's logger factory and expects both messages in the capture.
**Proof it can fail:** with the capture rule set to Information the new test failed (messages absent); reverted. No existing redaction test started failing with Debug and Trace on.

### WR-410: Aborted runs leave `ledger_it_*` databases behind
**Commit:** bb3f98a
**Files:** `Infrastructure/DatabaseFixture.cs`, new `Database/StaleDatabaseSweepTests.cs`
**Applied fix:**
- New name pattern `ledger_it_<yyyyMMddHHmmss UTC>_<16 hex>` (41 characters) embeds the creation time.
- At the start of `InitializeAsync` (after the role bootstrap, best-effort and never failing the run) `SweepStaleDatabasesAsync(now, 2 hours)` selects from `pg_database` only names matching `^ledger_it_[0-9]{14}_[0-9a-f]{16}$` that have no row in `pg_stat_activity`, checks in C# that the embedded time is more than two hours old, and runs a plain `DROP DATABASE IF EXISTS` (no FORCE; a failing drop because a session appeared is ignored). Every other name, including the old unstamped `ledger_it_<32 hex>` form and any non-test database, is never touched. A name-embedded timestamp was chosen over `pg_stat_file` because it needs no superuser file access.
- Each name is now registered before `CREATE DATABASE` runs, and `InitializeAsync` drops what it created if it fails part-way.
- `RegisterForCleanup` (accepts only throwaway names) and `AdminConnectionStringFor` let the sweep test clean up after itself.
**Tests:** name format and length; one test creates a stale idle, a fresh, a stale-but-connected, a legacy-named and a foreign-named database, sweeps, and checks that only the stale idle one is dropped and the other four survive.
**Proof it can fail:** with the age check removed the test failed (the fresh database and the fixture's own were dropped); reverted. Note the previous leftover databases of aborted runs with the old name form are not swept (no timestamp); there were none in the container.

### WR-405: Certificate-password redaction test passed on any failure
**Commit:** 701e4bc
**Files:** `Security/LogRedactionTests.cs`
**Applied fix:** the factory takes the caller's `CapturingLoggerProvider`, so the logs of a host that fails to start are readable. The test now requires the exception chain (including `AggregateException` branches) to name `DataProtection:CertificatePath`, and searches `Message`, `ToString()`, every `Data` value of each exception and every captured log line for the password sentinel. The unused `AllMessages` helper became `Flatten`.
**Proof it can fail:** temporarily appended the password to the production exception message; the test failed; reverted.

### WR-407: Adapter test used the encrypted key line as the sentinel
**Commit:** 8f09d84
**Files:** `Security/LogRedactionTests.cs`
**Applied fix:** sentinels now include, held in memory only, the base64 of the decrypted PKCS#8 DER, every unencrypted PEM body line after the constant first line, the private numbers D, P, Q as base64 and hex, and the signature segment of every client token, besides the previous values. The test asserts the sentinel list is large so it cannot silently shrink.
**Proof it can fail:** temporarily logged one unencrypted PEM line at Debug from the host; the test failed with "Did not expect logText ... to contain"; reverted.

### WR-413: Poll helpers used wall-clock limits and returned silently
**Commit:** 68779b3
**Files:** new `Infrastructure/Wait.cs`, new `Infrastructure/WaitTests.cs`, `BankLinkEndpointTests.cs`, `SchedulerAndQuotaTests.cs`, `SyncQueueTests.cs`, `LogRedactionTests.cs`, `ApiKeyAuthTests.cs`, `HealthAndCanaryTests.cs`, `DataProtectionCertificateTests.cs`
**Applied fix:** one `Wait.UntilAsync` (monotonic stopwatch, 60 s default, `TimeoutException` naming what was awaited and the last observation) plus `Wait.UntilReadyAsync` and `Wait.ForHealthStatusAsync`, replacing the five copies of the ready and health-status waits. `WaitForReportedRowsAsync` now waits until no sync run is unfinished and the reader sees at least the expected rows, so the row count is the complete result of the sync and later exact-count assertions see the settled state. `WaitForFinishedRunsAsync` waits for the finished count and for no run still open. `WaitForRunOutcomeAsync` throws instead of returning null. `WaitForLogAsync` uses the same helper.
**Tests:** helper returns the first satisfying state; helper times out with a message naming the wait and the last value; scheduler host times out descriptively when no run ever finishes (0 finished and 0 unfinished runs).
**Proof it can fail:** replaced the throw with `return current`; both timeout tests failed; reverted.
**Limitation:** no test seeds a run that stays open to prove the "no unfinished run" half of the row wait; it is covered by reasoning and by the full suite staying green, not by a dedicated failing case.

### WR-403 and WR-404: Role tests sampled tables and attributes
**Commit:** 9603ed6
**Files:** `Database/DatabaseRoleTests.cs`
**Applied fix:** catalog-driven checks, one query each, so the class stays fast:
- Runtime role: exact privilege set (SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER) on every relation in `public` and `reporting`, compared with an explicit expected list; a new relation fails the test until its privileges are decided. Sequences: USAGE and SELECT but never UPDATE. Column-level `UPDATE` grants on tables without table-level UPDATE must be exactly the twelve mutable `transactions` columns. No CREATE on any schema, no CREATE or TEMPORARY on the database.
- Backup role (also renamed, the old name overclaimed): SELECT and nothing else on every relation, no sequence UPDATE, no creation rights.
- Database: only the schemas `public` and `reporting` exist; no function lives in either (default EXECUTE for PUBLIC would reach every role).
- Attributes for all four roles: `rolsuper`, `rolcreatedb`, `rolcreaterole`, `rolreplication`, `rolbypassrls` false and `rolcanlogin` true. Direct memberships exactly `ledger_backup -> pg_read_all_data`; `pg_has_role` shows no application role inherits another and none is a member of `pg_write_all_data`, `pg_execute_server_program`, `pg_read_server_files`, `pg_write_server_files` or `pg_signal_backend`.
- `CONNECT` on the database is granted to the three non-owner application roles and never to PUBLIC (ACL entries compared exactly).
The existing statement-based tests were kept. The grafana reader test was already exhaustive and unchanged.
**Proof it can fail:** with `GRANT DELETE ON provider_calls`, `GRANT UPDATE (first_seen_at) ON transactions` and `CREATE FUNCTION public.probe_function` executed as the migrator at the start of the three new tests, all three failed (plus the existing delete test); reverted (the grants lived only in that throwaway test database).
**Not done:** the roles are still exercised through the admin connection with `SET ROLE`, because the local container's own authentication rules cannot be assumed to allow passwordless logins as these roles. Authentication is covered by the `rolcanlogin` check here and by the deployed connection rules; a per-role login test needs a cluster configured like the deployment.

## Skipped Issues

None.

## Notes for reviewers

- Judgement calls worth a look: the 60 s default wait limit; the two-hour sweep age and the new database name pattern; keeping the committed log-level pins in the capture instead of forcing Trace everywhere (WR-406); keeping the web server host service in the dormant test host (WR-409); not asserting on the redundant retry-day clause (WR-411).
- Interface changes in test infrastructure: `LedgerWebApplicationFactory` gained optional `startupEnvironment` and `loggerProvider` arguments and overrides `Services`; `DatabaseFixture` gained `SweepStaleDatabasesAsync`, `NewDatabaseName`, `RegisterForCleanup`, `AdminConnectionStringFor`; `SchedulerTestHost.WaitForRunOutcomeAsync` returns `Task<string>` and throws on timeout; `BankLinkTestHost.WaitForReportedRowsAsync` takes an optional timeout and also waits for the runs to finish.
- The earlier note in the Part B report ("the test factory still builds two complete hosts") no longer applies: only one host runs background services and `factory.Services` is that host.

---

_Fixed: 2026-10-07_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
