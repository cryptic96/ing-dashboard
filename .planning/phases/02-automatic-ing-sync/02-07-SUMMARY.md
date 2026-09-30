---
phase: 02-automatic-ing-sync
plan: 07
subsystem: ingestion
tags: [bank-link, consent, oauth-state, callback, renewal, revocation, rest-api, security]

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plan 04: provider-agnostic ingestion core, synthetic provider, SyncOrchestrator, connection store"
provides:
  - "Guided consent flow over REST: authenticated link and renew start, single anonymous callback, connection and account listing, account selection, revoke"
  - "LinkStateToken (256-bit, base64url, SHA-256 at rest) and bank_authorizations with atomic single-use consumption"
  - "ConsentState.Derive: linked, expiring (under 14.0 days), expired, revoked, superseded"
  - "Renewal that maps accounts by identification hash in one transaction and supersedes the old connection"
  - "ChannelSyncDispatcher and SyncWorker for operator-triggered syncs carrying the operator's PSU context"
  - "Provider selection by Ingestion:Provider with Production refusal of Synthetic and unknown values"
affects: [02-automatic-ing-sync]

tech-stack:
  added: []
  patterns:
    - "Every bank endpoint is a thin HTTP translation over BankLinkService; failures are BankLinkException with a safe message mapped to problem details"
    - "The callback is the only route with anonymous access; every failure path returns one identical body with no-store and no-referrer"
    - "Provider selection is read while services are registered, so tests pass Ingestion:Provider as a process environment value only during host startup"
    - "Test provider decorator (RenewableSyntheticProvider) returns the same accounts under new provider ids to exercise renewal without touching the shared scenario"

key-files:
  created:
    - Ledger.Domain/Ingestion/ConsentState.cs
    - Ledger.Domain/Ingestion/LinkStateToken.cs
    - Ledger.Domain/Ingestion/IBankAuthorizationStore.cs
    - Ledger.Repository/Entities/BankAuthorizationEntity.cs
    - Ledger.Repository/Stores/BankAuthorizationStore.cs
    - Ledger.Repository/Migrations/20260930183122_AddBankAuthorizations.cs
    - Ledger.Service/Ingestion/BankLinkService.cs
    - Ledger.Service/Ingestion/BankLinkOptions.cs
    - Ledger.Service/Ingestion/SyncDispatcher.cs
    - Ledger.Service/Ingestion/PsuContextFactory.cs
    - Ledger.Service/Ingestion/Synthetic/SyntheticDemoScenario.cs
    - Ledger.Service/Endpoints/BankEndpoints.cs
    - Ledger.UnitTests/Ingestion/LinkStateTokenTests.cs
    - Ledger.UnitTests/Ingestion/ConsentStateTests.cs
    - Ledger.IntegrationTests/Ingestion/BankLinkEndpointTests.cs
    - docs/bank-link.http
  modified:
    - Ledger.Domain/Ingestion/IBankConnectionStore.cs
    - Ledger.Repository/LedgerDbContext.cs
    - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
    - Ledger.Repository/Stores/BankConnectionStore.cs
    - Ledger.Repository/Migrations/LedgerDbContextModelSnapshot.cs
    - Ledger.Service/Ingestion/IngestionOptions.cs
    - Ledger.Service/Ingestion/IngestionServiceCollectionExtensions.cs
    - Ledger.Service/Hosting/ProductionConfigurationValidator.cs
    - Ledger.Service/Program.cs
    - Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs
    - Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs
    - Ledger.IntegrationTests/Security/LogRedactionTests.cs
    - docs/rest-api.md

key-decisions:
  - "Renewal is allowed for active and provider-expired connections (an ended consent is the main renewal case); revoked and superseded connections refuse it"
  - "A zero-account session is reported with the distinct control-panel message and records nothing; its session is not revoked, because ending it could close the consent being renewed at banks that keep one consent per customer"
  - "Infrastructure failures inside the callback are also answered with the generic failure body (logged by exception type only), so the failure response is identical in every case"
  - "GET /connections and ConsentState.Derive ship with the tracer, because the operator must learn the connection key before selecting accounts"
  - "BankLinkOptions carries AspspName and AspspCountry (defaults ING and NL) because the session contract has no bank name or country to record with a connection"
  - "The synthetic demo provider wraps the synthetic one and accepts any non-empty code, since nobody outside the process knows the scenario's random code"

