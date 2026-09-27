---
phase: 01-secure-platform-release-pipeline
plan: 07
subsystem: security
tags: [data-protection, x509, aspnet-core, npgsql, problem-details, log-redaction]

requires:
  - phase: 01-secure-platform-release-pipeline
    provides: LedgerDbContext/PersistKeysToLedgerDatabase, DataProtectionCanaryHealthCheck, LedgerWebApplicationFactory, DatabaseFixture (plan 01-01); authentication pipeline, log-capturing provider pattern, deploy/ledger.env.example (plan 01-06)
provides:
  - LedgerConnectionStringRules.Problems(connectionString) — socket-only, passwordless, ledger_runtime connection-string rule checks returning rule names, never values
  - DataProtectionSetup.AddLedgerDataProtection — certificate-protected, database-persisted key ring (ProtectKeysWithCertificate + UnprotectKeysWithAnyCertificate), certificate required in Production
  - ProductionConfigurationValidator.ThrowIfInvalid — one exception naming every offending Production configuration key, never a value
  - Production logging (systemd console formatter, quieted hosting-diagnostics/EF-command categories) and non-Development problem-details exception handling
  - Test infrastructure: TestCertificates (self-signed PFX generator), LedgerWebApplicationFactory certificate/test-service hooks
affects: [go-live plan (real certificate provisioning and restart/redeploy verification on the LXC), any future plan adding Production configuration checks or secrets]

actuals:
  tokens: 10917
  tasks: 2
  commits: 2

tech-stack:
  added: []
  patterns:
    - "Data Protection key-ring certificate protection: X509CertificateLoader.LoadPkcs12FromFile + ProtectKeysWithCertificate + UnprotectKeysWithAnyCertificate (the second call is what lets a restart on Linux decrypt the ring, since no certificate store resolves the decryptor otherwise)"
    - "Fail-fast Production validation collects every problem into one InvalidOperationException naming only configuration keys, never values"
    - "Sentinel-based log/response/metrics redaction tests: a random marker plus its URL-encoded and base64(url-safe) forms, injected via header/query/path, asserted absent everywhere observable"
    - "Test-only unhandled-exception endpoints must be added via an IStartupFilter middleware branch (app.Map), not IEndpointRouteBuilder.MapGet — ASP.NET Core applies IStartupFilter.Configure against a plain ApplicationBuilder, not the WebApplication instance, so it does not implement IEndpointRouteBuilder"

key-files:
  created:
    - Ledger.Repository/LedgerConnectionStringRules.cs
    - Ledger.Service/Security/DataProtectionSetup.cs
    - Ledger.Service/Hosting/ProductionConfigurationValidator.cs
    - Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs
    - Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs
    - Ledger.IntegrationTests/Infrastructure/TestCertificates.cs
    - Ledger.IntegrationTests/Security/DataProtectionCertificateTests.cs
    - Ledger.IntegrationTests/Security/LogRedactionTests.cs
  modified:
    - Ledger.Service/Program.cs
    - Ledger.Service/appsettings.json
    - Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs
    - deploy/ledger.env.example

key-decisions:
  - "Certificate path/password must reach the process as environment variables, set inside LedgerWebApplicationFactory's constructor before the host builds, rather than through the same in-memory ConfigureAppConfiguration collection used for the connection string. AddLedgerDataProtection loads the certificate eagerly (ProtectKeysWithCertificate needs an already-materialized X509Certificate2, which cannot be deferred behind IOptions without burying startup failures inside a background service's retry loop), and WebApplicationFactory's ConfigureAppConfiguration overrides only merge into IConfiguration at Build() time — too late for an eager pre-Build read. Environment variables are visible from WebApplicationBuilder.CreateBuilder(args)'s first line, before Program.cs's eager reads, and match the DataProtection__CertificatePath/DataProtection__CertificatePassword naming already used in deploy/ledger.env.example."
  - "ProductionConfigurationValidator and DataProtectionSetup both check 'certificate required in Production' independently: the validator is the fast, synchronous, pre-Build gate for a real Production run; DataProtectionSetup's own check keeps the same guarantee testable in isolation and defends the invariant even if the validator call site ever moves."
  - "LedgerConnectionStringRules.Problems returns human-readable rule descriptions (used for diagnostics) but ProductionConfigurationValidator folds any non-empty problem list into a single ConnectionStrings:Ledger key, so the exception always names configuration keys, never connection-string values or embedded passwords."

