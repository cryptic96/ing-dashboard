---
phase: 03-claude-reads-the-ledger
plan: 05
subsystem: oauth-operations
tags: [openiddict, revocation, cli, metrics, alerting, prometheus, grafana, systemd-run]

requires:
  - phase: 03-02
    provides: AddLedgerOAuth, OpenIddict server and validation, Authorize page, McpTestHost, OAuthTestDriver, LedgerUserEntity login stores
  - phase: 03-01
    provides: McpMetrics with ledger_mcp_tool_calls_total, LedgerTools, LedgerQuerySeed
provides:
  - ledger-login and ledger-grants host commands (sandboxed wrappers plus LoginCommand and GrantsCommand) to enrol, re-key, re-password and remove logins and to revoke one or every Claude connection
  - GrantRevocationService (ListAsync, RevokeAsync, RevokeAllAsync, RevokeForLoginAsync, CountActiveGrantsAsync) and AddLedgerOAuthCore shared by the web host and the command hosts
  - McpMetrics counters ledger_mcp_rejected_tokens_total{reason}, ledger_oauth_grants_created_total and ledger_oauth_refresh_token_reuse_total, all present at zero from startup
  - TokenRejectionReasons (Classify, ConfirmExpiry, IsRefreshTokenReuse) pinned to OpenIddict 7.7.1 identifiers
  - Three Access-folder alerts through the existing contact point, with lint and promtool expression tests
  - Claude access section in docs/monitoring.md
affects: [03-06, 03-07, 03-08]

tech-stack:
  added: []
  patterns:
    - "Operator commands dispatched in Program.cs before the web host is built, like apikey; each runs through a root-only wrapper that uses systemd-run with the full hardening list as the ledger user"
    - "Passwords reach the app only on stdin (piped or from a silent read -r -s prompt), never on an argument list"
    - "Rejected-token counting happens in a thin authentication scheme in front of OpenIddict validation, because OpenIddict stops its own handlers at the first rejection"
    - "Alert expression tests generate a Prometheus rule file from the Grafana rule file so the evaluated expression and threshold are the shipped ones, with mutation checks proving the suite can fail"

key-files:
  created:
    - Ledger.Service/OAuth/GrantRevocationService.cs
    - Ledger.Service/OAuth/TokenRejectionReasons.cs
    - Ledger.Service/OAuth/OAuthEventHandlers.cs
    - Ledger.Service/Cli/LoginCommand.cs
    - Ledger.Service/Cli/GrantsCommand.cs
    - Ledger.Service/Cli/OperatorHost.cs
    - deploy/bin/ledger-login
    - deploy/bin/ledger-grants
    - deploy/tests/oauth-commands-logic-test.sh
    - deploy/provisioning/grafana/provisioning/alerting/access-rules.yaml
    - build/tests/access-alert-expressions-test.sh
    - Ledger.UnitTests/Mcp/McpMetricsTests.cs
    - Ledger.IntegrationTests/Mcp/OperatorCommandTests.cs
    - Ledger.IntegrationTests/Mcp/TokenRejectionTests.cs
  modified:
    - Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs
    - Ledger.Service/Pages/Connect/Authorize.cshtml.cs
    - Ledger.Service/Mcp/McpMetrics.cs
    - Ledger.Service/Mcp/McpEndpoint.cs
    - Ledger.Service/Program.cs
    - deploy/tests/sandboxing-test.sh
    - build/lint/checks/60-observability.sh
    - docs/monitoring.md
    - Ledger.IntegrationTests/Infrastructure/OAuthTestDriver.cs

key-decisions:
  - "Token rejections are counted in a small authentication scheme (ledger-mcp-token) that the MCP scheme forwards to, which authenticates through OpenIddict and counts the failure. A validation event handler cannot do this: OpenIddict's dispatcher stops at the first rejecting handler, so a handler placed after its validation never runs for a rejected token (measured: it ran only for accepted tokens)."
  - "OpenIddict 7.7.1 reports an expired token and a revoked token entry with the same identifier (ID2019, 'no longer valid'), so Classify maps ID2019 to expired and ConfirmExpiry settles it against the stored token entry's expiration; a revoked or unknown token ends as invalid. The lookup runs only on the rejection path."
  - "Identifiers measured against the running host: ID2094 (no registered audience) gives wrong_audience, ID2004 gives invalid, ID2019 expired or revoked, and a reused refresh token is ID2012 on invalid_grant (the plan guessed ID2018)."
  - "Refresh reuse is counted from the token response (ApplyTokenResponseContext, invalid_grant plus ID2012), which fires only for the redeemed-refresh-token rejection and not for other invalid_grant causes such as a deleted login or a redeemed authorization code."
  - "The Authorize approve handler logs the client id and grant id only, both of which are registered or generated values."

