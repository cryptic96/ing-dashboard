---
phase: 01-secure-platform-release-pipeline
plan: 06
subsystem: auth
tags: [api-keys, authentication, sha256, aspnetcore, fallback-policy, forwarded-headers]

requires:
  - phase: 01-secure-platform-release-pipeline
    provides: LedgerDbContext, RepositoryServiceCollectionExtensions, LedgerWebApplicationFactory, DatabaseFixture, ledger_build_info version source (plan 01-01)
provides:
  - ApiKeyToken (generate/parse/hash, ldg_{keyid}_{secret} format)
  - IApiKeyStore / ApiKeyStore (create, list, revoke, validate against api_keys table)
  - AddApiKeys migration (api_keys table, unique key_id index, filtered unique name index)
  - Ledger.Service apikey create|list|revoke CLI, dispatched from Program.cs before the web host builds
  - deploy/bin/ledger-apikey LXC wrapper running the CLI as the service user
  - ApiKeyAuthenticationHandler (X-Api-Key scheme) plus a FallbackPolicy requiring authentication on every endpoint
  - GET /api/v1/status authenticated endpoint
  - Forwarded-header trust restricted to ReverseProxy:KnownProxies
affects: [any future REST endpoint (inherits the FallbackPolicy automatically), MCP/OAuth phase (may later replace or complement API keys per the phase context)]

actuals:
  tokens: 15190
  tasks: 2
  commits: 2

tech-stack:
  added: []
  patterns:
    - "Named per-client API keys: token format ldg_{16-hex-keyid}_{43-char-base64url-secret}, only key id + SHA-256(secret) stored"
    - "AuthenticationHandler<AuthenticationSchemeOptions> subclass for a custom header-based scheme, wired via AddScheme"
    - "AuthorizationOptions.FallbackPolicy = RequireAuthenticatedUser() as the default-deny mechanism, no per-endpoint [Authorize] needed"
    - "CLI subcommand dispatch inside Program.cs top-level statements, before WebApplication.CreateBuilder(args) proceeds past configuration loading"

key-files:
  created:
    - Ledger.Domain/Auth/ApiKeyToken.cs
    - Ledger.Domain/Auth/IApiKeyStore.cs
    - Ledger.Repository/Entities/ApiKeyEntity.cs
    - Ledger.Repository/Stores/ApiKeyStore.cs
    - Ledger.Repository/Migrations/20260927192231_AddApiKeys.cs
    - Ledger.Service/Cli/ApiKeyCommand.cs
    - Ledger.Service/Auth/ApiKeyAuthenticationHandler.cs
    - Ledger.Service/Endpoints/StatusEndpoints.cs
    - deploy/bin/ledger-apikey
    - deploy/ledger.env.example
    - docs/rest-api.md
    - Ledger.UnitTests/Auth/ApiKeyTokenTests.cs
    - Ledger.IntegrationTests/Auth/ApiKeyCommandTests.cs
    - Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs
  modified:
    - Ledger.Repository/LedgerDbContext.cs
    - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
    - Ledger.Service/Program.cs
    - Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs
    - .gitignore

key-decisions:
  - "Used a plain SHA-256 of the secret instead of a slow KDF (PBKDF2/Argon2) for hashing: the secret is a 256-bit CSPRNG value, not a human password, so offline guessing is already infeasible; a slow KDF would only add CPU cost per request on the low-power LXC host. Documented in a `///` summary on ApiKeyToken per the plan's explicit instruction."
  - "CLI dispatch happens on WebApplication.CreateBuilder(args).Configuration directly (before any further builder.Services calls), rather than constructing a separate ConfigurationBuilder — this reuses the same appsettings/env-var/command-line sources the web host would use, with no Kestrel/DI side effects from merely building (not .Build()-ing) the WebApplicationBuilder."
  - "ApiKeyCommand's internal Host.CreateApplicationBuilder() clears default logging providers (hostBuilder.Logging.ClearProviders()) so EF Core's Information-level command logging never leaks into the CLI's stdout, which must contain exactly one token line on create."

patterns-established:
  - "Any future REST endpoint automatically requires a valid API key (FallbackPolicy) without needing an explicit [Authorize] attribute; AllowAnonymous must never appear anywhere in Ledger.Service."

requirements-completed: [API-02, SEC-03, SEC-06]

