---
phase: 02
slug: automatic-ing-sync
status: verified
# threats_open = count of OPEN threats at or above workflow.security_block_on severity (the blocking gate)
threats_open: 0
asvs_level: 2
block_on: high
created: 2026-10-07
---

# Phase 02 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| adapter → logs | Tokens and session material must never cross | — |
| aggregator responses → mapping | Untrusted JSON, text and amounts | — |
| application → aggregator | Code exchange and session revocation | — |
| application → aggregator/bank quota | A shared, externally enforced daily call budget per account | — |
| application → api.enablebanking.com | Signed client token over TLS; the only outbound bank route | — |
| application → /metrics (loopback) → Prometheus | Operational data only | — |
| application and services → journal and /var/log | A secret written to a log would persist outside the env file | — |
| bank redirect in the operator's browser → anonymous callback | Untrusted query parameters; the only unauthenticated endpoint | — |
| Claude's SSH session → host | Verification with counts and statuses; secrets are off limits | — |
| Enable Banking/ING → operator browser → internal callback URL | The one-time code travels in the redirect URL | — |
| encrypted captures → analyze → terminal and Claude's context | Only counts may cross | — |
| encrypted captures → tmpfs → replay test | Real financial data briefly in memory on the workstation | — |
| GitHub → host | Only attested, approved releases from main are installed | — |
| Grafana alerting → mail relay → operator mailbox | Email leaves the home network | — |
| grafana_reader → reporting.account_status | Grafana's read path to status and balances | — |
| grafana_reader → reporting views | Grafana's only path to financial data | — |
| Grafana (Viewer sessions) → PostgreSQL | Dashboard SQL runs as grafana_reader | — |
| ledger_runtime → tables | The app's own role; least privilege enforced by grants | — |
| NuGet → build | One new runtime package | — |
| NuGet feed → build | Package versions resolved into lock files that CI restores in locked mode | — |
| operator browser (LAN/VPN) → internal callback | The one anonymous endpoint, reached only through the LAN/VPN route | — |
| operator client → authenticated bank endpoints | X-Api-Key, LAN/VPN via Traefik | — |
| operator client → POST /api/v1/bank/sync | Authenticated trigger that can spend quota | — |
| operator shell → ledger-bank-key → /etc/ledger | Generates and stores the aggregator key and its password | — |
| production configuration → startup | Incomplete or unsafe bank settings | — |
| provider balances → ledger | The bank's balance is the external truth the ledger is checked against | — |
| provider feed → reconciler | The bank's statuses and identifiers are not guaranteed stable | — |
| provider payload → ledger | Bank data (descriptions, names) is untrusted input stored as data | — |
| Pull request → CI/release workflow | The release gate must keep running the whole suite before attestation | — |
| raw bank payloads → workstation disk | Real financial data at rest outside the repository | — |
| repository → provisioned Grafana files | Committed JSON is what Grafana loads | — |
| root installer → host filesystem | The poll timer runs the installer as root on every poll | — |
| spike output → Claude's context | Only structural summaries may cross | — |
| workstation → aggregator | Quota probe and revocation calls | — |
| workstation → Enable Banking API | Signed client token and session id travel over TLS | — |

---

## Threat Register

Verified at ASVS level 2 against the code of release v0.2.3 (the deployed release), by three parallel security audits on 2026-10-07.

