---
phase: 03-claude-reads-the-ledger
plan: 02
subsystem: auth
tags: [totp, replay-guard, csp, antiforgery, rate-limiting, host-guard, cidr, production-validator, log-redaction]

requires:
  - phase: 03-claude-reads-the-ledger
    provides: OpenIddict authorization server, sign-in and consent pages, MCP endpoint, McpTestHost and OAuthTestDriver (plan 01)
provides:
  - Second-factor sign-in (password then authenticator code) with an atomic one-time-code replay guard and lockout
  - Hardened sign-in and consent pages (antiforgery, no-store, strict CSP with client-specific form-action, local redirects only)
  - PublicHostGuard: host and path allow-list on the MCP host, MCP surface hidden on every other host, sign-in limited to OAuth:SignInNetworks
  - RequestLimits: per-address fixed-window rate limits before authentication and a 256 KiB body limit answered with 413
  - Production configuration rules for the public address, sign-in networks and token lifetimes
  - Boundary tests: host, path, network, audience, scheme separation, hosted client, unregistered redirect, outbound calls, rate and size limits, log redaction
affects: [03-03, 03-04, 03-05, 03-06, 03-07, 03-08]

tech-stack:
  added: []
  patterns:
    - "Defence in depth for the public hostname: the app repeats the proxy's allow-list and keeps sign-in to home and VPN ranges by remote address"
    - "Sign-in networks fail closed: an empty list means nobody can sign in"
    - "Per-address, per-request-class fixed-window limiter placed before authentication"
    - "Test database pools release idle connections after seconds so a full run stays under the server's connection limit"

key-files:
  created:
    - Ledger.Service/Hosting/PublicHostGuard.cs
    - Ledger.Service/Hosting/RequestLimits.cs
    - Ledger.Service/OAuth/SignInNetworks.cs
    - Ledger.IntegrationTests/Mcp/EndpointBoundaryTests.cs
    - Ledger.UnitTests/Mcp/ResourceAndNetworkTests.cs
  modified:
    - Ledger.Service/OAuth/LedgerOAuthOptions.cs
    - Ledger.Service/Hosting/ProductionConfigurationValidator.cs
    - Ledger.Service/Program.cs
    - deploy/ledger.env.example
    - Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs
    - Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs
    - Ledger.IntegrationTests/Infrastructure/McpTestHost.cs
    - Ledger.IntegrationTests/Infrastructure/OAuthTestDriver.cs
    - Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs
    - Ledger.IntegrationTests/Security/LogRedactionTests.cs

key-decisions:
  - "A request body refused by Kestrel's size limit is answered 413 through an IExceptionHandler; without it the exception handler turned the refusal into a 500"
  - "Sign-in networks are an allow-list that fails closed; the validator also refuses ranges that overlap Anthropic's IPv6 outbound range, not only the IPv4 /21"
  - "Sign-in rate limit counts POSTs only (login, code, consent), so loading a page never spends the budget"
  - "Test database pools use a 5 second idle lifetime so the shared local server does not run out of connections"

patterns-established:
  - "McpTestHost always sets the home network that the default proxied client address belongs to; tests that need another address pass it to CreateBrowser"
  - "TestLogin records every one-time code it hands out so redaction tests can assert none was logged"

requirements-completed: [SEC-04, ADV-10]

duration: Task 1 in an earlier session; Task 2 and verification about 1h45m
completed: 2026-10-07
status: complete
actuals:
  tokens: 24500
  tasks: 2
  commits: 3
---

# Phase 3 Plan 2: Second factor and app-enforced public boundary Summary

**Password plus authenticator code sign-in with an atomic replay guard, strict sign-in page headers, and an in-app host/path allow-list, home-and-VPN-only sign-in, rate and size limits and fail-fast production configuration, all proven by tests on the real host and database.**

## Performance

- **Tasks:** 2 (tracer plus boundary enforcement)
- **Commits:** 51e7966 (Task 1), ae5d589 (Task 2), 771423d (test infrastructure fix)
- **Actuals scale:** chars/4 over added lines excluding lock files, about 24,500 tokens (estimate was 150,000)

## Accomplishments

### Task 1 (tracer, 51e7966, completed in an earlier session and approved at the gate)

