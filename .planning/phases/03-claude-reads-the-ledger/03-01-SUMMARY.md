---
phase: 03-claude-reads-the-ledger
plan: 01
subsystem: auth
tags: [openiddict, oauth, pkce, mcp, aspnet-identity, data-protection, postgres]

requires: []
provides:
  - Embedded OpenIddict 7.7.1 authorization server with two pre-registered public Claude clients, password sign-in and explicit consent
  - Stateless MCP endpoint at /mcp behind its own bearer-only policy, with Claude Code's discovery chain served from configuration
  - ledger_overview, the first read-only MCP tool, over LedgerQueryService and ILedgerQueryStore
  - Login and OAuth tables (migration AddOAuthAndLogins) on the existing DbContext under the snake-case convention
  - McpTestHost, OAuthTestDriver, ProxyEmulatingHandler and LedgerQuerySeed for in-process OAuth and MCP tests
  - Proven token behaviour: rotation, reuse revocation, retry within the leeway, short-lived access tokens, restart survival, deleted-login refusal
affects: [03-02, 03-03, 03-04, 03-05, 03-06, 03-07, 03-08]

tech-stack:
  added: [ModelContextProtocol.AspNetCore 2.2.0, OpenIddict.AspNetCore 7.7.1, OpenIddict.EntityFrameworkCore 7.7.1, Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.12]
  patterns:
    - "OAuth surface switched on only by OAuth:PublicBaseUrl; a LedgerOAuthSurface marker lets the Map methods skip when it is absent"
    - "Reference access and refresh tokens protected by the existing Data Protection key ring; ephemeral signing and encryption keys only satisfy OpenIddict startup validation"
    - "Thin MCP tool adapters over a scoped query service that reads through a Domain store interface"
    - "Tests reach the host through a proxy-emulating handler (https public address, forwarded headers, per-URI cookie jar)"

key-files:
  created:
    - Ledger.Service/OAuth/ (LedgerOAuthOptions, ClientRegistrations, ClientRegistrationSeeder, ResourceIndicator, OAuthServiceCollectionExtensions, TokenEndpoint, SignInPageHeadersMiddleware)
    - Ledger.Service/Mcp/ (McpEndpoint, LedgerTools, ServerInstructions, McpMetrics)
    - Ledger.Service/Pages/ (Account/Login, Connect/Authorize, Shared/_SignInLayout)
    - Ledger.Service/Queries/ (LedgerQueryService, OverviewResult)
    - Ledger.Repository/Entities/LedgerUserEntity.cs
    - Ledger.Repository/Stores/LedgerQueryStore.cs
    - Ledger.Repository/Migrations/ (AddOAuthAndLogins)
    - Ledger.Domain/Queries/ (ILedgerQueryStore, MoneyText)
    - Ledger.IntegrationTests/Infrastructure/ (McpTestHost, OAuthTestDriver, ProxyEmulatingHandler)
    - Ledger.IntegrationTests/Mcp/ (OAuthFlowTests, LedgerQuerySeed)
    - Ledger.UnitTests/Mcp/ (ResourceIndicatorTests, MoneyTextTests)
  modified:
    - Ledger.Repository/LedgerDbContext.cs
    - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
    - Ledger.Service/Program.cs
    - Ledger.Service/appsettings.json
    - Ledger.IntegrationTests/Database/DatabaseRoleTests.cs
    - every packages.lock.json

key-decisions:
  - "App-side canonical resource check kept (DisableResourceValidation plus IgnoreResourcePermissions in OpenIddict; Authorize page answers invalid_target for any non-canonical resource). Accepted by the operator at the tracer gate."
  - "Refresh token reuse leeway default stays 30 seconds; reuse revocations will be counted and alerted by a later plan"
  - "Data Protection token format with ephemeral OpenIddict keys is sufficient; no persistent signing or encryption certificates are needed"
  - "Reuse revocation kills every token of the grant but leaves the grant row, so the operator signs in again without any cleanup"

