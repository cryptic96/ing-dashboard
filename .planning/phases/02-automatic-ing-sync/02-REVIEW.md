---
phase: 02-automatic-ing-sync
reviewed: 2026-10-07T00:00:00Z
depth: standard
files_reviewed: 146
files_reviewed_list:
  - .github/workflows/ci.yml
  - .github/workflows/release.yml
  - .gitleaks.toml
  - Ledger.Dashboards/DashboardGenerator.cs
  - Ledger.Dashboards/Definitions/SyncDashboard.cs
  - Ledger.Dashboards/Ledger.Dashboards.csproj
  - Ledger.Dashboards/Model/Dashboard.cs
  - Ledger.Dashboards/Model/FieldConfig.cs
  - Ledger.Dashboards/Model/Panels.cs
  - Ledger.Dashboards/Program.cs
  - Ledger.Dashboards/Translator.cs
  - Ledger.Dashboards/translations.json
  - Ledger.Domain/Banking/IBankDataProvider.cs
  - Ledger.Domain/Banking/MoneyParser.cs
  - Ledger.Domain/Banking/ProviderModels.cs
  - Ledger.Domain/Ingestion/BalanceReconciler.cs
  - Ledger.Domain/Ingestion/CallBudget.cs
  - Ledger.Domain/Ingestion/ConsentState.cs
  - Ledger.Domain/Ingestion/IBalanceStore.cs
  - Ledger.Domain/Ingestion/IBankAuthorizationStore.cs
  - Ledger.Domain/Ingestion/IBankConnectionStore.cs
  - Ledger.Domain/Ingestion/IIngestionStatusStore.cs
  - Ledger.Domain/Ingestion/ILedgerStore.cs
  - Ledger.Domain/Ingestion/IProviderCallStore.cs
  - Ledger.Domain/Ingestion/ISyncRunStore.cs
  - Ledger.Domain/Ingestion/LedgerModels.cs
  - Ledger.Domain/Ingestion/LinkStateToken.cs
  - Ledger.Domain/Ingestion/OpaqueKey.cs
  - Ledger.Domain/Ingestion/ReconciliationPlan.cs
  - Ledger.Domain/Ingestion/SyncHealth.cs
  - Ledger.Domain/Ingestion/SyncSchedule.cs
  - Ledger.Domain/Ingestion/TextNormalizer.cs
  - Ledger.Domain/Ingestion/TransactionReconciler.cs
  - Ledger.Domain/Ingestion/TransactionRefs.cs
  - Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs
  - Ledger.IntegrationTests/Dashboards/DashboardQueryTests.cs
  - Ledger.IntegrationTests/Database/DatabaseRoleTests.cs
  - Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs
  - Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs
  - Ledger.IntegrationTests/Ingestion/AccountStatusViewTests.cs
  - Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs
  - Ledger.IntegrationTests/Ingestion/BankLinkEndpointTests.cs
  - Ledger.IntegrationTests/Ingestion/EnableBankingPipelineTests.cs
  - Ledger.IntegrationTests/Ingestion/FakeEnableBankingHandler.cs
  - Ledger.IntegrationTests/Ingestion/IngestionTestSupport.cs
  - Ledger.IntegrationTests/Ingestion/ReconciliationPipelineTests.cs
  - Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs
  - Ledger.IntegrationTests/Ingestion/SyntheticSyncPipelineTests.cs
  - Ledger.IntegrationTests/Ledger.IntegrationTests.csproj
  - Ledger.IntegrationTests/Metrics/SyncMetricsTests.cs
  - Ledger.IntegrationTests/Security/LogRedactionTests.cs
  - Ledger.Repository/Conventions/EnumText.cs
  - Ledger.Repository/Conventions/SnakeCaseNaming.cs
  - Ledger.Repository/Entities/BalanceSnapshotEntity.cs
  - Ledger.Repository/Entities/BankAuthorizationEntity.cs
  - Ledger.Repository/Entities/BankConnectionEntity.cs
  - Ledger.Repository/Entities/LedgerAccountEntity.cs
  - Ledger.Repository/Entities/LedgerTransactionEntity.cs
  - Ledger.Repository/Entities/ProviderCallEntity.cs
  - Ledger.Repository/Entities/SyncRunEntity.cs
  - Ledger.Repository/Entities/TransactionPayloadEntity.cs
  - Ledger.Repository/Entities/TransactionRefEntity.cs
  - Ledger.Repository/LedgerDbContext.cs
  - Ledger.Repository/Migrations/20260930181840_AddLedgerIngestion.cs
  - Ledger.Repository/Migrations/20260930183122_AddBankAuthorizations.cs
  - Ledger.Repository/Migrations/20260930185230_AddProviderCalls.cs
  - Ledger.Repository/Migrations/20260930191427_AddBalanceSnapshots.cs
  - Ledger.Repository/Migrations/20260930193516_AddAccountStatusView.cs
  - Ledger.Repository/Migrations/20261005152907_FlagDriftOnConsecutiveSnapshots.cs
  - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
  - Ledger.Repository/Stores/BalanceStore.cs
  - Ledger.Repository/Stores/BankAuthorizationStore.cs
  - Ledger.Repository/Stores/BankConnectionStore.cs
  - Ledger.Repository/Stores/IngestionStatusStore.cs
  - Ledger.Repository/Stores/LedgerStore.cs
  - Ledger.Repository/Stores/ProviderCallStore.cs
  - Ledger.Repository/Stores/SyncRunStore.cs
  - Ledger.Service/Endpoints/BankEndpoints.cs
  - Ledger.Service/Hosting/ProductionConfigurationValidator.cs
  - Ledger.Service/Ingestion/BankLinkOptions.cs
  - Ledger.Service/Ingestion/BankLinkService.cs
  - Ledger.Service/Ingestion/DisabledBankDataProvider.cs
  - Ledger.Service/Ingestion/EnableBanking/AisOnlyGuardHandler.cs
  - Ledger.Service/Ingestion/EnableBanking/EnableBankingClient.cs
  - Ledger.Service/Ingestion/EnableBanking/EnableBankingErrors.cs
  - Ledger.Service/Ingestion/EnableBanking/EnableBankingJson.cs
  - Ledger.Service/Ingestion/EnableBanking/EnableBankingOptions.cs
  - Ledger.Service/Ingestion/EnableBanking/EnableBankingTokenMinter.cs
  - Ledger.Service/Ingestion/IngestionOptions.cs
  - Ledger.Service/Ingestion/IngestionServiceCollectionExtensions.cs
  - Ledger.Service/Ingestion/ProviderCallMeter.cs
  - Ledger.Service/Ingestion/PsuContextFactory.cs
  - Ledger.Service/Ingestion/SyncDispatcher.cs
  - Ledger.Service/Ingestion/SyncMetricsRefresher.cs
  - Ledger.Service/Ingestion/SyncOrchestrator.cs
  - Ledger.Service/Ingestion/SyncScheduler.cs
  - Ledger.Service/Ingestion/Synthetic/SyntheticBankDataProvider.cs
  - Ledger.Service/Ingestion/Synthetic/SyntheticBankScenario.cs
  - Ledger.Service/Ingestion/Synthetic/SyntheticDemoScenario.cs
  - Ledger.Service/Ledger.Service.csproj
  - Ledger.Service/Metrics/SyncMetrics.cs
  - Ledger.Service/Program.cs
  - Ledger.Service/appsettings.json
  - Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs
  - Ledger.UnitTests/Dashboards/DashboardGeneratorTests.cs
  - Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs
  - Ledger.UnitTests/Ingestion/BalanceReconcilerTests.cs
  - Ledger.UnitTests/Ingestion/CallBudgetTests.cs
  - Ledger.UnitTests/Ingestion/ConsentStateTests.cs
  - Ledger.UnitTests/Ingestion/EnableBanking/AisOnlyGuardHandlerTests.cs
  - Ledger.UnitTests/Ingestion/EnableBanking/CapturedPairReplayTests.cs
  - Ledger.UnitTests/Ingestion/EnableBanking/EnableBankingClientTests.cs
  - Ledger.UnitTests/Ingestion/EnableBanking/EnableBankingFixtures.cs
  - Ledger.UnitTests/Ingestion/EnableBanking/EnableBankingTokenMinterTests.cs
  - Ledger.UnitTests/Ingestion/EnableBanking/RecordingHandler.cs
  - Ledger.UnitTests/Ingestion/InMemoryLedger.cs
  - Ledger.UnitTests/Ingestion/LinkStateTokenTests.cs
  - Ledger.UnitTests/Ingestion/MoneyParserTests.cs
  - Ledger.UnitTests/Ingestion/SyncHealthTests.cs
  - Ledger.UnitTests/Ingestion/SyncScheduleTests.cs
  - Ledger.UnitTests/Ingestion/TransactionReconcilerTests.cs
  - Ledger.UnitTests/Ledger.UnitTests.csproj
  - build/lint/checks/60-observability.sh
  - build/lint/compose.yaml
  - deploy/bin/ledger-apikey
  - deploy/bin/ledger-bank-key
  - deploy/bin/ledger-deploy
  - deploy/bin/ledger-selfcheck
  - deploy/ledger.env.example
  - deploy/provision.d/10-packages.sh
  - deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml
  - deploy/provisioning/grafana/provisioning/alerting/notification-policies.yaml
  - deploy/provisioning/grafana/provisioning/dashboards/ledger.yaml
  - deploy/provisioning/grafana/provisioning/datasources/ledger.yaml
  - deploy/systemd/ledger-deploy-poll.service
  - deploy/tests/backup-logic-test.sh
  - deploy/tests/bank-key-logic-test.sh
  - deploy/tests/provision-logic-test.sh
  - deploy/tests/sandboxing-test.sh
  - deploy/tests/selfcheck-logic-test.sh
  - deploy/versions.env
  - docs/bank-link.http
  - docs/bank-link.md
  - docs/lxc-setup.md
  - docs/monitoring.md
  - docs/rest-api.md
