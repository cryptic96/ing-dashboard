---
phase: 03
slug: claude-reads-the-ledger
status: verified
# threats_open = count of OPEN threats at or above workflow.security_block_on severity (the blocking gate)
threats_open: 0
asvs_level: 2
block_on: high
created: 2026-10-09
---

# Phase 03 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| Claude client → /mcp | Bearer-token calls to the four read-only tools (Claude Code from home/VPN today; hosted clients via Anthropic's range once the hosted-client todo is done) | Household transactions, balances (high) |
| Browser → sign-in and consent pages | Password, one-time code, consent decision; home/VPN only | Credentials (critical) |
| Claude client → /connect/token, discovery | Authorization codes, PKCE verifiers, refresh and access tokens | Tokens (critical) |
| Reverse proxy → application | TLS hop pinned to the host's own certificate | Everything above (critical) |
| Operator shell → ledger-login / ledger-grants | Enrolment, password reset, revocation; secrets on stdin only | Credentials, grants (critical) |
| GitHub → host | Attested, approved releases pulled by the host | Code (high) |
| Application → logs, metrics, alert mail | Fixed-word labels, redacted logs, no financial detail in mail | Operational metadata (medium) |

---

## Threat Register

All 64 threats from the `<threat_model>` blocks of plans 03-01 to 03-08. Evidence (file:line and test names) is in the audit returns summarised under the Audit Trail.

