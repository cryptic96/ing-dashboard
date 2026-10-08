---
phase: 03-claude-reads-the-ledger
reviewed: 2026-10-07
depth: standard
status: issues_found
files_reviewed: 78
findings:
  critical: 2
  warning: 9
  info: 11
  total: 22
parts:
  - "A: auth, sign-in, OAuth server, host boundary, operator commands, deployment scripts (52 files)"
  - "B: MCP tools, query layer, SQL, period/money/cursor logic, docs (26 files)"
---

# Phase 03 Code Review (pre-release)

Run before the v0.3.0 release candidate, at the operator's request, as two parallel reviewers.
Finding ids are prefixed with their part (A- or B-). A-WR-03 and B-WR-03 describe the same replay-store weakness.

# Part A

# Phase 3 (Part A): Code Review Report - auth, sign-in, OAuth server, host boundary, operator commands, deployment scripts

**Reviewed:** 2026-10-07
**Depth:** standard
**Files Reviewed:** 52
**Status:** issues_found

## Summary

The overall structure is sound: audience is pinned to one canonical constant on issue and on validation, redirect URIs are exact-match
registrations, clients are re-seeded at every start, grants are revocable and checked on every request, sign-in cookies are `__Host-`
prefixed, antiforgery is on, CSP and framing headers are set, the wrapper scripts never put a password on argv, and the
CLAUDE.md rules (no `//` comments, no planning references, no personal data) hold across every reviewed file (grepped, no hits).

Two defects defeat controls the code is explicitly built to provide, and both were reproduced against the pinned package versions
with throwaway probes (not by reading alone):

- The one-time-code replay guard hashes the text the person typed, but ASP.NET Identity parses it with `int.TryParse`, so the same
  code can be replayed by changing its spelling.
- The consent page forwards request parameters back as hidden form fields with a case-sensitive filter, while model binding is
  case-insensitive, so a crafted authorize link makes the **Deny** button approve.

Both should be fixed before `/mcp` goes internet-facing. The remaining findings are hardening gaps (PKCE `plain` still allowed, sign-in
sessions that survive a password or authenticator reset, a validator that does not require the proxy address for the OAuth surface,
a host guard that fails open for host-name variants) plus one deployment observation (cleartext proxy-to-app hop).

Accepted deviations from the SUMMARY files: the app-side resource check (`DisableResourceValidation` + `IgnoreResourcePermissions`)
is safe as implemented, because the Authorize page always sets exactly the canonical resource as the audience regardless of what was
requested, and validation pins the same constant. The remembered-device cookie scheme that nothing issues is safe. The "unheld
random secret" fail-closed behaviour in `LedgerUserStore` is safe. None of the accepted deviations is unsafe.

## Critical Issues

### A-CR-01: One-time-code replay guard is bypassed by changing the spelling of the code

**File:** `Ledger.Service/Pages/Account/Totp.cshtml.cs:63-90,127-130` (and `Ledger.Repository/Stores/TotpReplayStore.cs:24-27`)
**Issue:** `NormaliseCode` only strips spaces and hyphens. The code is then passed to `VerifyTwoFactorTokenAsync`, whose authenticator
provider does `int.TryParse(token)`, and separately hashed as raw text (`SHA256.HashData(Encoding.UTF8.GetBytes(code))`) for the
replay claim. Many different strings parse to the same integer. Verified against the pinned Identity build: for a valid code `050465`,
all of `+050465`, `0050465`, `<tab>050465`, `050465<space>` (space is stripped anyway) and `00050465` verify as true. The `pattern`/`maxlength`
on the form is client-side only.

Failure scenario: a code is observed (shoulder-surfing, screen share, a proxy log, the household member reading it out). The legitimate
login consumes it and the replay row stores `SHA256("050465")`. Within the roughly 150 second validity of that code the attacker
(who also has the password, since this is the second step) submits `+050465`. Identity accepts it, the hash differs from the stored
one, `TryClaimAsync` succeeds, and the second factor is bypassed. The test suite checks only the identical-text replay, so it passes.

