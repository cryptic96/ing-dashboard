---
phase: 01-secure-platform-release-pipeline
plan: 11
subsystem: infra
tags: [github-settings, proxmox-lxc, provisioning, traefik, grafana, selfcheck]

requires:
  - phase: 01-secure-platform-release-pipeline
    provides: "release workflow and GitHub settings doc (01-03), installer (01-04), backups (01-05), provisioning (01-09, 01-10), Grafana/Prometheus provisioning (01-08), ledger-selfcheck and docs/lxc-setup.md (01-10)"
provides:
  - "build/check-github-settings.sh: read-only (GET-only) verification of every GitHub-side control the release pipeline relies on; 11/11 PASS against the real repository"
  - "A configured GitHub repository: tag ruleset, deploy environment with required reviewer and v*.*.* tag policy, outside-contributor approval, read-only default token, SHA pinning, secret scanning and push protection, Dependabot, immutable releases, zero self-hosted runners"
  - "A provisioned, locked-down ledger LXC that passes ledger-selfcheck --pre-deploy --grafana-admin with 0 failures"
  - "A live LAN/VPN-only Traefik route with valid certificates for the Grafana and API hostnames"
  - "Provisioning, Grafana-accounts and selfcheck fixes found on the first real host (see Deviations)"
affects: [01-12 first release and go-live verification]

actuals:
  tasks: 3
  commits: 8

key-files:
  created:
    - build/check-github-settings.sh
  modified:
    - deploy/provision.d/10-packages.sh
    - deploy/provision.d/30-postgresql.sh
    - deploy/provision.d/40-services.sh
    - deploy/provision.d/60-grafana-accounts.sh
    - deploy/bin/ledger-selfcheck

key-decisions:
  - "The missing `deploy` environment was created (with the operator's explicit approval) rather than retargeting release.yml at a pre-existing `production` environment: workflow, docs and the check script all name `deploy`, and a missing environment would be auto-created unprotected on first use."
  - "Traefik's certificate resolver uses the DNS challenge, so the Grafana and API hostnames resolve to the reverse proxy's LAN address in both router and public DNS; no public address is involved."
  - "The existing LAN mail relay offers no STARTTLS; LEDGER_SMTP_STARTTLS=off is acceptable because alert emails never carry financial details."
  - "First provisioning used a clone of the milestone branch, since no release tag exists yet and main does not contain the code until the first release's pull request."
  - "Grafana's reporting datasource works over the Unix socket with peer authentication (datasource health OK), so no password for grafana_reader is needed."

requirements-completed: [SEC-08, SEC-10, OPS-03, DASH-08, DASH-09]

coverage:
  - requirement: SEC-08
    evidence: "build/check-github-settings.sh PASS lines for tag ruleset, deploy environment reviewer and tag policy, outside-contributor approval, read-only token, zero runners"
  - requirement: SEC-10
    evidence: "check-github-settings.sh PASS for secret scanning and push protection; history rewritten to the noreply identity; repository scan found no personal data"
  - requirement: OPS-03
    evidence: "provision.sh ran to completion on the real LXC; ledger-selfcheck --pre-deploy --grafana-admin reported 0 failures"
  - requirement: DASH-08
    evidence: "Selfcheck: admin login renamed, default password rejected, every non-admin user is Viewer, anonymous access and snapshots disabled; operator signed in with every account"
  - requirement: DASH-09
    evidence: "Traefik route verified: 302 to login from the LAN, anonymous API 401, security headers present, a source outside the allow-list gets 403; valid Let's Encrypt certificates"
---

# Plan 01-11: Platform Bring-Up Summary

**GitHub enforces the release gates, and the real LXC is provisioned, locked down and proven by selfcheck with 0 failures, ready for the first release.**

## Performance
- Tasks: 3 (1 operator-applied and orchestrator-verified, 1 automated, 1 operator/orchestrator on the real host)
- Real-host bring-up surfaced six defects the offline tests could not; all fixed in the repository, pushed, pulled on the host and re-verified.

## Accomplishments
- All GitHub-side controls confirmed by a reusable read-only script (11/11 PASS).
- LXC created by the operator (unprivileged Ubuntu 24.04, 2 cores, 2048 MB, 16 GB) and provisioned end to end: packages from fingerprint-verified sources, service accounts, a Data Protection certificate and env file, socket-only PostgreSQL with peer-mapped roles, services and timers, a default-drop firewall admitting SSH only from the LAN/VPN and the app and Grafana ports only from the reverse proxy.
- Backup recipient configured with the operator's age public key only; the private identity is in the operator's password manager and nowhere on the host (selfcheck confirms).
- Data Protection certificate and password copied to the password manager by the operator.
- Grafana admin renamed with a new password; one Viewer account per household member; default admin password verifiably rejected.
- Traefik route installed on the reverse proxy; certificates issued for both hostnames; Grafana reachable from the LAN through Traefik, refused from outside the allow-list.
- `ledger-selfcheck --pre-deploy --grafana-admin`: 0 failures, including `datasource ledger-reporting reports OK` (socket + peer auth as grafana_reader).

## Task Commits
1. **Task 1: Apply the GitHub repository protections**: operator-applied; no commit (repository settings only).
2. **Task 2: Verify the GitHub protections read-only**: `b139672` (build/check-github-settings.sh)
3. **Task 3: Build and provision the LXC, run the selfcheck**, with fixes:
   - `f410828` fix: feed bootstrap SQL to psql on stdin during provisioning
   - `9bb692f` fix: apply service configuration on every provisioning run and drop the template MTA
   - `f9c8ca1` fix: remove selfcheck false results found on the first real host
   - `165eb77` fix: let selfcheck wait for Prometheus targets' first scrape
   - `c1b3052` fix: never echo a rejected Grafana login back to the terminal
   - `d863359` fix: make the Grafana accounts module fail on anything but 2xx
   - `c777791` fix: recover Grafana admin setup from a half-finished run

