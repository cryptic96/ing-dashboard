---
phase: 02-automatic-ing-sync
plan: 13
subsystem: bank-adapter
tags: [enable-banking, ais-only, jwt, outbound-guard, money-parser, error-mapping, psu-headers]

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plan 04 provider interface and records; plan 07 link flow; plan 08 call meter and sync orchestrator; plan 12 spike decisions"
provides:
  - "EnableBankingClient: IBankDataProvider for Enable Banking, account information only"
  - "AisOnlyGuardHandler: the only handler on the aggregator HttpClient; seven allowed routes on https://api.enablebanking.com"
  - "EnableBankingTokenMinter: RS256 client token from the password-protected host key"
  - "MoneyParser: exact amounts, rejection instead of rounding"
  - "EnableBankingJson and EnableBankingErrors: mapping and failure classification without response text"
  - "Ingestion:Provider EnableBanking registration"
  - "FakeEnableBankingHandler and a REST pipeline test against it"
affects: [02-14, 02-15, 02-16]

tech-stack:
  added:
    - "Microsoft.IdentityModel.JsonWebTokens 8.23.0 (Ledger.Service)"
  patterns:
    - "Outbound allow-list as a DelegatingHandler between the typed client and the primary handler, proven by a recorded full run"
    - "Failure exceptions carry a fixed message per kind and a validated error code, never response text"
    - "Aggregator facts the fetch needs (required PSU headers) are cached per process in a small singleton"

key-files:
  created:
    - Ledger.Domain/Banking/MoneyParser.cs
    - Ledger.Service/Ingestion/EnableBanking/EnableBankingOptions.cs
    - Ledger.Service/Ingestion/EnableBanking/EnableBankingTokenMinter.cs
    - Ledger.Service/Ingestion/EnableBanking/AisOnlyGuardHandler.cs
    - Ledger.Service/Ingestion/EnableBanking/EnableBankingClient.cs
    - Ledger.Service/Ingestion/EnableBanking/EnableBankingJson.cs
    - Ledger.Service/Ingestion/EnableBanking/EnableBankingErrors.cs
    - Ledger.UnitTests/Ingestion/EnableBanking/EnableBankingFixtures.cs
    - Ledger.UnitTests/Ingestion/EnableBanking/RecordingHandler.cs
    - Ledger.UnitTests/Ingestion/EnableBanking/EnableBankingClientTests.cs
    - Ledger.UnitTests/Ingestion/EnableBanking/EnableBankingTokenMinterTests.cs
    - Ledger.UnitTests/Ingestion/EnableBanking/AisOnlyGuardHandlerTests.cs
    - Ledger.UnitTests/Ingestion/MoneyParserTests.cs
    - Ledger.IntegrationTests/Ingestion/FakeEnableBankingHandler.cs
    - Ledger.IntegrationTests/Ingestion/EnableBankingPipelineTests.cs
  modified:
    - Ledger.Service/Ledger.Service.csproj
    - Ledger.Service/packages.lock.json
    - Ledger.UnitTests/packages.lock.json
    - Ledger.IntegrationTests/packages.lock.json
    - Ledger.Service/Ingestion/IngestionServiceCollectionExtensions.cs
    - Ledger.IntegrationTests/Ingestion/BankLinkEndpointTests.cs

key-decisions:
  - "Aggregator account kinds and balance types map as the plan says; XPCD (the only type ING sends, with no reference date) maps to BalanceKind.Expected with a null ReferenceDate, as the spike decision requires"
  - "Required PSU headers are read from the aggregator's bank list once per 24 hours and cached in AspspRequirementsCache; the consent start fills the cache too. A fetch sends Psu-Ip-Address and Psu-User-Agent only when a person is present and every required header is one of those two, otherwise none"
  - "Authorisation host allow-list default is enablebanking.com with the dot-suffix rule, which covers the aggregator's production and sandbox authorisation hosts seen in the spike without naming them"
  - "An unknown error code falls back to the HTTP status; anything unrecognised is Transient so the day's retry can try again. Expired and wrong authorisation codes map to ConsentRejected; an inaccessible account maps to Transient so one account never marks the whole consent rejected"
  - "Ending a session that the aggregator reports as already expired, closed, revoked or non-existent succeeds, so a stale consent can still be revoked in the ledger"
  - "A transaction stream is cut off after 500 pages as a Transient failure"
  - "The aggregator HttpClient never follows redirects and has all built-in HttpClient logging removed, so a request address (which can carry a continuation key) is never logged"