**Fix:** reject anything that is not exactly six ASCII digits before verifying, so the hashed text is canonical:
```csharp
private static readonly Regex SixDigits = new("^[0-9]{6}$", RegexOptions.CultureInvariant);

var code = NormaliseCode(Code);

if (!SixDigits.IsMatch(code))
{
    return await RefuseAsync(user, countFailure: true);
}
```
(Use a `[GeneratedRegex]` to match the rest of the code base, or `code.Length == 6 && code.All(char.IsAsciiDigit)`.) Apply the same
check in `LoginCommand.ConfirmAsync`, which also passes free text to the provider.

### A-CR-02: A crafted authorize link makes the Deny button approve the connection

**File:** `Ledger.Service/Pages/Connect/Authorize.cshtml.cs:32,56,111-113,170-172` and `Ledger.Service/Pages/Connect/Authorize.cshtml:28`
**Issue:** The consent page copies every request parameter into hidden form fields, excluding only `decision` and
`__RequestVerificationToken` with `StringComparer.Ordinal`. The handler `OnPostAsync(string? decision)` is bound by Razor Pages model
binding, which matches form keys case-insensitively and takes the first value. Verified with `FormReader` + `FormValueProvider`:
posting `Decision=approve&...&decision=deny` binds `decision` to `approve` (the hidden field precedes the button in the form).

Failure scenario: an attacker starts their own custom-connector flow from claude.ai against the public MCP host (the host name is
discoverable), takes the `https://<mcp-host>/connect/authorize?...` URL it produces, appends `&Decision=approve`, and sends it to a
household member. The member opens it on the home network or VPN, sees the consent page, and clicks **Deny**. The hidden `Decision=approve`
wins, `ApproveAsync` runs, a grant and an authorization code are created, and the browser is redirected to claude.ai's callback
carrying the attacker's `state`. If the client does not bind `state` to the browser session, the attacker's Claude account is attached to
the household ledger. The consent page is the only defence against this link-based attack and its refusal path is defeated. (The same
mechanism also lets `__requestverificationtoken` spelled in lower case break the form, which is only a nuisance.)

**Fix:** forward an allow-list of the OAuth request parameters instead of "everything except two names", and read the decision only from
a field the page itself controls:
```csharp
private static readonly string[] ForwardedParameters =
[
    "client_id", "redirect_uri", "response_type", "scope", "state", "nonce",
    "code_challenge", "code_challenge_method", "resource", "response_mode", "prompt"
];

.Where(parameter => ForwardedParameters.Contains(parameter.Key, StringComparer.OrdinalIgnoreCase))
```
Also make the handler robust: bind the decision with `[FromForm(Name = "decision")]` and refuse the POST when more than one `decision` value
(any case) is present in `Request.Form`. Add an integration test that posts `Decision=approve` plus `decision=deny` and expects a denial.

## Warnings

### A-WR-01: PKCE `plain` is still accepted

**File:** `Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs:94-112`
**Issue:** `RequireProofKeyForCodeExchange()` only requires a challenge to be present. The pinned OpenIddict (7.7.1) default
`CodeChallengeMethods` is `plain,S256` (printed from `new OpenIddictServerOptions().CodeChallengeMethods`), and nothing in the repository
narrows it. With `plain` the `code_challenge` equals the verifier, so anything that can read the authorize URL (browser history, a proxy
or Traefik access log, a local process on the loopback-redirect machine) learns the verifier and can redeem a stolen code. The discovery
document also advertises `plain`, inviting downgrade by a client that picks it.
**Fix:**
```csharp
server.Configure(options => options.CodeChallengeMethods.Remove(OpenIddictConstants.CodeChallengeMethods.Plain));
```
Add a test that an authorize request with `code_challenge_method=plain` is refused with `invalid_request`.

### A-WR-02: Sign-in sessions survive a password change, authenticator reset or lockout

