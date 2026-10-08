---
phase: 03-claude-reads-the-ledger
fixed_at: 2026-10-08
review_path: .planning/phases/03-claude-reads-the-ledger/03-REVIEW.md
iteration: 1
findings_in_scope: 15
fixed: 15
skipped: 0
status: all_fixed
deferred:
  - "A-WR-06: proxy-to-application hop is plain HTTP — handled by quick task 261008-0av (TLS with a pinned certificate) before release"
  - "A-IN-04, B-IN-01, B-IN-04, B-IN-05, B-IN-07: info items outside the operator's chosen fix scope"
---

# Phase 03 Review Fix Report

Fix scope chosen by the operator: both criticals, every warning except A-WR-06 (handled separately), and the cheap info items
A-IN-01..03, B-IN-02, B-IN-03 and B-IN-06. B-WR-03 is the same defect as A-WR-03 and was fixed once, in Part A.
Two fixers ran in parallel worktrees; their reports follow.

# Part A

# Phase 3 (Part A): Code Review Fix Report

**Source review:** `.planning/phases/03-claude-reads-the-ledger/03-REVIEW.md` (Part A)
**Not in scope by instruction:** A-WR-06 (proxy-to-application hop, pending an operator decision) and A-IN-04.
**Verification:** run in the isolated fixer worktree against the local PostgreSQL container. `dotnet test --solution Ledger.slnx`: 1079 tests, 1077 passed, 2 skipped (both skipped before these changes: the packaged migration bundle needs a built bundle), 0 failed. `bash build/lint.sh`: repo-rules, workflows, shell, script-tests and observability pass; secrets reports one finding that is not from these changes (see Notes). deploy/tests/selfcheck-logic-test.sh and deploy/tests/oauth-commands-logic-test.sh pass.

## Fixed Issues

### A-CR-01: One-time-code replay guard bypassed by changing the spelling of the code

**Commit:** bc2c299
**Files:** `Ledger.Domain/Auth/TotpCodes.cs` (new), `Ledger.Service/Pages/Account/Totp.cshtml.cs`, `Ledger.Service/Cli/LoginCommand.cs`
**What changed:** `TotpCodes.IsWellFormed` accepts exactly six ASCII digits; the sign-in page counts anything else as a failed attempt before verifying or claiming, and the operator confirm command applies the same check.
**Tests:** `SignInTests.Another_spelling_of_an_accepted_code_is_refused_with_the_generic_message` (`+code`, `0code`, tab-prefixed, `00code`, after the real code was accepted) and `Another_spelling_of_a_fresh_code_is_refused_and_does_not_use_the_code_up`; `TotpCodesTests` for the format rule (sign, whitespace, leading zeros, non-ASCII digit, length).

### A-CR-02: A crafted authorize link makes the Deny button approve

**Commit:** 00b45de
**Files:** `Ledger.Service/Pages/Connect/Authorize.cshtml.cs`, `Ledger.IntegrationTests/Infrastructure/OAuthTestDriver.cs`, `Ledger.IntegrationTests/Mcp/ConsentPageTests.cs` (new)
**What changed:** the consent form carries only an exact-name allow-list of OAuth request parameters (never `decision`, in any casing, nor a copy of the antiforgery token). The handler no longer binds a `decision` parameter; it reads exactly one form field named `decision`, and any other arrangement (the field twice, another casing) is read as a refusal.
**Tests:** `ConsentPageTests` posts links carrying `decision=approve`, `Decision=approve`, `DECISION=approve` and `dEcIsIoN=approve` followed by Deny and expects `access_denied` and no grant; a form carrying both `Decision=approve` and `decision=deny` is refused; the hidden fields are exactly the OAuth parameters. The tests were confirmed to fail on the old page.

### A-WR-01: PKCE `plain` still accepted

**Commit:** 7e7ca76
**Files:** `Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs`, `Ledger.IntegrationTests/Mcp/OAuthFlowTests.cs`
**What changed:** `plain` is removed from OpenIddict's code challenge methods, so it is refused with `invalid_request` and the discovery document lists only `S256`.
**Tests:** `An_authorize_request_with_the_plain_code_challenge_method_is_refused`; the discovery test now requires the supported methods to equal exactly `S256`.