findings:
  critical: 2
  warning: 32
  info: 26
  total: 60
status: issues_found
---

# Phase 2 Code Review

Standard-depth review of every source file changed in phase 2, split into four parallel parts by area and merged here. Finding IDs carry the part in their hundreds digit (1xx Domain/Repository, 2xx Service/Dashboards, 3xx Deploy/CI/docs, 4xx Tests).

## Part A: Domain and Repository

**Reviewed:** 2026-10-07
**Depth:** standard
**Files Reviewed:** 48
**Status:** issues_found

## Summary

Reviewed the ingestion domain logic (reconciler, refs, balance reconciliation, schedule, health), the EF entities, the
DbContext, all stores and the six ingestion migrations. For cross-file facts I also read `SyncOrchestrator` (the only
caller of the balance and ledger stores) and `deploy/sql/bootstrap-database.sql` (default privileges).

What holds up: money is exact decimal end to end (strict `MoneyParser`, scale 4 validation, `numeric(19,4)`, exact
`==` comparison, no tolerance); the `(account_id, ref)` primary key makes "a reference on two rows" impossible at the
database level; the append-only and least-privilege grants are right (runtime has no UPDATE/DELETE on payloads, refs,
provider calls and balance snapshots, and only a column-level UPDATE grant on `transactions`; `grafana_reader` only
receives SELECT on the `reporting` schema through default privileges, and the views expose no IBAN, session material or
payload); no `//` comments, no planning references and no personal data were found in any of the 48 files.

The significant problem is in the undated balance reconciliation: the baseline snapshot treats ING's expected balance
(which includes pending items) as if it were booked-only, so on the live data (2 pending items on the second account)
the carried-forward expectation is permanently off by the sum of those items once they book. The rest are robustness
gaps around the loaded-state window, flagged pending rows that never leave, and poison-item failure modes.

## Critical Issues

### CR-101: Baseline balance snapshot includes pending items, so the carried-forward expectation is permanently wrong after they book

**File:** `Ledger.Domain/Ingestion/BalanceReconciler.cs:113` (with `Ledger.Repository/Stores/BalanceStore.cs:269-289` and `Ledger.Service/Ingestion/SyncOrchestrator.cs:205-216`)
**Issue:** `CheckUndated` starts from `previous.ExpectedAmount ?? previous.Amount`. For the first snapshot of an
account (the baseline, no verdict, `ExpectedAmount` is null) that is the bank's raw amount. ING reports only the
expected balance (XPCD), which by definition is booked entries plus pending items. The ledger side adds only
*booked* rows (`SumBookedSinceAsync` filters `Status == Booked`, keyed on `BookedAt`). So:

1. Day D (baseline): bank = X - 10, where -10 is a pending item P. Stored `Amount = X - 10`, `ExpectedAmount = null`.
2. Day D+1: P books (upgrade or merge sets `BookedAt` into the window). Bank is still X - 10. Expected = `(X - 10) + (-10)` = X - 20. Drift = +10, mismatch.
3. Day D+2 onward: `ExpectedAmount` of D+1 (X - 20) is carried forward, nothing new books, bank is still X - 10. Drift = +10 every day, forever.

After two consecutive mismatches both the Prometheus metric (`IngestionStatusStore.ReadFlaggedReconciliationAsync`)
and `reporting.account_status.balance_reconciled` turn to "no": a permanent false alarm. The same applies if a
baseline-day pending item is later dropped (expected is then too low by its amount in the other direction). The live
observation records exactly this setup: two baseline snapshots stored on link day and 2 pending rows on the second
account. Non-baseline days behave correctly (a pending item shows once as drift, then resolves when it books) because
their expectation starts from the ledger's own booked-only figure; only the baseline, and any snapshot that follows an
unknown verdict, anchors on the bank amount.

**Fix:** Anchor the baseline on the ledger's booked-only view of the bank balance. When no verdict was possible (and
therefore `ExpectedAmount` is null) store `ExpectedAmount = bank amount - sum of the account's pending rows at fetch time`
(add `SumPendingAsync(accountId)` to `IBalanceStore`, call it in the orchestrator for the baseline case, and store it
with `Reconciled = null`; relax `ck_balance_snapshots_verdict` accordingly because a baseline then has an expected amount
but no drift). Then `CheckUndated` can use `previous.ExpectedAmount` unconditionally, and the first real day shows the
pending sum once, which the two-snapshot rule absorbs, and resolves when the item books. Add a regression test that
baselines with one pending row, books it the next day, and asserts a match on day 3.

## Warnings

### WR-101: A flagged pending row can never be dropped or merged later, so it stays double-counted with its booked twin

**File:** `Ledger.Domain/Ingestion/TransactionReconciler.cs:134-137` (and `:93-111`)
**Issue:** When a booked item with a new reference plausibly matches pending rows but is not certain, the booked item is
inserted as its own row and the pending rows are flagged. From then on: (a) the pending row is exempt from
`AddDropsForAbsentPendingRows` (`state.Flag == MatchFlag.Ambiguous`), so even when the bank stops listing it, it stays
`pending` forever; (b) matching only considers `unresolved` (new-reference) booked items, and the booked twin is now a
known reference, so the pair is never re-evaluated. `reporting.transactions` shows every non-dropped row, so the same
payment is counted twice indefinitely, and nothing in this phase gives a person a way to resolve it. The ING live
sync already proves pending rows occur, and ING emits many identical same-day payments (the spike counted 37 groups),
which are precisely the case that is flagged rather than merged.
**Fix:** Remove the `state.Flag == MatchFlag.Ambiguous` exemption from the drop rule. A flagged row the bank no longer
lists in a complete fetch is by definition the pending version of something that booked, and dropping it removes the
duplicate while keeping history. A flagged row that is still listed stays pending and flagged, which is correct.
If the exemption is intentional, add an expiry (for example drop a flagged row once its effective date is older than
the match window) and a documented resolution path.

### WR-102: A reference outside the loaded window turns into a duplicate insert and a primary key violation that fails the sync permanently