**File:** `Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs:51-63`, `Ledger.Service/Pages/Connect/Authorize.cshtml.cs:78-79`
**Issue:** The application cookie is added without any security-stamp validation, and the Authorize page resolves the user with
`users.GetUserAsync(cookie.Principal)` (a lookup by id). `ledger-login set-password` and `reset-totp` update the security stamp, but nothing
ever compares it, and `TwoFactorEnabled` is not rechecked either.
Failure scenario: the operator suspects a password leak and runs `ledger-login set-password` plus `ledger-grants revoke-all`. An attacker
who already holds a live sign-in cookie (up to 15 minutes old) can still open the consent page and approve a brand-new grant, which yields
a 90-day sliding refresh token, immediately after the operator believed access was cut off.
**Fix:** validate the stamp (and lockout/second-factor state) where the cookie is trusted:
```csharp
var user = cookie.Succeeded ? await signInManager.ValidateSecurityStampAsync(cookie.Principal!) : null;
if (user is null || await users.IsLockedOutAsync(user) || !user.TwoFactorEnabled) { return Challenge(...); }
```
(inject `SignInManager<LedgerUserEntity>`). Add an integration test: sign in, run the equivalent of `set-password`, expect the consent page to redirect to sign-in.

### A-WR-03: The replay guard remembers only the single most recent code

**File:** `Ledger.Repository/Stores/TotpReplayStore.cs:18-33`, `Ledger.Repository/Entities/LedgerUserEntity.cs:11-15`
**Issue:** `LastTotpCodeSha256` holds one hash. Identity accepts a code for five 30-second steps, so two legitimate sign-ins in the window
(code A, then code B from the next step) overwrite A's hash while A is still valid. A observed earlier is then accepted again until its
step expires. The class comment promises "a restart or a second host cannot forget a code that was used", but a second sign-in forgets it.
**Fix:** record each accepted code, not only the latest: a small `identity_user_totp_used(login_id, code_sha256, accepted_at)` table with
a unique key on `(login_id, code_sha256)`, claimed by `INSERT ... ON CONFLICT DO NOTHING` (rows older than the window pruned on insert).
Alternatively compute the matched time step and refuse any step less than or equal to the last accepted one (RFC 6238 section 5.2).

### A-WR-04: The production validator does not require the proxy address when the OAuth surface is on

**File:** `Ledger.Service/Hosting/ProductionConfigurationValidator.cs:56-58,233-250`
**Issue:** `AddReverseProxyProblems` is called only from `AddBankLinkProblems`, after the early return for "no bank provider". A Production host with
`OAuth:PublicBaseUrl` set, `Ingestion:Provider` unset and no `ReverseProxy:KnownProxies` starts without complaint. Then `ForwardedHeaders`
trusts only loopback, so `RemoteIpAddress` is the Traefik address for every request. Consequences: the in-app sign-in network check
(`PublicHostGuard`) judges the proxy's own address, which normally sits inside the configured LAN range, so the second layer silently admits whatever the
proxy forwards; and the per-address rate limits (10 sign-in posts, 30 token, 300 MCP per minute) collapse into one shared bucket for all of
Anthropic plus the household, so a single noisy client can lock everyone out of `/mcp`. The README in `ledger.yml.example` promises the app enforces the lists a second time.
**Fix:** call `AddReverseProxyProblems` from `AddOAuthProblems` as well (whenever `OAuth:PublicBaseUrl` is present), and add a validator test for
"OAuth enabled, no bank provider, no proxy".

### A-WR-05: The host guard fails open for variants of the public host name