coverage:
  - id: D1
    description: "Every REST endpoint returns 401 without a valid X-Api-Key, proven by enumerating every mapped RouteEndpoint at runtime"
    requirement: "API-02"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs#Every_mapped_endpoint_returns_401_without_a_key"
        status: pass
    human_judgment: false
  - id: D2
    description: "GET /api/v1/status returns 200 with the running version and the caller's key name for a valid key"
    requirement: "API-02"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs#Status_endpoint_returns_the_callers_name_and_running_version"
        status: pass
    human_judgment: false
  - id: D3
    description: "Keys are created/listed/revoked via ledger-apikey / Ledger.Service apikey, shown once, stored only as key id + SHA-256(secret)"
    requirement: "SEC-03"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Auth/ApiKeyCommandTests.cs#Create_list_revoke_and_recreate_a_named_key"
        status: pass
      - kind: unit
        ref: "Ledger.UnitTests/Auth/ApiKeyTokenTests.cs (12 tests: generation shape/uniqueness, strict TryParse rejections, fixed-time Matches)"
        status: pass
    human_judgment: false
  - id: D4
    description: "A revoked key is rejected on the very next request; concurrent valid/invalid/revoked requests are each decided independently"
    requirement: "SEC-03"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs#Revoked_key_is_rejected_on_the_next_request"
        status: pass
      - kind: integration
        ref: "Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs#Concurrent_requests_with_valid_revoked_and_malformed_keys_are_each_decided_independently"
        status: pass
    human_judgment: false
  - id: D5
    description: "Malformed, empty, oversized or repeated X-Api-Key headers return 401 never 500, and presented values never appear in logs"
    requirement: "SEC-06"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs#Malformed_empty_wrong_secret_oversized_or_duplicate_headers_return_401_never_500"
        status: pass
      - kind: integration
        ref: "Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs#Log_capture_never_contains_a_presented_token_or_its_secret"
        status: pass
    human_judgment: false
  - id: D6
    description: "The loopback ops endpoint stays unauthenticated; /health and /metrics are not served on the API port even with a valid key"
    requirement: "SEC-06"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs#Health_and_metrics_are_not_served_on_the_api_port_even_with_a_valid_key"
        status: pass
    human_judgment: false
  - id: D7
    description: "Forwarded headers are honoured only from ReverseProxy:KnownProxies; deploy/ledger.env.example ships only placeholders, no password"
    requirement: "SEC-06"
    verification:
      - kind: other
        ref: "grep -c 'ReverseProxy:KnownProxies' Ledger.Service/Program.cs (=1); grep -c '192.0.2.10' deploy/ledger.env.example (=1); grep -niE 'password' deploy/ledger.env.example (no match)"
        status: pass
    human_judgment: false

duration: ~40min
completed: 2026-09-27
status: complete
---

# Phase 01 Plan 06: REST API Authentication Summary

**Named, hashed, revocable per-client API keys enforced on every REST endpoint via an ASP.NET Core FallbackPolicy, with a `ledger-apikey` CLI wrapper and a first authenticated `GET /api/v1/status` endpoint.**

## Performance

- **Duration:** ~40 min
- **Completed:** 2026-09-27
- **Tasks:** 2
- **Files modified:** 21 (14 created, 7 modified)

## Accomplishments
- `ApiKeyToken` generates `ldg_{16-hex-keyid}_{43-char-base64url-secret}` tokens, strictly parses and rejects any malformed variant, and compares secrets in fixed time via `CryptographicOperations.FixedTimeEquals`
- `ApiKeyStore` persists only a key id and `SHA-256(secret)`, enforces the `^[a-z][a-z0-9-]{1,31}$` name rule and a single-active-key-per-name constraint (filtered unique index on `name` where `revoked_at IS NULL`)
- `Ledger.Service apikey create|list|revoke` CLI, dispatched from `Program.cs` before the web host is built, backed by `deploy/bin/ledger-apikey` running it as the `ledger` service user via `systemd-run`
- Every REST endpoint now requires a valid key: `ApiKeyAuthenticationHandler` implements the `X-Api-Key` scheme, and `AuthorizationOptions.FallbackPolicy` requires an authenticated caller with no `[Authorize]`/`AllowAnonymous` attributes needed anywhere
- `GET /api/v1/status` is the first authenticated endpoint, returning the running version (same assembly-attribute source as `ledger_build_info`) and the caller's key name
- Forwarded headers (`X-Forwarded-For`/`X-Forwarded-Proto`) are trusted only from addresses listed in `ReverseProxy:KnownProxies`, with `ForwardLimit` 1