| Threat ID | Category | Component | Severity | Disposition | Mitigation | Status |
|-----------|----------|-----------|----------|-------------|------------|--------|
| T-02-01-01 | Tampering | test packages | medium | mitigate | xunit.v3 4.0.1 and Microsoft.Extensions.TimeProvider.Testing 10.10.0 were checked in the research package audit (first-party owners); lock files committed and CI restores with --locked-mode | closed |
| T-02-01-02 | Elevation of Privilege | release gate | medium | mitigate | No trait filter in either workflow; the bundle test still fails when CI is true; the diff is limited to the two test-step lines | closed |
| T-02-01-03 | Tampering | workflow edits | low | mitigate | build/lint.sh workflows (actionlint, zizmor) must pass; no new actions or interpolations added | closed |
| T-02-01-SC | Tampering | NuGet installs | low | accept | NuGet PackageReference runs no install scripts; both packages are Microsoft-owned and audited; the package-legitimacy checkpoint applies to npm/pip/cargo only | closed (accepted) |
| T-02-02-01 | Information Disclosure | captured payloads | high | mitigate | Piped straight into age with a public recipient; no plaintext file ever written (selftest asserts only .age files); identity stays in the operator's password manager | closed |
| T-02-02-02 | Information Disclosure | terminal output and Claude's context | high | mitigate | jq summaries print structure, counts, dates and enum codes only; selftest asserts no synthetic value or marker reaches the output | closed |
| T-02-02-03 | Spoofing | spike private key | medium | mitigate | Mode 600 inside a mode-700 directory; separate spike application; key deleted and session revoked when the spike ends (spike-completion plan) | closed |
| T-02-02-04 | Information Disclosure | one-time code in the redirect URL and proxy access log | medium | mitigate | Code is single-use and exchanged immediately; read with read -s, never echoed; the internal hostname is LAN/VPN-only | closed |
| T-02-02-05 | Elevation of Privilege | spike consent left active | medium | mitigate | revoke subcommand; the spike-completion plan runs it and confirms the session is closed | closed |
| T-02-02-06 | Spoofing | Claude's temporary SSH key | high | mitigate | Passphrase plus ssh-agent (D-04); hard removal before the public MCP endpoint, tracked in the reshaped todo | closed |
| T-02-02-07 | Tampering | payment services on the spike application | low | mitigate | application subcommand prints the services list; the operator confirms only AIS is enabled | closed |
| T-02-03-01 | Information Disclosure | secrets in logs | high | mitigate | check_log_secrets scans the journal and /var/log for every secret shape and value; FAIL output names only the kind and location | closed |
| T-02-03-02 | Information Disclosure | needles in process arguments | medium | mitigate | Fixed-string needles fed through /dev/fd/3; openssl passwords via env:, never argv | closed |
| T-02-03-03 | Elevation of Privilege | root installer unit | medium | mitigate | ProtectSystem=strict with a derived ReadWritePaths list, NoNewPrivileges, kernel/cgroup/namespace protection; live systemd-analyze check at go-live | closed |
| T-02-03-04 | Elevation of Privilege | apikey CLI run | low | mitigate | systemd-run sandboxing properties, AF_UNIX only, empty capability set | closed |
| T-02-03-05 | Information Disclosure | aggregator private key at rest | high | mitigate | Encrypted PKCS#8 with a random password in the env file, key mode 640 root:ledger, never in database backups, copies only in the password manager | closed |
| T-02-03-06 | Tampering | env file edits | medium | mitigate | Validated values, atomic replace-or-append, mode 640 root:ledger preserved, other lines untouched (tested) | closed |
| T-02-03-07 | Denial of Service | missing tzdata | low | mitigate | tzdata installed; selfcheck zone line; the application also fails fast on an unresolvable zone (scheduler plan) | closed |
| T-02-04-01 | Tampering | transaction identity and history | high | mitigate | Payloads and refs append-only for ledger_runtime; transactions cannot be deleted and id/account/first-seen cannot be updated (column grants); role tests prove each denial | closed |
| T-02-04-02 | Tampering | Grafana writing to the ledger | high | mitigate | grafana_reader has SELECT only on migrator-owned reporting views, no USAGE on public; data-driven privilege test over every reporting object | closed |
| T-02-04-03 | Information Disclosure | reporting views | medium | mitigate | Views expose no payloads, IBANs or session material; only account keys and display names | closed |
| T-02-04-04 | Information Disclosure | bank session id at rest | high | mitigate | Stored only as ISecretProtector ciphertext (session_id_protected); never logged or returned | closed |
| T-02-04-05 | Tampering | malformed amounts | medium | mitigate | Scale and magnitude validated before any write; the run fails as malformed instead of the column rounding | closed |
| T-02-04-06 | Denial of Service | concurrent runs | medium | mitigate | Partial unique index on unfinished sync_runs per connection; per-account single transaction | closed |
| T-02-04-07 | Information Disclosure | logs | medium | mitigate | One log line per run with run id, outcome and opaque account keys; no amounts, names or IBANs | closed |
| T-02-04-08 | Information Disclosure | committed fixtures | medium | mitigate | Synthetic data only, XX IBAN-like identifiers; negative grep for Dutch IBAN shapes plus the repository's gitleaks rule | closed |
| T-02-05-01 | Tampering | ledger integrity via wrong merges | high | mitigate | Merge only on a unique, mutual, non-empty-counterparty match; every other case stored separately and flagged (tests for each ambiguity shape) | closed |
| T-02-05-02 | Tampering | ledger integrity via wrong drops | high | mitigate | Drops only after a complete, non-empty fetch that covered the row's date; dropped rows kept and restorable | closed |
| T-02-05-03 | Repudiation | history of what the bank reported | medium | mitigate | Payload rows appended for every changed observation, including merges; rows are never deleted | closed (accepted) |
| T-02-05-04 | Denial of Service | feed-order dependent fingerprints | low | accept | Only affects banks without entry references; the spike reports whether ING sends them; identical same-day items stay separate by design | closed (accepted) |
| T-02-06-01 | Tampering | writes through dashboard SQL | high | mitigate | Datasource role is SELECT-only on reporting views (role tests from plan 02-04); generator test rejects any non-reporting identifier or other datasource | closed |
| T-02-06-02 | Tampering | hand-edited or UI-edited dashboards | medium | mitigate | editable false, allowUiUpdates false, byte-for-byte drift test in CI | closed |
| T-02-06-03 | Information Disclosure | bank text rendered in Grafana | low | accept | Bank descriptions are shown as table cell text, which Grafana escapes; no HTML or markdown panel renders bank text; LAN/VPN-only Viewer access | closed (accepted) |
| T-02-06-04 | Denial of Service | unbounded table query | low | mitigate | LIMIT 500 and the dashboard time filter | closed |
| T-02-07-01 | Spoofing | forged or replayed callback (login CSRF) | high | mitigate | 256-bit state created only by an authenticated call, SHA-256 at rest, atomic single use, 15-minute TTL, bound to purpose and connection; tests for reuse, expiry, unknown and malformed | closed |
| T-02-07-02 | Information Disclosure | callback response | medium | mitigate | Identical generic failure body; success reveals only counts; no-store and no-referrer headers; tests | closed |
| T-02-07-03 | Information Disclosure | code, state and session id in logs | high | mitigate | Never logged; request logging stays at Warning; LogRedaction sentinel tests over logs, responses and /metrics | closed |
| T-02-07-04 | Elevation of Privilege | anonymous endpoints | high | mitigate | Fallback policy unchanged; inventory test allows exactly one anonymous route | closed |
| T-02-07-05 | Tampering | synthetic data in the real ledger | high | mitigate | Production validator refuses Synthetic or unknown providers; test | closed |
| T-02-07-06 | Information Disclosure | session id at rest | high | mitigate | ISecretProtector ciphertext only; API never returns it; test that the stored value differs from the plaintext | closed |
| T-02-07-07 | Tampering | account selection input | medium | mitigate | Account keys must belong to the connection; display names 1 to 40 characters without control characters | closed |
| T-02-07-08 | Denial of Service | callback flooding from the LAN | low | accept | LAN/VPN-only route; shape check before any database or provider call; state guessing is infeasible | closed (accepted) |
| T-02-07-09 | Tampering | spoofed authorisation URL | medium | mitigate | Only absolute https URLs are returned; the adapter plan adds an aggregator host allow-list | closed |
| T-02-08-01 | Denial of Service | quota exhaustion (self-inflicted) | high | mitigate | Append-only call ledger recorded before every call; per-account budget; no same-day retry after rate limits; no HTTP-level retries; last-call refusal for header-less sync-now | closed |
| T-02-08-02 | Denial of Service | interactive paths triggering fetches | medium | mitigate | Only the scheduler, post-link/renew and sync-now can queue a sync; test over read endpoints, /health and /metrics | closed |
| T-02-08-03 | Tampering | call ledger manipulation | medium | mitigate | REVOKE UPDATE, DELETE, TRUNCATE on provider_calls for ledger_runtime; role test | closed |
| T-02-08-04 | Repudiation | silent failures | medium | mitigate | Every run finishes with an outcome and provider code; abandoned runs recorded at startup | closed |
| T-02-08-05 | Spoofing | PSU context claims | low | accept | PSU values come from the authenticated operator's own request through the known proxy; they only mark the operator as present and never grant access | closed (accepted) |
| T-02-08-06 | Denial of Service | unresolvable time zone | low | mitigate | Production validator names Ingestion:TimeZone and fails fast; tzdata installed by provisioning | closed |
| T-02-09-01 | Repudiation | hidden incompleteness | medium | mitigate | Exact reconciliation with recorded expected amount and drift; unknown instead of guessed when inputs are missing | closed |
| T-02-09-02 | Tampering | snapshot history | medium | mitigate | balance_snapshots append-only for ledger_runtime; role test | closed |
| T-02-09-03 | Denial of Service | balance calls spending quota | low | mitigate | One balance fetch per account per day, metered against the budget | closed |
| T-02-09-04 | Information Disclosure | balances in logs | medium | mitigate | Log line carries reconciled true, false or unknown only, never amounts | closed |
| T-02-10-01 | Information Disclosure | metric labels | high | mitigate | Opaque account and connection keys only; integration test asserts no name, IBAN or counterparty in the scrape | closed |
| T-02-10-02 | Information Disclosure | alert emails | high | mitigate | Static titles and summaries; lint rejects template markers in rule files; operator-only contact point | closed |
| T-02-10-03 | Repudiation | silent failures and stale data | high | mitigate | Failing, stale, consent and drift rules; metrics seeded from the database so restarts cannot mask them | closed |
| T-02-10-04 | Denial of Service | alert mail flood against the relay's daily limit | medium | mitigate | Existing grouping and hourly group interval kept; household repeat set to 24h | closed |
| T-02-10-05 | Denial of Service | metrics refresh load | low | accept | A handful of small queries per minute on a single-household database | closed (accepted) |
| T-02-11-01 | Information Disclosure | status view | medium | mitigate | A test pins the view's exact column list, so no identifier, session material or payload column can be added unnoticed; the migrator-owned view inherits SELECT-only for grafana_reader | closed |
| T-02-11-02 | Tampering | writes through the new view | high | mitigate | The existing data-driven test proves grafana_reader lacks INSERT, UPDATE, DELETE and TRUNCATE on every reporting object, including this one | closed |
| T-02-11-03 | Repudiation | dashboard and alerts disagreeing about consent | low | mitigate | Parity test between the SQL derivation and ConsentState.Derive at each boundary | closed |
| T-02-12-01 | Information Disclosure | decrypted captures | high | mitigate | Decryption in memory only, identity via stdin, counts-only output; selftest asserts no synthetic value leaks | closed |
| T-02-12-02 | Elevation of Privilege | leftover spike consent | medium | mitigate | revoke plus confirmation in the ING app; state file and keys deleted and checked | closed (accepted) |
| T-02-12-03 | Denial of Service | quota probe locking out the spike app | low | accept | Deliberate, one-off, on the spike application only, never the server's application | closed (accepted) |
| T-02-13-01 | Elevation of Privilege | payment initiation through the aggregator | critical | mitigate | No payment member on the interface (reflection test); outbound allow-list of seven account-information routes on one https host (recorded-run and refusal tests); pre-link refusal when the application offers payment initiation; no payment route literal in production code (acceptance grep) | closed |
| T-02-13-02 | Spoofing | forged or replayed client token | high | mitigate | RS256 with the host-generated, password-protected key; 30-minute lifetime; key never logged; constant base address so the token is only ever sent to the aggregator | closed |
| T-02-13-03 | Information Disclosure | tokens, session ids, codes or bank text in exceptions and logs | high | mitigate | Fixed exception messages; only validated error codes kept; no request URLs or bodies in messages; log-redaction sentinels extended in the runbook plan | closed |
| T-02-13-04 | Tampering | malformed amounts or dates | medium | mitigate | Strict MoneyParser and date parsing; malformed data fails the run instead of being stored | closed |
| T-02-13-05 | Denial of Service | retries burning the bank quota | high | mitigate | No HTTP retry or resilience handler (acceptance grep); every request metered before it is sent | closed |
| T-02-13-06 | Denial of Service | oversized responses | low | mitigate | 16 MB response buffer limit and a 60-second timeout | closed |
| T-02-13-07 | Tampering | spoofed authorisation URL | medium | mitigate | https plus aggregator host allow-list before the URL reaches the operator | closed |
| T-02-13-SC | Tampering | NuGet install of Microsoft.IdentityModel.JsonWebTokens | medium | mitigate | Approved in the research package audit (Microsoft-owned, verified); exact version pinned; lock files committed; locked-mode restore in CI | closed |
| T-02-14-01 | Information Disclosure | decrypted captures | high | mitigate | Decrypted only into a mode-700 tmpfs directory, identity never written to disk, counts-only report, everything deleted immediately after (checked) | closed |
| T-02-14-02 | Information Disclosure | secrets in logs through the adapter | high | mitigate | HttpClient at Warning; sentinel test over logs, responses and /metrics for token, key password, key body, session id and code | closed |
| T-02-14-03 | Elevation of Privilege | half-configured production link | medium | mitigate | Validator names every missing or invalid bank key and stops startup | closed |
| T-02-14-04 | Information Disclosure | real values in docs or env example | medium | mitigate | Placeholders only (example.com, zero GUID); repo-rules and secrets lint | closed |
| T-02-14-05 | Tampering | wrong merges on real data shapes | high | mitigate | Replay of real captured pairs through the real mapping and reconciler with invariant assertions before the first real link | closed |
| T-02-15-01 | Tampering | release to host | high | mitigate | Existing attested pull-based pipeline; the sandboxed poll unit is proven by installing v0.2.1 | closed |
| T-02-15-02 | Information Disclosure | real data in Claude's context | high | mitigate | Checks limited to counts, statuses, opaque keys and dates; the operator never pastes account lists | closed |
| T-02-15-03 | Information Disclosure | host secrets read over SSH | high | mitigate | Claude never reads /etc/ledger secrets; key generation and configuration are run by the operator | closed |
| T-02-15-04 | Elevation of Privilege | aggregator application with payment services | high | mitigate | Operator confirms account information only; the client refuses to link otherwise | closed |
| T-02-15-05 | Tampering | Grafana writing live | high | mitigate | Live INSERT as grafana_reader rejected on the host | closed |
| T-02-15-06 | Spoofing | temporary SSH access for Claude | high | accept | Kept through this phase by the operator's decision with a passphrase-protected key; removal is a hard gate before the public endpoint, tracked in the reshaped todo | closed (accepted) |
| T-02-16-01 | Repudiation | hidden incompleteness | medium | mitigate | Every snapshot keeps exact recorded drift; the persistence rule only delays the flag by one day | closed (accepted) |
| T-02-16-02 | Information Disclosure | balances in logs and metrics | medium | mitigate | Logs and metrics carry reconciled state only, never amounts | closed |