**File:** `Ledger.Service/Hosting/PublicHostGuard.cs:34-40`
**Issue:** Public-host detection is an exact (case-insensitive) match on `Request.Host.Host`. Any other value, including `mcp.example.com.`
(trailing dot, accepted by Kestrel and normally matched by Traefik host rules), is treated as an "internal" host that serves the REST API,
status and bank endpoints. The guard's stated purpose is that "a wrong rule in the reverse proxy cannot expose the REST API ... on the
public name"; for the trailing-dot spelling it does exactly that. With the shipped Traefik template the MCP-host routers are path-restricted, so
today this is defence in depth that is missing rather than an open hole, but it is the layer meant to survive a router mistake.
**Fix:** normalise before comparing and make the unknown case fail closed:
```csharp
var host = context.Request.Host.Host.TrimEnd('.');
var isOnPublicHost = string.Equals(host, _publicHostName, StringComparison.OrdinalIgnoreCase);
```
Better still, configure the internal host names and answer 404 to any host that is neither public nor internal.

### A-WR-06: The proxy-to-application hop is cleartext HTTP between two machines

**File:** `deploy/traefik/ledger.yml.example:145,152` (also `Kestrel:Endpoints:Api` in `Ledger.Service/appsettings.json`)
**Issue:** Traefik forwards to `http://<ledger-host>:5080`. Traefik and the ledger live in different containers/hosts, so passwords, one-time codes,
refresh and access tokens, session cookies and every MCP response (full financial history) cross the home network unencrypted on that hop.
The nftables rule limits who may connect, not who may sniff or ARP-spoof on the shared segment. This is not recorded as an accepted deviation and the docs do not mention it.
**Fix:** either terminate TLS in the app on 5080 (certificate pinned in a Traefik `serversTransport`), or record the decision explicitly in the
deployment guide together with the condition that makes it acceptable (point-to-point link or isolated bridge). Cookies are marked `Secure`
and OpenIddict insists on HTTPS only because `X-Forwarded-Proto` says so; that header does not make the hop private.

## Info

### A-IN-01: Loopback is always a trusted proxy

**File:** `Ledger.Service/Program.cs:77-90`
**Issue:** `ForwardedHeadersOptions` keeps its defaults (`::1` and `127.0.0.0/8`) and adds the configured proxies. Any local process on the ledger host
(Grafana plugins, an SSRF in a local service) can send `X-Forwarded-For` and `Host` to `127.0.0.1:5080` and be judged as an arbitrary client address,
including a sign-in network. The selfcheck relies on this behaviour. Needs local code execution, hence info.
**Fix:** `options.KnownProxies.Clear(); options.KnownNetworks.Clear();` before adding the configured proxies, and have the selfcheck probe pass no forwarded header.

### A-IN-02: `login confirm-totp` takes the code on the command line and does not claim it

**File:** `deploy/bin/ledger-login:119-129`, `Ledger.Service/Cli/LoginCommand.cs:93`
**Issue:** The six-digit code appears in `ps` and in the systemd journal entry for the transient unit. `ConfirmAsync` does not record the code in the replay store, so
the same code can sign in over the web within its validity window. Short-lived, root-visible only, hence info.
**Fix:** read the code from standard input like the password, and call `TryClaimAsync` in `ConfirmAsync`.

### A-IN-03: Login page treats a successful sign-in result as a failed one but leaves the cookie

**File:** `Ledger.Service/Pages/Account/Login.cshtml.cs:57-64`
**Issue:** If `PasswordSignInAsync` ever returns `Succeeded` (second factor not required, for example after a future change to how providers are counted), the code sets
`Failed = true` and shows the form, but Identity has already issued the application cookie, so the person is signed in without a second factor. Currently unreachable because
of the pre-checks, which is exactly why a one-line guard is cheap.
**Fix:** in that branch call `await signInManager.SignOutAsync();` before showing the page (or treat `Succeeded` explicitly as an error).

### A-IN-04: Sign-in network validation accepts any non-/0, non-Anthropic range, and provisioning derives it from the SSH allow-list