patterns-established:
  - "Every future secret-bearing configuration value follows the same rule-check shape: a Problems()-style pure function returning rule names, folded into ProductionConfigurationValidator by key name only."

requirements-completed: [SEC-03, SEC-05, SEC-06, OPS-05]

coverage:
  - id: D1
    description: "The Data Protection key ring is encrypted at rest with a PFX certificate from configuration; key XML rows contain encryptedSecret and no plaintext masterKey value"
    requirement: "SEC-03"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Security/DataProtectionCertificateTests.cs#Key_rows_are_encrypted_with_the_certificate_and_never_store_a_plaintext_master_key"
        status: pass
    human_judgment: false
  - id: D2
    description: "The key ring survives a restart and a redeploy (different content root, same certificate); a wrong certificate reports Unhealthy without healing or rewriting the canary"
    requirement: "OPS-05"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Security/DataProtectionCertificateTests.cs#Restart_with_a_different_content_root_decrypts_with_the_same_certificate"
        status: pass
      - kind: integration
        ref: "Ledger.IntegrationTests/Security/DataProtectionCertificateTests.cs#Wrong_certificate_reports_unhealthy_and_leaves_the_canary_row_unchanged"
        status: pass
    human_judgment: false
  - id: D3
    description: "An unreadable certificate path or wrong password fails startup with an exception naming only the offending configuration key, never the password"
    requirement: "SEC-03"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Security/DataProtectionCertificateTests.cs#Unreadable_certificate_path_fails_startup_naming_only_the_path_key"
        status: pass
      - kind: integration
        ref: "Ledger.IntegrationTests/Security/DataProtectionCertificateTests.cs#Wrong_certificate_password_fails_startup_naming_only_the_password_key"
        status: pass
    human_judgment: false
  - id: D4
    description: "Production startup fails before serving with one exception naming every offending key (missing/invalid certificate path or password, unsafe connection string) and no values"
    requirement: "SEC-05"
    verification:
      - kind: unit
        ref: "Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs (9 tests: valid pass, each single problem fails, three problems name exactly three keys)"
        status: pass
    human_judgment: false
  - id: D5
    description: "Sentinel secrets sent raw, URL-encoded and base64-encoded via header/query/path, plus a bad connection-string password and a bad certificate password, never appear in logs, responses or /metrics; unhandled exceptions return redacted problem+json"
    requirement: "SEC-06"
    verification:
      - kind: integration
        ref: "Ledger.IntegrationTests/Security/LogRedactionTests.cs (6 tests covering header/query/path sentinels, DB password, certificate password, thrown-exception redaction, empty header, EF sensitive-data logging disabled)"
        status: pass
    human_judgment: false
  - id: D6
    description: "Committed appsettings*.json parse and carry no non-empty password/secret/token/apikey value"
    requirement: "SEC-06"
    verification:
      - kind: unit
        ref: "Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs#Every_committed_appsettings_file_parses_and_has_no_non_empty_secret_values"
        status: pass
    human_judgment: false

duration: ~1h 10min
completed: 2026-09-27
status: complete
---

# Phase 01 Plan 07: Certificate-Protected Key Ring, Fail-Fast Config and Log Redaction Summary

**Data Protection key ring encrypted at rest with a PFX certificate (survives restart and redeploy, reports Unhealthy on a wrong certificate rather than healing), Production startup that refuses to serve on unsafe configuration, and sentinel-proven redaction of secrets from logs, error responses and metrics.**

## Performance

- **Duration:** ~1h 10min
- **Completed:** 2026-09-27
- **Tasks:** 2
- **Files modified:** 12 (8 created, 4 modified)

## Accomplishments