## Task Commits

Each task was committed atomically:

1. **Task 1: API key tokens, store, migration, CLI and LXC wrapper** - `ba33c35` (feat)
2. **Task 2: X-Api-Key authentication, fallback policy, status endpoint and forwarded-header trust** - `e2a2b64` (feat)

_Both tasks were `tdd="true"`; tests and implementation were authored and iterated together against the plan's declared `<behavior>` and landed in a single commit per task, matching the same pattern already used in plan 01-01 (tests all independently verified passing before each commit)._

## Files Created/Modified
- `Ledger.Domain/Auth/ApiKeyToken.cs` - token generation, strict parsing, fixed-time hash comparison
- `Ledger.Domain/Auth/IApiKeyStore.cs` - store interface, `CreatedApiKey`/`ApiKeySummary`/`ApiKeyIdentity` records, name-rule validator, `ApiKeyOperationException`
- `Ledger.Repository/Entities/ApiKeyEntity.cs` - EF Core entity for the `api_keys` table
- `Ledger.Repository/Stores/ApiKeyStore.cs` - `IApiKeyStore` implementation against `LedgerDbContext`
- `Ledger.Repository/LedgerDbContext.cs` - added `ApiKeys` DbSet and model configuration (unique `key_id` index, filtered unique `name` index)
- `Ledger.Repository/RepositoryServiceCollectionExtensions.cs` - registered `IApiKeyStore`
- `Ledger.Repository/Migrations/20260927192231_AddApiKeys.cs` (+ Designer/Snapshot) - creates `api_keys`
- `Ledger.Service/Cli/ApiKeyCommand.cs` - `apikey create|list|revoke` implementation
- `Ledger.Service/Auth/ApiKeyAuthenticationHandler.cs` - `X-Api-Key` authentication scheme handler
- `Ledger.Service/Endpoints/StatusEndpoints.cs` - `GET /api/v1/status`
- `Ledger.Service/Program.cs` - CLI dispatch before host build; forwarded headers, authentication/authorization, routing/endpoint pipeline wiring
- `Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs` - added `CapturingLoggerProvider`/`CapturedLogMessages` for log-secrecy assertions
- `deploy/bin/ledger-apikey` - root-only LXC wrapper invoking the CLI as the `ledger` service user
- `deploy/ledger.env.example` - `ReverseProxy__KnownProxies__0` placeholder, no password
- `docs/rest-api.md` - plain-language operator guide (create/list/revoke, sending the header, leaked-key procedure)
- `Ledger.UnitTests/Auth/ApiKeyTokenTests.cs` - 12 tests covering generation, strict parsing, fixed-time matching
- `Ledger.IntegrationTests/Auth/ApiKeyCommandTests.cs` - 3 tests (CLI lifecycle, invalid-name theory, store validation) against the real database
- `Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs` - 7 tests (endpoint enumeration, malformed headers, status endpoint, revocation, concurrency, ops-port isolation, log secrecy)
- `.gitignore` - added `!deploy/bin/` negation (see Deviations)

