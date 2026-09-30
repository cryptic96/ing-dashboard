# Phase 2 Spike: first half of the real-consent protocol

Structural findings only. No value from the bank (names, account numbers, amounts, descriptions, identifiers, hostnames) appears here; the raw responses exist only as age-encrypted files on the operator's workstation, outside the repository. Recorded 2026-09-30.

Status: first half done (sandbox proof, spike production application, linking observation, first authorisation, initial longest capture). Second half pending (see "Open questions for the spike completion").

## Savings account offered

**No.** The linking step of the spike production application (restricted mode) offered exactly two accounts, both current accounts (cash account type CACC), both joint household accounts. No savings account was listed, so none could be linked.

The operator notes that an ING savings account is tied to a current account as a fixed contra account. This matches the expectation that ING savings sit outside the PSD2 payment-account scope.

Consequence (the fallback agreed beforehand, not re-asked): ship with the joint current accounts only.

- Transfers to and from savings stay visible as transactions on the joint current account.
- Savings balances and interest are missing from the ledger.
- The provider-agnostic ingestion interface keeps a second provider for savings possible later; nothing in this phase depends on it.

Both linked accounts are joint accounts and both will be synced. The plan assumed one joint account plus one savings account. Transfers between the two joint accounts appear on both sides; categorisation must treat them as internal transfers.

## Consent validity

| Item | Sandbox (mock bank) | ING NL (spike production application) |
| --- | --- | --- |
| Advertised maximum consent validity | 180 days | 180 days |
| valid_until returned for the session | about +180 days | 2027-03-29 (authorised on 2026-09-30, so the full 180 days were granted) |

ING's advertised maximum is 180 days and Enable Banking granted the full 180 days on the first authorisation. The roughly 90-day real-world cap reported by community sources did not show up at session creation. Whether renewal and re-authorisation behave the same way is still to be observed in practice.

## Required PSU headers

| Bank | Required PSU headers |
| --- | --- |
| Sandbox mock bank | none |
| ING NL | `psu-ip-address` |

Both entries list the psu types business and personal. ING therefore requires the end user's IP address header on calls made with the user present. The capture made for the initial history sent it. Whether a call without it is admitted as a background (user-absent) call is still open (see below).

## History depth

| Capture | Account | Pages | Transactions | Earliest booking date | Latest booking date |
| --- | --- | --- | --- | --- | --- |
| initial, longest strategy, with PSU header, minutes after authorisation | first account | 25, all HTTP 200 (100 per page, last page 71) | 2471 | 2024-09-30 | 2026-09-30 |
| initial, longest strategy, with PSU header, minutes after authorisation | second account | 11, all HTTP 200 | 1009 | 2024-09-30 | 2026-09-29 |
| after-2h | both | not captured | not captured | not captured | not captured |

Findings:

- The earliest booking date on both accounts is exactly two years before the capture date, which points to a fixed 24-month window rather than the accounts' true age.
- Continuation pagination worked: pages were followed until the continuation key ended, all with HTTP 200.
- Every transaction carries an entry reference. The transaction id field is absent on all of them, so the entry reference is the only provider-supplied identifier available for deduplication.
- Every transaction had status BOOK (booked). See "Pending transactions so far".
- Amount strings carry at most 2 decimals, none is negative (direction comes from the credit or debit indicator), currency EUR throughout.
- The "after-2h" capture, which would show whether full history is still available more than one hour after authorisation, could not be made two hours after authentication. It is replaced by a next-morning capture (see below).

## Balance types

ING returned only the type XPCD (expected balance) on both accounts, and none of the balances carries a reference date. Both accounts answered the balances call with HTTP 200.

The sandbox returned OTHR, ITAV and ITBD, all with reference dates, which does not reflect ING.

Impact: the balance reconciliation was built with closed-booked and interim-booked types as its defaults. Against ING it will find neither type and will report the balance as unknown. See the open questions.

## Pending transactions so far