### A-WR-02: Sign-in sessions survive a password change, authenticator reset or lockout

**Commit:** 97d81d6
**Files:** `Ledger.Service/Pages/Connect/Authorize.cshtml.cs`, `Ledger.IntegrationTests/Infrastructure/OAuthTestDriver.cs`, `Ledger.IntegrationTests/Mcp/OperatorCommandTests.cs`, `Ledger.IntegrationTests/Mcp/ConsentPageTests.cs`
**What changed:** the authorize page (both the GET that shows consent and the POST that decides) validates the cookie with `SignInManager.ValidateSecurityStampAsync` and also refuses a locked-out login or one without a confirmed second factor; the stale cookie is signed out and the person is sent to the sign-in page. `set-password` and `reset-totp` already change the security stamp through Identity (`ResetPasswordAsync`, `ResetAuthenticatorKeyAsync`, `SetTwoFactorEnabledAsync`); the tests prove it. Removal makes the user lookup fail.
**Tests:** a session signed in before `ledger-login set-password`, `reset-totp` or `remove` cannot approve a grant afterwards (redirect to sign-in, no grant); a session whose login became locked out, or lost its second factor, cannot approve. The set-password test was confirmed to fail without the stamp check.

### A-WR-03 (also B-WR-03): Replay guard remembers only the most recent code

**Commit:** 74bd2c7 (plus follow-ups 5a823db and the store hardening noted under A-IN-02)
**Files:** `Ledger.Domain/Auth/TotpCodes.cs`, `Ledger.Domain/Auth/ITotpReplayStore.cs`, `Ledger.Repository/Stores/TotpReplayStore.cs`, `Ledger.Repository/Entities/LedgerUserEntity.cs`, `Ledger.Repository/LedgerDbContext.cs`, new migration `20261008064523_TrackLastTotpTimeStep`, `Ledger.Service/Pages/Account/Totp.cshtml.cs`, test helpers
**What changed:** Identity's provider does not expose the matched step, so `TotpCodes.MatchTimeStep` checks the code against the authenticator key itself with the same window Identity uses (two 30-second steps either side), comparing in constant time and returning the matched step. The login row stores the highest accepted step (`last_totp_step`, replacing the code hash and timestamp columns) and `TryClaimAsync` is one conditional `UPDATE ... WHERE last_totp_step IS NULL OR last_totp_step < @step`. The page no longer depends on the injected time provider (tests replace it with a fixed date for the query tools); it judges codes against the system clock, as Identity did (commit 5a823db). `ReplayWindow` is gone. The new migration drops the two old columns.
**Test helper:** `TestLogin.NextCode` now hands out strictly increasing time steps; `NoteCodeUsedAt` records a code used by another route.
**Tests:** `A_code_from_an_earlier_time_step_is_refused_once_a_later_step_was_accepted` (A, B, A again); the existing concurrent-submission test still gives exactly one success; `TotpCodesTests` uses the RFC 6238 SHA-1 test vector for the matched step, the tolerance and malformed input.

### A-WR-04: Production validator does not require the proxy address when OAuth is on

**Commit:** a99d391
**Files:** `Ledger.Service/Hosting/ProductionConfigurationValidator.cs`, `Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs`
**What changed:** `ReverseProxy:KnownProxies` is required (and every entry must parse) whenever `OAuth:PublicBaseUrl` is set, not only for a bank provider; the key is named once when both need it; only key names appear in the message.
**Tests:** OAuth on with no, empty or unparseable proxy names only that key and no value; both features together name it once.

### A-WR-05: Host guard fails open for variants of the public host name

**Commit:** 4cbd0c1
**Files:** `Ledger.Service/Hosting/PublicHostGuard.cs`, `Ledger.UnitTests/Hosting/PublicHostGuardTests.cs` (new)
**What changed:** one trailing dot is stripped from both the configured and the requested host name before a case-insensitive comparison; the host never includes the port, so `:443` is ignored. Both the public-host and the other-host branch use the normalised value.
**Tests:** trailing dot, upper case, explicit `:443` and combinations are treated as the public host (REST and metrics 404, MCP and discovery served, sign-in pages limited to the sign-in networks); other hosts, with and without a trailing dot, keep the REST API and never get the OAuth or MCP surface.

