# Phase 3: Claude Reads the Ledger - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-07
**Phase:** 03-claude-reads-the-ledger
**Areas discussed:** Who signs in, and from where; How local clients connect; What a total counts; Tool set and answer shape

---

## Todos cross-referenced

| Option | Description | Selected |
|--------|-------------|----------|
| Remove Claude SSH access | Major; set to happen in this phase by Phase 2 D-04 | ✓ |
| Confirm first consent renewal | Major; only possible around March 2027 | |
| Confirm pending books as one row | Minor; ingestion verification | |
| Confirm first real alert email | Minor; monitoring verification | |

**User's choice:** Remove Claude SSH access only.

---

## Who signs in, and from where

### Who should be able to connect Claude to the ledger?

| Option | Description | Selected |
|--------|-------------|----------|
| Both partners, own logins | Each connects their own Claude account; per-person audit and revocation | |
| Only the operator | One login; partner uses Grafana | ✓ |
| One shared household login | Same credentials for both | |

**User's choice:** Free text: "I'm the only one with a Claude account. My partner has ChatGPT however. So would that be a problem? If so, keep only me."
**Notes:** Claude explained three things. ChatGPT calls from OpenAI's IP ranges, which aren't allowlisted. It would make OpenAI a second AI provider receiving transactions, which isn't an accepted data flow. And it needs separate connector testing. Result: operator only; partner access through ChatGPT deferred, with the login model left open to a second user.

### Where should the OAuth sign-in page be reachable?

| Option | Description | Selected |
|--------|-------------|----------|
| Home/VPN only | Login page LAN+VPN only; token/MCP calls open to Anthropic's ranges | ✓ |
| Public, with MFA | Sign in from anywhere; a login form on the internet | |

### How long before Claude must sign in again?

| Option | Description | Selected |
|--------|-------------|----------|
| Rarely: ~90 days sliding | Rotating refresh tokens, short access tokens, reuse detection | ✓ |
| Monthly hard limit | Fixed 30-day ceiling | |
| Weekly | Tightest, most friction | |

### How should you prove it's you on the sign-in page?

| Option | Description | Selected |
|--------|-------------|----------|
| Password + second factor | TOTP or passkey, whichever the server supports natively | ✓ |
| Passkey only | Phishing-resistant, needs a backup passkey | |
| Password only | Relies on the home/VPN gate | |

---

## How local clients connect

### How should Claude Code authenticate to /mcp?

| Option | Description | Selected |
|--------|-------------|----------|
| Same OAuth sign-in | One way in, one token check, one revoke point | ✓ |
| Named API key header | Reuses the REST keys; a static secret in Claude Code's config | |

### Which hostname serves /mcp?

| Option | Description | Selected |
|--------|-------------|----------|
| One dedicated MCP hostname | Only /mcp + OAuth; one issuer and audience; internal API name stays private | |
| Path on the existing API hostname | Fewer DNS names; a wrong router rule could expose /api | |
| Separate public and internal names | Two issuers and audiences to validate | |

**User's choice:** Free text. The existing dashboard and API names already have public DNS records (needed for certificates) and should be restricted from outside; could `/mcp` go on the existing name? The user asked for advice.
**Notes:** Claude confirmed the existing names are restricted: Traefik's allowlist answers outside requests with 403. Claude explained that reusing the API name works with exact-path routers, an unchanged `/api/` allowlist and an outside-in test. A dedicated name separates at the hostname level for one extra DNS record. Claude also flagged that the record must be DNS-only, not proxied, or the IP allowlist breaks. Follow-up:

| Option | Description | Selected |
|--------|-------------|----------|
| New dedicated name | One extra DNS-only record; only /mcp + OAuth behind it | ✓ |
| Reuse ledger-api | Second exact-path router; /api stays LAN/VPN only | |

### Kill switch and visibility

| Option | Description | Selected |
|--------|-------------|----------|
| Revoke CLI + metrics/alert | Revoke-all command, MCP call and rejected-token metrics, operator alert on bursts | ✓ |
| Revoke CLI only | No new metrics or alert | |
| Disable the router manually | Tokens stay valid; needs proxy access | |