**File:** `Ledger.Service/Hosting/ProductionConfigurationValidator.cs:119-143`, `deploy/provision.d/20-accounts.sh:140-141`
**Issue:** A typo such as `1.0.0.0/8` or `0.0.0.0/1` passes validation and widens the in-app sign-in gate to a large part of the internet; the application check is
only a second layer behind Traefik, but nothing flags it. Provisioning also writes `OAuth__SignInNetworks` from `LEDGER_ADMIN_SSH_SOURCES`, so narrowing SSH access
silently narrows who can approve connections (fails closed, but surprising), and an existing host's env file is never re-rendered, so already-provisioned hosts
need the new `OAuth__*` keys added by hand.
**Fix:** require every sign-in range to be private (RFC 1918, unique-local, CGNAT or link-local) in the validator; give provisioning its own `LEDGER_SIGN_IN_NETWORKS`
key; document the manual env-file step for existing hosts.

---

_Reviewed: 2026-10-07_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

# Part B

# Phase 3 (part B): Code Review Report

**Reviewed:** 2026-10-07
**Depth:** standard
**Files Reviewed:** 26
**Status:** issues_found

## Summary

The query layer is in good shape for its core purpose. I traced the sums, the period boundaries, the keyset cursor, the SQL
parameterisation and the docs against the code and found no wrong-number defect and no injection path. Findings are limited to
one privacy gap, one documentation gap on a security-critical go-live step, one replay-protection weakness, and smaller items.

Verified clean (so the orchestrator does not need to re-check):

- **Sums:** every amount is `decimal` end to end (`numeric` in SQL, strings out through `MoneyText`); no `double` or `float`
  anywhere. Totals are booked-only (`status IN ('booked','pending')`, then split by `Kind`); pending and own-account transfers are
  summed separately per currency; currencies are never added (aggregation is per `Currency`, and `ExcludedFor` zero-fills every
  currency present). Dropped rows are excluded in every query, including counterparty-ref resolution.
- **Period boundaries:** `Today()` uses `TimeZoneInfo.ConvertTime` into the zone, never the UTC date. SQL uses
  `(first_seen_at AT TIME ZONE zone)::date`, which is DST-correct. Monday-based week arithmetic is correct for Sunday
  (`(DayOfWeek + 6) % 7`). Month, year and `last_N_days` ranges are correct and inclusive.
- **Keyset pagination:** the row-value comparison `(period_date, first_seen_at, id) < (...)` matches the all-DESC ordering. The
  `id` tie-break is compared inside PostgreSQL (uuid ordering), not in .NET, so there is no Guid-ordering mismatch.
  `first_seen_at` round-trips at microsecond precision because the cursor is built from a value read back from the database. A
  forged or tampered cursor can only AND an extra restriction onto filters that are always applied, so it cannot widen access.
  The 16-hex filter hash is enough for its stated purpose (detect reuse against another search). Decode is bounded (512
  characters) and every parse failure maps to a fixed message.
- **SQL injection and LIKE escaping:** `SqlQuery<T>` is called with an interpolated `FormattableString`, so every `{x}` is a
  bind parameter. `LikePattern` escapes `\`, `%` and `_` in the right order with PostgreSQL's default escape character.
- **Snapshot consistency:** totals, search and counterparty reads run in a `REPEATABLE READ`, `READ ONLY` transaction, and ref
  resolution happens inside the same snapshot as the sums.
- **Output leakage:** only `DisplayName` or `"Account " + opaque key` is shown, never the provider's account name. IBANs leave
  only as `IbanText.Mask` output. Metric labels are fixed tool names or fixed reason words.
- **Resource bounds:** list arguments are capped at 10 values of 100 characters, explicit spans at 3660 days, search pages at
  100 rows, day grouping at 92 days and week grouping at 731 days.
- **CLAUDE.md rules:** no `//` comments and no planning references in the reviewed source or docs. Doc hostnames and addresses
  are `example.*` and RFC 5737 ranges. Anthropic's public `160.79.104.0/21` is not personal data.
- **Docs versus code:** token lifetimes (15 minutes, 90 days), client ids, the `ledger-login` and `ledger-grants` subcommands,
  the `ledger-selfcheck` and `check-exposure.sh` flags, the Access alert thresholds and the Traefik path lists all match.

