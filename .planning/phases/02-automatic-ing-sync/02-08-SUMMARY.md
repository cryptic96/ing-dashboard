---
phase: 02-automatic-ing-sync
plan: 08
subsystem: ingestion
tags: [scheduler, retry, call-budget, provider-calls, sync-now, consent-expiry, quota]

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plan 01 SyncSchedule time math; plans 04 and 05 orchestrator and reconciliation; plan 07 link flow, dispatcher, PSU context"
provides:
  - "Daily Amsterdam-time scheduler over persisted runs (minute tick, late-start catch-up, one same-day retry)"
  - "Append-only provider_calls ledger written before every account-data call, with a per-account background call budget"
  - "Failure policy per outcome, including the immediate consent-expired flip on a consent rejection"
  - "POST /api/v1/bank/sync with attended (PSU) runs and the last-call refusal for unattended ones"
  - "Restart safety: unfinished runs are abandoned at startup and count as a transient failure for the day's retry"
affects: [02-automatic-ing-sync]

tech-stack:
  added: []
  patterns:
    - "Providers must await FetchContext.Meter.BeforeCallAsync immediately before each account-data request; the meter records first and refuses over-budget background calls"
    - "SyncScheduler is always registered and idles unless Ingestion:SchedulerEnabled is true and a provider is configured, so options are read at runtime rather than at registration"
    - "Schedule decisions are pure (SyncSchedule.Decide over the day's persisted runs), so the tick loop is stateless and restart-safe"
    - "SchedulerTestHost: a host over its own fresh database with a FakeTimeProvider, for tests that call RunDueSyncsAsync with explicit instants"

key-files:
  created:
    - Ledger.Domain/Ingestion/CallBudget.cs
    - Ledger.Domain/Ingestion/IProviderCallStore.cs
    - Ledger.Repository/Entities/ProviderCallEntity.cs
    - Ledger.Repository/Stores/ProviderCallStore.cs
    - Ledger.Repository/Migrations/20260930185230_AddProviderCalls.cs
    - Ledger.Service/Ingestion/ProviderCallMeter.cs
    - Ledger.Service/Ingestion/SyncScheduler.cs
    - Ledger.UnitTests/Ingestion/CallBudgetTests.cs
    - Ledger.IntegrationTests/Ingestion/SchedulerAndQuotaTests.cs
  modified:
    - Ledger.Domain/Ingestion/SyncSchedule.cs
    - Ledger.Domain/Ingestion/ISyncRunStore.cs
    - Ledger.Domain/Ingestion/IBankConnectionStore.cs
    - Ledger.Domain/Banking/ProviderModels.cs
    - Ledger.Repository/LedgerDbContext.cs
    - Ledger.Repository/Stores/SyncRunStore.cs
    - Ledger.Repository/Stores/BankConnectionStore.cs
    - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
    - Ledger.Service/Ingestion/SyncOrchestrator.cs
    - Ledger.Service/Ingestion/IngestionOptions.cs
    - Ledger.Service/Ingestion/IngestionServiceCollectionExtensions.cs
    - Ledger.Service/Ingestion/BankLinkService.cs
    - Ledger.Service/Ingestion/Synthetic/SyntheticBankDataProvider.cs
    - Ledger.Service/Endpoints/BankEndpoints.cs
    - Ledger.Service/Hosting/ProductionConfigurationValidator.cs
    - Ledger.UnitTests/Ingestion/SyncScheduleTests.cs
    - Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs
    - Ledger.IntegrationTests/Database/DatabaseRoleTests.cs
    - Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs
    - Ledger.IntegrationTests/Ingestion/IngestionTestSupport.cs
    - docs/rest-api.md
    - docs/bank-link.http

key-decisions:
  - "The scheduler skips connections that are not Active and Active ones whose consent end has passed, so a revoked, superseded, expired or provider-expired connection never spends budget"
  - "Retry is allowed after FailedTransient, FailedMalformed and Abandoned only; every other non-success outcome (rate limit, quota, consent, credentials) suppresses all automatic runs for the local day, whatever trigger produced it"
  - "MarkStatusAsync(ProviderExpired) only applies to an active connection, so a consent rejection arriving late can never turn a revoked or superseded connection back into an expired one"
  - "Sync now without an unambiguous target answers 409 (several active connections and no key, an ended consent) rather than guessing; an unknown key is 404"
  - "merged/dropped counters were not added to sync_runs; the migration only adds provider_calls"

patterns-established:
  - "Meter-before-call contract for every provider adapter (the real adapter must honour FetchContext.Meter)"
  - "Test hosts disable the background scheduler by default through the factory's base configuration; scheduler tests opt in or call RunDueSyncsAsync directly"

requirements-completed: [INGEST-01, INGEST-05, INGEST-07, API-01]

duration: 95min
completed: 2026-09-30
status: complete
actuals:
  tokens: 29000
  tasks: 3
  commits: 3
---