## Files Created/Modified
- `build/check-github-settings.sh`: read-only GitHub settings verification (new).
- `deploy/provision.d/30-postgresql.sh`: bootstrap SQL fed on stdin.
- `deploy/provision.d/10-packages.sh`: purges a template-shipped local MTA.
- `deploy/provision.d/40-services.sh`: restarts Prometheus, the node exporter and Grafana on every run.
- `deploy/provision.d/60-grafana-accounts.sh`: Host header, strict 2xx handling, recoverable admin setup, no echo of rejected logins.
- `deploy/bin/ledger-selfcheck`: pipefail-safe matching, SQLSTATE detection, full-identity age scan, Grafana Host header, DNS stub allowance, pre-deploy SKIPs, first-scrape wait, default-admin-password check.

## Decisions Made
See `key-decisions` in the frontmatter.

## Deviations from Plan

### Orchestrator actions on GitHub (acceptance criterion "No repository setting was changed by Claude")
1. **The `deploy` environment was created by the orchestrator**, with the operator's explicit approval in chat. It was missing (only an unused `production` environment existed); since release.yml targets `deploy`, GitHub would have auto-created it unprotected on the first tag, publishing without approval.
2. **Commit history rewritten at the operator's request**: every commit on main and the milestone branch now carries the GitHub noreply identity instead of a personal name and email. The main-branch ruleset was disabled for exactly one force-push, then restored and verified identical. Trees, dates and messages were verified unchanged; commit hashes quoted in earlier summaries predate the rewrite.

### Real-host defects found and fixed (Rule 1)
1. **Bootstrap SQL unreadable by postgres**: the documented checkout under /root is not readable by the postgres OS user, so `psql -f <path>` failed. Fixed by feeding the file on stdin.
2. **Node exporter listening on every interface**: the package starts its service before the defaults file exists, and `systemctl enable --now` never restarts a running service. Prometheus, the node exporter and Grafana are now restarted on every provisioning run, which also makes re-runs apply changed configuration. The firewall blocked the port throughout.
3. **Template-shipped Postfix listening on loopback**: purged by provisioning; mail leaves only through msmtp and Grafana. The household's separate relay container is unaffected.
4. **Selfcheck false results**: `nft … | grep -q` under pipefail broke the pipe on an early match. This gave a false "policy is not drop", and the same pattern could give false passes on the postgres-TCP and token checks. Output is now captured before matching. SQLSTATE is printed with VERBOSITY verbose; the age scan matches a complete identity rather than the marker mentioned in docs; Grafana requests carry its enforced Host header; the loopback DNS stub is an expected listener; `--pre-deploy` prints SKIP for checks that need a release; targets get time for a first scrape after a restart.
5. **Grafana accounts module reported success on redirects**: Grafana's enforce_domain redirected every local API call, and `curl --fail` accepts 3xx, so the module reported the admin rename as done while nothing had changed and the default admin login kept working. Calls now send the configured hostname and treat any non-2xx as failure.
6. **Half-finished admin setup was unrecoverable**: after a password change was refused (Grafana's brute-force lock, triggered by earlier browser login attempts), the admin was renamed but kept the default password, and a re-run skipped to the viewers. The password is now replaced before the rename, a re-run fixes whichever is still default, and the selfcheck verifies the default password is rejected.
7. **Rejected logins echoed to the terminal**: an operator pasting a password into the login prompt saw it printed in the error. The error now describes the format instead.

### Other
- `docs/lxc-setup.md` step 2 says to clone at the latest release tag; for the first provisioning there is none, so the milestone branch was cloned. Once released, provisioning runs from the installed tree.
- The selfcheck header wrongly claimed the installer runs `--pre-deploy`; corrected to describe a freshly provisioned host.

## Issues Encountered
- The home network's intrusion prevention blocked the workstation's SSH to the LXC after a burst of short connections; the operator excluded the workstation's source address. Orchestrator SSH now uses a single shared connection per host.
- The session scratchpad was wiped mid-phase; orchestrator state moved under `.git/`.

## User Setup Required
- Temporary SSH access granted to the orchestrator on the ledger LXC (sudo) and the reverse proxy (dynamic-config write only, no sudo) must be removed after go-live; the commands are in the operator's local notes.
- Optional: restrict the reverse proxy's static config file to its service group, and delete the now-unneeded public DNS records.

## Known Stubs
None. App-dependent checks (ports 5080/5081, the app Prometheus target, the grafana_reader read on data_protection_keys) print SKIP until the first release in plan 01-12.

## Next Phase Readiness
The platform is ready for the first release: GitHub gates are enforced and verified, the host is provisioned and passes selfcheck, and the reverse proxy serves both hostnames with valid certificates. Plan 01-12 merges the milestone branch to main through a pull request, tags v0.1.0 and runs the go-live checks.

## Self-Check: PASSED
- `build/check-github-settings.sh`: 11/11 PASS.
- `ledger-selfcheck --pre-deploy --grafana-admin` on the real host: 0 failures (operator-run, output reviewed).
- `build/lint.sh` passes on every fix commit; CI green on the milestone branch.
