---
phase: 02-automatic-ing-sync
plan: 15
subsystem: release-and-go-live
tags: [release, attestation, provisioning, enable-banking, consent, first-sync, scheduled-sync, observability]

requires:
  - phase: 02-automatic-ing-sync
    provides: "Plans 01-14 and 16: ingestion pipeline, Enable Banking adapter, reconciler, balance snapshots, status view, dashboards, alert rules, runbook, production validation"
provides:
  - "Releases v0.2.0 and v0.2.1 installed on the host through the attested pull pipeline, with eight migrations applied by the migrator role"
  - "Provisioning that carries a bumped Grafana or Prometheus pin to an existing host (Grafana 13.2.3, Prometheus 3.13.4 running)"
  - "Sandboxed deploy poll service proven by installing a real release (exposure 9.1 UNSAFE to 6.9 MEDIUM)"
  - "The server's own Enable Banking production application (restricted mode) and host key, with the app started on the EnableBanking provider"
  - "A live consent (180 days) over both joint accounts, two years of history, and a scheduled sync that ran by itself the next morning"
  - "An operator API key for ongoing REST use"
affects: [02-16, phase-03, public-mcp-endpoint]

actuals:
  tokens: 5000
  tasks: 3
  commits: 3

key-files:
  created:
    - .planning/phases/02-automatic-ing-sync/02-15-SUMMARY.md
  modified:
    - deploy/provision.d/10-packages.sh
    - deploy/tests/provision-logic-test.sh
    - .planning/phases/02-automatic-ing-sync/02-SPIKE.md

key-decisions:
  - "Tag v0.2.1 was placed on a later main commit (the provisioning fix) instead of the v0.2.0 commit, so the fix itself ships through the release path"
  - "Both joint accounts are synced with display names Joint and Joint 2; no savings account is offered by this consent"
  - "The spike conclusion that ING exposes booked transactions only is corrected: pending items are exposed and the pending-to-booked reconciliation is in real use"

requirements-completed: [INGEST-01, INGEST-02, INGEST-04, INGEST-05, OPS-01, OPS-02, SEC-01]

duration: 2 days of calendar time (2026-10-05 to 2026-10-07), dominated by waiting for approvals and the next morning's sync
completed: 2026-10-07
status: complete
---

# Phase 2 Plan 15: Go-live Summary

**The attested v0.2.1 release runs on the host, the real ING joint accounts are linked once with two years of history (3481 rows, no duplicates), and the next morning's scheduled sync ran unattended, added nothing twice, and reconciled both balances.**

## Performance

- **Calendar span:** 2026-10-05 to 2026-10-07
- **Tasks:** 3 of 3 resolved (one tracer, two operator checkpoints)
- **Evidence rule kept:** only counts, statuses, opaque keys and dates were recorded; no account names, IBANs, amounts or descriptions.

## Accomplishments

### Task 1: release candidate (tracer)

- `build/package-release.sh --version 0.2.0` at 39740bc; the manifest lists eight migrations: InitialCreate, AddApiKeys, AddLedgerIngestion, AddBankAuthorizations, AddProviderCalls, AddBalanceSnapshots, AddAccountStatusView, FlagDriftOnConsecutiveSnapshots.
- Full suite with `LEDGER_EFBUNDLE` pointing at the packaged bundle: 527 total, 526 passed, 1 skipped (the real-capture replay, which needs real data that is never in the repository).
- Dashboards check up to date; all six `build/lint.sh` checks pass. Pull request #14 opened.

### Task 2: release and host

The operator approved both deploy environments and created the Enable Banking application. At the operator's explicit request the orchestrator performed the non-gate steps (merge, tag push, provisioning, key generation and configuration, restart).

- PR #14 merged to main (177cf9f) and tag v0.2.0 pushed. The host installed 0.2.0 through the poll service and the migrator role applied the migrations.
- `provision.sh --only 10-packages` and `--only 40-services` ran clean (Grafana 13.2.3 installed, new scripts and sandboxed units installed).
- The first real version bump exposed two provisioning defects, fixed by PR #15 (da554d9): the Grafana apt pin file was only written when missing, so the host stayed pinned to 13.2.2 at priority 1001 and a later upgrade would have downgraded Grafana; Prometheus was only installed when the binary was missing, so the host stayed on 3.13.3. The pin is now rewritten when it differs, and Prometheus is reinstalled (checksum-verified) and restarted when the version differs. Five new provision-logic tests.
- Tag v0.2.1 on da554d9; the operator approved it and the host installed it through the newly sandboxed poll service, with artifact and provenance verified for commit da554d9. Re-running `10-packages` from 0.2.1 installed Prometheus 3.13.4 and set the Grafana pin to 13.2.3 (candidate 13.2.3).
- `systemd-analyze security ledger-deploy-poll.service`: 9.1 UNSAFE before, 6.9 MEDIUM after.
- `ledger-bank-key generate` printed only the certificate and public key; the key password went into the env file unprinted. The operator created the server's production application in restricted mode (account information only; placeholder privacy and terms URLs, which restricted mode does not check) and activated it by linking both joint accounts. `ledger-bank-key configure` then set the application id and the internal callback URL; after a restart the app passed production validation with the EnableBanking provider and `/health` returned 200.

Host verification:

| Check | Result |
|-------|--------|
| Installed release | 0.2.1 (current symlink and `ledger_build_info` version 0.2.1, commit da554d9) |
| `ledger-selfcheck` | 51 PASS, 0 FAIL (includes log secret scan, bank key mode, Europe/Amsterdam zone) |
| `ledger-apikey list` under the sandbox | works |
| `ledger_sync_errors_total` | present for consent_rejected, provider_auth, rate_limited, transient |
| DELETE on `public.transactions` as `grafana_reader` | refused with SQLSTATE 42501 |
| SELECT on `reporting.transactions` as `grafana_reader` | works |

### Task 3: link and observation

- 2026-10-06 about 21:12 UTC: the operator ran the link over REST from the home network and approved it in the ING app; the callback page reported 2 accounts. The first selection PUT used placeholder keys and was rejected with nothing selected. The second PUT (built from the account list, both joint accounts, display names Joint and Joint 2, sync true) returned `firstSync` queued.
- Post-link sync: succeeded in 29 s, 38 calls, 3481 rows inserted (2474 and 1007), earliest booking date 2024-10-06 on both accounts (two years), latest 2026-10-06. No reference on more than one row. Two balance snapshots of kind expected stored as the reconciliation baseline.
- Consent: one active connection, valid until 2027-04-04 (180 days). `ledger_bank_consent_state` shows linked 1 and days until expiry about 179.7.
- Pending items: 2 rows on the second account carry no booking date. ING does expose pending items, so the spike's booked-only conclusion is corrected in the spike notes.
- 2026-10-07 06:30:16 Amsterdam: the scheduled sync succeeded with no operator action. It made 4 calls (2 per account) and inserted 0 rows, because there was no new bank activity overnight. Row counts are unchanged (no doubling), still 0 references on more than one row and 0 rows with two references. `ledger_sync_last_success_timestamp_seconds` moved forward for both accounts, balance snapshots for 2026-10-07 are reconciled on both accounts, and the drift metric is 0. The 2 pending rows are still pending (not yet booked by the bank), so the pending-to-booked path remains unobserved live.
- Claude's temporary SSH access remains scheduled for removal before the public MCP endpoint goes live.

## Deviations from Plan

**1. [Process] Stuck release run re-run**
- **Found during:** Task 2, first release run for v0.2.0
- **Issue:** The publish job stuck in "queued" with no pending review: the deployment status went waiting to queued within 2 s and no approval was recorded.
- **Fix:** The orchestrator cancelled and re-ran the run; it then waited correctly and the operator approved it. Nothing was published by the stuck attempt.
- **Follow-up:** worth watching on later releases; the cause was not established.

**2. [Rule 1 - Bug] Provisioning did not carry a bumped pin to an existing host**
- **Found during:** Task 2, first real version bump
- **Issue:** Grafana pin file written only when missing; Prometheus installed only when the binary was missing.
- **Fix:** PR #15 (eb59e1d, merged as da554d9), with five new provision-logic tests.
- **Files modified:** `deploy/provision.d/10-packages.sh`, `deploy/tests/provision-logic-test.sh`

**3. [Plan change] v0.2.1 on a later commit**
- The plan said to push v0.2.1 on the same main commit as v0.2.0. It was tagged on da554d9 so the provisioning fix ships through the release path. The sandboxed-poll proof is unaffected: v0.2.1 was still installed by the newly sandboxed unit.

**4. [Process] Orchestrator performed the non-gate operator steps**
- At the operator's explicit request the orchestrator ran the merge, tag pushes, provisioning, key generation and configuration. The operator kept the gates that only they can pass: approving both deploy environments, creating and activating the Enable Banking application, and approving the consent in the ING app. No secret was read or printed.

**5. [Finding] No active operator API key existed**
- Only a revoked go-live test key was present. The operator created `operator` with `ledger-apikey` and stored it in the password manager. Pasting it into `read -rs` first picked up a different password-manager secret; the operator resolved that.

**6. [Finding] ING exposes pending transactions**
- Two pending rows appeared on the link day. The spike notes now correct the booked-only conclusion.

**7. [Plan variance] No savings account**
- The consent exposed the two joint accounts only, so live acceptance covers those, as the plan's flagged assumption allowed.

**8. [Branch hygiene]**
- GitHub deletes merged head branches, so the milestone branch was re-pushed after PR #14 merged.

## Pending human checks (end of phase, not yet done by the operator)

- Both partners sign in with their own Viewer login and open the English and Dutch bank-sync dashboards (covers the dashboard requirements DASH-05 and DASH-07).
- Grafana test notification from the operator-email contact point arrives without financial detail, and the eight household rules show Normal.
- ING app consents overview shows the old spike access is gone.

## Flagged assumptions carried forward

- Consent renewal and the 14-day, 7-day and expiry alerts cannot be observed live until the consent ages; they rest on the automated tests and the loaded rules.
- Pending-to-booked reconciliation on real data is still to be observed once the bank books the two pending rows.

## Known Stubs

None.

## Threat Flags

None. The attestation-gated release, the sandboxed poll unit, the restricted-mode application, the read-only Grafana role and the counts-only evidence rule behaved as the threat register required.

## Self-Check: PASSED

The commits cited (39740bc, 177cf9f, eb59e1d, da554d9, 8bafc96) exist in the milestone branch history.