# Phase 2 Plan 08: Daily scheduler, call budget and sync now Summary

**The ledger now syncs every active connection at 06:30 Amsterdam time on its own, retries a temporary failure once four hours later on the same day only, counts every bank call in an append-only ledger before sending it, never exceeds four unattended calls per account in 24 hours, and the operator can sync on demand without touching that allowance.**

## Performance

- **Duration:** ~95 min
- **Tasks:** 3 (tracer, failure policy and budget, sync now)
- **Files changed:** 33 (9 created, 24 modified), 2,959 insertions

## Accomplishments

- **Scheduler:** `SyncScheduler.RunDueSyncsAsync(now)` visits active connections in authorisation order, loads the local day's runs and asks `SyncSchedule.Decide`. The hosted loop runs once at start (catch-up), then on a one-minute `PeriodicTimer` built with the injected `TimeProvider`. DST days (2026-10-25, 2027-03-28) are covered by unit tests.
- **Decide rules:** None before the scheduled local time, while a run is unfinished, after any success, and for the rest of the day after a rate limit, quota exhaustion, consent rejection or credential rejection (even when it came from a manual run). A failed scheduled run gives exactly one Retry at finish plus four hours, only when that moment is still the same local day (239 minutes is None, 240 is Retry; a 21:00 failure gives none).
- **Call ledger:** `provider_calls` (id, account, run, called_at, kind, background) with an `(account_id, called_at)` index; `ledger_runtime` can only insert and select (role test proves `UPDATE`, `DELETE`, `TRUNCATE` fail with 42501). `ProviderCallMeter` writes the row before each call; the synthetic provider awaits it before each page and balances read.
- **Budget:** `CallBudget.Remaining` counts `(now - 24h, now]` or the current Amsterdam date; a call exactly 24 hours old does not count. A background run over budget stops before calling (one call made, then `QuotaExhausted`, nothing applied for that account). Attended runs are recorded but never refused.
- **Failure policy:** rate limit -> `FailedRateLimited` with the provider code and no retry that day (and exactly one page call, so no hidden HTTP retry); consent rejection -> `FailedConsent` and the connection becomes `provider_expired` at once; credential rejection -> `FailedProviderAuth`; restart leftovers -> `Abandoned` at startup and a transient failure for the retry.
- **Sync now:** `POST /api/v1/bank/sync[?connectionKey=]` returns 202 `{"status":"queued"}` and queues a Manual run carrying the operator's PSU context (calls recorded `background=false`). With `Ingestion:PsuHeadersOnOperatorSyncs=false` it returns 429 when any selected account has one or fewer background calls left (two left is accepted). 409 for no connection, no selected account, a running sync, several active connections without a key or an ended consent; 404 unknown key; 503 not configured; 401 without a key.
- **Nothing interactive fetches:** a recording dispatcher plus the provider call log prove that GET connections, GET accounts, `/health` and `/metrics` queue nothing, create no `sync_runs` row and make no provider transaction or balance call.
- **Unselected-account warning:** one warning naming only the connection key, 30 minutes after authorisation, when a connection has no selected account and no run; repeated ticks log it once.
- **Production validation:** `Ingestion:TimeZone` must resolve and `Ingestion:ScheduleLocalTime` must be `HH:mm` and exist on every day of the next 400 days; only the key is named.
- **Verification (local PostgreSQL container):** unit Scheduler, Sync and Configuration categories, integration Sync (22) and DatabaseRoles (7), then the full solution: 261 tests, 260 passed, 1 skipped (the bundle test, as before). `build/lint.sh repo-rules` and `secrets` pass.

## Task Commits

1. **Task 1 (tracer): scheduler tracer with provider call ledger** - `cd4f2c8` (feat)
2. **Task 2: retry once when safe, enforce the call budget, flip consent on rejection** - `2fc09e2` (feat)
3. **Task 3: sync now with the operator present, proof nothing interactive fetches** - `5beb192` (feat)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] MarkStatusAsync could resurrect a closed connection as expired**
- **Found during:** Task 2 (wiring the consent flip)
- **Issue:** `MarkStatusAsync` updated any connection by id, so a consent rejection arriving after a revoke or renewal would have overwritten `revoked` or `superseded` with `provider_expired`.
- **Fix:** Marking `ProviderExpired` only applies where the stored status is `active` (conditional `ExecuteUpdate`); the interface documents it.
- **Files modified:** Ledger.Repository/Stores/BankConnectionStore.cs, Ledger.Domain/Ingestion/IBankConnectionStore.cs
- **Commit:** 2fc09e2

**2. [Rule 3 - Blocking] Existing pipeline tests now hit the default budget**
- **Found during:** Task 3 (full-suite run)
- **Issue:** `SyntheticSyncPipelineTests` and `ReconciliationPipelineTests` sync one account many times within 24 hours and ended `QuotaExhausted` under the default of four calls.
- **Fix:** `IngestionTestSupport.CreateFactory` sets `Ingestion:BackgroundCallsPerDay` to 1000; the reconciliation tests are about matching, not quota.
- **Files modified:** Ledger.IntegrationTests/Ingestion/IngestionTestSupport.cs
- **Commit:** 5beb192