*Status: open · closed · open — below high threshold (non-blocking)*
*Severity: critical > high > medium > low — only open threats at or above workflow.security_block_on count toward threats_open*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| R-02-01 | T-02-01-SC | Two test-only packages (xunit.v3 4.0.1 and Microsoft.Extensions.TimeProvider.Testing 10.10.0) are trusted at build time. Corrected rationale: xunit.v3 is owned by its long-standing third-party maintainers, not Microsoft, and both packages ship buildTransitive targets that run at build time. The risk stays acceptable because exact versions and content hashes are pinned in committed lock files and every CI and release build restores in locked mode. | operator | 2026-10-07 |
| R-02-02 | T-02-05-04 | Fingerprint references for items without a bank reference could collide. ING supplies an entry reference on every item (100 % in the spike, unique and stable), so the fingerprint path is not used for this bank. | operator | 2026-10-07 |
| R-02-03 | T-02-06-03 | Dashboard panels render bank text. Both dashboards use table panels only (no text, HTML or markdown panels, no data links), Grafana sanitising is on, and Grafana is reachable on the home network and VPN only. | operator | 2026-10-07 |
| R-02-04 | T-02-07-08 | The anonymous callback can be hit by anyone who can reach it. It is LAN/VPN-only behind the proxy allow-list and the host firewall, rejects malformed state before any database or provider call, and a 256-bit state cannot be guessed; a well-formed unknown state costs one database update. | operator | 2026-10-07 |
| R-02-05 | T-02-08-05 | The PSU address and user agent sent to the bank come from the operator's request. They only change bank headers and metering, never access; forwarded headers are trusted from the configured proxy only, which production now requires. | operator | 2026-10-07 |
| R-02-06 | T-02-10-05 | The metrics refresher queries the database every 60 seconds. The queries are small, serialised and bounded by the number of connections and accounts. | operator | 2026-10-07 |
| R-02-07 | T-02-12-03 | The rate-limit probe deliberately exhausted the spike application's background quota. It ran once, on the spike application only (no 429 occurred), and that application has since been deleted; the server uses its own application. | operator | 2026-10-07 |
| R-02-08 | T-02-15-06 | Claude's temporary root SSH access to the ledger host stays in place after real bank data arrived. The key is passphrase-protected and agent-loaded (stripping the passphrase fails), access is restricted to the workstation address, and the hard removal point is before /mcp goes public. | operator | 2026-10-07 |
| R-02-09 | T-02-05-03 | Some bank observations are not stored as raw payload rows: a cancelled status that drops a row, a pending report for a row that is already booked, and a merge skipped because the reference is already known. The ledger's data stays correct in all three cases; only the raw audit trail of those particular observations is missing. | operator | 2026-10-07 |
| R-02-10 | T-02-16-01 | On the dated balance path, each check restarts from the previous bank amount, so a transaction the ledger never receives shows one mismatch and is never flagged by the two-mismatch rule. ING sends only undated expected balances, which use the chained expectation and do flag persistent drift, so this is latent for the current bank. | operator | 2026-10-07 |
| R-02-11 | T-02-12-02 | Confirming in the ING app that the spike consent is gone was not possible (the operator could not find the access overview). The spike consent was revoked through the provider API (HTTP 200), its session state and keys were deleted, and the spike application itself was deleted. | operator | 2026-10-07 |