- `LedgerConnectionStringRules.Problems` parses a connection string with `NpgsqlConnectionStringBuilder` and flags a non-socket host, any password, a non-`ledger_runtime` username, or `Include Error Detail`, returning rule names only
- `DataProtectionSetup.AddLedgerDataProtection` loads the configured PFX with `X509CertificateLoader.LoadPkcs12FromFile` and wires `ProtectKeysWithCertificate` + `UnprotectKeysWithAnyCertificate`, so the key ring both encrypts at rest and decrypts after a restart on Linux; a certificate is required whenever the environment is Production
- `ProductionConfigurationValidator.ThrowIfInvalid` collects every problem (missing/unreadable certificate path, empty password, unsafe connection string) into one `InvalidOperationException` naming only the offending keys, called from `Program.cs` before the host is built in Production
- Production logging clears default providers and adds the systemd console formatter; `Microsoft.AspNetCore.Hosting.Diagnostics` and `Microsoft.EntityFrameworkCore.Database.Command` are quieted to `Warning` so request URLs and SQL parameters never log at `Information`
- `AddProblemDetails()` plus `UseExceptionHandler()` outside Development return problem+json without exception details; the developer exception page stays Development-only
- `LogRedactionTests` proves a sentinel sent raw/URL-encoded/base64 via header, query string and path, a bad connection-string password and a bad certificate password, never reach captured logs, response bodies or `/metrics`; a test-only always-throwing endpoint returns a redacted 500 with no stack trace or exception type name
- `CommittedConfigurationTests` proves every `appsettings*.json` in `Ledger.Service` parses and carries no non-empty `password`/`secret`/`token`/`apikey` value

## Task Commits

Each task was committed atomically:

1. **Task 1: Certificate-protected key ring and fail-fast production configuration** - `bd6ec5e` (feat, tdd)
2. **Task 2: Keep secrets out of logs, errors and metrics, proven with sentinels** - `5b87818` (feat, tdd)

_Both tasks were `tdd="true"`; tests and implementation were authored and iterated together against the plan's declared `<behavior>` and landed in a single commit per task, matching the precedent already recorded in this phase's `01-01-SUMMARY.md`/`01-09-SUMMARY.md` (tests independently verified passing before each commit)._

## Files Created/Modified

- `Ledger.Repository/LedgerConnectionStringRules.cs` - socket-only, passwordless, `ledger_runtime` connection-string rule checks
- `Ledger.Service/Security/DataProtectionSetup.cs` - certificate-protected key-ring wiring, required in Production
- `Ledger.Service/Hosting/ProductionConfigurationValidator.cs` - one-exception, keys-only Production configuration gate
- `Ledger.Service/Program.cs` - validator call site, `AddLedgerDataProtection` wiring, Production logging providers, `AddProblemDetails`/`UseExceptionHandler`/developer exception page
- `Ledger.Service/appsettings.json` - quieted `Microsoft.AspNetCore.Hosting.Diagnostics` and EF Core command logging
- `Ledger.IntegrationTests/Infrastructure/TestCertificates.cs` - self-signed RSA PFX generator for tests
- `Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs` - certificate environment-variable hook, `ConfigureTestServices` hook for the log-redaction throwing endpoint
- `Ledger.IntegrationTests/Security/DataProtectionCertificateTests.cs` - encryption, restart/redeploy, wrong-certificate and bad-configuration behavior
- `Ledger.IntegrationTests/Security/LogRedactionTests.cs` - sentinel redaction across headers/query/path/config, exception redaction, EF sensitive-data-logging assertion
- `Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs` - validator behavior over configuration objects
- `Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs` - committed-configuration secret scan
- `deploy/ledger.env.example` - `DataProtection__CertificatePath`/`DataProtection__CertificatePassword` placeholders

## Decisions Made