patterns-established:
  - "AddLedgerOAuthCore is the shared registration for anything that touches logins or grants outside the web host"
  - "Command hosts use an ephemeral data-protection key ring so a sandboxed command never writes a key ring"

requirements-completed: [SEC-04, ADV-10]

duration: 2 sessions
completed: 2026-10-07
status: complete
actuals:
  tokens: 31000
  tasks: 2
  commits: 2
---

# Phase 3 Plan 05: Operator access controls and token metrics Summary

**The operator can enrol a login and cut every Claude connection off from the host with one command, sees MCP use and token rejections as opaque metrics, and is emailed on a rejection burst, a new connection or a reused refresh token.**

## Performance

- **Tasks:** 2 of 2 (tracer plus metrics and alerts), tracer gate approved by the user
- **Commits:** 293d4d0 (Task 1, tracer), 48dc904 (Task 2)
- **Verification:** full solution test (no --no-restore) 967 total, 0 failed, 2 skipped; full build/lint.sh all six checks pass (repo-rules, workflows, shell, secrets, script-tests, observability)

## Accomplishments

### Task 1 (tracer): enrolment and the kill switch

- AddLedgerOAuthCore was extracted from AddLedgerOAuth so the web host and the command hosts share Identity and the OpenIddict stores. GrantRevocationService provides ListAsync, RevokeAsync, RevokeAllAsync, RevokeForLoginAsync and CountActiveGrantsAsync. Program.cs dispatches `login` and `grants` before the web host is built, like `apikey`.
- Evidence: integration Category=OperatorCommands 14/14. A CLI-created login cannot sign in before confirm-totp, a wrong code exits 1, and after the correct code sign-in works and /mcp answers 200. Duplicate or invalid names and empty or short passwords exit 1 creating nothing, and usage errors exit 2. `login list` shows second factor yes/no and active grants and never a password, secret or token. `grants list` shows grant id, client id, login, created, status and live tokens and never a token. `revoke-all` makes /mcp answer 401 and a refresh answer invalid_grant for both clients. `revoke GRANT_ID` revokes only that grant. set-password, reset-totp and remove each revoke only that login's grants, and after reset-totp the login cannot sign in until it is confirmed.
- deploy/tests/oauth-commands-logic-test.sh passes: the wrappers refuse non-root and out-of-pattern names, codes and grant ids, a piped password is forwarded on stdin and never on the systemd-run argument list, and the `read -r -s` prompt path is exercised through `script` (skipped when `script` is missing). deploy/tests/sandboxing-test.sh passes for the apikey, login and grants wrappers. Category=OAuth 68/68 regression at that point.

### Task 2: metrics, rejection reasons and alerts

- McpMetrics gained ledger_mcp_rejected_tokens_total{reason} (expired, wrong_audience, invalid), ledger_oauth_grants_created_total and ledger_oauth_refresh_token_reuse_total with TokenRejected, GrantCreated and RefreshTokenReused. Every reason series is created at zero in InitialiseCounters, the two unlabelled counters start at zero by construction, and an unknown reason string is folded into invalid so the label set cannot grow.
- CountingTokenAuthenticationHandler counts a rejected bearer token by reason (an absent header or an empty bearer value is never counted and still gets the 401 Bearer challenge). ReusedRefreshTokenHandler counts a refresh reuse and logs exactly one warning, "A refresh token was reused; every token of its grant was revoked.", with no token, code, client or address. The Authorize approve handler calls GrantCreated and logs the client id and grant id.
- access-rules.yaml (folder Access, group access, interval 1m) holds ledger-mcp-rejected-tokens-burst (sum of increase over 10m above 10), ledger-oauth-grant-created and ledger-oauth-refresh-token-reused (above 0), with fixed titles and summaries that only name what to check next. The existing root route sends them to the operator contact point.
- 60-observability.sh now expects 19 provisioned rule uids (the current count was 16, plus 3), asserts the three new uids, and checks that every ledger_ metric in access-rules.yaml is defined in McpMetrics.cs.
- build/tests/access-alert-expressions-test.sh generates the Prometheus rules from the Grafana file and runs 12 promtool cases (11 rejections fire the burst rule and 10 do not, reasons are summed, old counts do not fire, one grant fires only the grant rule, one reuse fires only the reuse rule, no series fires nothing) plus 7 mutation checks that each make the suite fail.
- docs/monitoring.md gained a "Claude access" section: the four metrics, the three alerts with what to do, revoke-all and revoke GRANT_ID, ledger-login management, and that proxy-blocked requests appear only in the proxy's access log.
- Tests: McpMetricsTests (unit, Category=Metrics, 47/47 in the category) and TokenRejectionTests (integration, Category=OAuth, 9/9) read the counters from the ops port's /metrics before and after each action and assert the deltas: expired (2-second lifetime, call after 3 seconds), wrong_audience (token from a second host with another public URL on the same database), invalid (random bearer, revoked token), nothing counted for no header or an empty bearer, grant +1 on consent, refresh reuse +1 with leeway 0 and no token text in any captured log, no reuse inside the default leeway, and ledger_overview +1 per call.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Authorize page stored the client_id string as the grant's application id (Task 1)**
- **Found during:** Task 1, `grants list` showed empty clients
- **Fix:** pass `applications.GetIdAsync(application)` to IOpenIddictAuthorizationManager.CreateAsync
- **Files modified:** Ledger.Service/Pages/Connect/Authorize.cshtml.cs
- **Commit:** 293d4d0