- Login page is step one: an unknown login or a login without a confirmed second factor fails with the generic "Sign-in failed." (dummy password hash for timing parity). A correct password goes through PasswordSignInAsync with lockout and on to /account/totp with only a local return address.
- The code page reads the pending login from the two-factor cookie, refuses a locked-out login without counting another failure, verifies with the authenticator provider, then claims the code through ITotpReplayStore.TryClaimAsync (SHA-256 of the code, five-minute window, one conditional ExecuteUpdateAsync on the login row, true only when exactly one row changed). A wrong or replayed code calls AccessFailedAsync and shows the same message. Success resets the failed count, signs in with amr mfa (not persistent), clears the two-factor cookie and redirects locally.
- The consent page's CSP form-action is 'self' plus the origins of the requesting client's registered redirect addresses read from the OpenIddict application (hosted: https://claude.ai https://claude.com; Claude Code: http://localhost:* http://127.0.0.1:*).
- Test support: TotpCode (RFC 6238), McpTestHost.CreateLoginAsync enrols an authenticator by default, TestLogin.NextCode hands out a code from a fresh time step, OAuthTestDriver submits password then code.
- Evidence at the time: Category=OAuth 26 of 26 (17 earlier plus 9 new SignInTests).

### Task 2 (ae5d589)

- **PublicHostGuard** (right after UseForwardedHeaders, before rate limiting, routing and authentication): on the host equal to the public base address (case-insensitive, port ignored) only /mcp, /.well-known/oauth-protected-resource/mcp, /.well-known/oauth-authorization-server, /.well-known/openid-configuration, /connect/token, /connect/authorize and the /account prefix answer; everything else is 404. On any other host those same paths answer 404. /connect/authorize and /account additionally answer 404 unless the remote address (as rewritten from X-Forwarded-For only for the trusted proxy) lies inside OAuth:SignInNetworks. No forwarded host header is used. The loopback ops port is unaffected.
- **SignInNetworks**: CIDR parsing and membership for IPv4 and IPv6, inclusive at both ends, IPv4-mapped IPv6 addresses mapped first, plus an overlap test used by the validator.
- **RequestLimits**: fixed-window limiter keyed by client address and request class (sign-in POSTs 10 per minute, /connect/token 30, /mcp and discovery and sign-in page loads 300, everything else unlimited), 429 on refusal, placed before authentication. Kestrel body limit 262144 bytes for every endpoint. No buffering added on /mcp.
- **ProductionConfigurationValidator**: with OAuth:PublicBaseUrl present, names OAuth:PublicBaseUrl for a non-https address, a path, query, fragment, trailing slash, user info or an example.com/.org/.net host; names OAuth:SignInNetworks for an empty list, an invalid range, any /0, or any overlap with 160.79.104.0/21 or Anthropic's IPv6 range; names a lifetime key outside 1 to 60 minutes, 1 to 180 days or 0 to 120 seconds. Only key names appear in the exception. Nothing is required while the public address is absent.
- **deploy/ledger.env.example** gained OAuth__PublicBaseUrl and two OAuth__SignInNetworks placeholders with an explanation; the committed-placeholder test still passes.
- **Tests**: ResourceAndNetworkTests (unit), validator and committed-configuration additions (log-level pin for OpenIddict, ModelContextProtocol and Microsoft.AspNetCore.Identity), EndpointBoundaryTests (42 cases), LogRedactionTests extension (full flow with a wrong password, a wrong code, refresh and a tool call; password, codes, authorization code, verifier and both token pairs checked in plain and URL-encoded form).

## Task Commits

1. **Task 1 (tracer): second factor sign-in with replay guard and hardened pages** - `51e7966` (feat)
2. **Task 2: app-enforced public boundary** - `ae5d589` (feat)
3. **Test infrastructure: release idle database connections after seconds** - `771423d` (test)

## Deviations from Plan

### Task 1 (carried from the earlier session)

1. **[Rule 3 - Blocking]** A remembered-device cookie scheme (`__Host-ledger-2fa-remember`) is registered because PasswordSignInAsync authenticates it on every call. Nothing ever issues it, so every sign-in asks for a code.
2. No separate /account/site.css: the pages keep the per-response style nonce and inline style, which the CSP allows without any external resource.
3. SignInPageHeadersMiddleware.cs was renamed to SignInPageHeaders.cs and extended instead of adding a second type.
4. The antiforgery cookie is named `__Host-ledger-af` (the tracer had used `__Host-ledger-csrf`), matching the plan's artifact list.
5. Tests for Task 1 were written alongside the implementation; there is no recorded red run.

### Task 2 auto-fixed issues

**1. [Rule 1 - Bug] A request body above the limit answered 500**
- **Found during:** Task 2, the 300 KiB body test against /mcp with a valid token
- **Issue:** Kestrel throws a BadHttpRequestException while the MCP handler reads the body; the exception handler turned it into 500 and logged it as an unhandled error.
- **Fix:** RequestRejectionHandler (an IExceptionHandler in RequestLimits.cs) answers any request the server itself refused with the status the server chose (413 here) and a problem document that says nothing more.
- **Files modified:** Ledger.Service/Hosting/RequestLimits.cs
- **Commit:** ae5d589

**2. [Rule 3 - Blocking] The full test run exhausted the shared local database server's connections**
- **Found during:** full-solution verification. Three runs failed with "sorry, too many clients already" in the data-protection certificate tests, always late in the run.
- **Diagnosis:** sampling the server showed the baseline commit (without this task) already peaks at about 104 connections against a limit of 100 and passed by luck; this task's extra tests raised the plateau from about 40 to about 57 and tipped it over. Every throwaway database and role has its own Npgsql pool, and idle connections live five minutes by default.
- **Fix:** DatabaseFixture builds its connection strings with a 5 second idle lifetime and 2 second pruning interval. The peak during a full run is now 56 connections.
- **Files modified:** Ledger.IntegrationTests/Infrastructure/DatabaseFixture.cs (not in the plan's file list)
- **Commit:** 771423d

### Adjustments inside the plan's intent

- The outbound-call test reads the HTTP activity's `server.port` and `url.full` instead of `server.address`, because the activity reports the Host header the proxy emulation sets (mcp.example.com) rather than the loopback address it connects to. Every recorded request must end on the host's own API port; the listener is process-wide and test collections run one at a time, so no other traffic can appear.
- The test for a resource carrying a fragment accepts either OpenIddict's own 400 error page or the app's invalid_target redirect, because OpenIddict rejects a fragment before the app's check runs. Either way the request is refused and never reaches sign-in.
- The sign-in rate limit counts only POSTs to /account and /connect/authorize; page loads share the 300 per minute class. This keeps a normal flow (three POSTs) far from the limit while the eleventh form post from one address is refused.
- The validator also refuses overlap with Anthropic's IPv6 outbound range (2607:6bc0::/48), not only the IPv4 /21 named in the plan.

**Total deviations:** 2 auto-fixed in Task 2 (one bug, one blocking test-infrastructure issue), 5 carried from Task 1, 4 small adjustments.

## Verification

- Unit Category=OAuth 48/48, Category=Configuration 109/109
- Integration Category=OAuth 68/68, Category=LogRedaction 9/9, Category=ApiAuth 7/7
- Full solution `dotnet test --solution Ledger.slnx` (without --no-restore, as in plan 01's environment note): 872 tests, 0 failed, 870 succeeded, 2 skipped. Peak server connections during the run: 56.
- `build/lint.sh`: repo-rules, workflows, shell, secrets, script-tests and observability all pass
- Acceptance greps: `UsePublicHostGuard` at Program.cs line 105 before `UseRouting` at line 124; `262144`, `OAuth__PublicBaseUrl=https://mcp.example.com` and `160.79.104.0/21` present once or more; no `X-Forwarded-Host` in the service code
- The sibling plan's tracer is in the same tree; its tests pass in the full run.

## Behaviour proven by the boundary tests

- /api/v1/status, /metrics, /health, / and /api/v1/bank/connections answer 404 on the MCP host; /mcp, /connect/token, /connect/authorize, /account/login and all discovery paths answer 404 on the loopback host and on any other host name.
- Sign-in and consent answer 404 for 203.0.113.7, 160.79.104.10, 192.0.1.255, 192.0.3.0 and an IPv6 address outside the range, and answer for 192.0.2.0, 192.0.2.50 and 192.0.2.255. Discovery, token and /mcp stay reachable from any address. A client address the caller prepends to the forwarded chain is ignored.
- A resource that differs from the MCP address by host case, default port or one trailing slash is accepted; /mcp2, /mcp/x, another path, a foreign host, http and a fragment are refused; with several resource values each must be canonical. The protected-resource metadata lists exactly one authorization server equal to the issuer.
- A token issued by a second instance on the same database for https://other.example.com is rejected at the first instance's /mcp with 401 (and accepted where it was issued).
- An API key on /mcp answers 401 with the Bearer challenge; a bearer token on /api/v1/status answers 401 with the ApiKey challenge; every REST route on the OAuth-enabled host (except the anonymous bank callback) still answers 401 without a key.
- The hosted client receives its code at https://claude.ai/api/mcp/auth_callback and exchanges it; four unregistered redirect addresses (foreign host, other path, look-alike host, http) get a 400 page and never a redirect.
- Three tool calls produce only requests to the host's own loopback API port: nothing leaves the app.
- The eleventh sign-in POST in a minute from one address answers 429 while another address is unaffected; the 31st token request and the 301st /mcp request answer 429; a 300 KiB body to /mcp answers 413.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model.

## Deferred

- Manual check at the end of the phase: open the login and consent pages from a desktop and a phone browser on the home network after the LAN go-live; confirm legibility, that the consent page names the client and redirect host, and that the console shows no blocked script or style.
- `/.well-known/jwks` is not on the allow-list, so it answers 404 on the MCP host even though the authorization-server metadata may advertise it; access and refresh tokens are reference tokens validated server-side, so no client needs it. Revisit if a client ever asks for it.

## Self-Check: PASSED

- 51e7966, ae5d589 and 771423d found in git history
- Ledger.Service/Hosting/PublicHostGuard.cs, Ledger.Service/Hosting/RequestLimits.cs, Ledger.Service/OAuth/SignInNetworks.cs, Ledger.IntegrationTests/Mcp/EndpointBoundaryTests.cs and Ledger.UnitTests/Mcp/ResourceAndNetworkTests.cs present