Accepted deviation, explicitly not a finding: bank text (counterparty name, description) flows into tool output with no
untrusted-data label, no sanitising and no instruction line. This is the recorded operator decision and is enforced by a unit
test. It is safe as a recorded risk because the tools are read-only and `docs/mcp.md` tells the operator to keep ledger
conversations separate from other connectors. B-WR-01 below is a different matter (identifier leakage) and is not covered by that decision.

## Warnings

### B-WR-01: Full IBANs can reach Claude through the description text, contradicting the "masked everywhere" guarantee

**File:** `Ledger.Repository/Stores/LedgerQueryStore.cs:447`, `Ledger.Service/Queries/LedgerQueryService.cs:183`, `docs/mcp.md:33`
**Issue:** Only the structured `counterparty_iban` column is masked. `description` is the bank's remittance text joined with
spaces (`EnableBankingJson.Description`) and is returned verbatim for up to 100 rows per call. Dutch remittance text commonly
carries the counterparty's full IBAN (for example an "IBAN:" segment). It can also carry the household's own IBAN. When it does,
`search_transactions` hands that IBAN to Claude unmasked.
- This breaks the stated rule that full IBANs never leave the server.
- `docs/mcp.md:33` ("Account numbers are masked in everything Claude sees") then becomes false.
- `ILedgerQueryStore` and `IbanText` both promise that a masked number is the only form that may appear in a result.

Scenario: the household searches "rent"; a row's `description` contains `... IBAN: <a full Dutch account number> ...` and the full number is
now in the Claude conversation and in Anthropic's logs.

This is an identifier-masking requirement, not the untrusted-data marking that was deferred. Masking an IBAN-shaped token does
not label or sanitise the bank's text for injection purposes. If the operator reads it as the same family, the doc claim must
change instead.

**Fix:** mask IBAN-shaped tokens in `description` (and `counterparty_name`) at the service boundary.
```csharp
private static readonly Regex IbanShape = new(@"\b[A-Z]{2}\d{2}(?:\s?[A-Za-z0-9]){11,30}\b", RegexOptions.CultureInvariant);

internal static string? MaskIbans(string? text) =>
    text is null ? null : IbanShape.Replace(text, match => IbanText.Mask(match.Value)!);
```
Apply it where `SearchRow` is built (`LedgerQueryService.cs:180` and `:183`). Add a test with a synthetic IBAN in a description. If
masking is intentionally not done, change `docs/mcp.md:33` and the `IbanText` summary to say descriptions are shown as the bank sent them.

### B-WR-02: "Going public" does not require removing the temporary operator access to the hosts before the public router is enabled

**File:** `docs/mcp.md:169-182`
**Issue:** The go-live checklist gates on "`ledger-selfcheck` reports no failures, including its checks that no extra login can
use sudo". That covers only sudo-capable accounts on the ledger host. The recorded precondition for going public is stronger:
delete the extra logins on the ledger host and on the reverse proxy, and delete the workstation key, then run the selfcheck, and
only then enable the public router. The reverse-proxy host is outside the selfcheck's reach, and a login without sudo (an SSH
key on either host) passes the check. An operator following the doc literally can go public with leftover remote access to
the reverse proxy that fronts the household's financial data endpoint.
**Fix:** add an explicit step before step 2 of "Going public", written without personal detail:
```
0. Remove every temporary login and SSH key that was used to set the hosts up: on the ledger host,
   on the reverse proxy host, and on the workstation. Then run ledger-selfcheck and confirm it passes.
   Only after that change the public router's first middleware to ledger-mcp-allow.
```
Consider also mentioning `authorized_keys` and the reverse proxy's SSH configuration as places to check.

### B-WR-03: The one-time-code replay guard remembers only the last code, so an earlier code can be replayed within its validity window