## Decisions Made
- Plain SHA-256 over a KDF for secret hashing (documented in the `///` summary on `ApiKeyToken.Generate`, per the plan's explicit instruction) — the secret is already a 256-bit CSPRNG value, so a slow KDF would only add per-request CPU cost on the low-power LXC host for no security benefit.
- `ApiKeyCommand`'s internal generic host clears default logging providers so EF Core's Information-level SQL command logging never pollutes the CLI's stdout contract (exactly one token line on `create`).
- `StatusEndpoints` duplicates the small `AssemblyInformationalVersionAttribute` parsing helper already used by `LedgerMetrics` rather than extracting a shared utility, since `Ledger.Service/Metrics/LedgerMetrics.cs` was outside this plan's declared `<files>` scope for both tasks.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `.gitignore`'s `bin/` build-output rule silently ignored `deploy/bin/`**
- **Found during:** Task 1, staging files for commit
- **Issue:** The repository's `.gitignore` excludes `bin/` anywhere in the tree (build output convention), which also matched the plan's required `deploy/bin/ledger-apikey` deployment script directory, making it impossible to track the file.
- **Fix:** Added a `!deploy/bin/` negation rule immediately after the build-output block.
- **Files modified:** `.gitignore`
- **Committed in:** `ba33c35` (Task 1 commit)

**2. [Rule 1 - Bug] EF Core command logging leaked into CLI stdout**
- **Found during:** Task 1, first `ApiKeyCommandTests` run
- **Issue:** `Host.CreateApplicationBuilder()`'s default Console logging provider, combined with the default Information log level, wrote every executed SQL command (including the freshly generated token, since it appears in the `INSERT` statement's logged parameter values in some configurations) to the swapped `Console.Out`, breaking the "prints exactly one token line" contract and observed directly as a failing assertion (multiple lines captured instead of one).
- **Fix:** Added `hostBuilder.Logging.ClearProviders()` in `ApiKeyCommand.RunAsync` before building the host.
- **Files modified:** `Ledger.Service/Cli/ApiKeyCommand.cs`
- **Committed in:** `ba33c35` (Task 1 commit)

**3. [Rule 3 - Blocking] `dotnet ef migrations add` output file had a UTF-8 BOM preceding the `// <auto-generated />` marker**
- **Found during:** Task 1, removing the auto-generated header per the established plan 01-01 pattern
- **Issue:** A plain `sed` line-anchor removal didn't match because the BOM bytes (`EF BB BF`) precede the comment text on line 1; a naive fix risked stripping the BOM entirely, diverging from the existing `InitialCreate` migration's byte layout.
- **Fix:** Used a small Python script operating on raw bytes to strip exactly the `// <auto-generated />\n` line while preserving the leading BOM, matching the existing migration files byte-for-byte in structure.
- **Files modified:** `Ledger.Repository/Migrations/20260927192231_AddApiKeys.Designer.cs`, `Ledger.Repository/Migrations/LedgerDbContextModelSnapshot.cs`
- **Committed in:** `ba33c35` (Task 1 commit)

---

**Total deviations:** 3 auto-fixed (1 blocking gitignore, 1 bug, 1 blocking tooling quirk)
**Impact on plan:** All three were necessary to satisfy the plan's own explicit acceptance criteria (a trackable `deploy/bin/ledger-apikey`, exactly one stdout token line, no `<auto` marker in `Migrations/`). No scope creep.

## Issues Encountered
- The migration bundle (`build/package-release.sh`) had to be rebuilt after adding the `AddApiKeys` migration so the `Category=Migrations` and `Category=DatabaseRoles` integration tests (which run against a packaged `efbundle`) would exercise the new table; this was expected per the orchestrator's instructions and is not committed (bundle output lives under the gitignored `artifacts/` directory).
- A C# switch-expression precedence quirk (`i % 3 switch { ... }` does not parse the way `(i % 3) switch { ... }` does) surfaced while writing the concurrent-request test; fixed with explicit parentheses before any test run, so it never affected a committed state.

## User Setup Required

None - no external service configuration required. `deploy/ledger.env.example` and `docs/rest-api.md` document the operator-facing `ledger-apikey` workflow for the LXC deployment covered by a later phase's provisioning work.

## Next Phase Readiness
- Every current and future REST endpoint in `Ledger.Service` inherits the `FallbackPolicy` automatically; no further wiring is needed as new endpoints are added.
- The API-key mechanism is explicitly designed to be replaceable by OIDC once the OAuth server for the public MCP endpoint exists (per the phase context); nothing here blocks that later migration.
- No blockers for the remaining phase 01 plans.

## Self-Check: PASSED

- All 15 created/modified files listed above confirmed present on disk
- Commit `ba33c35` (Task 1) — present in `git log`
- Commit `e2a2b64` (Task 2) — present in `git log`
- `dotnet build Ledger.slnx` exits 0; `dotnet test Ledger.UnitTests` 20/20; `dotnet test Ledger.IntegrationTests` 24/24 (Health, DataProtectionRestart, ApiKeyCli, DatabaseRoles, Migrations, ApiAuth), all re-verified immediately before writing this summary

---
*Phase: 01-secure-platform-release-pipeline*
*Completed: 2026-09-27*