| Threat ID | Category | Component | Severity | Disposition | Mitigation | Status |
|-----------|----------|-----------|----------|-------------|------------|--------|
| T-03-01-01 | Spoofing | /mcp bearer validation | high | mitigate | Audience pinned to the canonical resource; reference tokens; token- and grant-entry validation; MCP-scheme-only policy with ledger.read | closed |
| T-03-01-02 | Spoofing | resource parameter at authorize | high | mitigate | Canonical-match check before sign-in, invalid_target otherwise; tokens carry only the canonical resource | closed |
| T-03-01-03 | Spoofing | client identity and redirect | high | mitigate | Two pre-registered public clients reconciled at start; exact redirect URIs; PKCE required, S256 only | closed |
| T-03-01-04 | Elevation of Privilege | scheme cross-acceptance (API key vs bearer) | high | mitigate | Separate default/fallback policies; tested both directions | closed |
| T-03-01-05 | Spoofing | issuer and metadata URLs behind the proxy | medium | mitigate | All URLs derived from PublicBaseUrl only | closed |
| T-03-01-06 | Spoofing | stolen refresh token | high | mitigate | 15-min access tokens; rotating refresh with reuse revocation; deleted/locked login refused | closed |
| T-03-01-07 | Information Disclosure | ledger_overview output | high | mitigate | Projection without IBAN or provider names | closed |
| T-03-01-08 | Tampering | NuGet supply chain | high | mitigate | Pinned versions, lock files with hashes, locked-mode restore in release | closed |
| T-03-01-09 | Spoofing | password-only sign-in | medium | mitigate | Confirmed, readable second factor required; unexpected success signed out; consent re-checks 2FA | closed |
| T-03-01-10 | Elevation of Privilege | runtime role on new tables | medium | mitigate | No grants in migrations; default privileges; exact privilege-matrix test | closed |
| T-03-01-11 | Repudiation | consent decisions | low | mitigate | Grant rows with subject, client, time; ids logged | closed |
| T-03-01-12 | Information Disclosure | library logs | medium | mitigate | OpenIddict, MCP SDK, Identity pinned at Warning; log-redaction tests | closed |
| T-03-01-13 | Denial of Service | MCP sessions | low | accept | Stateless server; rate and size limits exist | closed |
| T-03-02-01 | Spoofing | sign-in credentials | high | mitigate | Password + TOTP, lockout, sign-in rate limit, uniform failure, home/VPN-only sign-in; authenticator secret encrypted at rest | closed |
| T-03-02-02 | Spoofing | one-time code replay | high | mitigate | Exactly six ASCII digits; highest accepted time step claimed atomically; confirm code claimed too | closed |
| T-03-02-03 | Tampering | CSRF, clickjacking, open redirect on sign-in/consent | high | mitigate | Antiforgery with __Host- cookie; strict CSP and frame-ancestors none; local redirects only; consent forwards an OAuth-parameter allow-list and reads the decision from one field | closed |
| T-03-02-04 | Information Disclosure | proxy rule drift exposing REST/ops/sign-in | high | mitigate | App-side host and path guard with host normalisation; proxy hop TLS with pinned certificate | closed |
| T-03-02-05 | Spoofing | forged X-Forwarded-For | high | mitigate | Forward limit 1, loopback defaults cleared, only configured proxies; KnownProxies required with OAuth | closed |
| T-03-02-06 | Elevation of Privilege | token for another audience | high | mitigate | Audience validation; wrong-audience tests | closed |
| T-03-02-07 | Elevation of Privilege | scheme cross-acceptance | high | mitigate | As T-03-01-04 | closed |
| T-03-02-08 | Information Disclosure | token passthrough | high | mitigate | No outbound HTTP in MCP/query/OAuth code; no-outbound-request test | closed |
| T-03-02-09 | Denial of Service | request floods and large bodies | medium | mitigate | Per-address, per-class limits before auth; 256 KiB body limit (413) | closed |
| T-03-02-10 | Information Disclosure | credentials and tokens in logs | high | mitigate | Warning pins; generic messages; end-to-end log-redaction test | closed |
| T-03-02-11 | Tampering | unsafe production configuration | medium | mitigate | Fail-fast validator (https base, sign-in networks, no /0, no Anthropic overlap, lifetimes, HTTPS Api endpoint and certificate) | closed |
| T-03-02-12 | Spoofing | consent phishing via an attacker's connector | medium | mitigate | Consent names client and redirect host; sign-in home/VPN only; new-grant alert | closed |
| T-03-03-01 | Tampering | filter text reaching SQL | high | mitigate | Parameterised SQL; LIKE escaping | closed |
| T-03-03-02 | Denial of Service | huge periods or groupings | medium | mitigate | Period, grouping and term caps | closed |
| T-03-03-03 | Information Disclosure | account numbers in results | high | mitigate | No IBAN in totals; display name or opaque key; free-text masking | closed |
| T-03-03-04 | Repudiation | totals that cannot be checked | medium | mitigate | Full provenance and summary sentence | closed |
| T-03-03-05 | Information Disclosure | exception messages | medium | mitigate | Constant refusal messages; other exceptions reach the client generically | closed |
| T-03-03-06 | Tampering | prompt injection through bank text (totals) | medium | accept | Read-only tools; operator decision | closed |
| T-03-03-07 | Tampering | inconsistent totals during a sync | low | mitigate | Repeatable-read, read-only snapshot per call | closed |
| T-03-04-01 | Information Disclosure | account numbers in search/counterparty results | high | mitigate | Masking in the store and in free text | closed |
| T-03-04-02 | Denial of Service | unbounded result sets | high | mitigate | Clamp 1..100, keyset paging, counterparty cap | closed |
| T-03-04-03 | Tampering | cursor replay against other filters | low | mitigate | Filter-bound, size-bounded, strictly decoded cursor | closed |
| T-03-04-04 | Tampering | filter text reaching SQL | high | mitigate | As T-03-03-01 plus term limits | closed |
| T-03-04-05 | Repudiation | Claude summing rows into wrong totals | medium | mitigate | No sums in search; instructions route totals to money_totals | closed |
| T-03-04-06 | Information Disclosure | tool arguments in logs | medium | mitigate | SDK at Warning; argument-sentinel log test | closed |
| T-03-04-07 | Tampering | prompt injection through bank text (rows) | medium | accept | Read-only tools; operator decision; usage advice in the guide | closed |
| T-03-05-01 | Elevation of Privilege | stolen or abused grant | high | mitigate | Revoke one/all; credential changes revoke the login's grants; stale sessions refused via security stamp | closed |
| T-03-05-02 | Information Disclosure | password/code on command line | high | mitigate | Hidden prompt or stdin only; argv test | closed |
| T-03-05-03 | Elevation of Privilege | CLI wrappers running as root | medium | mitigate | systemd-run as ledger with full sandbox; strict argument patterns | closed |
| T-03-05-04 | Spoofing | consent phishing or token theft unnoticed | high | mitigate | Rejected-token, new-grant and refresh-reuse metrics and alerts; promtool tests | closed |
| T-03-05-05 | Information Disclosure | metric labels and alert texts | medium | mitigate | Fixed-word labels; fixed alert text; lint forbids templating | closed |
| T-03-05-06 | Denial of Service | alert mail volume | low | mitigate | Grouping and repeat intervals | closed |
| T-03-05-07 | Repudiation | who approved a connection | low | mitigate | ledger-grants list shows grant, client, login, time | closed |
| T-03-06-01 | Information Disclosure | template drift exposing REST/Grafana/sign-in | high | mitigate | Exact-path public router; sign-in router home/VPN only; template tests incl. weakened variants; proven live from outside (403) | closed |
| T-03-06-02 | Spoofing | allowlist bypass via forwarded headers | high | mitigate | No ipStrategy; no source NAT (proven live from mobile data) | closed |
| T-03-06-03 | Elevation of Privilege | leftover temporary sudo login | high | mitigate | Selfcheck sudo-login check; flagged then 0 FAIL after removal | closed |
| T-03-06-04 | Information Disclosure | stale Anthropic ranges | medium | mitigate | Template test; network range check passed on go-live day and at audit | closed |
| T-03-06-05 | Information Disclosure | tokens/secrets in host logs | medium | mitigate | Selfcheck log scan for token shapes; clean live | closed |
| T-03-06-06 | Information Disclosure | personal hostnames/addresses in the public repo | medium | mitigate | Template address rules; gitleaks rules; scan clean (see residual note) | closed |
| T-03-06-07 | Denial of Service | public record proxied by DNS provider | low | mitigate | Documented DNS-only/no-AAAA/no-CGNAT; verified live | closed |
| T-03-07-01 | Tampering | release to host | high | mitigate | SHA-pinned hosted build, provenance attestation, approval gate, offline verification on host; certificate before start | closed |
| T-03-07-02 | Information Disclosure | login secrets reaching Claude | high | mitigate | Operator-run enrolment; Claude read key names/counts only | closed |
| T-03-07-03 | Information Disclosure | exposure before the boundary is proven | high | mitigate | MCP routers started home/VPN only; inside check before public change | closed |
| T-03-07-04 | Elevation of Privilege | temporary Claude SSH with real data | high | mitigate | Read-only use; flagged by selfcheck; removed in 03-08 | closed |
| T-03-07-05 | Information Disclosure | real figures in Claude's context | medium | mitigate | Operator answered yes/no only; records scanned clean | closed |
| T-03-08-01 | Elevation of Privilege | Claude SSH on an internet-facing host | high | mitigate | Logins, sudoers, ACLs and key removed; "Permission denied" verified; selfcheck 0 FAIL before route change | closed |
| T-03-08-02 | Information Disclosure | exposure wider than designed | high | mitigate | Outside matrix 403/404 proven live; route closed again (authorize-only-from-home log evidence deferred with hosted clients) | closed |
| T-03-08-03 | Spoofing | another claude.ai user probing from the shared range | high | mitigate | Pre-registered clients, PKCE S256, audience binding, home/VPN sign-in, access alerts (Anthropic-range traffic not yet observed — deferred) | closed |
| T-03-08-04 | Information Disclosure | stale allowlist | medium | mitigate | Range re-checked at go-live and audit | closed |
| T-03-08-05 | Denial of Service | kill switch locking the operator out | low | mitigate | Revoke-all and re-auth proven with Claude Code (UAT); hosted part deferred | closed |
| T-03-08-06 | Tampering | prompt injection once claude.ai holds other connectors | medium | accept | Read-only tools; operator decision; guide advice | closed |