patterns-established:
  - "Test names describe behaviour in plain words and carry no planning references"
  - "OAuth configuration for tests lives in McpTestHost only"

requirements-completed: [ADV-10, SEC-04, ADV-01]

duration: tracer in an earlier session, rotation tests about 25min
completed: 2026-10-07
status: complete
actuals:
  tokens: 54000
  tasks: 2
  commits: 3
---

# Phase 3 Plan 1: OAuth server, MCP endpoint and ledger_overview Summary

**Embedded OpenIddict authorization server (PKCE, explicit consent, two pre-registered public Claude clients) in front of a stateless MCP endpoint serving ledger_overview, with refresh rotation, grant-wide reuse revocation and restart survival proven by tests on the real host and database.**

## Performance

- **Tasks:** 2 (tracer plus token behaviour tests)
- **Files changed:** 45 in the plan (about 6,300 added lines including lock files)
- **Actuals scale:** chars/4 over the added lines excluding lock files, about 54,000 tokens (estimate was 180,000)

## Accomplishments

- Claude Code's discovery chain runs in-process: unauthenticated POST /mcp answers 401 with a Bearer challenge pointing at the configured public metadata URL; the protected-resource document names the canonical resource and exactly one authorization server; the authorization-server metadata has a byte-identical issuer, S256, ledger.read and offline_access, and no registration endpoint.
- Password sign-in, explicit consent naming the client and redirect host, PKCE code exchange and an MCP SDK client calling ledger_overview all work; the result carries no IBAN or provider account name.
- Access tokens are audience-bound to exactly the canonical MCP resource; /mcp validates audience, token entry and grant entry on every request.
- Refresh behaviour is proven end to end (see below), including a restart on the same database.

## Task Commits

1. **Task 1 (tracer): discovery chain, sign-in with consent, PKCE exchange, ledger_overview** - `aeb58fc` (feat)
2. **Task 2: refresh rotation, reuse revocation, expiry, restart survival** - `0c9cade` (test)

## Token behaviour observed (Task 2)

- **Rotation:** a refresh returns a new access token and a new refresh token, both different from the old pair; the new access token calls ledger_overview.
- **Reuse with zero leeway:** presenting the first refresh token again after rotation answers `invalid_grant`; the second (current) refresh token is then also `invalid_grant`; both the first and the latest access token answer 401 at /mcp. The grant (authorization) row remains and stays in the valid status, so the operator signs in again without cleanup. Every token of the grant is revoked, as designed.
- **Retry within the default 30-second leeway:** the first refresh token presented again immediately after rotation succeeds and yields a second working token pair, so a retried refresh survives a lost response.
- **Parallel calls:** four concurrent tools/call requests over two MCP clients sharing one access token all succeed; the stateless server keeps no per-session state.
- **Short access token:** with `OAuth:AccessTokenLifetime` of two seconds, the token answers 401 at /mcp after three seconds and a refresh restores access.
- **Deleted login:** after removing the login through UserManager, a refresh answers `invalid_grant` from the token endpoint passthrough.
- **Restart (research assumption A1):** the Data Protection token format with ephemeral OpenIddict signing and encryption keys **survived the restart**. Host A issued tokens, was stopped and disposed, and host B started on the same database and key ring accepted A's access token at /mcp, refreshed with A's refresh token, and the new access token called ledger_overview. No persistent certificates are needed, so provisioning and secret custody are unchanged. (The test factory runs without a key-ring certificate; the certificate only encrypts the key ring at rest and does not affect whether tokens survive.)
- The leeway default stays 30 seconds. Reuse revocations will be counted and alerted by a later plan.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Unconfigured host answers /mcp and OAuth paths like any unmapped path**
- **Found during:** Task 1
- **Issue:** the plan expected 404 for POST /mcp without OAuth:PublicBaseUrl. The existing REST fallback policy answers every unmapped path with the ApiKey 401 challenge, so 404 was never what the host does.
- **Fix:** the test asserts the unconfigured host treats /mcp, /account/login, /connect/authorize, /connect/token and the discovery paths exactly like an unmapped address (same status, ApiKey challenge, no Bearer challenge). REST behaviour is unchanged.
- **Commit:** aeb58fc

