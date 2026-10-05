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
| next-morning, longest strategy, no PSU header, about 12 hours after authorisation | first account | 7, all HTTP 200 (per page 0, 0, 0, 0, 100, 100, 77) | 277 | 2026-07-03 | 2026-10-01 |
| next-morning, longest strategy, no PSU header, about 12 hours after authorisation | second account | 6, all HTTP 200 (per page 0, 0, 0, 0, 100, 41) | 141 | 2026-07-03 | 2026-09-29 |

Findings:

- The earliest booking date on both accounts is exactly two years before the capture date, which points to a fixed 24-month window rather than the accounts' true age.
- Continuation pagination worked: pages were followed until the continuation key ended, all with HTTP 200.
- Every transaction carries an entry reference. The transaction id field is absent on all of them, so the entry reference is the only provider-supplied identifier available for deduplication.
- Every transaction had status BOOK (booked). See "Pending transactions so far".
- Amount strings carry at most 2 decimals, none is negative (direction comes from the credit or debit indicator), currency EUR throughout.
- The "after-2h" capture could not be made two hours after authentication. It was replaced by a next-morning capture at 2026-10-01 07:52 UTC, about 12 hours after authorisation.
- **Full history is only available right after authorisation.** The next-morning longest capture reached back only to 2026-07-03 (90 days) on both accounts, against 24 months in the initial capture. ING applies the usual rule: more than 90 days of history only shortly after strong customer authentication. The ledger's post-link sync must therefore fetch the full history immediately after linking (and after each renewal if a gap ever needs filling); a later sync can never recover it.
- **Header-less calls were admitted.** All 15 next-morning calls (2 balances, 13 transaction pages) were sent without the PSU IP header and every one returned HTTP 200, with no rate-limit error. Either transaction pages are not counted one by one against ING's background quota, or the quota is higher than four per account per day. The quota probe settles which.
- **Leading empty pages.** Under the longest strategy without the header, both accounts first returned four empty pages that still carried a continuation key, then the data. If the daily window (10 days) shows the same, one scheduled sync costs several calls, which matters for the ledger's per-account call budget (it counts every page).
- A transaction booked on the capture day (2026-10-01) was already present as booked on the first account. Still no pending items.

## Balance types

ING returned only the type XPCD (expected balance) on both accounts, and none of the balances carries a reference date. Both accounts answered the balances call with HTTP 200.

The sandbox returned OTHR, ITAV and ITBD, all with reference dates, which does not reflect ING.

Impact: the balance reconciliation was built with closed-booked and interim-booked types as its defaults. Against ING it will find neither type and will report the balance as unknown. See the open questions.

## Pending transactions so far

Day-1 capture (2026-10-04 10:45 UTC, Sunday, 10-day window, no PSU header): one page per account, all HTTP 200, 47 and 16 transactions, every one booked (BOOK), entry reference on all, no transaction id, booking dates 2026-09-24 to 2026-10-03 and 2026-10-04. Still no pending item on either account. The 10-day window returned no leading empty pages: one balances call and one transaction page per account, so a normal daily sync costs two calls per account.

Operator observation for day-1: an iDEAL payment was made on Friday and a card payment (mobile wallet) on Saturday. In the ING app the Saturday card payment already shows as processed, not pending. Card payments therefore book within a day, and the API shows no pending state for them. This points to the adapter treating ING transactions as booked-only. Day-2 and day-3 on weekdays should confirm it.

Day-2 capture (2026-10-05 15:04 UTC, Monday afternoon; whether a card payment was made on Sunday is not yet confirmed): one page per account, all HTTP 200, 45 and 14 transactions, every one booked, entry reference on all, no transaction id, latest booking date 2026-10-05 on both accounts. Still no pending item.

Conclusion on pending items (weak-evidence caveat): across the initial, next-morning, day-1 and day-2 captures no pending item appeared on either account, and the operator saw a weekend card payment already processed by the next day. ING almost certainly does not expose pending items through this connection, but this was not proven with a payment that the bank app showed as pending at the moment of a capture. Zero pending-to-booked pairs were observed. The ledger's pending-to-booked reconciliation stays in place as a safety net. The day-3 capture was skipped by the operator.

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
| 2026-10-01 | next-morning | first account: balances plus 7 transaction pages, longest strategy, no PSU header | 8 |
| 2026-10-01 | next-morning | second account: balances plus 6 transaction pages, longest strategy, no PSU header | 7 |
| 2026-10-04 | day-1 | first account: balances plus 1 transaction page, 10-day window, no PSU header | 2 |
| 2026-10-04 | day-1 | second account: balances plus 1 transaction page, 10-day window, no PSU header | 2 |
| 2026-10-05 | day-2 | first account: balances plus 1 transaction page, 10-day window, no PSU header | 2 |
| 2026-10-05 | day-2 | second account: balances plus 1 transaction page, 10-day window, no PSU header | 2 |
| skipped | day-3 | operator could not capture shortly after a fresh card payment; evidence judged sufficient | 0 |

## Security notes

- The temporary SSH key used by Claude for the ledger host and the reverse proxy is now passphrase protected and loaded through ssh-agent for sessions. Stripping the passphrase (`ssh-keygen -y` with an empty passphrase) fails, as required.
- The backup age key of the operator was exposed once in a terminal during the sandbox check and was rotated the same day. The spike recipient and the server backup recipient now use the new key.

## Open questions for the spike completion

1. **Next-morning capture.** Done on 2026-10-01: history beyond 90 days is only available right after authorisation, and header-less calls are admitted (15 of 15 returned HTTP 200). See "History depth".
2. **Daily pending captures.** On at least three mornings, after a card purchase and an iDEAL payment the day before, run `capture day-N` (no longest strategy). Needed to see whether ING exposes pending items, their reference presence and booking date presence, and how pending items turn into booked ones. The initial capture had none.
3. **Rate-limit quota probe.** The real background-call quota has not been probed. The next-morning capture made 8 and 7 header-less calls per account without a rate-limit error, so either pages are not counted individually or the quota is above four. The probe must establish what counts as a call, because the ledger's call budget (default 4 per account, every page counted) would refuse a sync that ING itself admits, especially if the daily window also returns leading empty pages.
4. **Balance types and reconciliation.** ING returns only XPCD without a reference date. Decide for the adapter whether reconciliation maps XPCD to a usable balance (with a documented, weaker meaning), compares against the booked running total instead, or reports unknown for ING. This changes the reconciliation defaults and the adapter plan.
5. **Two joint accounts.** Both accounts will be synced, and transfers between them appear on both. Categorisation needs an internal-transfer rule, and deduplication must stay per account so the two sides of a transfer are not collapsed.
6. **History window.** Answered in part: outside the window right after authorisation ING returns 90 days. Whether the 24-month depth right after authorisation is a fixed window still needs no action, since the post-link sync takes whatever is offered.
7. **Consent renewal and control panel link.** Confirm that the Control Panel link persists across renewals and that a renewal also grants the full advertised validity.
8. **Deletion of old server backups.** Done on 2026-10-01. The nightly backup at 02:43 UTC succeeded under the new key. The operator then deleted the five older backups made with the exposed key. The orchestrator confirmed that only the new backup remains and that the host self-check backup lines all pass.
9. **Spike teardown.** When the spike ends, run `revoke`, delete the spike key and confirm the session is closed.