**2. [Rule 3 - Blocking] A validation event handler never sees a rejected token (Task 2)**
- **Found during:** Task 2, a probe handler at the end of ProcessAuthenticationContext logged only accepted tokens; five of nine rejection tests failed
- **Issue:** OpenIddict's dispatcher stops at the first handler that rejects, so the plan's "validation handler that runs after OpenIddict's own token validation" cannot count rejections.
- **Fix:** CountingTokenAuthenticationHandler, a thin scheme the MCP scheme forwards authentication to; it authenticates through OpenIddict and counts the failure, reading error identifiers from the failure's properties. One line in McpEndpoint.cs (ForwardAuthenticate), which is not in the plan's file list, now points at the new scheme.
- **Files modified:** Ledger.Service/OAuth/OAuthEventHandlers.cs, OAuthServiceCollectionExtensions.cs, Ledger.Service/Mcp/McpEndpoint.cs
- **Commit:** 48dc904

**3. [Rule 1 - Bug] Plan's expectation of distinct identifiers for expired and revoked did not hold (Task 2)**
- **Found during:** Task 2, a revoked token was counted as expired
- **Issue:** OpenIddict 7.7.1 gives both the same identifier (ID2019, "The specified token is no longer valid"), and the redeemed refresh token identifier is ID2012, not ID2018.
- **Fix:** ConfirmExpiry settles an expired classification against the token entry's stored expiration (looked up only when a rejection occurred); identifiers are pinned by unit tests.
- **Files modified:** Ledger.Service/OAuth/TokenRejectionReasons.cs, OAuthEventHandlers.cs
- **Commit:** 48dc904

**4. [Rule 3 - Blocking] Added Ledger.Service/Cli/OperatorHost.cs (Task 1)**
- Shared command-host builder with an ephemeral data-protection key ring, because Identity's default token providers need IDataProtectionProvider and the sandboxed command must not write a key ring. AddLedgerLoginStores was left unchanged (in Ledger.Repository, outside this plan's files).
- **Commit:** 293d4d0

**5. Test infrastructure additions (Task 1)**
- TestLogin.WithPassword added in OAuthTestDriver.cs (not in the plan's file list) to carry used time steps across a password change. The planned ConnectAsync overload was not needed.
- **Commit:** 293d4d0

**6. No red run recorded for either task.** Tests were written alongside the implementation, so there is no separate failing-test commit for the tdd="true" tasks.

**7. Counter zero assertion is presence-based in the unit test.** The counters live in the process-wide Prometheus registry, so a unit test cannot assert an absolute zero once other tests in the process have incremented them; it asserts that every series exists after initialisation and asserts exact deltas for each action. The integration test checks the series exist on a freshly started host's scrape.

**Total deviations:** 7 (2 bugs fixed, 2 blocking issues resolved, 3 documented departures). **Impact:** the observable behaviour in the plan's must-haves is met; the mechanism for counting rejections differs from the plan text.

## Known Stubs

None.

## Threat Flags

| Flag | File | Description |
|------|------|-------------|
| threat_flag: auth-path | Ledger.Service/OAuth/OAuthEventHandlers.cs | Every /mcp authentication now passes through ledger-mcp-token before OpenIddict validation. It forwards the unmodified result and only adds a counter and, on an expired classification, one read of the token entry by the presented reference value; the value is never logged or used as a label. |

## Self-Check: PASSED

- Commits 293d4d0 and 48dc904 are present in the history.
- Created files exist: GrantRevocationService.cs, LoginCommand.cs, GrantsCommand.cs, OperatorHost.cs, TokenRejectionReasons.cs, OAuthEventHandlers.cs, ledger-login, ledger-grants, oauth-commands-logic-test.sh, access-rules.yaml, access-alert-expressions-test.sh, McpMetricsTests.cs, OperatorCommandTests.cs, TokenRejectionTests.cs.
- Acceptance greps: 3 `uid: ledger-` lines in access-rules.yaml, no `{{` in it, one `"ledger_mcp_rejected_tokens_total"` in McpMetrics.cs, `ledger-grants revoke-all` appears in docs/monitoring.md.