### A-IN-01: Loopback is always a trusted proxy

**Commit:** 8240168
**Files:** `Ledger.Service/Program.cs`, `Ledger.IntegrationTests/Infrastructure/McpTestHost.cs`, `Ledger.IntegrationTests/Mcp/EndpointBoundaryTests.cs`, `deploy/bin/ledger-selfcheck`, `deploy/tests/selfcheck-logic-test.sh`
**What changed:** the default loopback proxy and network entries are cleared before the configured proxies are added. The test host configures loopback as its proxy because its emulated proxy connects from there. The selfcheck probes send no forwarded header.
**Behaviour change to know about:** the MCP protected-resource metadata document is only served for a request whose scheme is https, which from loopback now needs a trusted forwarded scheme. The selfcheck's loopback probe of that document therefore could not stay; it was removed. The 401 challenge check (which names the exact metadata URL), the 404 checks on the REST path and the sign-in page remain, and the document itself is covered by the integration tests. `docs/lxc-setup.md` still describes the selfcheck as checking that document (docs are owned by the parallel fixer).
**Tests:** a caller on loopback is not a trusted proxy unless configured (forwarded client address ignored); a plain loopback probe with the public host name gets the challenge and 404 for the status endpoint and the sign-in page; the selfcheck logic test is green.

### A-IN-02: `login confirm-totp` takes the code on the command line and does not claim it

**Commit:** 7ffd84a
**Files:** `deploy/bin/ledger-login`, `Ledger.Service/Cli/LoginCommand.cs`, `deploy/tests/oauth-commands-logic-test.sh`, `Ledger.IntegrationTests/Mcp/OperatorCommandTests.cs`, `Ledger.Domain/Auth/ITotpReplayStore.cs`, `Ledger.Repository/Stores/TotpReplayStore.cs`, `Ledger.Repository/LedgerDbContext.cs`, `Ledger.Repository/Entities/LedgerUserEntity.cs`, `Ledger.IntegrationTests/Mcp/SignInTests.cs`
**What changed:** usage is `ledger-login confirm-totp NAME`; the wrapper reads the code from standard input, or from a hidden prompt on a terminal (checked for six digits), and never passes it as an argument, so a code on the command line is a usage error. The command matches the step against the authenticator key and claims it in the replay store, so a confirming code cannot be reused on the command or the web; reset-totp clears the stored step through the store because the key it belonged to is gone.
**Defect found and fixed in the same commit:** the stored step was overwritten by a general save of a stale copy of the login. The sign-in page resets the failed-attempt count after a correct code, which saves the whole login and wrote the old step back, so a code accepted after one wrong attempt could be replayed. The property is now ignored by saves (`SetAfterSaveBehavior(Ignore)`) and written only by the store's conditional update and clear. Regression test: `A_code_accepted_after_a_failed_attempt_is_still_refused_when_presented_again` (confirmed to fail without the change).
**Tests:** `OperatorCommandTests.A_code_that_confirmed_an_authenticator_cannot_be_used_again_on_the_command_or_the_web`, usage error for a code on the command line, all confirm calls moved to standard input; `oauth-commands-logic-test.sh` covers piped and prompted codes, bad prompted codes never reaching systemd-run, and the code never on the argument list.

### A-IN-03: Login page treats a successful sign-in result as a failed one but leaves the cookie

**Commit:** e235de0
**Files:** `Ledger.Service/Pages/Account/Login.cshtml.cs`, `Ledger.UnitTests/Hosting/LoginPageTests.cs` (new)
**What changed:** when the password check returns `Succeeded`, the page signs out the application and second-factor scheme cookies before showing the generic failure. It does not call `SignInManager.SignOutAsync`, which also signs out the external scheme; that scheme is not registered here and the call would throw.
**Tests:** handler-level tests with substituted Identity services: an unexpected success is a failure and both schemes are signed out; the normal two-factor result redirects to the code page without signing out. (An integration test is not reachable: pre-checks make the unexpected success unreachable through the web.)

## Skipped Issues

