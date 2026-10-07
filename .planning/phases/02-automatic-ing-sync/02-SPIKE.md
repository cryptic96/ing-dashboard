# Phase 2 Spike: real-consent protocol, findings and decisions

Structural findings only. No value from the bank (names, account numbers, amounts, descriptions, identifiers, hostnames) appears here; the raw responses exist only as age-encrypted files on the operator's workstation, outside the repository. First half recorded 2026-09-30, second half and decisions recorded 2026-10-05.

Status: complete. Sandbox proof, spike production application, linking observation, first authorisation, initial longest capture, next-morning capture, two daily captures, quota probe, pending-to-booked analysis and session revocation are done. The operator chose the values the adapter applies (see "Decisions"). Two cleanup steps remain with the operator (see "Spike cleanup").

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

**Confirmed.** ING advertised 180 days and the spike session was granted the full 180 days. The roughly 90-day real-world cap reported by community sources did not show up at session creation. The consent expiry window the ledger alerts on needs no change. Whether a renewal also grants the full advertised validity has not been observed and is left to the first real renewal.

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

## Pending to booked (analyze)

The operator ran `analyze` on 2026-10-05 with the identity at the hidden prompt. It used 53 transaction files and skipped 1 sandbox file. 4 of 4 snapshots per account were complete, from the initial, next-morning, day-1 and day-2 captures.

| Measure | First account | Second account | Total |
| --- | --- | --- | --- |
| Distinct booked items | 2491 | 1016 | 3507 |
| Distinct pending items | 0 | 0 | 0 |
| Pending-to-booked pairs (any kind) | 0 | 0 | 0 |
| Booked items first seen after the first snapshot, with no earlier pending | 20 | 7 | 27 |
| Identical same-day booked groups (items) | 25 (50) | 12 (25) | 37 (75) |
| Booked reference repeated within one snapshot | 0 | 0 | 0 |
| Entry reference present on booked | 100% | 100% | 100% |
| Transaction id present | 0 | 0 | 0 |

Findings:

- **ING exposes booked transactions only.** Not one pending item appeared in any snapshot, and 27 new transactions arrived directly as booked with no pending stage. Together with the operator's observation that card payments are processed within a day, the adapter can treat ING as booked-only. The pending-to-booked reconciliation stays as a safety net.
- **The entry reference is unique and stable across fetches.** The distinct booked count equals the initial count plus the later new items exactly (first account 2471 + 20 = 2491, second 1009 + 7 = 1016). Re-fetching the same transactions in later snapshots never produced a new reference. No reference repeats within a snapshot. Deduplication by entry reference per account is sound.
- **Fingerprints would collide.** 37 groups (75 items) are identical on amount, day and counterparty. A fingerprint built from those fields would wrongly merge real, distinct payments, so the entry reference must stay the primary identifier, and the fingerprint fallback must only apply when no reference is present (which ING never does).
- `bank_transaction_code` change at booking and transaction-id stability are not applicable: there are no pairs and no transaction ids.

## Rate limit

Quota probe on 2026-10-05, first account only, run by the orchestrator at the operator's request. Probe 1 ran from 15:07:40 to 15:07:48 UTC with a maximum of 12 calls. Probe 2 ran from 15:07:54 to 15:08:13 UTC with a maximum of 30 calls. Each call was a header-less single-page transactions fetch with date_from today. That day the account had already received two header-less calls from the day-2 capture at 15:04 UTC. All 42 probe calls returned HTTP 200 and none was rejected, so 44 header-less calls reached the account that day with no rate-limit error.

Findings:

- Neither ING nor Enable Banking enforces a background-call limit of about four per account per day for this restricted application, at least not for transaction fetches and not within about 30 seconds. Whether a limit exists further up (per day above 44, or on balances or details) was not probed.
- The ledger's call budget (default 4 background calls per account per rolling 24 hours, every page counted) is therefore far stricter than the observed bank behaviour. A daily sync costs 2 calls per account, so the default still allows the scheduled sync plus its single retry. It mainly constrains sync-now without PSU headers, which the ledger avoids anyway by sending PSU headers on operator-triggered syncs.
- The after-reset capture lost its purpose because no rejection occurred, so it is not needed.

## Pending transactions so far

Day-1 capture (2026-10-04 10:45 UTC, Sunday, 10-day window, no PSU header): one page per account, all HTTP 200, 47 and 16 transactions, every one booked (BOOK), entry reference on all, no transaction id, booking dates 2026-09-24 to 2026-10-03 and 2026-10-04. Still no pending item on either account. The 10-day window returned no leading empty pages: one balances call and one transaction page per account, so a normal daily sync costs two calls per account.

Operator observation for day-1: an iDEAL payment was made on Friday and a card payment (mobile wallet) on Saturday. In the ING app the Saturday card payment already shows as processed, not pending. Card payments therefore book within a day, and the API shows no pending state for them. This points to the adapter treating ING transactions as booked-only. Day-2 and day-3 on weekdays should confirm it.