**File:** `Ledger.Repository/Stores/TotpReplayStore.cs:21-26`, `Ledger.Domain/Auth/ITotpReplayStore.cs:6-10`, `Ledger.Service/Pages/Account/Totp.cshtml.cs:75-86`
**Issue:** ASP.NET Identity's authenticator verification accepts the previous, current and next 30-second step, about 90 seconds in all. The store keeps a single `LastTotpCodeSha256`. Sequence:
1. Code A is accepted at step s and stored.
2. Code B (step s+1) is accepted and overwrites A.
3. A is replayed while it is still valid; `LastTotpCodeSha256 (B) != A`, so the claim succeeds.

An attacker who observed A (shoulder-surfing, a phishing relay) can sign in once the owner has logged in again with the next
code. The practical likelihood is low, since it needs two logins in about a minute plus a captured code. It still defeats the
stated guarantee that a code accepted once cannot be accepted again. Separately, storing an unsalted SHA-256 of a six-digit code
gives no protection of the stored value, which is harmless only because the value expires within the window.
**Fix:** follow RFC 6238 section 5.2 and reject any time step at or below the last accepted one. Store the matched time step
(a `long`) instead of a code hash. If Identity does not expose the matched step, verify the code yourself against steps
`now-1..now+1` and keep the highest accepted step:
```csharp
.Where(login => login.Id == loginId && (login.LastTotpStep == null || login.LastTotpStep < step))
.ExecuteUpdateAsync(s => s.SetProperty(login => login.LastTotpStep, step), cancellationToken);
```
This also removes the need for `ReplayWindow`.

## Info

### B-IN-01: `ResolveCounterpartyNamesAsync` is dead on the interface, and `PeriodDate.Of` is production-dead while SQL repeats its rule three times

**File:** `Ledger.Domain/Queries/ILedgerQueryStore.cs:28`, `Ledger.Repository/Stores/LedgerQueryStore.cs:114`, `Ledger.Domain/Queries/PeriodDate.cs:22`, `Ledger.Repository/Stores/LedgerQueryStore.cs:315-318,409-412,488`
**Issue:** `ResolveCounterpartyNamesAsync` has no production caller (only the private `ResolveNamesAsync` is used). `PeriodDate.Of` is
called only from unit tests. The real period-date rule is a `CASE` expression pasted into three SQL statements. The unit tests
therefore pin a C# function that production never runs, and the three SQL copies can drift apart without a failing test.
**Fix:** delete the unused interface member. Either keep `PeriodDate.Of` only as a documented oracle for an integration test that
compares it to the SQL on seeded rows, or remove it. Extract the `CASE` into one C# string constant interpolated into the
three queries as a raw fragment.

### B-IN-02: The totals basis text says "by booking date" even when the date came from a fallback

**File:** `Ledger.Service/Queries/LedgerQueryService.cs:432`
**Issue:** Booked rows with no booking date silently land on their value date, then transaction date, then the first-seen day. The
result still says "booked transactions by booking date". Claude can then tell the user a figure is "by booking date" when
a few rows were placed by a fallback. Normally the bank sends a booking date for booked rows, so this is rare.
**Fix:** use "by booking date (value or transaction date when the bank gave none)", or return a count of rows that used a fallback.

### B-IN-03: The counterparty breakdown ranks by money out only, so large income sources sink into "Other counterparties" for direction=both

**File:** `Ledger.Domain/Queries/TotalsAggregator.cs:230-232`, `:194`
**Issue:** The ordering is `MoneyOut desc, MoneyIn desc, Name`. With the default `both` direction and more than 25 counterparties
that have outflows, a counterparty with large inflows and no outflow falls past the 25-row cap into the remainder row. The totals
stay correct and the remainder is labelled, but the list is described as "the top counterparties". "Who paid us the most" answered
from this list would be wrong.
**Fix:** order by `MoneyOut + MoneyIn` descending (the magnitude), or build the top-N separately per direction. At minimum state the
ranking in the tool description.

### B-IN-04: Masking leaves almost nothing hidden for short account identifiers

