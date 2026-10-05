---
phase: 02-automatic-ing-sync
plan: 02
subsystem: infra
tags: [enable-banking, spike, ing, age, psd2, consent]
requires: []
provides:
  - "Workstation spike tool outside the repository that captures encrypted and prints structure only"
  - "02-SPIKE.md: structural findings of the first spike half and the open questions for the spike completion"
  - "Confirmed fallback: no savings account is offered for ING NL, ship with the joint current accounts only"
affects: [enable-banking-adapter, balance-reconciliation, categorisation, spike-completion, go-live]
tech-stack:
  added: []
  patterns:
    - "Raw bank responses go from curl straight into age with a public recipient; no plaintext file exists"
    - "Only structure, counts, dates and enum codes cross into the conversation"
key-files:
  created:
    - .planning/phases/02-automatic-ing-sync/02-SPIKE.md
  modified: []
key-decisions:
  - "No savings account is offered by ING NL in restricted mode: ship with the joint current accounts only (agreed fallback, not re-asked); savings balance and interest are missing, transfers to and from savings stay visible on the current account"
  - "Both joint current accounts are synced; transfers between them appear on both and need internal-transfer handling in categorisation"
  - "The after-2h capture is replaced by a next-morning capture without the PSU header and moved to the spike-completion plan"
requirements-completed: []
requirements-progressed: [INGEST-01, INGEST-04, INGEST-05, INGEST-07]
status: complete
duration: 2 sessions across 2026-09-30 (tool build, operator checkpoint, recording)
completed: 2026-09-30
actuals:
  tokens: 9300
  tasks: 2
  commits: 2
---

# Phase 2 Plan 02: Real-consent spike, first half Summary

The spike tool exists and passed its offline selftest, and the first authorisation against a real ING consent ran: ING grants the full 180 days, requires the PSU IP header, returns 24 months of booked history on both accounts, offers no savings account, and returns only XPCD balances without reference dates.

## Tasks

| Task | Name | Commit |
| --- | --- | --- |
| 1 (tracer) | Spike tool that captures encrypted and prints structure only, proven offline | none: the deliverable lives in the operator's home directory, outside the repository, by design |
| 2 (checkpoint:human-action) | Operator runs the first half of the spike and passphrase-protects the SSH key | c27e76a (02-SPIKE.md) |

The tracer feedback gate was satisfied before any expansion: the tool's offline selftest and shell lint passed, and the tool then ran end to end against the sandbox and the real consent without a failure. Auto mode flags were false; the tracer's check was the operator-run sandbox capture.

## Findings

Full detail is in `02-SPIKE.md`. In short:

- **Savings account offered:** no. Two joint current accounts only, so the fallback applies: joint accounts only, savings balance and interest absent.
- **Consent validity:** ING advertises 180 days and the session was granted the full 180 days (valid until 2027-03-29). The roughly 90-day real-world cap from community reports did not appear at session creation.
- **PSU headers:** ING requires `psu-ip-address`.
- **History depth:** earliest booking date is exactly two years before the capture (2024-09-30) on both accounts; 2471 and 1009 booked transactions; continuation pagination worked; entry reference on every transaction, transaction id on none.
- **Balance types:** XPCD only, no reference date.
- **Pending transactions:** none in the initial capture.
- **Key format and redirect URL:** PEM public key accepted; the internal-only redirect hostname accepted for both applications.

## Deviations from Plan

**1. [Operator-side] The after-2h capture was not made.**
- **Found during:** Task 2 checkpoint
- **Issue:** the capture could not be run two hours after the authorisation.
- **Fix:** the operator runs `capture next-morning --longest` without `--psu` the next morning. It still answers whether full history is available more than an hour after authorisation and whether a header-less background call is admitted. Recorded as pending in `02-SPIKE.md` and moved to the spike-completion plan. The plan's must-have "a second capture about two hours later" is therefore not met; "the one-hour full-history window is measured" is open.
- **Files modified:** none
- **Commit:** c27e76a (records it)

**2. [Plan assumption refuted] Two joint current accounts instead of one joint plus one savings account.** Not a deviation in execution, but the plan's assumption about the account set was wrong. Both accounts are synced.

**3. Daily captures not yet started as a routine.** Only the initial capture exists. The daily pending captures are the operator's ongoing task and are carried by the spike-completion plan.

## Security incident

During the sandbox key check the operator's backup age secret key was echoed in the terminal and pasted into the conversation. Handling, all on 2026-09-30:

- A new key pair was generated and stored in the password manager.
- The server backup recipients file was replaced; the orchestrator verified that it matches the spike recipient and that the host selfcheck's recipients line passes.
- `SPIKE_RECIPIENT` in the spike environment file was updated.
- Existing server backups made with the old key hold no bank data and are to be deleted once a backup under the new key succeeds (operator decision pending).
- Resolved by the orchestrator from timestamps: the sandbox captures (mock data only) were written at 19:22 UTC with the old recipient. The server recipients file was replaced at 19:29 UTC. spike.env was last edited at 19:42 UTC and holds the new recipient, which matches the server's. The real initial captures were written at 19:44 UTC. All real bank captures are therefore encrypted to the new key, and only the mock sandbox captures used the exposed one.

Lesson: never route a secret through a visible terminal prompt whose output is pasted back.

## SSH key mitigation

Done. `ssh-keygen -y -P ''` on the key used for the ledger host now fails (passphrase protected, confirmed by the orchestrator) and the key is loaded through ssh-agent for sessions. Hard removal before the public endpoint remains tracked in the existing todo.

## Verification

- `find` over the captures directory for files that are not age files printed nothing.
- Modes: spike directory 700, state directory 700, spike environment file 600.
- 42 encrypted capture files: 2 session responses, 2 sandbox files, 26 and 12 files for the two accounts of the initial capture.
- `02-SPIKE.md` holds no account number (the IBAN pattern check passed), no URL, no IP address and no hostname; reviewed by reading it through.

## Open for the spike-completion plan

Next-morning capture, daily pending captures, rate-limit quota probe, balance-type impact on reconciliation, two joint accounts, history window, renewal behaviour, old-backup deletion and teardown (revoke, key deletion). Details in `02-SPIKE.md`.

## Known Stubs

None.

## Threat Flags

None. The threat register mitigations T-02-02-01, T-02-02-02, T-02-02-04 and T-02-02-06 held; T-02-02-03 and T-02-02-05 (key deletion and revoke) are due at spike teardown. The one lapse, the exposed backup key, is under "Security incident".

## Self-Check: PASSED

- FOUND: .planning/phases/02-automatic-ing-sync/02-SPIKE.md
- FOUND: commit c27e76a
- STATE.md and ROADMAP.md untouched by this plan execution, as instructed.