Day-2 capture (2026-10-05 15:04 UTC, Monday afternoon; whether a card payment was made on Sunday is not yet confirmed): one page per account, all HTTP 200, 45 and 14 transactions, every one booked, entry reference on all, no transaction id, latest booking date 2026-10-05 on both accounts. Still no pending item.

Conclusion on pending items (weak-evidence caveat): across the initial, next-morning, day-1 and day-2 captures no pending item appeared on either account, and the operator saw a weekend card payment already processed by the next day. ING almost certainly does not expose pending items through this connection, but this was not proven with a payment that the bank app showed as pending at the moment of a capture. Zero pending-to-booked pairs were observed. The ledger's pending-to-booked reconciliation stays in place as a safety net. The day-3 capture was skipped by the operator.

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

## Decision table

Each row of the research's spike outcome table, with what was observed and the branch taken.

| Spike outcome | Observed | Branch taken |
| --- | --- | --- |
| Savings account absent | Observed. Only two joint current accounts were offered. | Ship with the joint current accounts only. Keep the cash account type column and the generic dashboard; no savings balance snapshots exist. Savings need a manual or non-PSD2 route later. |
| Bank sends no entry reference | Not observed. Entry reference is on 100% of booked items, unique and stable across fetches. | Entry reference stays the primary identifier. The fingerprint fallback with occurrence index stays only for items that lack a reference, which ING never does. |
| Stable entry reference from pending to booked | Not observable. No pending item ever appeared, so no pending-to-booked pair exists. | The reference-based step does all the work. The match-window reconciliation stays as a safety net for banks that change references. |
| Bank returns no pending transactions | Observed, with a weak-evidence caveat (no payment shown as pending by the bank app at the moment of a capture). 27 new items arrived directly as booked. | Pending support (schema, dashboard marker, reconciler) is kept but is exercised only by synthetic tests. Treat ING as booked-only. |
| Full history is only 90 days after about one hour | Observed. 24 months right after authorisation, 90 days about 12 hours later. | Confirms the sync-immediately design. The post-link sync is never skipped or deferred. A missed window means renewing again to obtain the full history. |
| Quota counts every page | Not observed. 44 header-less calls on one account in one day, all HTTP 200, no 429. | The call budget default is raised (see "Decisions"). Operator-triggered syncs still send PSU headers. A daily sync costs 2 calls per account. |
| Quota is a calendar day rather than a rolling 24 hours | Not observed. No limit was hit, so the reset behaviour is unknown. | Keep the rolling 24 hours window. |
| Maximum consent validity about 90 days | Not observed. ING advertised 180 days and the full 180 days were granted. | The expiring window of 14 days still fits and the alert cadence is unchanged. |
| Balance types include the closed-booked or interim-booked kinds | Not observed. ING returns only the expected balance (XPCD), with no reference date. | Reconcile on the expected balance with a fetch-time reference date and a two-snapshot drift rule (see "Decisions", item 4). |

## Decisions

The operator chose the measured values with specific choices. The adapter plan applies exactly these.

1. **Background calls per day.** `Ingestion:BackgroundCallsPerDay` = 12 (was 4). No limit was observed up to 44 calls in a day; a daily sync costs 2 calls per account; 12 leaves headroom while staying well under the observed behaviour.
2. **Quota window.** `Ingestion:QuotaWindow` = Rolling24Hours, unchanged. Reset behaviour is unobserved because no limit was hit.
3. **PSU headers on operator syncs.** `Ingestion:PsuHeadersOnOperatorSyncs` = true, unchanged. ING lists `psu-ip-address` as required.
4. **Balance reconciliation for ING (required code change, not only a config value).** Reconcile on the expected balance (XPCD), dated at fetch time because ING sends no reference date, and flag drift only when it persists across two consecutive daily snapshots, so a card payment still in progress cannot raise a false alarm. The current reconciler returns unknown without a reference date and has no persistence rule. The adapter plan (or a small reconciler change within it) must therefore add: an XPCD balance kind mapping, a fetch-time reference date for providers that send none, and the two-consecutive-snapshots drift rule.
5. **Match window.** `Ingestion:MatchWindowDays` = 5 and the pending-to-booked reconciliation stay as a safety net, even though ING sends no pending items.
6. **Deduplication.** The entry reference stays the primary identifier. The fingerprint fallback applies only when a reference is absent, which ING never does (identical same-day payments would otherwise collide).
7. **Post-link and post-renewal sync.** Run immediately after linking, with PSU headers and the longest strategy. Full history is only available right after authorisation; afterwards ING returns 90 days. The adapter must never skip or defer this sync.
8. **Savings.** Joint accounts only. Recorded, not re-asked.

Unchanged and not re-decided: `Ingestion:ReconcileBalanceKinds` is superseded for ING by item 4; `Ingestion:OverlapDays` keeps its default.