**File:** `Ledger.Repository/Stores/LedgerStore.cs:41-46` and `:107-150`; `Ledger.Domain/Ingestion/TransactionReconciler.cs:70-74`
**Issue:** `LoadStateAsync` loads booked rows only when `BookingDate ?? TransactionDate ?? ValueDate >= from`, where the
orchestrator passes `from = query.DateFrom - MatchWindowDays`. The reconciler treats every reference not in that
loaded set as new and plans an insert. The bank's `date_from` is applied by the bank on a date field the code does not
control (booking, value or transaction date). If the bank returns an already-stored booked item whose booking date
is older than `from` (for example it filters on value date, or an item's booking date was later corrected), the
reconciler plans an insert, `AddInserts` adds a second `TransactionRefEntity` with the same `(account_id, ref)`, and
`SaveChanges` throws a unique violation. The run is then recorded as a transient failure, and since the same item is
returned again on every following sync until the window slides past it, the failure repeats daily with no recovery. The
predicate also uses booking-date-first ordering whereas `TransactionReconciler.EffectiveDate` is transaction-date-first.
**Fix:** Make the store authoritative for reference identity instead of trusting the window. In `ApplyAsync`, before
`AddInserts`, look up which of the planned insert and merge references already exist for the account
(`TransactionRefs.Where(r => r.AccountId == accountId && refs.Contains(r.Ref))`) and either skip them or convert them to
updates. Alternatively load all refs of the account (cheap at household scale) and only restrict the heavy columns by date.

### WR-103: Undated pending rows are invisible to the fetch window and are never dropped once older than the window

**File:** `Ledger.Repository/Stores/LedgerStore.cs:23,28-30`; `Ledger.Domain/Ingestion/TransactionReconciler.cs:142`
**Issue:** `GetFetchWindowAsync` derives `OldestPendingDate` from `BookingDate ?? TransactionDate ?? ValueDate`. The live
sync observed pending items with no booking date; a pending row with none of the three dates yields null, so
`OldestPendingDate` ignores it and `ChooseQuery` never widens `from` back to cover it. The drop rule is then asymmetric:
`EffectiveDate(state)` falls back to `FirstSeenAt`, which is before `coverage.From` after a few days, so the row is
skipped by `if (coverage.From is { } from && EffectiveDate(state) < from) continue;`. A pending row with no dates that
the bank cancels without a Cancelled status therefore stays `pending` forever (and counts in reports), while the same
row that books under its own reference is fine.
**Fix:** Use the same effective-date definition in the store and the reconciler, including the first-seen fallback:
compute `OldestPendingDate` as the minimum of `COALESCE(transaction_date, booking_date, value_date, first_seen_at::date)`
for pending rows, so the fetch reaches back to them and the drop rule can evaluate them.

### WR-104: A NUL character in any bank text poisons the sync permanently

**File:** `Ledger.Domain/Ingestion/TransactionReconciler.cs:308-352`; `Ledger.Repository/LedgerDbContext.cs:257` (jsonb payload) and the `text` columns of `transactions`
**Issue:** `Validate` accepts any syntactically valid JSON. PostgreSQL rejects `\u0000` in `jsonb` and cannot store a NUL
in `text` columns (description, counterparty name). One such transaction makes `ApplyAsync` throw on every attempt;
the exception is not a `BankProviderException`, so the run is classed as a generic transient failure and retried
forever, blocking all ingestion for that account, while a validator that exists precisely to reject unstorable items
let it through.
**Fix:** In `Validate`, reject (as `MalformedData`, with a code such as `text_nul`) an item whose raw JSON contains the
escape sequence `\u0000` or whose counterparty or description contains `'\0'`; or strip NULs deterministically before
storing, keeping the payload hash on the original text. Prefer a loud `MalformedData` so the operator sees it, or
quarantine the single item instead of failing the whole account.

### WR-105: Re-linking an already known account re-points it without superseding the old connection

**File:** `Ledger.Repository/Stores/BankConnectionStore.cs:153-179` (`AddConnectionAsync`)
**Issue:** `AttachAccountsAsync` finds accounts by `(provider, identification_hash)` and moves `BankConnectionId` of an
existing account to the new connection, but `AddConnectionAsync` (unlike `ApplyRenewalAsync`) leaves the old connection
`active`. After a second link of the same bank accounts the old connection has no accounts, stays `active`, and keeps its
own `valid_until`: `IngestionStatusStore` still reports it, so its consent will trigger a consent-expiry alert for a
connection that no longer syncs anything, and its sync target is empty. In addition `SingleOrDefaultAsync` runs against
the database, so two accounts in one session sharing an identification hash would both be added and fail on the unique
index instead of mapping once.
**Fix:** When `AddConnectionAsync` re-attaches at least one existing account, supersede any active connection that no
longer owns an account (or reject the link and direct the operator to renew), in the same transaction. De-duplicate
`session.Accounts` by identification hash before the loop.

### WR-106: The dashboard view and the metric disagree about the reconciliation state, and "consecutive" is not enforced

**File:** `Ledger.Repository/Migrations/20261005152907_FlagDriftOnConsecutiveSnapshots.cs:34-67` vs `Ledger.Repository/Stores/IngestionStatusStore.cs:443-476`
**Issue:** The metric path takes the latest snapshot that has a verdict (any date), the view takes the latest snapshot row by
date and kind priority whether or not it has a verdict. A day whose latest row has no verdict shows `unknown` in
Grafana but the older verdict in Prometheus. Both paths also take "the previous snapshot with a verdict" regardless of
how long ago it was: two mismatches three weeks apart (with matches impossible in between only because there was no
sync) count as "persists across two consecutive daily snapshots". The view also hard-codes `interval '14 days'` while
`ConsentState.Derive` takes a configurable `warnDays`, so the dashboard and the alert can disagree once the threshold
is changed.
**Fix:** Define the rule once. Select the latest row with a verdict in the view too (`WHERE s.reconciled IS NOT NULL` for the
verdict lookup while still showing the latest balance), and require the previous verdict to be from the immediately
preceding snapshot day or at most N days back. Either remove the configurable `warnDays` parameter or feed the same value
to the view through a setting table.

### WR-107: Status transitions and run completion are unconditional overwrites

**File:** `Ledger.Repository/Stores/BankConnectionStore.cs:93-112`; `Ledger.Repository/Stores/SyncRunStore.cs:537-556`
**Issue:** `MarkStatusAsync` only guards `ProviderExpired`. Calling it with `Active` can resurrect a revoked or superseded
connection, and `Revoked` overwrites a superseded connection's `ClosedAt` and status; the interface contract promises
only that revoked or superseded rows are not changed back to expired. `FinishAsync` has no `finished_at IS NULL` guard,
so a run already marked `Abandoned` by `AbandonUnfinishedAsync` (or finished once) can be silently overwritten by a late
second completion, losing the first outcome that schedule and health decisions were based on.
**Fix:** Add status-transition guards (`Active` only from `ProviderExpired`; `Revoked` from any non-revoked; never leave
`Revoked`) and add `.Where(run => run.Id == runId && run.FinishedAt == null)` to `FinishAsync`, returning the affected row count.

### WR-108: Europe/Amsterdam is hard-coded in the reconciler and the reporting view, beside a configurable sync zone

**File:** `Ledger.Domain/Ingestion/TransactionReconciler.cs:16,254`; `Ledger.Repository/Migrations/20260930181840_AddLedgerIngestion.cs:35`
**Issue:** The reconciler has a static `TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")` while everything else
uses the configured sync zone (`ScheduleSettings.Zone`). If tzdata is missing on the host (minimal LXC image or
invariant globalization), the static initialiser throws a `TypeInitializationException` on first use of
`TransactionReconciler`, which fails every sync with a generic transient failure and no actionable reason. If the sync zone
is configured to something else, the first-seen-date fallback used for matching and drop windows is computed in a
different calendar than the schedule.
**Fix:** Pass the zone in through `ReconcilerOptions` (`ReconcilerOptions(int MatchWindowDays, TimeZoneInfo Zone)`) and
resolve it once at startup where a failure is visible. Keep the view's zone literal documented as the display zone.

## Info

### IN-101: Additional balances of the same kind are silently dropped when snapshotting

**File:** `Ledger.Repository/Stores/BalanceStore.cs:338-342`
**Issue:** `GroupBy(balance => balance.Kind).Select(group => group.First())` keeps one row per kind because of the unique
`(account_id, snapshot_date, balance_kind)` index, so several `Other` balances (or two balances of the same kind with
different provider types) lose all but the first without any trace.
**Fix:** Either widen the unique index to include `provider_type`, or log (counts only) when a kind was collapsed.

### IN-102: `DateTimeOffset.UtcNow` bypasses the injected clock

**File:** `Ledger.Repository/Stores/BankConnectionStore.cs:24,53`
**Issue:** `AddConnectionAsync` and `ApplyRenewalAsync` read the wall clock directly while the rest of the ingestion code
takes a `TimeProvider`, so connection `created_at` and `closed_at` cannot be controlled in tests and may differ from the
timeline that sync decisions use.
**Fix:** Inject `TimeProvider` into `BankConnectionStore` and use `timeProvider.GetUtcNow()`.

### IN-103: Payload is stored as `jsonb` although the contract is "untouched provider payload"

**File:** `Ledger.Repository/LedgerDbContext.cs:257`; `Ledger.Repository/Entities/TransactionPayloadEntity.cs:20,23`
**Issue:** `jsonb` normalises whitespace, key order and duplicate keys, while `PayloadSha256` is computed on the exact text
received. The hash can therefore never be re-verified against the stored value, and the "raw payload" is not byte-faithful
(ordering of keys is lost, which matters if the payload is ever re-parsed for fields not yet mapped).
**Fix:** Either store the payload as `text`/`json` (byte-faithful), or document that the hash identifies the received text
only and not the stored value.

---

_Reviewed: 2026-10-07_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

## Part B: Service and Dashboards

**Reviewed:** 2026-10-07
**Depth:** standard
**Files Reviewed:** 35 (Ledger.slnx and global.json are also in the list; they hold no logic and raised nothing)
**Status:** issues_found

## Summary

The security-critical surface is mostly sound:

- **State handling.** The one-time state is 256 bits and hashed before storage. It is validated against a strict shape and consumed with an atomic conditional update, so a replay or a race cannot win twice.
- **Read-only guard.** The outbound handler is anchored to host, scheme, default port, method and a fully anchored path allow-list. It is evaluated on the normalised path, so `..` segments in an account uid cannot widen it. The primary handler has `AllowAutoRedirect = false`, and all HTTP client loggers are removed.
- **Error mapping.** Errors are reduced to a sanitised `[A-Z_]{1,64}` code plus a fixed message. No response body reaches an exception, log line, metric or HTTP response.
- **Token minting.** The JWT claims and header are correct: RS256, `kid` set to the application id, no `nbf`, 30 minute lifetime. Key failures name only the configuration key.
- **Metric labels.** All labels are opaque keys or fixed words.
- **Hard rules.** A scan of every file in scope found no `//` comments, planning references or personal data.

The serious problems are in flow and state handling, not in the guards:

- The decided "sync immediately after linking" step is not actually immediate.
- A background-context first sync cannot complete the longest history.
- Several failure paths leave a live consent at the bank that the ledger has no record of.
- Orphaned-run recovery is gated on a config flag.

## Critical Issues

### CR-201: Full-history sync after a first link is deferred behind a manual account selection, which can lose irrecoverable history

**File:** `Ledger.Service/Ingestion/BankLinkService.cs:178-188` (callback) and `:249-277` (selection); `Ledger.Service/Endpoints/BankEndpoints.cs:214`
**Issue:**

- The spike decisions say the post-link sync must run immediately after linking with the longest strategy, and that the adapter must never skip or defer it. They record that ING returns 24 months only shortly after SCA, and only 90 days by the next morning.
- For a first link, `CompleteAsync` queues no sync. The callback text tells the operator to close the page and select accounts.
- The first sync is queued only from `SelectAccountsAsync`, after a separate authenticated `PUT` that must also supply a valid display name for every account.
- Nothing bounds how long the operator takes. The scheduler only logs a one-shot warning after 30 minutes. The bank's cut-off window is unknown. It is somewhere between minutes and about 12 hours.
- Once the window passes, history older than 90 days cannot be recovered. This is irreversible data loss, and it contradicts the recorded decision. Renewal does queue the sync from the callback, so first link and renewal behave differently.

**Fix:**

- In `CompleteAsync`, persist the connection with every offered account enabled and a neutral default display name (for example "Account 1"). Queue `SyncTrigger.PostLink` with the callback request's PSU context right there, as `CompleteRenewalAsync` already does. Let the operator rename accounts afterwards.
- If account choice must stay manual, at least fetch and store the longest history for all accounts in the callback, before any selection.
- On a full queue, run or retry the sync rather than log and continue (see WR-204).

## Warnings

### WR-201: A post-link or post-renewal sync without a PSU context is a metered background fetch and cannot finish the long history

**File:** `Ledger.Service/Ingestion/SyncOrchestrator.cs:116-119`; `Ledger.Domain/Banking/ProviderModels.cs` (`FetchContext.IsBackground => Psu is null`); `Ledger.Service/Ingestion/PsuContextFactory.cs:17-25`
**Issue:**

- `PsuContextFactory` returns null when `PsuHeadersOnOperatorSyncs` is false or the request has no User-Agent. The `SyncRequest` is still enqueued as `PostLink`, and the orchestrator meters it as a background fetch.
- The spike measured 25 pages of 100 for the first account's initial history. The call budget is 12 per rolling 24 hours.
- The run therefore dies with `QuotaExhausted` partway through. Nothing is applied, because items are applied only after a complete fetch. A later retry runs after the privileged window has closed (see CR-201).
- The post-link sync is the one fetch that must not be allowance-limited.

**Fix:** For `SyncTrigger.PostLink`, either skip budget enforcement while still recording calls, or build the meter with `background: false`. For example, in `SyncAccountAsync` use `context.IsBackground && trigger != SyncTrigger.PostLink`.

### WR-202: Failed link or renewal completion leaves a live consent at the bank that the ledger does not know about

**File:** `Ledger.Service/Ingestion/BankLinkService.cs:156-189` and `:423-446`; `Ledger.Service/Ingestion/EnableBanking/EnableBankingClient.cs:76-101`
**Issue:**

- The `/sessions` exchange creates a real consent at the aggregator. If anything after it fails, the session is never stored and never ended. The failures are:
  - the account list is empty (the `NoAccounts` return)
  - the persistence call throws (`AddConnectionAsync`, which is not wrapped and falls to the endpoint's generic catch)
  - `ApplyRenewalAsync` throws `InvalidOperationException`
  - `MapAccount` throws a malformed-data error for one bad account, so `CompleteAuthorizationAsync` fails after the session exists
- The consent stays active at the bank for up to 180 days. The ledger has no record and so cannot revoke it from its own UI. The bank holds a live read grant on the household's accounts that the operator cannot see or end from here. Because the state is already consumed, retrying creates yet another session.

**Fix:** On every failure path after a successful exchange, make a best-effort `provider.RevokeSessionAsync(session.SessionId, CancellationToken.None)` inside a try/catch that logs only `exception.Kind`. In `CompleteAuthorizationAsync`, wrap the mapping so that a mapping failure triggers a revoke of the just-created session before the exception propagates.

### WR-203: Orphaned unfinished runs are only cleaned up when the scheduler is enabled, and a failed run finish blocks the connection until a restart

**File:** `Ledger.Service/Ingestion/SyncScheduler.cs:71-82`; `Ledger.Service/Ingestion/SyncOrchestrator.cs:74`
**Issue:**

- `AbandonOrphanedRunsAsync` runs inside `ExecuteAsync`, after the early return for `!SchedulerEnabled || provider is DisabledBankDataProvider`. The documented option "Turning it off leaves operator-triggered syncs working" is then wrong. After a crash mid-sync, the unfinished row stays forever.
  - Every `POST /sync` answers 409 "A sync is running".
  - `SyncWorker` runs fail with `SyncAlreadyRunningException`.
  - The metrics and dashboard show no failure.
- A second path leaves an unfinished row with no restart involved. When the database is unavailable at the end of a run, `FinishAsync` throws out of `SyncConnectionAsync` at line 74. That leaves the row open until the next restart, and the scheduler wrongly logs a generic failure.
- `AbandonOrphanedRunsAsync` is not wrapped in the `ExecuteAsync` try/catch for non-cancellation exceptions. A database that is briefly unreachable at startup faults the `BackgroundService` and stops the host (default `StopHost` behaviour).

**Fix:** Move the orphan recovery to a small startup step that runs whenever a provider is configured, independent of `SchedulerEnabled`. Wrap it so a failure logs and continues. In the orchestrator, make the final `FinishAsync` resilient or retried, so a transient database error does not leave the run open.

### WR-204: Dispatcher has no deduplication, is lost on restart, and a full queue silently drops the renewal sync

**File:** `Ledger.Service/Ingestion/SyncDispatcher.cs:23-38`; `Ledger.Service/Ingestion/BankLinkService.cs:311-321` and `:448-451`
**Issue:**

- `SyncNowAsync` guards against concurrent syncs with `HasUnfinishedRunAsync`. A request sitting in the channel has not created a run yet, so it is invisible to that check. Repeated `POST /sync` calls (or a double-click) all queue, and each then runs as a full fetch.
  - For attended requests (PSU present) the fetches are unmetered, and each page is a call to the bank.
  - Header-less requests only get the `<= 1` pre-check, which also cannot see queued work.
- Queued requests live only in memory. A restart between the callback and the worker picking up the request loses the `PostLink` sync. The history window is then lost (see CR-201).
- The renewal path in `CompleteRenewalAsync` only logs a warning when `TryEnqueue` fails. That contradicts the rule that this sync must never be skipped.

**Fix:**

- Track queued connection ids in the dispatcher and reject or collapse duplicates (a `ConcurrentDictionary<Guid, byte>` cleared when the worker finishes the request).
- Persist the pending post-link intent. For example, the scheduler can treat an active connection with enabled accounts and no sync run as a `PostLink` candidate.
- Surface a full queue as a retryable failure to the callback instead of continuing.

### WR-205: Production validation does not require the reverse-proxy trust list, so the PSU IP sent to the bank can be the proxy's address

**File:** `Ledger.Service/Program.cs:65-78`; `Ledger.Service/Hosting/ProductionConfigurationValidator.cs:100-125`; `Ledger.Service/Ingestion/PsuContextFactory.cs:22`
**Issue:**

- `ForwardedHeadersOptions` honours `X-Forwarded-For` only from loopback plus the configured `ReverseProxy:KnownProxies`. The reverse proxy runs on a different host (the Traefik host). With `ReverseProxy:KnownProxies` empty, `RemoteIpAddress` is the proxy's address.
- ING requires `psu-ip-address`, and the ledger forwards `RemoteIpAddress` as the operator's address. With the key missing, the bank receives the proxy's address as the end user's address, or the sync is rejected. The validator makes `Ingestion:Provider=EnableBanking` start fine in that state.

**Fix:** In `AddBankLinkProblems`, when the provider is EnableBanking, require at least one parseable address in `ReverseProxy:KnownProxies`, and add `ReverseProxy:KnownProxies` to the offending keys otherwise.

## Info

### IN-201: The API listener is plain HTTP on all interfaces and carries the API key and the one-time state

**File:** `Ledger.Service/appsettings.json:3-6`
**Issue:** `http://0.0.0.0:5080` means the bearer API key and the callback query (`state` and `code`) cross the LAN between the reverse proxy and the app unencrypted. If the proxy host differs from the app host, that hop is plaintext. This is a security-first flag, not a bug.
**Fix:** Bind to the specific LAN interface, or to loopback behind a local TLS-terminating proxy. Or terminate TLS in Kestrel for the API endpoint. Restrict the port at the firewall to the proxy host.

### IN-202: An unbounded continuation loop is only cut at 500 pages, and an unchanged continuation key is not detected

**File:** `Ledger.Service/Ingestion/EnableBanking/EnableBankingClient.cs:30-31`, `:144-174`
**Issue:**

- A bank that keeps returning the same continuation key is followed for 500 calls in a foreground sync. The meter does not gate foreground calls, and every call is recorded as a bank call.
- Header-less runs are protected by the budget, but attended runs are not.

**Fix:** Stop with `ForTransport("repeated_continuation_key")` when a returned key equals the one just sent, and consider a lower cap (for example 100 pages, 10,000 transactions).

### IN-203: The PSU header gate can classify a call as attended when no PSU headers are sent

**File:** `Ledger.Service/Ingestion/EnableBanking/EnableBankingClient.cs:225-249`; `Ledger.Domain/Banking/ProviderModels.cs` (`IsBackground`)
**Issue:**

- `ResolvePsuHeadersAsync` returns null when the bank requires a header the ledger cannot supply. The ledger's meter has already treated the same call as foreground (unmetered), while the bank sees a header-less call.
- The method also always sends both `Psu-Ip-Address` and `Psu-User-Agent` even when the bank requires only the IP, so a request whose User-Agent is missing loses the IP header too.
- The doc comment ("a bank is sent either all of its required headers or none") is accurate only for the required set.
- A User-Agent with non-ASCII characters passes `IsUsableHeaderValue` (control-character check only). The send then fails inside `HttpClient` with `HttpRequestException`, which is mapped to `connection_failed`. That is a misleading transient error that repeats on every attended sync from that client.

**Fix:** Send only the headers the bank requires. Validate header values as printable ASCII. Return foreground or background from the same decision that the headers are built from, and meter accordingly.

### IN-204: A singleton scheduler captures a transient typed HTTP client

**File:** `Ledger.Service/Ingestion/SyncScheduler.cs:12-17`; `Ledger.Service/Ingestion/IngestionServiceCollectionExtensions.cs:93`
**Issue:** `SyncScheduler` is a singleton taking `IBankDataProvider`, which is registered transient and backed by a typed `HttpClient`. The scheduler holds one `EnableBankingClient` for the process lifetime. It uses it only for `is DisabledBankDataProvider`, but it keeps a handler out of the factory's rotation and so defeats DNS refresh.
**Fix:** Decide with a small `IBankProviderSelector` or the `Ingestion:Provider` option instead of resolving the provider. Or resolve it from a scope when needed.

### IN-205: The orchestrator doc overstates atomicity, and the code carries a dead local

**File:** `Ledger.Service/Ingestion/SyncOrchestrator.cs:9-13`, `:114`, `:127`
**Issue:**

- The summary says the sync "stops at the first failure, so one failing account never leaves another half-applied". Each account is applied in its own transaction, so earlier accounts stay applied when a later one fails. That is acceptable behaviour but the doc is wrong.
- `complete` is declared false and set true unconditionally after the loop. It is dead state, because any failure throws before the assignment.

**Fix:** Reword the doc (accounts are applied one at a time; a failure leaves earlier accounts applied and later ones untouched) and drop the variable, passing `true` directly.

### IN-206: Dashboard `check` ignores stray generated files, and the anonymous callback has no rate limit

**File:** `Ledger.Dashboards/Program.cs:33-41`; `Ledger.Service/Endpoints/BankEndpoints.cs:25`
**Issue:**

- `check` compares only the files the generator would write. If a language or dashboard is removed, an orphan JSON stays provisioned in Grafana and `check` still passes.
- The anonymous callback performs a database write (`ExecuteUpdate`) per request before it can reject. Unauthenticated callers on the LAN can force that cost. The state is unguessable, so this is only a nuisance.

**Fix:** Have `check` also list `*.json` in the output directory and report any file not in the generated set. Consider a simple rate limiter or a cheap shape rejection before the database (the strict state shape check already does the latter for malformed input).

---

_Reviewed: 2026-10-07_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

## Part C: Deploy, provisioning, CI and docs

**Reviewed:** 2026-10-07
**Depth:** standard
**Files Reviewed:** 28
**Status:** issues_found

## Summary

Reviewed the deploy scripts, systemd unit, Grafana and Prometheus provisioning, CI and release workflows, lint check, gitleaks config, shell tests and docs. For context I also read `deploy/lib/deploy.sh`, `deploy/lib/common.sh`, `build/validate-release-tag.sh`, `deploy/msmtp/msmtprc.in` and `Ledger.Service/Metrics/SyncMetrics.cs`, but only to check cross-file facts. They are not part of this finding set.

Overall the hardening is solid:
- Actions are pinned by SHA.
- The release is draft-then-approve and re-verifies its attestation.
- The host verifies the attestation offline and checks the attested commit is on main.
- Key custody never prints the password.
- Secrets are fed by file descriptor or stdin, never argv.
- Alert text carries no query results.
- The datasource `database` now sits in `jsonData`, and the lint asserts it.
- The doc and code placeholders checked are synthetic (`example.com`, `192.0.2.x`).
- No planning-reference violations were found. The only hits are the two `.planning` path allowlists in `.gitleaks.toml` (see IN-305).

I found no blocker. The real defects are:
- A stale-sync alert that fires on a perfectly healthy retry.
- A key-generation ordering that can strand an unusable key.
- Provisioning pins that still do not reach existing hosts for signing keys.
- A sandbox write-path gap for the installer's notification mail log.

## Warnings

### WR-301: "Bank sync is stale" fires on every successful same-day retry

**File:** `deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml:239-251`
**Issue:** The threshold is 93600 s (26 h) with `for: 10m`.
- `docs/bank-link.md` and `docs/monitoring.md` say the morning sync runs at 06:30 and a transient failure is retried once, no earlier than 4 h later, on the same day.
- A transient failure is deliberately not marked as failing until that retry also fails.
- If the 06:30 attempt fails and the 10:30 retry succeeds, the gap since the previous success (about 06:31 the day before) is about 28 h.
- The stale alert therefore fires around 08:40 and resolves at 10:31.
- The household gets a "no sync for more than 26 hours" email on a day when the system worked exactly as designed. With the hourly group interval and 24 h repeat this is real alert noise.
**Fix:** The threshold must exceed 24 h plus the retry delay plus slack. Derive it from the schedule, or use a larger constant:
```yaml
params: [129600]   # 36 hours
...
summary: No bank sync has succeeded for more than 36 hours.
```
Update the matching text in `docs/monitoring.md` (the two places that say 26 hours).

### WR-302: Stale-sync alert is silent when no sync has ever succeeded

**File:** `deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml:225,248`
**Issue:** `ledger_sync_last_success_timestamp_seconds` only gets a series for accounts whose `LastSuccessAt` is set (`SyncMetrics.cs:109`). The rule queries `time() - min(...)` with `noDataState: OK`.
- After a link whose first sync never succeeds, the series does not exist and the query returns no data. That maps to OK.
- Only the failing and consent alerts can fire in that case.
- If the scheduler stops (a crash loop that comes back up, or a hung scheduler with a healthy `/health`) before any success, nothing alerts.
- The "app down" alert covers a down process, not a stuck scheduler.
**Fix:** Either set `noDataState: Alerting` for this rule, with the docs noting it stays quiet until the first bank link (there is no series for connection-less installs, so check this does not make the rule fire on a fresh host), or export a sentinel such as `ledger_sync_selected_accounts` and alert on `selected_accounts > 0 unless last_success`. The first option is a one-line change but needs the "no data before first link" behaviour in the docs re-checked.

### WR-303: `ledger-bank-key generate` can strand an unusable key and leaks its work directory on failure

**File:** `deploy/bin/ledger-bank-key:165-210`
**Issue:**
- The key, certificate and public key are installed into `/etc/ledger` (lines 195-203) before the env file receives the key path and password (lines 205-206). Under `set -e` any failure in `bank_key_set_env_value` aborts here.
  - Examples: a read-only or full filesystem, or an env file that cannot be replaced.
  - The encrypted key is then on disk with its password only in an exported shell variable that is lost.
  - Re-running `generate` refuses ("never overwritten") because `-e "$key_path"` is true. The operator must know to delete the key by hand.
  - The key is effectively unusable and the tool gives no hint.
- No `trap` removes `workdir`, or restores `umask`, on any error path other than the single openssl failure branch. A failing `install`, `mkdir -p` or `bank_key_set_env_value` leaves `workdir` behind.
- `LEDGER_BANK_KEY_PASSWORD` is also left exported. Only an encrypted key is in the leaked workdir, so the exposure is limited. The password stays in the process environment until exit, though.
**Fix:** Write the env entries first (or install key and env atomically), and clean up with a trap:
```bash
trap 'rm -rf "$workdir"; unset LEDGER_BANK_KEY_PASSWORD; umask "$old_umask"' RETURN
...
bank_key_set_env_value "$env_file" EnableBanking__PrivateKeyPassword "$LEDGER_BANK_KEY_PASSWORD"
bank_key_set_env_value "$env_file" EnableBanking__PrivateKeyPath "$key_path"
install -m 640 ... "$key_path"   # only after the env file holds the password
```
If a later step fails, remove the files installed in this invocation (they cannot pre-exist, because the function refuses otherwise). Add a bank-key logic test for "env file not writable".

### WR-304: Signing keyrings and apt sources are only written on first install, so pin changes still do not reach an existing host

**File:** `deploy/provision.d/10-packages.sh:30-32,104-115`
**Issue:** The recent fix made the Grafana version pin and the Prometheus version reach existing hosts. The same first-install-only pattern remains for the supply-chain pins:
- `install_apt_signing_key` returns immediately when the keyring file exists (line 30). The `*_KEY_FINGERPRINT` values in `versions.env` are therefore never re-checked after the first run.
  - If a vendor rotates a signing key and the pin is bumped, existing hosts keep the old keyring and `apt-get update` starts failing.
  - A keyring written by a half-finished earlier run (`gpg --dearmor` output is created before it finishes) is trusted forever.
  - A keyring left from before fingerprint checking existed is never verified.
- The three `*.list` files are only written when absent (lines 104-115). A changed codename, URL or `signed-by` path never propagates.
**Fix:** Compare the installed keyring's fingerprint with the pin on every run, and reinstall when it differs. Rewrite the sources lines when their content differs, as is already done for the Grafana preferences file:
```bash
if [[ -f "$keyring_path" ]]; then
  current="$(gpg --batch --with-colons --show-keys "$keyring_path" 2>/dev/null | provision_key_fingerprint || true)"
  [[ "$current" == "$expected_fpr" ]] && return 0
fi
```
Add `provision-logic-test.sh` cases next to the existing pin tests.

### WR-305: The installer unit cannot create `/var/log/msmtp.log`, so the first deploy notification mail is logged into a read-only path

**File:** `deploy/systemd/ledger-deploy-poll.service:24`
**Issue:** `ProtectSystem=strict` makes `/var/log` read-only. The unit only whitelists `-/var/log/msmtp.log`, and the `-` prefix makes a missing file silently skipped, so nothing is bind-mounted writable.
- `msmtprc.in` sets `logfile /var/log/msmtp.log`.
- Nothing in provisioning creates that file (grep of `deploy/provision.d` and `deploy/msmtp` shows no `touch` or `install`).
- On a host where no mail was ever sent from outside the sandbox, the first deploy outcome mail is sent from inside the unit. msmtp then cannot open its log. It either reports an error or exits non-zero.
- `ledger_notify_email` swallows the failure, so the operator only sees a log warning and may miss the mail that reports a failed or rolled-back deploy.
- The sandboxing test only text-matches directives, so it cannot catch this class of gap.
**Fix:** Create the file during provisioning with correct ownership and mode, for example in `40-services.sh` next to the msmtprc install:
```bash
install -m 640 -o root -g adm /dev/null /var/log/msmtp.log
```
Do this idempotently, only when absent. Optionally drop the `-` prefix once it is guaranteed to exist.

### WR-306: Publish-time attestation re-verification does not bind to the tag's commit

**File:** `.github/workflows/release.yml:216-223`
**Issue:** `gh attestation verify` pins repository, signer workflow and `--source-ref refs/tags/$TAG`, but not `--source-digest`.
- If a tag is deleted and re-pushed at a different commit (tag protection is a repository setting, not enforced in the workflow), an attestation from an earlier run of the same tag still satisfies every constraint.
- It is then paired with that run's earlier draft assets.
- The `Validate release tag` step only checks that the current `GITHUB_SHA` is on main.
- The host-side `ledger_commit_on_branch` check limits the blast radius, but this gate is described as the approver's last check.
**Fix:**
```yaml
gh attestation verify "release/ledger-$VERSION.zip" \
  --bundle "release/ledger-$VERSION.zip.sigstore.json" \
  --repo "$GITHUB_REPOSITORY" \
  --signer-workflow "$GITHUB_REPOSITORY/.github/workflows/release.yml" \
  --source-ref "refs/tags/$TAG" \
  --source-digest "$GITHUB_SHA" \
  --deny-self-hosted-runners
```
Check that `gh` accepts `--source-digest` together with `--bundle` on the runner version in use.

## Info

### IN-301: Build job runs tests and restores packages with `contents: write` and `id-token: write` in scope

**File:** `.github/workflows/release.yml:95-98`
**Issue:** A single job holds the release-write, OIDC and attestation permissions while it runs `dotnet restore`, `package-release.sh` and the whole test suite. With `id-token: write` the OIDC request variables are present in the environment of every step. A compromised build or test dependency could request a token or write to the release. The locked-mode restore and SHA-pinned actions reduce this, but do not remove it.
**Fix:** Split into `build-test` (read-only, uploads the zip as an artifact) and `attest-release` (downloads the artifact, attests, creates the draft). Only the second job gets write and OIDC permissions.

### IN-302: `ledger-deploy install --from-dir` verifies and then reads an operator-supplied path

**File:** `deploy/bin/ledger-deploy:448-476` (and `deploy/lib/deploy.sh:430`)
**Issue:** The artifact is verified from `from_dir` and then unzipped from the same path later in `ledger_install_verified_release`. If that directory is writable by anyone other than root, the file can be swapped between the two reads. The default (download) path is root-owned and safe.
**Fix:** Copy the artifact and bundle into a root-owned temporary directory under `DOWNLOAD_DIR` first, then verify and install from the copy. Alternatively require `from_dir` to be root-owned and not group- or world-writable.

### IN-303: Poll aborts before recording failure metrics when the API reply is not valid JSON

**File:** `deploy/bin/ledger-deploy:497-509`
**Issue:** Under `set -euo pipefail`, `tag="$(printf '%s' "$response" | jq -r ...)"` exits the script if `jq` rejects the body. This happens before `ledger_write_deploy_poll_metrics 0`, so the last-poll metrics keep the previous success value until the one-hour stale alert catches it. The `--fail` flag covers HTTP errors but not a 200 with a non-JSON body (captive portal, proxy page). `cmd_verify` and `cmd_rollback` also hit `unbound variable` instead of a usage message when called without arguments (`$2`, `$1`).
**Fix:** `tag="$(... | jq -r '.tag_name // empty' 2>/dev/null || true)"`. Check `$#` before reading `$2` or `$1` and call `usage`.

### IN-304: Test hooks in a root-run script execute environment-supplied commands

**File:** `deploy/bin/ledger-selfcheck:38,301,304`
**Issue:** `LEDGER_SELFCHECK_JOURNAL_CMD` is passed to `bash -c` as root, and `LEDGER_SELFCHECK_ENV_FILE`, `_LOG_ROOTS`, `_SCAN_ROOTS` and `LEDGER_PROVISION_CONF` redirect what the "lockdown proof" reads. Anything that can set the environment of a root `ledger-selfcheck` run (a `sudo` env-keep rule, a wrapper) gets command execution or a falsified all-PASS result. The same pattern exists in `ledger-deploy` (`LEDGER_DEPLOY_ROOT` disables the root check, `LEDGER_DEPLOY_CONF` and `LEDGER_POLL_LATEST_URL` redirect inputs).
**Fix:** Gate the overrides behind a single explicit test switch (for example `LEDGER_TESTING=1`, not set by any unit), and ignore them otherwise. Alternatively run the real binaries against the test fixtures through a test-only wrapper.

### IN-305: gitleaks allowlists are broader than their descriptions, and one defeats the planning-reference lint

**File:** `.gitleaks.toml:60,71,87,95`
**Issue:**
- `deploy/tests/fixtures/` and `(^|/)artifacts/` are unanchored path regexes. Any file under any directory named `artifacts` (for example `docs/artifacts/` or `deploy/artifacts/`) is exempt from every rule, including the IBAN and email rules. The stated rationale is "gitignored output cannot be committed", but a forced add, or a differently named top-level directory, bypasses it. `^artifacts/` and `^Ledger\.[A-Za-z]+/(bin|obj)/` would be safe. Anchor `deploy/tests/fixtures/` to `^`.
- The two `generic-api-key` allowlists name planning documents (`ARCHITECTURE.md`, `*-SUMMARY.md`) outside `.planning/`, spelled `\.plannin[g]` so the repo's own planning-reference lint does not see them. They exist only because planning prose trips the generic rule. A narrower alternative is to reword that prose and drop the allowlist, so the hard rule needs no exception.
- The IBAN rule only covers `NL`. Counterparty IBANs in real exports can be any country. The private-IP rule skips CGNAT/Tailscale (`100.64.0.0/10`) and IPv6 ULA ranges, which are typical homelab and VPN addresses.
**Fix:** Anchor the path regexes. Prefer fixing the prose over the planning allowlists. Consider a generic `\b[A-Z]{2}[0-9]{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?\b` IBAN rule with the same synthetic-value allowlist the tests need, and add the CGNAT range.

### IN-306: Lint check hard-codes the alert rule count and passes credentials on argv

**File:** `build/lint/checks/60-observability.sh:229-242,284-303,305`
**Issue:**
- `[ "$alert_rule_count" -ne 16 ]` has to be edited whenever a rule is added or removed. Derive it from the YAML instead.
- The throwaway admin password is passed to `docker compose run` through `-e` and to `curl -u`, so it is visible in `ps` and `docker inspect` on a shared machine. It is random and ephemeral, so the risk is low.
- The detached Grafana container is started with `docker compose run -d` under a wrapper that adds `--rm`. Whether `--rm` takes effect with `-d` depends on the Compose version. If it does not, a stopped container is left behind on every local lint run.
**Fix:** Count rule `uid:` lines in the two rule files. Use `--env-file` or `curl --config -` for the password. Add `docker rm -f "$GRAFANA_CID"` to `cleanup_grafana`.

### IN-307: The bank key password adds little protection, and the docs overstate "never travels"

**File:** `deploy/bin/ledger-bank-key:174-206`, `docs/bank-link.md:59-66,79`, `docs/lxc-setup.md:225-229`
**Issue:**
- The key is mode 640 `root:ledger` and its password sits in `ledger.env`, mode 640 `root:ledger`. Anyone who can read the key can read the password. The encryption only helps if the key file leaks on its own, for example through a stray copy. That is acceptable, but the docs present it as stronger than it is.
- The docs say the key "is created on the host and never travels", and in the same step tell the operator to copy the key file and the env file into a password manager. Both statements are true but read as contradictory. Say the key is created on the host and never leaves it except as an operator-initiated backup copy.
**Fix:** Reword the docs. Optionally keep the password in a systemd credential (`LoadCredentialEncrypted`) so it is not stored beside the key.

### IN-308: Docs inconsistencies

**File:** `docs/lxc-setup.md:223-267`, `docs/bank-link.md:67`, `docs/monitoring.md`
**Issue:**
- `lxc-setup.md` uses `https://ledger.example.com/api/v1/bank/callback`, while `bank-link.md` and `bank-link.http` use `ledger-api.example.com`. Pick one placeholder. Step 3 of `lxc-setup.md` also calls the hostnames "the Grafana and API hostnames".
- The "Bank link key" section follows step 17 without a number and duplicates the `ledger-bank-key` walk-through in `bank-link.md`. Link to one instead of repeating it.
- `bank-link.md` states the consent lasts "up to 180 days" while the project's own notes treat roughly 90 days as the real renewal cadence. The 14 d and 7 d alert bands fit either, but say "up to" explicitly.
**Fix:** Align the placeholder hostname, number the section, and cross-link instead of duplicating.

### IN-309: Changing `PG_MAJOR` on an existing host installs a second server, and the sandboxing test cannot detect write-path gaps

**File:** `deploy/provision.d/10-packages.sh:127-128`, `deploy/versions.env:588`, `deploy/tests/sandboxing-test.sh:232-240`
**Issue:**
- `versions.env` describes PG_MAJOR as keeping "the major version current". On an existing host, bumping it installs `postgresql-<new>` next to the old cluster. Debian creates a second cluster on port 5433, with no `pg_upgradecluster`. `ledger-selfcheck` then picks `head -n1` of the `postgresql@*-main.service` units, which may be either.
- `sandboxing-test.sh` asserts the `ReadWritePaths` list as a string but never derives which paths the installer writes. That is why WR-305 passed.
**Fix:** Document that a major bump needs a manual cluster upgrade, or make provisioning refuse a mismatched installed major. In the sandboxing test, assert that every path the installer is known to write (metrics directory, mail log, release tree, provisioning trees, state directory) is covered by the unit.

---

_Reviewed: 2026-10-07_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

## Part D: Tests

**Reviewed:** 2026-10-07
**Depth:** standard
**Files Reviewed:** 36
**Status:** issues_found

## Summary

All 36 files in scope were read in full. No finding is a BLOCKER: no test hides a production defect I could prove, and nothing in the test code leaks personal data or real values.

Hard-rule checks, run with grep over the whole file list:
- No `//` comments anywhere (only `///` summaries).
- No planning references (requirement keys, phase or plan numbers, planning document names) in test names, strings or comments.
- All fixture data is synthetic: `XX00...` account numbers, `example.com` / `example.org` hosts, `192.0.2.10` (TEST-NET) for the PSU address, "Example Grocer"-style counterparties, all-zero GUIDs.
- The `/v1/` hits are API route segments, not planning versions.

Real-capture replay leak safety holds. `CaptureReplay.Run` converts every failure into a `ReplayFailedException` whose message is built only from fixed words, counts and an exception type name. The `BankProviderException` code is passed through the `^[a-z_]{1,48}$` filter. The report file is a single label=number line, and the test asserts that shape. Captured pages are never put into a message, and the skip path prints only the variable name. I found no way for a captured value to reach test output.

The weaknesses are all in how convincingly the tests prove what their names claim. The main groups:
- Several replay and redaction assertions are tautological or only weakly able to fail.
- The role tests sample a few tables instead of enumerating the catalog.
- The shared test infrastructure mutates process-global state, starts two hosts per factory, and leaves databases behind on abort.
- The poll-then-assert helpers use wall-clock timeouts.

## Warnings

### WR-401: "Reference on more than one row" invariant can never fail, so the assertions that claim to check it are tautologies

**File:** `Ledger.UnitTests/Ingestion/InMemoryLedger.cs:48-51,190-198`, `Ledger.UnitTests/Ingestion/EnableBanking/CapturedPairReplayTests.cs:54,150,288-292,403-406`
**Issue:** `InMemoryLedger.Attach` throws as soon as a reference would map to a second row (`_byReference.TryAdd`). So `ReferencesOnMoreThanOneRow` is structurally always 0.
- `ReplayInvariants.Verify`'s first branch can never run.
- `result.ReferencesOnMoreThanOneRow.Should().Be(0)` (lines 54 and 150) can never fail.
- The real-capture test's name promises "without ... a reference on two rows". Today that is only detected indirectly, as an `InvalidOperationException`.
- `Run`'s catch-all then turns that exception into "stopped on an unexpected InvalidOperationException", which does not name the broken invariant.
- `A_broken_invariant_fails_with_counts_only` exercises only the partner check, not this one.

**Fix:** Make the ledger record the violation instead of throwing, so the counter and `Verify` are meaningful, or catch the exception in `Run` and rethrow it with the invariant name.
```csharp
catch (InvalidOperationException)
{
    throw new ReplayFailedException("Invariant broken: a reference would map to more than one row.");
}
```
Then add a unit test that applies a plan reusing a reference and expects exactly that message.

### WR-402: Replay order is nondeterministic for captures with equal timestamp and account

**File:** `Ledger.UnitTests/Ingestion/EnableBanking/CapturedPairReplayTests.cs:446-449`
**Issue:** Captures are grouped by `(Timestamp, Label, Account)` and then ordered by `Timestamp` and `Account` only. Two groups with the same second and account but different labels are replayed in `Directory.EnumerateFiles` order, which is filesystem-dependent. For real captures (several labels per fetch run is plausible) the replay outcome, and thus the counts in the report, can differ between machines or runs. The result is a flaky pass or fail with no stable way to reproduce.
**Fix:**
```csharp
.OrderBy(group => group.Key.Timestamp, StringComparer.Ordinal)
.ThenBy(group => group.Key.Account)
.ThenBy(group => group.Key.Label, StringComparer.Ordinal);
```

### WR-403: Runtime and backup role tests sample a few tables, so a new table with broad default privileges would not be caught

**File:** `Ledger.IntegrationTests/Database/DatabaseRoleTests.cs:13-31,50-71,157-164`
**Issue:**
- The grafana_reader test is exhaustive: it enumerates `pg_class` and checks every privilege.
- The runtime and backup tests are hand-picked statements against named tables. `Backup_role_can_read_everything_but_never_write` tests exactly one table (`data_protection_keys`), one SELECT and one INSERT, yet claims "everything".
- If a later table is created under default privileges that give `ledger_runtime` DELETE or TRUNCATE (or give `ledger_backup` write access), no test fails.
- `bank_authorizations`, `api_keys`, `bank_connections` columns such as `session_id_protected`, and the sequences and functions are not checked at all for the "append only / no delete" rules.

**Fix:** Add catalog-driven checks mirroring the grafana one.
- For `ledger_backup`: no INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES or TRIGGER on any relation in `public` and `reporting`, and SELECT on every relation.
- For `ledger_runtime`: none of DELETE, TRUNCATE, REFERENCES or TRIGGER on any public relation unless the table is in an explicit allowlist, plus no CREATE on schemas.
- Also assert there is no EXECUTE on functions that were not intended.

### WR-404: Attribute test omits the attributes that matter for least privilege, and roles are exercised via SET ROLE rather than as logins

**File:** `Ledger.IntegrationTests/Database/DatabaseRoleTests.cs:168-177,221-234`, `Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs:62-71`
**Issue:**
- `No_role_is_a_superuser_or_can_create_databases_or_roles` checks only `rolsuper`, `rolcreatedb` and `rolcreaterole`. It does not check `rolbypassrls`, `rolreplication`, `rolcanlogin` (the runtime and reader roles must be able to log in, the migrator and backup roles should match the deployment), role membership (`pg_auth_members`, for example runtime being a member of the migrator or of a `pg_*` predefined role), or `rolconnlimit`.
- All role connections go through the admin account with `Options = "-c role=..."`, that is `SET ROLE`. This proves table privileges. It does not prove the deployed login path: the role can authenticate, `CONNECT` is granted only on the intended database, and nothing is inherited from the admin session.

**Fix:** Extend the attribute query to `rolbypassrls`, `rolreplication` and `rolcanlogin`, and add an assertion that `pg_auth_members` has no row where a runtime, reader or backup role is a member of a more privileged role. Add a short note, or a separate test, for `CONNECT` privilege on other databases.

### WR-405: Certificate-password redaction test passes on any startup failure and inspects only `Message`

**File:** `Ledger.IntegrationTests/Security/LogRedactionTests.cs:97-112`
**Issue:** `act.Should().Throw<Exception>()` accepts any exception. The test passes if construction fails for an unrelated reason (database unreachable, port clash, bad config), because the sentinel is then trivially absent. It also checks only each `Exception.Message` in the chain, not `ToString()`, `Data`, or the captured logs. A password in a stack frame argument dump, an exception `Data` entry or a log line would go unnoticed. The factory that threw is also never disposed, and the sentinel stays in the process environment (see WR-408).
**Fix:** Assert the failure is the expected one (for example the exception chain names `DataProtection:CertificatePath`), check `exception.ToString()` and every `Data` value for the sentinel, and also assert the captured log text of the failed host does not contain it. Use the factory-less pattern from the validator unit tests where possible.

### WR-406: Log-capture based redaction tests only observe messages at the host's configured level

**File:** `Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs:88,169-205`
**Issue:** `CapturingLoggerProvider.IsEnabled` returns true, but the provider is added through `AddProvider`, so the host's `Logging:LogLevel` filter still decides what reaches it. Debug and Trace messages (where request URLs, headers and bodies are logged by Microsoft and System categories) are never captured. A regression that logs a secret at Debug passes every redaction test, and flips to a leak the moment someone raises the log level on the server. The tests are blind exactly where the framework logs the most.
**Fix:** In `ConfigureWebHost` force everything through the capture provider:
```csharp
builder.ConfigureLogging(logging =>
{
    logging.AddProvider(_loggerProvider);
    logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
});
```
Keep the committed-configuration test that pins `System.Net.Http.HttpClient` to Warning, so the production level is still verified separately.

### WR-407: Adapter redaction test uses the encrypted key line as the sentinel, not the decrypted key material

**File:** `Ledger.IntegrationTests/Security/LogRedactionTests.cs:240-248,298-302`
**Issue:** `firstKeyBodyLine` is the first line of the password-encrypted PKCS#8 PEM. The thing that must never be logged is the decrypted private key and the signed JWT. The JWT/Authorization values are covered, but a log or exception that printed the decrypted PKCS#8 bytes (base64) would not match the encrypted line and would pass. Test name claims "key material".
**Fix:** Also add a body line of the unencrypted export (`rsa.ExportPkcs8PrivateKeyPem()`) and the base64 of `rsa.ExportPkcs8PrivateKey()` to the sentinel list.

### WR-408: Tests mutate process-global environment variables and never restore the previous values

**File:** `Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs:42,153-157`, `Ledger.IntegrationTests/Ingestion/BankLinkEndpointTests.cs:515-530`, `Ledger.IntegrationTests/Ingestion/EnableBankingPipelineTests.cs:180-196`, `Ledger.IntegrationTests/Security/LogRedactionTests.cs:328-344`
**Issue:**
- The factory constructor overwrites `DataProtection__CertificatePath` and `DataProtection__CertificatePassword` in the process environment on every construction, and nothing restores them. After `Sentinel_certificate_password_never_reaches_the_startup_exception_chain` the sentinel password and a bogus path stay in the environment until the next factory is built.
- The three `Ingestion__Provider` blocks set it, then reset it to `null`, destroying any value the developer or CI already had.
- Isolation holds only because `xunit.runner.json` disables collection parallelism for this assembly. One later config change, or any non-factory test reading environment configuration, makes this a cross-test leak.
- The same try/finally is copy-pasted three times.

**Fix:** Put the environment handling in one disposable helper that captures the prior value and restores it, and have the factory own it.
```csharp
internal sealed class EnvironmentOverride : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public EnvironmentOverride(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
```
Better still, make the production composition accept these two values from the configuration the factory supplies, so no environment variable is needed.

### WR-409: The factory builds and starts two complete hosts per instance, so every hosted service runs twice

**File:** `Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs:97-120`
**Issue:** `CreateHost` calls `builder.Build()` once for the TestServer host, reconfigures the builder with Kestrel and builds a second host, and starts both (`realHost.Start()`, `testHost.Start()`). Both start the application's background services (metrics refresher, orphan-run sweeper, scheduler when enabled), both open database connections, and both share the same `CapturingLoggerProvider`.
- Scheduler and refresher tests therefore run against two instances of every singleton background worker.
- The restart test (`SchedulerEnabled=true`) runs two schedulers.
- Duplicate work can mask or cause races that production, with one host, never has.
- Log assertions such as "exactly once" (`A_connection_without_a_selected_account_warns_exactly_once...`) pass only because the scheduler is disabled in that test.

**Fix:** Build only the Kestrel host and hand the factory its address; do not start a second host that only exists to satisfy `WebApplicationFactory`. If the dual host is unavoidable, remove the hosted services from the TestServer host in `ConfigureTestServices` and add a test that pins the number of running background workers.

### WR-410: Aborted or failed runs leave `ledger_it_*` databases behind in the user's persistent PostgreSQL container

**File:** `Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs:25-34,37-56,74-104`
**Issue:** Databases are dropped only in `DisposeAsync`.
- A killed run (Ctrl-C, crash, CI timeout) never drops them.
- If `InitializeAsync` throws after `CREATE DATABASE` (for example the bootstrap SQL fails), `DisposeAsync` is not guaranteed to run, and the database name is added to the list only after creation.
- Every scheduler, balance and metrics test creates its own migrated database, so one run creates dozens, and the container is long-lived (project rule: never torn down).
- Nothing ever sweeps stale ones.

**Fix:** At the start of `InitializeAsync`, drop any `ledger_it_%` database whose creation is old (or that has no active backends), and register the new database name before running the bootstrap SQL so a failed init can still clean it up.

### WR-411: Assertions that cannot fail

**File:** `Ledger.UnitTests/Ingestion/SyncScheduleTests.cs:149-150`, `Ledger.IntegrationTests/Ingestion/EnableBankingPipelineTests.cs:86`
**Issue:**
- `Decide_gives_no_retry_when_the_retry_moment_falls_on_the_next_local_day`: the second assertion calls `Decide(date.AddDays(1) 01:00, Settings(), [])` with an empty run list. At 01:00, before the 06:30 schedule, the answer is `None` whatever the retry logic does. It adds no coverage for the claim in the test name.
- `fake.Requests.Where(IsTransactionRequest).Skip(3).Should().OnlyContain(...)`: if the second sync made no transaction request at all, the sequence is empty and `OnlyContain` passes. Nothing asserts that a second fetch happened or that it carried `date_from`.

**Fix:** Pass the failed run into the second `Decide` call (`runs`, with the evening failure) at 01:00 the next day and expect `None`. For the pipeline test, assert `.Should().NotBeEmpty()` before `OnlyContain`, or assert the exact request count.

### WR-412: The committed-secret scan matches on key names only, so a password inside a connection string value is invisible

**File:** `Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs:12,65-104`
**Issue:** A value is flagged only if its key contains `password`, `secret`, `token` or `apikey`. `ConnectionStrings:Ledger` (or any `...Url`, `...Dsn`, `...Credentials`, `...Key`, or `PrivateKey` blob) with `Password=...` embedded in the value never matches, which is the most likely way a secret ends up in `appsettings.json`. Only `Ledger.Service/appsettings*.json` at the top level is scanned.
**Fix:** Also scan every string value for secret-shaped content (`(?i)(password|pwd|secret|token|apikey)\s*=\s*\S`, PEM headers such as `-----BEGIN`, long base64 strings) and widen the key list (`key`, `credential`, `connectionstring` with embedded credentials). Include other committed config locations (deploy, provisioning) if they carry settings.

### WR-413: Poll-until-timeout helpers use wall-clock limits and return silently on timeout

**File:** `Ledger.IntegrationTests/Ingestion/BankLinkEndpointTests.cs:662-680`, `Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs:717-735,810-827`, `Ledger.IntegrationTests/Security/LogRedactionTests.cs:373-396`
**Issue:**
- `WaitForReportedRowsAsync` (10 s) and `WaitForFinishedRunsAsync` (10 s) return whatever they last read on timeout and leave the failure to a later assertion. On a loaded CI runner a slow sync fails the test with a misleading count mismatch.
- `WaitForReportedRowsAsync` also returns as soon as the count is `>= expected`. Tests that then assert `HaveCount(n)` as a "no duplicates" check (`Renewal_keeps_account_keys...`, the first test of the file) only see the state at that instant; a duplicate inserted a moment later is missed.
- The ready-wait loop is copy-pasted across `ApiKeyAuthTests`, `BankLinkTestHost` and `LogRedactionTests`.

**Fix:** Throw a descriptive `TimeoutException` on timeout, make the timeout configurable and generous, wait for the sync to finish (the run row has an outcome) rather than for row counts, and then assert exact counts. Extract one shared `WaitUntilReadyAsync`.

## Info

### IN-401: Role test leaves residue in the shared database, and a later test depends on it

**File:** `Ledger.IntegrationTests/Database/DatabaseRoleTests.cs:28-30,44`
**Issue:** `reporting.role_test_view` and the `data_protection_keys` rows `role-test-runtime`, `role-test-reader` and `role-test-backup` (with an `<xml/>` payload that is not a real key) stay in the collection database. The exhaustive reporting test then checks every reporting relation, including the leftover view, so its result also exercises default-privilege behaviour on an object created by another test. That is order-dependent, and the junk key rows sit in the ring read by every host in the collection.
**Fix:** Drop the view and delete the inserted rows in a `finally` (as the migrator and a role allowed to delete), or run these tests against a private database.

### IN-402: Several scheduler and balance tests mix a fake clock with the real clock through `SessionValidUntil`

**File:** `Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs:500-516`, `Ledger.IntegrationTests/Ingestion/BalanceSnapshotTests.cs:379-436`, `Ledger.Service/Ingestion/Synthetic/SyntheticBankScenario.cs:22`
**Issue:** The scenario defaults `SessionValidUntil` to `DateTimeOffset.UtcNow.AddDays(90)` while the host clock is fixed in late October 2026. The tests are stable only because real time is already past late July 2026. `SyncMetricsTests` and the consent tests set the value explicitly. The implicit coupling is easy to break.
**Fix:** Set `scenario.SessionValidUntil` relative to the fake start in the shared helper, as `SyncMetricsTests.Scenario` already does.

### IN-403: Replay proves the reconciler against a hand-written copy of the store, not the real store

**File:** `Ledger.UnitTests/Ingestion/InMemoryLedger.cs:72-198`
**Issue:** `InMemoryLedger` re-implements the store's apply semantics (merge unconditionally sets status and clears flags, `Attach` rejects a repeated reference even for the same row, restore and upgrade ordering). Any divergence from the PostgreSQL store makes the replay result meaningless, and nothing runs the same plans through both. Also a stray blank line sits before the closing brace of `ApplyUpdate` (line 170).
**Fix:** Add an integration test that applies the same synthetic captures through the real store and compares the resulting rows and counters with `InMemoryLedger`.

### IN-404: Dashboard "read-only reporting" guard misses comma joins and only checks the first relation after FROM/JOIN

**File:** `Ledger.UnitTests/Dashboards/DashboardGeneratorTests.cs:17,177-182`
**Issue:** `\b(?:FROM|JOIN)\s+([^\s,;()]+)` captures one relation per `FROM`. `FROM reporting.a, public.b` passes (`public.b` is never seen), and any query shaped like `EXTRACT(EPOCH FROM now())` would be flagged as a non-reporting relation. The role privileges still block `public`, so this is a guard-quality issue, not an exposure.
**Fix:** Also reject `public.` anywhere in a query and any statement other than `SELECT`/`WITH`, or parse the SQL properly.

### IN-405: Test name promises "exactly three" offending keys but only asserts presence

**File:** `Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs:79-90`
**Issue:** `ThrowIfInvalid_names_exactly_three_offending_keys_and_no_values` asserts three keys are present and `localhost` is absent. It does not assert that no other key is named.
**Fix:** Assert the set of named keys is exactly those three (for example by counting `:`-containing tokens or checking `NotContain` for each of the other known keys).

### IN-406: Misleading test names and parameter labels

**File:** `Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs:171-195`, `Ledger.UnitTests/Ingestion/EnableBanking/EnableBankingClientTests.cs:380-389`, `Ledger.UnitTests/Ingestion/SyncScheduleTests.cs:187-198`
**Issue:**
- `A_run_that_would_exceed_the_call_budget_stops_before_calling_and_applies_nothing` asserts that exactly one transaction call was made and the call ledger holds four entries.
- The `daysBack == 400` inline case is really 2025-09-01, which is 399 days before 2026-10-05.
- `Local_date_follows_the_zone_rather_than_utc_across_the_autumn_change` uses 22:30 to 23:30 UTC on 24 October, which does not cross the change at 01:00 UTC on 25 October.

**Fix:** Rename the first to say it stops after the last allowed call, drop the sentinel value 400 for the real date, and use instants that straddle the actual changeover (for example 00:30 and 01:30 UTC on the 25th).

### IN-407: Some redaction tests do not assert their own preconditions or can never fail on the negative

**File:** `Ledger.IntegrationTests/Security/LogRedactionTests.cs:27-75,143-157`
**Issue:**
- The first test never asserts the status of the three requests (401, 200, 404). If the app answered 500 or the route moved, the sentinel is still absent and the test passes.
- It also does not include the valid token itself in the search lists.
- `Empty_api_key_header_...is_never_logged` sends an empty value and then asserts that the log does not contain `"X-Api-Key: "`. A log line quoting an empty header would not necessarily contain that exact text, and nothing is sent that could leak.

**Fix:** Assert the expected status code of each request, add `validToken` to the sentinels, and drop or rework the empty-header log assertion.

### IN-408: Free-port picking is racy

**File:** `Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs:39-40,159-166`
**Issue:** Each port is obtained by binding a `TcpListener` on port 0, stopping it, and handing the number to Kestrel later. Another process can take the port in between, and the two ports are chosen before either is bound. This is rare but produces unexplained failures on busy machines.
**Fix:** Let Kestrel bind port 0 and read the actual addresses from `IServerAddressesFeature` after start, or retry host start on `AddressInUseException`.

---

_Reviewed: 2026-10-07_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