**2. [Rule 3 - Blocking] OpenIddict 7.7.1 rejects unregistered resources (user-approved, accepted deviation)**
- **Found during:** Task 1
- **Issue:** OpenIddict's own resource validation accepts only resources registered as audiences or per-client permissions, and compares one exact string, but clients may send any canonical spelling of the MCP URL.
- **Fix:** `DisableResourceValidation` and `IgnoreResourcePermissions` on the server, with the Authorize page rejecting any non-canonical resource with `invalid_target` through ResourceIndicator and setting exactly the canonical resource on the issued tokens. The operator approved keeping this app-side check at the tracer gate.
- **Files modified:** Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs, Ledger.Service/Pages/Connect/Authorize.cshtml.cs
- **Commit:** aeb58fc

**3. [Rule 3 - Blocking] Test configuration overrides arrive after Program.cs reads configuration**
- **Found during:** Task 1
- **Issue:** WebApplicationFactory overrides apply after the service registration reads OAuth settings.
- **Fix:** McpTestHost also sets the OAuth settings as process environment variables for the duration of startup; AddLedgerOAuth registers a LedgerOAuthSurface marker so MapLedgerOAuth and MapLedgerMcp map only when the surface is registered.
- **Commit:** aeb58fc

**4. [Rule 1 - Bug] MoneyText rendered 1234.5000 for numeric(19,4)**
- **Found during:** Task 1
- **Fix:** trailing zeros beyond two decimals are trimmed, with unit tests.
- **Commit:** aeb58fc

### Additions beyond the plan

- SignInPageHeadersMiddleware: no-store, a CSP with a style nonce, frame-ancestors none and nosniff on /account/* and /connect/authorize; form-action allows the registered redirect hosts.
- ResourceIndicator unit tests and MoneyText unit tests (Ledger.UnitTests/Mcp).
- Task 2 also asserts the grant row survives a reuse revocation (the plan states it as design; the test now proves it).

**Total deviations:** 4 auto-fixed, 1 accepted architectural choice (resource validation), none from Task 2.

## Verification

- Category OAuth: 17 of 17 pass (10 from Task 1, 7 new)
- Category DatabaseRoles: 12 of 12 pass; Category ApiAuth: 7 of 7 pass
- Full solution `dotnet test --solution Ledger.slnx`: 681 tests, 0 failed, 679 succeeded, 2 skipped
- `build/lint.sh`: repo-rules, workflows, shell, secrets, script-tests and observability all pass
- Flake watch: `A_request_for_any_other_resource_is_refused_before_sign_in` failed once in a full run during Task 1 with a socket error (suspected free-port race in `LedgerWebApplicationFactory.GetFreeLoopbackPort`). It did not recur in this session: it passed in two OAuth category runs and one full-solution run. Still worth watching.
- Environment note: in this worktree `dotnet test --solution Ledger.slnx --no-restore` stops before running with "Ledger.UnitTests.csproj is using the VSTest runner"; the same command without `--no-restore` runs normally. Treated as a worktree restore quirk, not a code problem.

## Known Stubs

None. ServerInstructions is the first version by design and later plans extend it.

## Threat Flags

None beyond the plan's threat model. The new network surface (/connect/authorize, /connect/token, /account/login, /mcp, discovery documents) is exactly what the plan's trust boundaries describe.

## Self-Check: PASSED

- 0c9cade and aeb58fc found in git history
- Ledger.IntegrationTests/Mcp/OAuthFlowTests.cs, Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs and the AddOAuthAndLogins migration present