## Replay against the real adapter

Run on 2026-10-05, split between the operator and the orchestrator. The orchestrator created a mode 700 directory on the in-memory filesystem. The operator decrypted the 53 non-sandbox transaction pages into it with the age identity at a hidden prompt, then unset the identity. The orchestrator never saw the identity or any decrypted content. The orchestrator ran the replay test (category Replay) through the real Enable Banking mapping and the real reconciler, and displayed only the test summary and a counts-only report line.

Test run: 8 passed, 0 failed, 0 skipped.

Counts (the report line, validated against a label=number pattern before display):

| Measure | Value |
| --- | --- |
| Accounts | 2 |
| Captures | 8 |
| Pages | 53 |
| Items | 4020 |
| Rows after reconciliation | 3507 |
| Inserted | 3507 |
| Merged | 0 |
| Upgraded | 0 |
| Flagged | 0 |
| Dropped | 0 |
| Restored | 0 |
| Live pending | 0 |
| Live booked | 3507 |
| Rows with two references | 0 |
| References on more than one row | 0 |
| Unreadable pages | 0 |
| Incomplete captures | 0 |
| Excluded sandbox pages | 0 |
| Changes when re-applying every snapshot | 0 |

Interpretation: all invariants held.

- The row count of 3507 equals the distinct booked count from the analyze step exactly.
- Every row is booked and every item was inserted once. Nothing was merged, upgraded, flagged, dropped or restored, which matches ING exposing booked transactions only.
- No row carries two references and no reference sits on more than one row, so deduplication by entry reference is sound on real data.
- Re-applying every snapshot changed nothing, so the reconciliation is idempotent.
- No page was unreadable and no capture was incomplete. The sandbox pages were excluded by the operator before decryption, so the excluded count is 0.

No reconciler fix is needed before the real link.

## Spike cleanup

- Session revocation: done on 2026-10-05. The revoke call returned HTTP 200 and the session state file was deleted (`test ! -e` on the state file passes).
- Plaintext captures: none. A check of the captures directory for files that are not age files printed nothing.
- All real spike data, keys and captures deleted on 2026-10-05 (verified). After the replay the operator removed the decrypted replay directory, the replay report and run log, and the whole spike directory in the home directory. That removed the spike and sandbox private keys and every encrypted capture. The orchestrator verified that the spike directory and the replay directory no longer exist.
- ING app check that the aggregator's access is gone from the consents overview: pending (operator).

## Open questions for the spike completion

1. **Next-morning capture.** Answered on 2026-10-01: history beyond 90 days is only available right after authorisation, and header-less calls are admitted (15 of 15 returned HTTP 200). See "History depth".
2. **Daily pending captures.** Answered with a weak-evidence caveat: two daily captures (day-1, day-2) plus the initial and next-morning captures showed no pending item; day-3 was skipped. See "Pending transactions so far".
3. **Rate-limit quota probe.** Answered: 44 header-less calls on one account in one day, no rejection. See "Rate limit". Reset behaviour stays unobserved.
4. **Balance types and reconciliation.** Answered: reconcile on the expected balance with a fetch-time date and a two-snapshot drift rule. See "Decisions", item 4.
5. **Two joint accounts.** Open for the adapter and categorisation work: both accounts are synced, transfers between them appear on both sides, categorisation needs an internal-transfer rule, and deduplication stays per account so the two sides of a transfer are not collapsed.
6. **History window.** Answered: 24 months right after authorisation, 90 days afterwards. The post-link sync takes whatever is offered.
7. **Consent renewal and control panel link.** Open: confirm at the first real renewal that the Control Panel link persists and that a renewal also grants the full advertised validity. The initial 180 days are confirmed.
8. **Deletion of old server backups.** Done on 2026-10-01. The nightly backup at 02:43 UTC succeeded under the new key. The operator then deleted the five older backups made with the exposed key. The orchestrator confirmed that only the new backup remains and that the host self-check backup lines all pass.
9. **Spike teardown.** Mostly done: session revoked and state deleted on 2026-10-05, and all real spike data, keys and captures deleted the same day after the replay. Only the ING app check is pending (operator). See "Spike cleanup".

## Live observation after go-live (2026-10-06)

The server's first post-link sync (with PSU headers, longest strategy) succeeded in 29 seconds with 38 calls and inserted 3481 transactions: 2474 and 1007 on the two joint accounts, both reaching back to 2024-10-06 (two years). No reference sits on two rows. Two balance snapshots of kind expected were stored as the reconciliation baseline.

**Correction to the booked-only conclusion:** the second account returned 2 pending transactions (no booking date, first seen on the link day). ING does expose pending items; the spike simply never caught one. The pending-to-booked reconciliation, kept as a safety net, is therefore in real use. The next scheduled sync shows how these two pending rows turn into booked ones (same reference, changed reference, or certain match).
