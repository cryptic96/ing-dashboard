---
phase: 01-secure-platform-release-pipeline
plan: 12
subsystem: release
tags: [release, attestation, go-live, backup-restore, grafana, rest-auth]

requires:
  - phase: 01-secure-platform-release-pipeline
    provides: "release workflow (01-03), installer (01-04), backups and restore (01-05), API keys (01-06), provisioning and selfcheck (01-09, 01-10), configured GitHub repository and provisioned host (01-11)"
provides:
  - "build/verify-published-release.sh: credential-free verification of a published release from a workstation (download, attestation, main ancestry, refusal of a modified copy)"
  - "Two real releases (v0.1.0, v0.1.1) built, attested, approved, published and installed on the real host"
  - "Observed evidence for every go-live criterion of the phase (below)"
  - "Fixes to backup, installer, mail, build info, selfcheck, Grafana mail, alert volume and test isolation found on the real system"
affects: [phase verification, every later release]

actuals:
  tasks: 2
  commits: 12

key-files:
  created:
    - build/verify-published-release.sh
    - deploy/tests/lib/host-guard.sh
    - Ledger.UnitTests/Metrics/LedgerMetricsTests.cs
  modified:
    - deploy/bin/ledger-backup
    - deploy/systemd/ledger-backup@.service
    - deploy/lib/deploy.sh
    - deploy/msmtp/msmtprc.in
    - deploy/bin/ledger-selfcheck
    - deploy/provision.d/40-services.sh
    - deploy/provisioning/grafana/provisioning/alerting/notification-policies.yaml
    - Ledger.Service/Metrics/LedgerMetrics.cs
    - deploy/tests/*-test.sh
    - .github/dependabot.yml

key-decisions:
  - "The milestone branch reached main as the full branch with a merge commit (not a filtered branch): planning files were already public on it, and a merge commit keeps later phases mergeable."
  - "The first merge commit, created by GitHub's web UI with the operator's name and email, was rewritten once to the noreply identity before tagging (tag v0.1.0 would otherwise have pinned it immutably). Afterwards the operator chose to accept their name and email on future web merge commits; they are no longer rewritten."
  - "Backup recipients reach the sandboxed backup unit as a systemd credential (LoadCredential) rather than by widening /etc/ledger permissions, so the backup user still cannot read the env file or the Data Protection certificate."
  - "Alert mail is capped for a relay with a shared daily limit: one group for all platform alerts, updates at most hourly, repeats every 12 hours (about 24 messages a day at worst)."
  - "Dependabot: Prometheus stays on the LTS line (patch updates only); xunit 4.x is ignored until the test projects move to Microsoft.Testing.Platform (captured as a todo)."

requirements-completed: [SEC-07, SEC-08, OPS-03, OPS-04, OPS-05, OPS-07, API-02, DASH-08, DASH-09]

coverage:
  - requirement: SEC-07
    evidence: "v0.1.0 and v0.1.1 built on GitHub-hosted runners with build-provenance attestation; the host verified each attestation offline before unpacking; build/verify-published-release.sh v0.1.0 passes from a credential-free workstation"
  - requirement: SEC-08
    evidence: "Each release paused at the deploy environment until the operator approved; the host pulled only published releases whose attested commit is on main; no runner exists"
  - requirement: OPS-03
    evidence: "Host provisioned from the repository; ledger-selfcheck --grafana-admin --restart-check 0 failures on v0.1.1"
  - requirement: OPS-04
    evidence: "Encrypted nightly and pre-migration backups written; restore drill PASS with the operator's key supplied on stdin; scratch database dropped; no identity on disk"
  - requirement: OPS-05
    evidence: "Data Protection canary Healthy after a service restart and after redeploying into a new release directory"
  - requirement: OPS-07
    evidence: "v0.1.0: pre-migration backup, then InitialCreate and AddApiKeys applied by the migration bundle as the migrator OS user"
  - requirement: API-02
    evidence: "Through the reverse proxy from the LAN: 401 without a key, 401 with a made-up key, 200 with a valid key naming the client, 401 after revoking it"
  - requirement: DASH-08
    evidence: "Both household Viewer accounts and the renamed admin signed in; selfcheck confirms every non-admin user is Viewer and the default admin password is rejected"
  - requirement: DASH-09
    evidence: "From mobile data with the VPN off neither hostname is reachable; from the LAN both work with valid certificates; a source outside the allow-list gets 403"
---

# Plan 01-12: First Release and Go-Live Summary

**A tag on main deployed itself to the real host through the gated, attested pipeline, twice. Every go-live criterion of the phase was observed on the real system.**

## Performance
- Tasks: 2 (1 operator-gated, 1 automated plus consolidated live checks)
- Two releases published and installed; nine defects found only on the real system, all fixed, pushed and re-verified.

## Accomplishments
- `build/verify-published-release.sh`: from a workstation with no credentials, downloads a published release, verifies its attestation against the release workflow and tag, confirms the attested commit is on main, and requires a one-byte-modified copy to be refused.
- The first release went end to end: pull request to main, tag v0.1.0, GitHub-hosted build and attestation, draft with zip, checksum and Sigstore bundle, operator approval, re-verification and publish (immutable), then host poll, offline verification, pre-migration backup, migration as the migrator role, activation, health and metrics.
- A redeploy (v0.1.1) into a new release directory, with the canary still decrypting, the build's commit reported, and the outcome email delivered.

## Go-live evidence (observed 2026-09-28)

| Check | Result |
|---|---|
| `build/verify-published-release.sh v0.1.0` | All 7 steps PASS: strict tag, repository, both downloads, attestation (source digest = main merge commit), main ancestry, modified copy refused. Also passes with an ambient `GH_TOKEN`; malformed tags are rejected. |
| v0.1.0 install on the host | Verified offline; pre-migration backup; InitialCreate and AddApiKeys applied as the migrator OS user; `current` → `releases/0.1.0`; `/health` Healthy; deploy metrics success, not rolled back |
| v0.1.1 redeploy | `current` → `releases/0.1.1`, with 0.1.0 kept; EF applied no migrations; Healthy; `ledger_build_info{version="0.1.1",commit=<main merge commit>}`; outcome email received by the operator |
| Modified artifact on the real installer | `ledger-deploy verify` on a one-byte-modified copy exits 1 (Sigstore verification error); the genuine copy exits 0; the releases directory is unchanged |
| Restart survival | `ledger-selfcheck --restart-check`: the service restarted and became Healthy |
| Restore drill | Nightly backup taken; drill PASS (migration history, canary row, per-schema table counts match); scratch database dropped; drill timestamp recorded for alerting; no age identity found in any recently written file |
| LAN/VPN only | From mobile data with the VPN off, neither hostname answers (operator-observed). From the LAN both answer with valid certificates. A source outside the allow-list gets 403. |
| REST authentication through the reverse proxy | 401 without a key; 401 with a made-up key; 200 `{"version":"0.1.0","client":"golive-check"}` with a temporary key; 401 after revoking it; the token was never printed. Paths outside `/api/` get 404. |
| Grafana accounts | Every household Viewer and the renamed admin signed in (operator); selfcheck: admin not `admin`, default password rejected, every non-admin user Viewer, anonymous access and snapshots disabled, both datasources OK |
| Alert email | Grafana contact-point test email arrived through the LAN relay (operator) after the STARTTLS fix |
| Final selfcheck | `ledger-selfcheck --grafana-admin --restart-check` on v0.1.1: 57 checks, 0 failures |

## Task Commits
1. **Task 1: merge to main, tag v0.1.0, approve the deploy environment**: operator merged the pull request; the orchestrator rewrote the web merge commit's author, pushed tag v0.1.0 and later v0.1.1 at the operator's request; the operator approved both deploys.
2. **Task 2: verify the published release and record the live evidence**: `d028f0f` (build/verify-published-release.sh), plus the fixes below.

Fixes, in order:
- `00c32d5` fix: hand the backup recipients to the backup unit as a systemd credential
- `199ba4f` fix: let the installer retry a version whose earlier attempt did not activate
- `0385f40` fix: give the msmtp relay account its own name
- `bb4f004` chore: keep Dependabot's Prometheus updates on the LTS line
- `534edf6` fix: report the build's commit in ledger_build_info
- `c5603cc` fix: selfcheck the reporting role and build info against a real release
- `59f0a55` fix: derive Grafana's STARTTLS policy from the relay setting
- `79e6f1c` fix: read applied migrations from EF's own history columns
- `208dfd0` fix: keep offline tests from managing services on the machine they run on
- (this PR) fix: cap Grafana alert email volume under the relay's daily limit

## Deviations from Plan

### Real-system defects (Rule 1), each fixed and re-verified
1. **Pre-migration backup could not read its recipients.** `/etc/ledger` is closed to the backup user. The first v0.1.0 attempt stopped safely before touching anything. Fixed with a systemd credential.
2. **A failed attempt blocked every retry of the same version.** The unpacked release had already been moved into `releases/<version>`. A leftover that is not the active release is now replaced by the freshly verified copy.
3. **Outcome email never sent.** The msmtp template redefined its `default` account.
4. **Build info reported commit `unknown`.** MSBuild puts SourceRevisionId in the informational version, not in assembly metadata. The parsing is now a tested pure function.
5. **Selfcheck against a real release:**
   - The reporting-role probe hit 42P01 through search_path resolution; it is now schema-qualified.
   - The build-info check read the HELP comment; it now reads the metric line.
   - A new check compares the running commit with the manifest.
6. **Grafana alert email failed with a plaintext LAN relay.** Grafana always required STARTTLS; it now follows the same setting as msmtp.
7. **Migration detection always reported every migration as pending.** The installer queried `migration_id`, not EF's `"MigrationId"`, and the error was swallowed. v0.1.1 therefore took a needless backup and ran the bundle, and it counted as migrated, which would have disabled automatic rollback. A missing table still means nothing is applied; any other failure now stops the deploy. On the host, the fixed function reports none pending for v0.1.1. Proving that a deploy skips backup and migration when nothing is pending needs the next release (v0.1.2).
8. **An offline test reached the real `systemctl` on the developer workstation.** It was introduced by the migration-detection fix, whose new query the test's `runuser` stand-in answered wrongly, and it raised two polkit prompts. The system has no `ledger.service`, so nothing changed. Every offline test now has failing `systemctl`/`pkexec` stand-ins first on PATH; a mutation run confirmed the guard catches it with no prompt.
9. **Alert email volume was unbounded for flapping alerts**, against a relay with a 100-per-day limit. It is now capped at about 24 a day.

### Process
- The GitHub web merge of the first pull request carried the operator's name and email. It was rewritten before tagging, with a lease-protected force-push of main and the main ruleset disabled only for that push and then verified identical. The operator later accepted web merge commits with their identity.
- The first Dependabot pull requests were created on the pre-rewrite merge commit:
  - NSubstitute 6 and the Test SDK update were recreated on the current main and merged.
  - The Prometheus minor update was closed (LTS policy).
  - The xunit 4.x pair was closed and ignored pending the Microsoft.Testing.Platform migration (todo captured).
- The release-workflow plan expected tags only on the same main commit. v0.1.1 was cut from a newer main that includes the fixes, because the redeploy proof needed them.

## Issues Encountered
- The home network's intrusion prevention briefly blocked the workstation's SSH to the host; the operator excluded the workstation. The orchestrator now uses one shared SSH connection per host.

## User Setup Required
- Merge the open pull request with tonight's fixes, tag v0.1.2, approve the deploy, and confirm the installer skips the backup and migration.
- Remove the orchestrator's temporary SSH access to the host and the reverse proxy once no further on-host work is needed.

## Known Stubs
None.

## Next Phase Readiness
The platform is live and proven: two attested releases installed from main through operator approval, backups restorable with the operator's key, the host locked down and reachable only from the LAN and VPN, and a selfcheck with no failures.

## Self-Check: PASSED
- `build/verify-published-release.sh v0.1.0`: all PASS.
- `ledger-selfcheck --grafana-admin --restart-check` on the host (v0.1.1): 0 failures.
- `dotnet test` 72/72 and `build/lint.sh` all checks PASS on the fix commits.