*Status: open · closed · open — below high threshold (non-blocking)*
*Severity: critical > high > medium > low — only open threats at or above workflow.security_block_on count toward threats_open*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-03-01 | T-03-01-13 | Stateless MCP server keeps no per-session state; request rate and size limits bound abuse | operator (plan register) | 2026-10-07 |
| AR-03-02 | T-03-03-06, T-03-04-07, T-03-08-06 | Untrusted bank text reaches Claude unmarked; all tools are read-only and the guide advises keeping ledger conversations apart from mail and calendar connectors. Revisit when write tools arrive | operator (phase decision) | 2026-10-07 |

*Accepted risks do not resurface in future audit runs.*

---

## Deferred Evidence

Implemented, tested and partly proven live, but only fully observable once a hosted Claude client connects (tracked in `.planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md`):

- Anthropic's range reaching discovery, `/connect/token` and `/mcp` with 200 through the public router, and `/connect/authorize` appearing only from home/VPN in the proxy access log (T-03-08-02, T-03-08-03).
- Hosted-client kill-switch and reconnect behaviour, including mobile (T-03-08-05).
- `build/check-exposure.sh --from outside` run end to end (the go-live outside check used the equivalent phone-browser table).

## Non-blocking Observations

- The gitleaks private-IPv4 rule ignores `.0` network addresses (how LAN/VPN subnets are written) and does not cover CGNAT `100.64.0.0/10`, IPv6 ULA or real hostnames; a real subnet once reached an unpushed commit and was caught only by a manual scan. Hardening recommended.
- Test-coverage gaps where the mitigation exists in code: authorize without `code_challenge`; rogue-client deletion by the seeder; consent POST without antiforgery token; repeatable-read isolation (source only); the SDK's generic error for unexpected tool exceptions.
- Review info items left out of the fix scope: short non-IBAN identifiers mostly visible after masking; ledger_overview read outside a snapshot; broad sign-in ranges accepted by the validator; host guard strips only one trailing dot (defence in depth behind exact-path proxy routers).
- During the home-network proof, real transaction data passed through a Claude account owned by a work organisation; the operator was advised to keep ledger conversations in a household account.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-07 | — | — | — | Pre-release code review (03-REVIEW.md: 2 critical, 9 warning, 11 info; 15 fixed in 03-REVIEW-FIX.md; proxy hop fixed in quick 261008-0av) |
| 2026-10-09 | 46 (plans 01–05) | 46 | 0 | gsd-security-auditor, ASVS L2, static verification |
| 2026-10-09 | 18 (plans 06–08) | 18 | 0 | gsd-security-auditor, ASVS L2, logic tests, template and boundary tests, live-evidence review, range check |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-09
