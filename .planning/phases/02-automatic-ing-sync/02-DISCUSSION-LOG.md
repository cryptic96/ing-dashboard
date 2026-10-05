# Phase 2: Automatic ING Sync - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-09-29
**Phase:** 02-automatic-ing-sync
**Areas discussed:** Spike & fallback, Linking & renewal, Sync & pending, Dashboard & alerts

---

## Pending todos

| Option | Description | Selected |
|--------|-------------|----------|
| Remove Claude SSH access | [security, major] Remove the temporary sudo login and proxy login before real bank data | ✓ |
| Harden units, scan logs | [security, minor] Sandbox the deploy-poll unit and apikey CLI; selfcheck scans logs for secrets | ✓ |
| xunit v4 test platform | [testing, minor] Migrate tests to Microsoft.Testing.Platform ("after go-live") | ✓ |

**User's choice:** All three folded.
**Notes:** The SSH todo was later reshaped (see Spike & fallback).

---

## Spike & fallback

### Where does real spike data live?

| Option | Description | Selected |
|--------|-------------|----------|
| Workstation, encrypted | Spike from the workstation; payloads in an age-encrypted folder outside the repo, deleted after fixtures are written | ✓ |
| LXC only | Real data never leaves the host; every iteration needs a release or the SSH access being removed | |
| Workstation, plaintext | Git-ignored scratch folder, unencrypted | |

**User's choice:** Workstation, encrypted.

### When is the temporary Claude SSH access removed?

| Option | Description | Selected |
|--------|-------------|----------|
| Before first real link on LXC | Keep helping with non-data bring-up, remove right before production consent | |
| Now, before any Phase 2 work | Cleanest; slower bring-up | |
| After live acceptance | Root access while real data is present | |

**User's choice (free text):** "Claude is going to use the MCP to read the data anyway in the end. I'd like to keep the SSH access for now to help me speed up development on the LXC."
**Notes:** Claude pointed out that MCP read access and root SSH aren't equivalent: SSH reaches the env file, the Data Protection certificate and the aggregator private key, and the key file is unencrypted on the workstation. Claude proposed two mitigations: (1) a passphrase on the key plus `ssh-agent`; (2) a hard removal point before `/mcp` goes public in Phase 3, or alternatively at milestone end. User: "both sound good". Claude recorded the Phase 3 point it had recommended and said so.

### Fallback if the savings account isn't covered

| Option | Description | Selected |
|--------|-------------|----------|
| Joint only, savings later | Ship with the joint account; savings behind the provider interface later | ✓ |
| Salt Edge for savings | Second provider for savings; two consents and expiry clocks | |
| Stop and decide then | Pause the phase and decide with spike results | |

**User's choice:** Joint only, savings later.

### Does the spike gate the rest of the phase?

| Option | Description | Selected |
|--------|-------------|----------|
| Run in parallel | Everything provider-agnostic is built against the synthetic provider; only the adapter waits | ✓ |
| Spike gates everything | Nothing starts until the spike answers | |

**User's choice:** Run in parallel.

---

## Linking & renewal

### How is a link or renewal started?

First presentation: CLI on the LXC / REST call from workstation / Both.
**User's response (free text):** "How many times do you expect this to happen?"
**Notes:** Claude answered: the first link once; renewals about every 90 days in practice (~4 a year, 8 with a second login); re-links out of the blue are rare. Claude then re-asked:

| Option | Description | Selected |
|--------|-------------|----------|
| REST call only | Required endpoints anyway; `.http` file / curl with the operator API key | ✓ |
| CLI on the LXC | `ledger-bank link/renew` over SSH | |

**User's choice:** REST call only.

### Does one ING login see all accounts?

| Option | Description | Selected |
|--------|-------------|----------|
| Yes, one login sees all | One consent, one expiry clock | |
| No, savings are split | Second consent needed | |
| Not sure | Spike will tell | |

**User's choice (free text):** "My login in the app shows the joint account and the savings account. I don't see personal accounts from my partner."

### Which returned accounts are synced?