**File:** `Ledger.Domain/Queries/IbanText.cs:34-36`
**Issue:** The mask keeps two leading and four trailing characters and hides only identifiers shorter than eight. An 8-character
non-IBAN account number shows 6 of its 8 characters. A 10-character one shows 6 of 10. The mask is meant to hide the identity of the counterparty account.
**Fix:** raise the threshold, for example hide fully below 12 characters, or show the last four only for anything that does not
look like an IBAN (two letters, two digits, then up to 30 alphanumerics).

### B-IN-05: The overview is read outside a snapshot, one query per account, and its history range ignores the first-seen fallback

**File:** `Ledger.Repository/Stores/LedgerQueryStore.cs:17-79`, `:45-46`
**Issue:** `ReadOverviewAsync` issues several independent queries with no transaction, so a sync that commits in between can show a
pending count, history range and balance that never coexisted. `ReadLatestBalanceAsync` also runs two queries per account.
`Earliest` and `Latest` use `booking ?? value ?? transaction date` and ignore the first-seen fallback that totals use, so a booked row
with no bank dates is counted by totals but missing from `history_from` and `history_to`.
**Fix:** wrap the method in `InSnapshotAsync`, and add the same `first_seen_at` fallback (in the database zone) to the min and max.

### B-IN-06: Edge inputs near the maximum date, and a non-IANA zone id, fail the whole call with a generic error

**File:** `Ledger.Domain/Queries/TotalsAggregator.cs:360,386`, `Ledger.Repository/Stores/LedgerQueryStore.cs:316-317,410-411,488`
**Issue:** `toDate=9999-12-31` with `groupBy=day` or `month` makes `AddDays(1)` or `AddMonths(1)` throw
`ArgumentOutOfRangeException`. The tool returns the SDK's generic failure instead of a clear message, because only `LedgerQueryException` is
translated. Separately, `AT TIME ZONE {zone.Id}` passes the zone's id to PostgreSQL. If `Ingestion:TimeZone` is ever set to a Windows
id (which .NET maps on Linux but keeps in `.Id`), every totals, search and counterparty query fails with "time zone not recognized".
**Fix:** reject explicit dates beyond a sane ceiling (for example year 9000) in `PeriodResolver.Resolve`. Validate at startup that
`TimeZone` is an IANA id the database accepts, or resolve the zone to its IANA id (`TimeZoneInfo.TryConvertWindowsIdToIanaId`).

### B-IN-07: Small contract and wording mismatches in tool results and descriptions

**File:** `Ledger.Service/Mcp/LedgerTools.cs:66`, `Ledger.Service/Queries/LedgerQueryService.cs:178`, `:467`, `Ledger.Repository/Stores/LedgerQueryStore.cs:459-460`
**Issue:**
- The `counterpartyRef` parameter says "to focus on exactly those counterparties". When `counterparty` words are also given, the two
  selections are OR-ed (see `LedgerQueryFilter.HasCounterpartySelection`), which widens the result instead of focusing it.
- A zero-amount row gets `direction: "in"`.
- The totals summary text only mentions currencies with booked rows, so pending or transfer rows of a zero-count currency appear only in the
  structured arrays, not in the sentence Claude will quote.
- The page order depends on PostgreSQL keeping the inner `ORDER BY` through the subquery EF wraps around raw SQL. That holds in practice
  but is not guaranteed. Sorting in C# is not a safe fix, because .NET Guid order differs from uuid order. If it ever changed,
  `Take(limit)` and the cursor taken from `Rows[^1]` would silently go wrong.
**Fix:**
- Reword the `counterpartyRef` description, for example "added to the counterparty words; a transaction matches if it matches either".
- Emit `direction: "none"` for zero amounts.
- List every currency in the summary that has pending or transfer rows.
- Select a `ROW_NUMBER() OVER (ORDER BY ...)` column in the SQL and order by it in C#.

---

_Reviewed: 2026-10-07_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