---

## What a total counts

### Before categories exist, how is "groceries in August" answered?

| Option | Description | Selected |
|--------|-------------|----------|
| Merchant filter + breakdown | Claude supplies merchants; total with per-counterparty breakdown and count | ✓ |
| Period/account/merchant only | Decline category-style questions until categories exist | |

### How do totals treat pending items?

| Option | Description | Selected |
|--------|-------------|----------|
| Booked only, pending listed | Pending reported separately next to the total | ✓ |
| Include pending | Closer to now; totals can shift | |
| Booked only, ignore pending | Pending invisible until booked | |

### Transfers between the two synced joint accounts?

| Option | Description | Selected |
|--------|-------------|----------|
| Exclude synced-to-synced now | Exact own-IBAN match, reported separately; other own-account cases wait for categorisation | ✓ |
| Count everything, with a caveat | Spending totals inflated until categorisation | |

### Which date decides the period?

| Option | Description | Selected |
|--------|-------------|----------|
| Booking date | Statement date, never shifted through UTC; relative periods from Amsterdam's current date | ✓ |
| Transaction date when present | Closer to the moment of payment; may mix date kinds | |

### Refunds and returns?

| Option | Description | Selected |
|--------|-------------|----------|
| Out, in and net, all three | Claude picks and says which it used | ✓ |
| Net only | Broad filters could net salary against spending | |
| Outflows only | Overstates spending where there are returns | |

**Notes:** Claude stated the account-scope default (all synced accounts combined, per-account split on request), and the user accepted it.

---

## Tool set and answer shape

### Which starting tool set?

| Option | Description | Selected |
|--------|-------------|----------|
| Four core tools | Overview, search, totals, find counterparties | |
| Core four + period comparison | Comparison is formally next phase's | |
| Two tools: search + totals | Minimal | |

**User's choice:** "I don't know, up to you." Recorded as Claude's discretion, defaulting to the four core tools.

### How much of a counterparty IBAN should Claude see?

| Option | Description | Selected |
|--------|-------------|----------|
| Masked: last 4 only | Full IBANs never leave the server | ✓ |
| Full counterparty IBAN | Exact matching; every IBAN goes to Anthropic | |
| None at all | Same-name payees can't be told apart | |

### Mark bank text as untrusted now?

| Option | Description | Selected |
|--------|-------------|----------|
| Mark it now | Labelled fields plus an instruction line; the envelope is reused later | |
| Wait for the write tools | Smaller phase; read-only tools can't change the ledger | |

**User's choice:** Free text: "Is marking the right way? Shouldn't we then just wait for the write tools? Advise please."
**Notes:** Claude advised as follows. Marking is a partial defence; the structural defences (explicit parameters, confirmations, rate limits, an injection test) come with the write tools. A light version now (labelled fields, invisible-character stripping, length caps, one instruction line) is cheap and covers injected text steering Claude towards other connectors. Follow-up:

| Option | Description | Selected |
|--------|-------------|----------|
| Light version now | Labelled fields, stripping, caps, one instruction line | |
| Wait entirely | Everything arrives with the write tools | ✓ |

### Page size, cap and output format

**User's choice:** "That's fine, done." About 50 rows per page, a hard maximum, a truncation notice and the full provenance block, all left to the planner as defaults.

---

## Claude's Discretion

- The exact tool set (four core tools by default), names and parameters
- Page size, hard cap, truncation notice wording, and the provenance block format
- Amount representation and sign convention within out/in/net
- MCP server instructions text, stateless vs stateful HTTP mode, SDK patch version
- Revoke CLI shape, metric names, alert thresholds, any visibility of requests blocked at Traefik
- Hostname label; whether the authorization server shares it
- How Anthropic's IP ranges are kept current in the template and docs

## Deferred Ideas

- Partner access through ChatGPT's MCP connectors
- A period-comparison tool (Phase 4, PLAN-09)
- Untrusted-data marking, including the light version (Phase 4, ADV-07)
- General own-account transfer detection (Phase 4, CAT-02)