| Option | Description | Selected |
|--------|-------------|----------|
| Choose at link time | Pick accounts and display names; unselected accounts never fetched | ✓ |
| IBAN allowlist in env file | Fixed config; edit and restart to add | |
| Sync everything | Personal accounts could land in the ledger | |

**User's choice:** Choose at link time.

### Callback reachable from home/VPN only?

| Option | Description | Selected |
|--------|-------------|----------|
| Yes, home or VPN only | Nothing new internet-facing | ✓ |
| No, needs public callback | New public route through Traefik | |

**User's choice:** Yes, home or VPN only.

---

## Sync & pending

### Daily rhythm

| Option | Description | Selected |
|--------|-------------|----------|
| Morning + one retry | One early sync, one retry on transient failure, no same-day retry on rate limit | ✓ |
| Morning + evening | Two scheduled syncs | |
| Once, no retry | One sync, failures wait a day | |

**User's choice:** Morning + one retry.

### Sync outside the schedule

| Option | Description | Selected |
|--------|-------------|----------|
| Auto after link + REST trigger | First sync after link; quota-guarded REST "sync now" | ✓ |
| Auto after link only | No manual trigger | |
| Schedule only | First data at next scheduled run | |

**User's choice:** Auto after link + REST trigger.

### Pending transactions

| Option | Description | Selected |
|--------|-------------|----------|
| Shown, marked pending | Stored and shown with a marker; become booked in place | ✓ |
| Stored, hidden until booked | Invisible until booked | |

**User's choice:** Shown, marked pending.

### Uncertain pending-to-booked match / vanished pending

| Option | Description | Selected |
|--------|-------------|----------|
| Merge only when certain | Unique match merges; ambiguous flagged; vanished pending marked dropped | ✓ |
| Best-guess merge | Always merge with the closest candidate | |

**User's choice:** Merge only when certain.

### Balances

| Option | Description | Selected |
|--------|-------------|----------|
| Yes, once per day | Daily snapshot on the first successful sync; reconcile to the cent | ✓ |
| Yes, every run | Freshest, can exhaust quota | |
| No balances | Manual reconciliation | |

**User's choice:** Yes, once per day.

---

## Dashboard & alerts

### EN/NL dashboard generator

| Option | Description | Selected |
|--------|-------------|----------|
| C# tool in the solution | Typed panel model + translation file; one toolchain | ✓ |
| Foundation SDK (Go/Python) | Grafana's official builders; second toolchain | |
| JSON template + script | Raw JSON with placeholders | |

**User's choice:** C# tool in the solution.

### First dashboard layout

| Option | Description | Selected |
|--------|-------------|----------|
| One dashboard, status on top | Per-account status row above recent transactions | ✓ |
| Two dashboards | Separate sync-health and transactions dashboards | |

**User's choice:** One dashboard, status on top.

### Alert recipients

| Option | Description | Selected |
|--------|-------------|----------|
| Operator only | Only the operator can act on them | ✓ |
| Both partners | Partner informed but can't act | |
| Consent to both, sync to you | Partner gets consent reminders only | |

**User's choice:** Operator only.

### When sync problems alert

| Option | Description | Selected |
|--------|-------------|----------|
| After the retry, auth at once | Transient after retry or ~26h without success; auth and rate-limit immediately | ✓ |
| On every failed attempt | Every failure mails | |

**User's choice:** After the retry, auth at once.

---

## Claude's Discretion

- Raw payload retention and storage shape
- Enable Banking private key custody (default: `/etc/ledger`, a password-manager copy, excluded from backups; the spike's workstation copy deleted), and whether the spike and production share one Enable Banking application
- Exact sync time, retry delay, pending-match window
- Metric names/labels (opaque account key, never an IBAN)
- Whether reconciliation drift also alerts (default: yes, operator)
- Alert wording and rule grouping
- Provider interface and synthetic fixture design
- How "no payment path" is proven

## Deferred Ideas

- Savings via a second provider (only if the spike shows no coverage)
- CLI or web page for linking/renewal
- Public consent callback (rejected)
- Removal of Claude SSH access, moved to before `/mcp` goes public (Phase 3)