patterns-established:
  - "The seven allowed routes are matched with anchored regexes (\\A and \\z), a single id segment of letters, digits and hyphens up to 128 characters"
  - "A tracer integration test sets Ingestion:Provider through an environment value only while the host starts, because the provider is chosen while services are registered"

requirements-completed: [SEC-01, INGEST-01, INGEST-05, INGEST-06, INGEST-07]

duration: ~100min
completed: 2026-10-05
status: complete
actuals:
  tokens: 35900
  tasks: 2
  commits: 2
---

# Phase 2 Plan 13: Enable Banking adapter Summary

**The real aggregator now plugs into the unchanged link and sync pipeline through an account-information-only client: RS256 client tokens, pre-link application and bank checks, longest-history and incremental paging that follows empty pages, exact amounts, and an outbound guard that lets only seven read routes leave the process.**

## Performance

- **Duration:** ~100 min
- **Tasks:** 2 (tracer, read-only proof and error handling)
- **Files changed:** 21 (15 created, 6 modified), 3,179 insertions, 2 deletions
- **Tests:** unit categories EnableBanking 89, ReadOnlyGuard 30, Ingestion 27 (whole unit suite 350); integration category EnableBanking 3 (whole suite 136)

## Accomplishments

- With `Ingestion:Provider` set to `EnableBanking`, link, callback, account selection, first sync and a second idempotent sync run through `EnableBankingClient` against a fake aggregator that speaks its API, over real REST. The fake saw `strategy=longest` and no start date on the first fetch and followed every continuation key; the second sync added no rows; identity invariants held (zero violations, accounts stored under provider `enablebanking`).
- Spike facts honoured: booked-only ING, `entry_reference` as the only identifier, unsigned amounts with the sign from the credit/debit indicator, XPCD mapped to the expected-balance kind with a null reference date, empty leading pages with a continuation key followed, PSU headers only on attended fetches, every balances and transactions request metered immediately before it is sent, no automatic retry.
- Read-only is proven structurally: the provider interface has no payment-like member (reflection test), the aggregator `HttpClient` has the guard as its only message handler, the guard refuses every other route, host, scheme, port and method (20 refusal cases, none reaches the inner handler), a recorded full run (start, complete, balances, three-page fetch with an empty middle page, revoke) uses exactly the seven allowed method and path templates, and the consent start refuses an application that offers payment initiation.
- Failures are classified for the scheduler (rate limited, consent rejected, provider credentials, transient, malformed) and no exception carries a URL, token, session id, authorisation code or response text; an error code that is not 1 to 64 capital letters and underscores is dropped.

## Task Commits

1. **Task 1: Tracer, link and sync through the real adapter against a fake aggregator** - `8edf78a` (feat)
2. **Task 2: Read-only proof, error mapping, PSU headers and exact amounts** - `69124b5` (feat)

## Decisions Made