None. The initial capture held only booked transactions (status BOOK: 2471 on the first account, 1009 on the second; no pending items on either). The daily captures will show whether ING exposes pending items at all, and if so how a pending item relates to its later booked version (reference presence, booking date on pending items).

## Key format accepted

A PEM public key, pasted into the Enable Banking Control Panel, was accepted for both the sandbox application and the spike production application. A self-signed certificate was not needed.

## Redirect URL accepted

Yes. The internal-only redirect hostname (the same one the server will use for its bank callback) was accepted for both applications, with no error and no extra verification. Each application reports one registered redirect URL and the spike's redirect URL is among them.

Other application facts: the sandbox application is environment SANDBOX, active, with services AIS and PIS. The spike production application is environment PRODUCTION, active, with services AIS only (payment initiation is not enabled). The authorisation flow ran on Enable Banking's own authorisation host. The ING entry lists 36 banks for the country; the sandbox lists 3.

Session shape: both accounts have product present, uid, identification hash, account number and name present, and the uid is a UUID.

## Capture log

Labels and dates only. All captures are age-encrypted files in the operator's workstation spike directory. A check of the captures directory for files that are not age files printed nothing. The spike directory and its state directory are mode 700, the spike environment file is mode 600.

| Date (UTC) | Label | What | Encrypted files |
| --- | --- | --- | --- |
| 2026-09-30 | sandbox | session response (sandbox) | 1 |
| 2026-09-30 | sandbox | balances and transactions, longest strategy, one account | 2 |
| 2026-09-30 | initial | session response (spike production application) | 1 |
| 2026-09-30 | initial | first account: balances plus 25 transaction pages, longest strategy, with PSU header | 26 |
| 2026-09-30 | initial | second account: balances plus 11 transaction pages, longest strategy, with PSU header | 12 |
| not done | after-2h | longest capture two hours after authorisation | 0 |
| pending | next-morning | longest capture without PSU header, see below | not yet |
| pending | day-N | daily captures without the longest strategy | not yet |

## Security notes

- The temporary SSH key used by Claude for the ledger host and the reverse proxy is now passphrase protected and loaded through ssh-agent for sessions. Stripping the passphrase (`ssh-keygen -y` with an empty passphrase) fails, as required.
- The backup age key of the operator was exposed once in a terminal during the sandbox check and was rotated the same day. The spike recipient and the server backup recipient now use the new key.

## Open questions for the spike completion

1. **Next-morning capture.** The operator runs `capture next-morning --longest` without `--psu`. It answers two things: is full history still available more than one hour after authorisation, and is a call without the PSU IP header admitted as a background call. Replaces the missed after-2h capture.
2. **Daily pending captures.** On at least three mornings, after a card purchase and an iDEAL payment the day before, run `capture day-N` (no longest strategy). Needed to see whether ING exposes pending items, their reference presence and booking date presence, and how pending items turn into booked ones. The initial capture had none.
3. **Rate-limit quota probe.** The real background-call quota (commonly about four per day per account) has not been probed. The next-morning and daily captures count towards it; plan the probe so it does not trigger a limit error on the day the server first syncs.
4. **Balance types and reconciliation.** ING returns only XPCD without a reference date. Decide for the adapter whether reconciliation maps XPCD to a usable balance (with a documented, weaker meaning), compares against the booked running total instead, or reports unknown for ING. This changes the reconciliation defaults and the adapter plan.
5. **Two joint accounts.** Both accounts will be synced, and transfers between them appear on both. Categorisation needs an internal-transfer rule, and deduplication must stay per account so the two sides of a transfer are not collapsed.
6. **History window.** Confirm whether the 24-month earliest date is a fixed window (for example by comparing the next-morning capture's earliest date with the initial one) before the first server sync relies on it.
7. **Consent renewal and control panel link.** Confirm that the Control Panel link persists across renewals and that a renewal also grants the full advertised validity.
8. **Deletion of old server backups.** Existing server backups made with the previous backup key hold no bank data and are to be deleted once a backup under the new key has succeeded (operator decision pending).
9. **Spike teardown.** When the spike ends, run `revoke`, delete the spike key and confirm the session is closed.