None. A-WR-06 and A-IN-04 were excluded by instruction.

## Notes

- Lint secrets check: one finding remains, a Dutch IBAN pattern at `.planning/phases/03-claude-reads-the-ledger/03-REVIEW.md:296` in the review document itself (commit 1dbb062, the base of this work). It is not from these changes and was not edited. An earlier draft of the RFC test vector in `TotpCodesTests.cs` was flagged as an API key; it was reworded and folded into its commit before this report.
- A new migration was added (`TrackLastTotpTimeStep`); the packaged-bundle test that would exercise it end to end is skipped in this environment, as before.
- Existing hosts need no configuration change for A-IN-01 beyond what provisioning already writes (`ReverseProxy__KnownProxies__0` is the proxy address). A host that served OAuth without that key now refuses to start in Production (A-WR-04), which is the intended effect.

---

_Fixed: 2026-10-08_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_

# Part B

# Phase 3 (part B): Code Review Fix Report

**Base:** 1dbb062d9b52809e500ed5f77b063b439591ab5c (worktree branch worktree-agent-aa0f31e6c68d197ba)
**Verification ran in:** the isolated worktree, against the local PostgreSQL container (isolated per-test databases).

**Summary:**
- Findings in scope: 5 (B-WR-01, B-WR-02, B-IN-02, B-IN-03, B-IN-06), plus the `confirm-totp` documentation update
- Fixed: 5, plus the documentation update
- Skipped: 0
- Not attempted by instruction: B-WR-03 (parallel auth fixer), B-IN-01, B-IN-04, B-IN-05, B-IN-07

**Verification:**
- `dotnet test --solution Ledger.slnx`: 1039 total, 1037 passed, 0 failed, 2 skipped (the skips are pre-existing and need external capture files).
- `bash build/lint.sh`: repo-rules, workflows, shell, script-tests, observability pass. The `secrets` check fails, not from any change here: gitleaks flags the Dutch example account number quoted in the B-WR-01 scenario of `.planning/phases/03-claude-reads-the-ledger/03-REVIEW.md` (line 296), in the working tree and in the history that includes 1dbb062. That file is outside this fixer's scope; it needs the example redacted (and, for the history scan, an allowlist entry or a rewrite) by whoever owns the review file. This report deliberately does not repeat the number.

## Fixed Issues

### B-WR-01: Full IBANs can reach Claude through the description text

**Commit:** 707f96b
**Files modified:** `Ledger.Domain/Queries/IbanText.cs`, `Ledger.Service/Queries/LedgerQueryService.cs`, `docs/mcp.md`, `Ledger.UnitTests/Mcp/LedgerTextTests.cs`, `Ledger.IntegrationTests/Mcp/LedgerQuerySeed.cs`, `Ledger.IntegrationTests/Mcp/FreeTextMaskingTests.cs`
**Applied fix:** Added `IbanText.MaskInText`, which finds account-number-shaped tokens in free text (two letters, two digits, then eleven to thirty letters or digits, with or without spaces, any case, no word boundary required so a glued prefix does not hide one) and replaces each with the same mask as the structured counterparty account. The service applies it where results are built, so every tool is covered: search row `description` and `counterparty_name`, the money_totals counterparty breakdown and counterparty groups, and the find_counterparties name and spellings. Counterparty references are still computed from the raw name, so a reference taken from a masked name keeps working as a filter. The regex has a 100 ms timeout; on timeout the text is replaced by the hidden marker rather than passed through. Short look-alikes (under 15 normalised characters) are left alone. `docs/mcp.md` now says what is masked.
**Trade-off noted:** when an account number written in four-character groups is directly followed by words, the matcher may take up to a few following characters with it. That over-masks (never under-masks).
**Tests added:** unit theories for unspaced, spaced lower-case, mixed-case, two-in-one-text and glued-prefix numbers, for text that must stay unchanged, and for null. An integration test over the real MCP client calls all four tools against a seeded ledger with synthetic numbers (unspaced, spaced lower case, mixed case, and one inside a counterparty name) and asserts none appears in any result and the surrounding text is kept. A second integration test checks a reference from a masked counterparty still filters totals.
**Status:** fixed.