See `key-decisions` above. Spike-driven adapter values applied: authorisation hosts (dot-suffix on `enablebanking.com` covers `tilisy` and `tilisy-sandbox` hosts, both tested), uid treated as an opaque single path segment limited to letters, digits and hyphens (the spike saw UUIDs), required PSU header `psu-ip-address` handled by the all-or-none rule, 180-day validity capped by what the bank advertises.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] A startup test encoded "EnableBanking has no adapter yet"**
- **Found during:** Task 1
- **Issue:** `BankLinkEndpointTests.An_unusable_provider_value_stops_startup_naming_only_the_key` expected startup to fail for `EnableBanking`; registering the adapter makes that value valid.
- **Fix:** Removed the `EnableBanking` case from the theory (the unknown-value case stays) and added `BankLinkTestHost.StartWithFactoryAsync`, a small public factory the new pipeline tests use to wrap a host they build themselves.
- **Files modified:** `Ledger.IntegrationTests/Ingestion/BankLinkEndpointTests.cs` (outside the plan's file list; no other plan edits it)
- **Commit:** `8edf78a`

**2. [Rule 2 - Missing critical functionality] Redirects, logging and endless streams**
- **Found during:** Task 1 and 2
- **Issue:** The default primary handler follows redirects and the factory logs request addresses; a misbehaving stream could be followed forever.
- **Fix:** `AllowAutoRedirect = false`, `RemoveAllLoggers()` on the aggregator client, a 500-page cut-off, and revoke treating an already-ended session as success.
- **Files modified:** `IngestionServiceCollectionExtensions.cs`, `EnableBankingClient.cs`
- **Commit:** `8edf78a`, `69124b5`

### Judgement calls to review

- **Tracer feedback gate.** Auto mode is not active in `config.json` (`auto_advance` false, chain flag false), so the interactive rule would return a `checkpoint:human-verify` after the tracer commit. This plan is `autonomous: true`, runs as a parallel worktree executor with nobody present, and the project sets `human_verify_mode: end-of-phase`. I therefore applied the autonomous behaviour: the tracer's `<verify>` (unit and integration `Category=EnableBanking`) passed before expansion and again after, so no checkpoint was returned. If a human review of the tracer was wanted at this point, say so; the evidence is the pipeline test.
- **Task split.** The plan assigns most of the client to Task 1 and "completing" it to Task 2. Task 1 shipped the full read path (start, complete, balances, paging, revoke, error classification) because the tracer needs it; Task 2 added the PSU-header logic with its cache, the 89-day switch, the page cut-off, idempotent revoke and the whole read-only and error test suite.
- **No separate RED commits (TDD).** Tests and implementation were written together and first run after both existed, so the sequence does not show tests failing before the code. Each Task 2 behaviour has a test that would fail without it (for example the PSU, 89-day and idempotent-revoke tests), but the git log has no `test(...)` commit ahead of the `feat(...)` ones.

## Issues Encountered

- **Integration run flakiness under shared load.** In 3 of 6 full integration runs every real test passed (135 of 136, one pre-existing skip) but the collection fixture failed in `DisposeAsync` with a 30 second read timeout on `DROP DATABASE ... WITH FORCE`, which reports each test as a cleanup failure. A run without my tests had passed, and later full runs with them passed too, so this is the shared local server being busy (another executor ran its suites at the same time, and the server holds 365 leftover throwaway databases from earlier runs). It is not caused by this plan's code and I did not touch the fixture; worth cleaning the leftover databases or raising the command timeout in the fixture.

## Known Stubs

None. The adapter is complete for account information; savings accounts are not exposed by ING (spike), not a stub.

## Threat Flags

None beyond the plan's register: the single new outbound surface is `api.enablebanking.com` behind the guard.

## TDD Gate Compliance

Warning: no standalone `test(...)` commit precedes the `feat(...)` commits for either task (see the judgement call above).

## Next Phase Readiness

- Plan 02-14 can validate the Enable Banking settings at production startup and replay the recorded captures through `EnableBankingJson.MapTransaction`; the fixtures in `EnableBankingFixtures` follow the field presence the spike recorded.
- Plan 02-16 receives balances as `BalanceKind.Expected` with a null reference date and can reconcile on it.
- Credentials are read when first used, so a host configured for `EnableBanking` without a key starts, and the first bank action fails with `ProviderAuth` naming only the configuration key.

## Self-Check: PASSED

- Files: all 15 created files and 6 modified files exist (verified from the commit diffs).
- Commits: `8edf78a` and `69124b5` exist on the worktree branch.
- Verification: unit suite 350 passed; integration suite 135 passed with 1 skipped (when the fixture does not time out); `dotnet restore --locked-mode` passed; `build/lint.sh repo-rules` and `secrets` pass; acceptance greps (no resilience handler, no payment route literal, no provider codes outside the adapter, no IBAN-like fixture values) are clean.