- Certificate path/password reach the test host through process environment variables set in `LedgerWebApplicationFactory`'s constructor, not through the same `ConfigureAppConfiguration` in-memory collection used for the connection string — `AddLedgerDataProtection` must load the certificate eagerly (before `builder.Build()`), and `WebApplicationFactory`'s configuration overrides only merge in at `Build()` time, mirroring the same timing pitfall this phase's `01-01-SUMMARY.md` already documented for `OpsEndpoint`.
- `ProductionConfigurationValidator` and `DataProtectionSetup` each independently enforce "certificate required in Production": the validator is the fast pre-Build gate for a real Production run; `DataProtectionSetup`'s own check keeps the guarantee testable in isolation.
- `LedgerConnectionStringRules.Problems` returns rule descriptions for diagnostics, but the validator folds any non-empty problem list into a single `ConnectionStrings:Ledger` key so the exception never echoes connection-string values.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Certificate configuration invisible to `WebApplicationFactory` test overrides**
- **Found during:** Task 1, first `DataProtectionCertificateTests` run
- **Issue:** `DataProtectionSetup.AddLedgerDataProtection` reads the certificate path/password synchronously during service registration (required, since `ProtectKeysWithCertificate` needs an already-loaded `X509Certificate2`), but `LedgerWebApplicationFactory`'s `ConfigureAppConfiguration` overrides only merge into `IConfiguration` at `Build()` time — an eager pre-Build read sees appsettings defaults, not the test's certificate. All four certificate tests failed: keys were written unprotected, and the "unreadable path"/"wrong password" tests never threw.
- **Fix:** `LedgerWebApplicationFactory` now sets `DataProtection__CertificatePath`/`DataProtection__CertificatePassword` as process environment variables in its constructor (visible from `WebApplicationBuilder.CreateBuilder(args)`'s first line, before Program.cs's eager read), clearing them when no certificate is configured for that instance.
- **Files modified:** `Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs`
- **Verification:** All 11 `DataProtection`/`DataProtectionRestart`/`Health` tests pass.
- **Committed in:** `bd6ec5e` (Task 1 commit)

**2. [Rule 3 - Blocking] Test-only throwing endpoint never reached via `IEndpointRouteBuilder`**
- **Found during:** Task 2, first `LogRedactionTests` run
- **Issue:** The initial test-only `IStartupFilter` cast its `app` parameter to `IEndpointRouteBuilder` to `MapGet` a throwing route. ASP.NET Core applies `IStartupFilter.Configure` against a plain `ApplicationBuilder`, not the `WebApplication` instance itself, so the cast always failed and the route was never registered (404).
- **Fix:** The filter now adds a middleware branch via `app.Map(path, branch => branch.Run(...))`, which works against the plain `IApplicationBuilder` every `IStartupFilter` actually receives.
- **Files modified:** `Ledger.IntegrationTests/Security/LogRedactionTests.cs`
- **Verification:** `Unhandled_exception_returns_problem_json_without_sentinel_stack_trace_or_exception_type` passes.
- **Committed in:** `5b87818` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 3 - blocking, both test-infrastructure timing/API-shape issues, no production-code behavior changes)
**Impact on plan:** Both fixes were necessary for the plan's own declared tests to exercise the real behavior; no scope creep.

## Issues Encountered

None beyond the two auto-fixed deviations above.

## User Setup Required

None - no external service configuration required. The go-live plan provisions the real Data Protection certificate on the LXC per `deploy/ledger.env.example`.

## Next Phase Readiness

- The certificate-protected, restart/redeploy-safe key ring is ready for the bank-sync phase to encrypt consent tokens with the same mechanism.
- `ProductionConfigurationValidator` and `LedgerConnectionStringRules` establish the pattern any future secret-bearing configuration value should follow.
- No blockers for the remaining phase 01 plans.

## Self-Check: PASSED

- All 12 created/modified files confirmed present on disk
- Commit `bd6ec5e` (Task 1) — present in `git log`
- Commit `5b87818` (Task 2) — present in `git log`
- `dotnet build Ledger.slnx` exits 0; `dotnet test Ledger.UnitTests` 30/30; `LEDGER_EFBUNDLE=... dotnet test Ledger.slnx` 65/65 (30 unit + 35 integration), all re-verified immediately before writing this summary
- `build/lint.sh` — PASS on all five checks (repo-rules, workflows, shell, secrets, script-tests)

---
*Phase: 01-secure-platform-release-pipeline*
*Completed: 2026-09-27*