### B-WR-02: "Going public" does not require removing temporary operator access

**Commit:** 9d41cad
**Files modified:** `docs/mcp.md`
**Applied fix:** Added a paragraph at the start of "Going public": delete every temporary operator login and SSH key on the ledger host, the reverse proxy host and the workstation (check `authorized_keys` and the SSH configuration on both hosts), then rerun `ledger-selfcheck`, and state that the selfcheck only catches sudo-capable logins on the ledger host, so a login without sudo, any SSH key and anything on the reverse proxy host must be removed and checked by hand. Plain language, no personal details.
**Tests added:** none (documentation only; the repository-rules lint passes).
**Status:** fixed.

### B-IN-02: The totals basis text claims "by booking date" when a fallback date was used

**Commit:** 1514081
**Files modified:** `Ledger.Service/Queries/LedgerQueryService.cs`, `Ledger.IntegrationTests/Mcp/TotalsQueryTests.cs`
**Applied fix:** The basis now reads "booked transactions by booking date (value date, transaction date or first-seen day where the bank gave none) in <zone>; pending reported separately; transfers between the household's own synced accounts excluded".
**Tests added:** the August totals test asserts the exact fallback wording.
**Status:** fixed.

### B-IN-03: The counterparty breakdown ranks by money out only

**Commit:** b86899c
**Files modified:** `Ledger.Domain/Queries/TotalsAggregator.cs`, `Ledger.UnitTests/Mcp/TotalsAggregatorTests.cs`
**Applied fix:** Counterparties are ordered by money out plus money in (total movement) descending, then money out descending, then name. The remainder row is still the sum of everything past the cap, so the parts add up exactly to the totals. The doc comment on the breakdown now states the ranking. Existing tie-break tests stay green.
**Tests added:** a unit test with 30 outflow counterparties plus a large income source and a mixed refund counterparty: the income source ranks first, the remainder merges the right counterparties, and money out, money in and counts all sum exactly to the totals.
**Status:** fixed.

### B-IN-06: Edge inputs near the maximum date and a non-IANA zone id fail with a generic error

**Commit:** 523adae
**Files modified:** `Ledger.Domain/Queries/PeriodResolver.cs`, `Ledger.Service/Queries/QueryTimeZone.cs` (new), `Ledger.Service/Queries/LedgerQueryService.cs`, `Ledger.Service/Mcp/LedgerTools.cs`, `Ledger.UnitTests/Mcp/PeriodResolverTests.cs`, `Ledger.UnitTests/Mcp/QueryTimeZoneTests.cs` (new), `Ledger.IntegrationTests/Mcp/QueryEdgeInputTests.cs` (new)
**Applied fix:** `PeriodResolver.Resolve` refuses an explicit end date after 9000-12-31 with the fixed message "Dates after 9000-12-31 are not accepted.", which keeps day, week and month slicing away from the calendar's end. A new `QueryTimeZone.Resolve` turns the configured zone into an IANA zone (a Windows name is converted, so the name passed to the database is always IANA) and refuses a blank or unknown zone with a fixed message naming the IANA form. The service uses it in all four queries and reports the resolved zone id in results. The overview tool now translates query refusals into a tool error like the other three. The zone was resolved in the query layer rather than at host startup because the startup validator lives in the sign-in and host files owned by the parallel fixer.
**Tests added:** unit tests for the last allowed date and the day after it, for a period ending on the maximum date, and for IANA, Windows, unknown, blank and null zone names. Integration tests over the real client for money_totals (grouped by day, week, month and none), search_transactions and find_counterparties with a period ending 9999-12-31, and for all four tools with a nonexistent and a blank configured zone.
**Status:** fixed.

### Documentation: `ledger-login confirm-totp` usage

**Commit:** aee0759
**Files modified:** `docs/mcp.md`, `docs/monitoring.md`
**Applied fix:** Every occurrence now reads `sudo ledger-login confirm-totp NAME`, with the current code entered at the prompt or piped on standard input, and notes that the code is never an argument. `docs/lxc-setup.md` does not mention the command.
**Tests added:** none (documentation only).
**Status:** fixed.

## Skipped Issues

None.

---

_Fixed: 2026-10-08_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