*Accepted risks do not resurface in future audit runs.*

---

## Audit Notes

- **Register versus evidence:** T-02-15-03 planned that the operator would generate and configure the bank key; at the operator's explicit request the orchestrator ran `ledger-bank-key generate` and `configure` over SSH. The control that matters holds: the password is generated on the host, passed to openssl through the environment, and only the certificate and public key were printed. T-02-15-05 names a live INSERT probe for the reporting role; the recorded probe was a DELETE refused with SQLSTATE 42501. Both test the same database-enforced boundary, and the catalog-wide role test covers every privilege.
- **Budget exemption (T-02-08-01):** since the review fixes, the first sync after a link or renewal is metered but not capped by the background allowance, so the full history window is never cut off. It is bounded by a fresh bank approval, runs only for a connection without a prior run, is de-duplicated per connection and trigger, and its calls count in the next scheduled run's rolling window.
- **Attended without headers (T-02-08-01, review IN-203):** if a bank required a PSU header other than the address or user agent, a call could be classed as attended without sending headers. Not triggered by ING, which requires only the address.
- **Referrer header on the callback (T-02-07-02):** the reverse proxy's shared security-headers middleware may replace the application's no-referrer with strict-origin-when-cross-origin. Practical exposure is nil (plain text page, no links or subresources, single-use state).
- **Plain HTTP hop (review IN-201):** the hop from the reverse proxy to the application on port 5080 is plain HTTP and carries the API key and the callback state and code; the host firewall restricts that port to the proxy's address.
- **Untested control (T-02-07-07):** rejection of foreign or duplicate account keys and invalid display names exists in the service and the store but has no dedicated test.
- **Exposed backup key (T-02-02-01):** the household backup age identity was pasted into a conversation on 2026-09-30 during the spike; it was rotated before any real capture was written, and backups made with it were deleted on 2026-10-01.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-07 | 85 | 81 verified + 4 accepted after audit | 0 | gsd-security-auditor (three parallel runs: plans 01-06, 07-10, 11-16) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-07