patterns-established:
  - "Generic-failure callback contract: one body, one status, hardened headers on success and failure"
  - "BankLinkTestHost: a reusable test host speaking the operator API, exported for later plans' tests"

requirements-completed: [INGEST-01, INGEST-04, INGEST-05, API-01]

duration: 75min
completed: 2026-09-30
status: complete
actuals:
  tokens: 42000
  tasks: 3
  commits: 3
---

# Phase 2 Plan 07: Guided bank link, hardened callback and renewal Summary

**One authenticated call, one approval in the bank app and one selection call now link the accounts and start the longest-history sync; renewal keeps every account's identity and history, and the single anonymous callback is protected by a one-time, hashed, 15-minute state and answers every failure identically.**

## Performance

- **Duration:** ~75 min
- **Tasks:** 3 (tracer, hardening, consent and renewal)
- **Files changed:** 30 (16 created, 13 modified, plus the generated designer and snapshot), 3,435 insertions

## Accomplishments

- Link, callback, selection and first sync work over real HTTP on PostgreSQL: the selected account's rows appear in `reporting.transactions` under its display name, the first query uses the longest history, and the unselected account is never fetched.
- `bank_authorizations` stores only the SHA-256 of a 256-bit state; `TryConsumeAsync` is one `ExecuteUpdate` guarded by `consumed_at IS NULL AND expires_at > now`, so a state works exactly once.
- The callback rejects a malformed state or an oversized code before any database or provider call; reused, unknown, expired, cancelled, wrong-code, missing-parameter and oversized-code requests return the same 400 body, all with `Cache-Control: no-store` and `Referrer-Policy: no-referrer`.
- An inventory test proves the callback is the only route with anonymous metadata; every other bank route returns 401 without a key. The existing 401 inventory test skips exactly that route.
- Provider selection: `None` keeps the disabled provider (bank endpoints answer 503 "not configured", detected by the resolved provider type so tests can inject one); `Synthetic` registers a fixed demo scenario; `EnableBanking` and unknown values stop startup naming only `Ingestion:Provider`. The Production validator refuses Synthetic and unknown values and requires an https `BankLink:RedirectUrl` for EnableBanking, naming keys only.
- `ConsentState.Derive`: exactly 14 days left is linked, one second less is expiring, exactly zero is expired, provider-expired is expired, revoked and superseded are kept; days are negative after expiry.
- Renewal runs in one transaction: new connection, accounts matched by `(provider, identification_hash)` keep id, key, display name and selection with refreshed provider name, product and IBAN, new accounts arrive unselected, the old connection becomes superseded through a conditional update (race-safe), and a PostLink sync with the longest history is queued with the callback browser's PSU context. The old session is deliberately not revoked at the aggregator.
- Revoke ends the provider session first and marks the connection revoked only afterwards; a provider failure returns 502 and changes nothing.
- Verification against the local PostgreSQL container: unit Callback (8), Consent (9) and Configuration (11) categories; integration Callback (9 including the provider-selection and startup cases), Consent (5), ApiAuth (7) and LogRedaction (7); the full solution passes (155 tests, 154 passed, 1 skipped bundle test, as before). `build/lint.sh repo-rules` and `secrets` pass.

## Task Commits

1. **Task 1 (tracer): link, callback, selection and first sync over real HTTP** - `149ea8b` (feat)
2. **Task 2: hardened callback, anonymous-route inventory, redaction, provider selection** - `c8a7aea` (feat)
3. **Task 3: consent state, renewal that keeps history, revocation, documentation** - `774c99a` (feat)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Existing 401 inventory test failed once the callback existed**
- **Found during:** Task 1 (running the ApiAuth category after adding the callback)
- **Issue:** `Every_mapped_endpoint_returns_401_without_a_key` enumerates every route and now hit the intentionally anonymous callback.
- **Fix:** Skipped exactly `/api/v1/bank/callback` in that test in the Task 1 commit (the plan scheduled this for Task 2), so no commit left a failing suite.
- **Files modified:** Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs
- **Commit:** 149ea8b

**2. [Rule 2 - Missing critical functionality] Operator could not learn the connection key**
- **Found during:** Task 1 (designing the tracer test)
- **Issue:** The callback deliberately reveals no identifier, so selecting accounts was impossible without listing connections.
- **Fix:** GET `/api/v1/bank/connections` and `ConsentState.Derive` (plus the `ConnectionStatus`, `ConsentView` and `ConsentSnapshot` types) ship with the tracer; Task 3 added the boundary tests and the renew and revoke routes.
- **Commit:** 149ea8b