**3. [Rule 3 - Blocking] Hosted scheduler would run inside unrelated test hosts**
- **Found during:** Task 1 (design of the registration)
- **Issue:** The plan registers the hosted service only when `Ingestion:SchedulerEnabled` is true, read at registration. Test host configuration is applied after registration, and existing hosts share one database, so a live scheduler could have synced leftover connections of other tests.
- **Fix:** The scheduler is always registered and decides at runtime (`SchedulerEnabled` false or the disabled provider means it does nothing); `LedgerWebApplicationFactory` sets `SchedulerEnabled=false` by default and the restart test opts back in.
- **Files modified:** Ledger.Service/Ingestion/SyncScheduler.cs, Ledger.Service/Ingestion/IngestionServiceCollectionExtensions.cs, Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs
- **Commit:** cd4f2c8

**4. [Rule 3 - Blocking] Npgsql connection pools exhausted the database server**
- **Found during:** Task 3 (22 tests each with their own database and two roles)
- **Issue:** "too many clients already" once per-test databases accumulated idle pooled connections.
- **Fix:** `SchedulerTestHost.DisposeAsync` clears the pools of its runtime and backup connection strings.
- **Commit:** 5beb192

### Plan reading choices

- The scheduler also skips an Active connection whose consent end has passed, and sync now refuses one with 409; the plan only names superseded and revoked. This avoids spending a call on a session the bank will reject.
- `SyncNowResult` has the six planned members; ambiguity (several active connections, no key), an unknown key and an ended consent are reported as `BankLinkException` (409 or 404) so the endpoint mapping stays in one place.
- `ProviderCallMeter` reads `IngestionOptions` directly rather than `IOptions`, and resolves the time zone once per meter.
- The warning for an unselected connection needs a full history, so it is gated on "no run ever recorded", not just "no run today".
- Not covered by a test: sync now with several active connections and no key (the synthetic scenario links one identity per provider, so a second active connection cannot be made without renewal plumbing). The branch is three lines and throws the same conflict type as the other tests exercise.
- The orchestrator notes offered a merged/dropped column on `sync_runs`; it did not fit this migration and was left out.

### Process notes

- **Test-first order:** as in the preceding plans, implementation and tests were written in one pass per task and committed together, with no separate failing-test commits. The plan type is `execute`, so no plan-level TDD gate applies.
- **Tracer gate:** auto mode was not active; `human_verify_mode` is `end-of-phase`, so the human check is deferred to phase verification. The tracer's automated verify (unit Scheduler, integration Sync and DatabaseRoles, repo-rules lint) passed before the expansion tasks.
- **Shared files:** STATE.md, ROADMAP.md and REQUIREMENTS.md were not touched. Requirement ids covered: INGEST-01, INGEST-05, INGEST-07, API-01.

## Notes for later plans

- **Every provider adapter must call `context.Meter.BeforeCallAsync` immediately before each account-data request** (transaction page and balances read). The synthetic provider does; the Enable Banking adapter must too, and must not add any automatic HTTP retry.
- The call budget counts every page and balances call against `Ingestion:BackgroundCallsPerDay` (default 4, rolling 24 hours). If the quota probe shows the bank counts differently, switch `Ingestion:QuotaWindow` or the number; no code change is needed.
- Consent expiry alerts and sync metrics (the observability plan) can read `sync_runs.outcome`, `provider_error` and the `provider_expired` status introduced here.
- `BankLinkTestHost` and `RenewableSyntheticProvider` were not modified. `SchedulerTestHost` in `SchedulerAndQuotaTests.cs` is the reusable host for tests that need a fresh database and a controllable clock.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model. Mitigations implemented and tested: self-inflicted quota exhaustion (call ledger before every call, per-account budget, no same-day retry after refusals, no HTTP-level retry, last-call refusal for unattended sync now), interactive paths never fetch (test over read endpoints, health and metrics), call ledger is append-only for the runtime role (role test), every run finishes with an outcome and provider code and orphaned runs are abandoned at startup, production validation names an unresolvable time zone or schedule time.

## Issues Encountered

None beyond the deviations above.

## Self-Check: PASSED

- Files present: every file listed under key-files.created exists; modified files changed as listed.
- Commits present: cd4f2c8, 2fc09e2, 5beb192.
- Acceptance checks: one `_AddProviderCalls` migration and no generator marker under Migrations; `PeriodicTimer` in the scheduler; `CallBudgetExhaustedException` in the meter; `ProviderExpired` in the orchestrator; `Ingestion:TimeZone` in the validator; `/api/v1/bank/sync` in the endpoint file and both docs; no `AddStandardResilienceHandler` in the service; exactly one `AllowAnonymous`.