**3. [Rule 2 - Missing critical functionality] Bank name and country had no source**
- **Found during:** Task 1 (BankLinkService completing a consent)
- **Issue:** `AddConnectionAsync` needs an ASPSP name and country, which the fixed provider session contract does not carry.
- **Fix:** `BankLinkOptions.AspspName` and `AspspCountry`, defaulting to the bank the household uses and its country.
- **Commit:** 149ea8b

**4. [Rule 2 - Missing critical functionality] Demo provider was unusable outside tests**
- **Found during:** Task 2 (registering the synthetic provider by configuration)
- **Issue:** The scenario's authorisation code is random and private, so a local operator could never complete a demo consent.
- **Fix:** `SyntheticDemoScenario.CreateProvider` wraps the synthetic provider so a demo consent completes with any non-empty code. Production startup refuses the provider.
- **Commit:** c8a7aea

**5. [Rule 1 - Bug] Existing accounts kept stale provider details on re-link**
- **Found during:** Task 3 (renewal mapping)
- **Issue:** `AddConnectionAsync` never refreshed IBAN, provider name or product of an already known account.
- **Fix:** Shared `AttachAccountsAsync` refreshes those three fields for existing accounts (kind and currency are never changed) for both link and renewal.
- **Commit:** 774c99a

### Plan reading choices

- The plan text says renewal "refuses a connection that is no longer active"; I allow active and provider-expired (an expired consent is the usual renewal case) and refuse revoked and superseded.
- The worker logs the connection's internal id plus the exception type name on failure, not the connection key, because the queued request carries only the id; the id is not a bank identifier.
- Sentinel values for the redaction test are the scenario's own random session id and authorisation code (plus the state from the link address). The state legitimately appears in the link response address, so it is checked against logs, metrics and every other response.
- `IngestionTestSupport.cs` and `SyntheticBankScenario.cs` were not touched; the renewal tests use a provider decorator defined in the test file. The shared test host `BankLinkTestHost` lives in `BankLinkEndpointTests.cs`.

### Process notes

- **Test-first order:** as in the preceding plan, implementation and tests were written in one pass per task and committed together; no separate failing-test commits. The plan type is `execute`, so no plan-level TDD gate applies.
- **Tracer gate:** auto mode was not active. The project config sets `human_verify_mode: end-of-phase`, so the human check is deferred to phase verification rather than blocking a parallel worktree agent. The tracer's automated verify (unit and integration Callback categories, ApiAuth inventory, repo-rules lint) passed end to end before the expansion tasks.
- **Test host environment:** `Ingestion:Provider` is read while services are registered, before the factory's in-memory configuration applies, so two tests set it as a process environment value only while the host starts. Test collections run sequentially, so nothing else can observe it.
- **Shared files:** STATE.md, ROADMAP.md and REQUIREMENTS.md were not touched. Requirement ids covered: INGEST-01, INGEST-04, INGEST-05, API-01.

## Known Stubs

None. The Enable Banking adapter is intentionally absent: `Ingestion:Provider = EnableBanking` fails startup naming the key until the adapter plan registers it, and the aggregator host allow-list for authorisation addresses arrives with that adapter (the service already refuses any non-https address).

## Threat Flags

None beyond the plan's threat model. Mitigations implemented and tested: one-time hashed state with atomic consumption and 15-minute lifetime (T-02-07-01), identical generic failure body and hardened headers (T-02-07-02), no state, code or session id in logs, responses or metrics (T-02-07-03), a single anonymous route proven by an inventory test (T-02-07-04), Production refusal of the synthetic or unknown provider (T-02-07-05), protected session id at rest (T-02-07-06), account-key ownership and display-name validation (T-02-07-07), https-only authorisation addresses (T-02-07-09).

## Issues Encountered

None beyond the deviations above.

## Self-Check: PASSED

- Files present: every file listed under key-files.created exists; modified files changed as listed.
- Commits present: 149ea8b, c8a7aea, 774c99a.
- Acceptance checks: one `_AddBankAuthorizations` migration and no generator marker under Migrations; exactly one `AllowAnonymous` in the service; `MapBankEndpoints` appears once in Program.cs; `no-referrer`, `Ingestion:Provider` and the callback route are present where required; `Superseded`, `IdentificationHash`, and the link route in both docs are present.
