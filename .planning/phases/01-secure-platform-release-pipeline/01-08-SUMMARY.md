---
phase: 01-secure-platform-release-pipeline
plan: 08
subsystem: infra
tags: [grafana, prometheus, unified-alerting, provisioning-as-code, promtool]

requires:
  - phase: 01-secure-platform-release-pipeline
    provides: "grafana_reader role and reporting schema (bootstrap-roles.sql/bootstrap-database.sql), the installer's deploy/provisioning/{grafana,prometheus} install contract (ledger_install_provisioning), the build/lint.sh harness and its digest-pinned grafana/promtool compose services"
provides:
  - "Hardened deploy/provisioning/grafana/grafana.ini: no anonymous access, public dashboards or snapshots; auto-assigned Viewer role; hardened cookies/CSP/HSTS; disabled gravatar, analytics, news feed and update checks"
  - "Provisioned PostgreSQL (grafana_reader over the Unix socket) and Prometheus datasources, both non-editable"
  - "Empty dashboards provider reserving the Household Ledger folder for future English/Dutch dashboards"
  - "grafana-server systemd drop-in loading only /etc/ledger/grafana.env; deploy/grafana.env.example with placeholders"
  - "Eight platform alert rules (app down, platform service down, failing health check, stale/failed backup, failed/rolled-back deploy, stale deploy poll) plus one email contact point and a root notification policy"
  - "Loopback-only deploy/provisioning/prometheus/prometheus.yml scraping the app, node exporter and Prometheus itself"
  - "build/lint/checks/60-observability.sh: promtool config check, self-tested ini-hardening assertions, and a live Grafana boot proving provisioning and the 401 anonymous lockdown"
  - "docs/monitoring.md operator documentation"
affects: [go-live/LXC provisioning, any later dashboard-adding plan, Phase 2 reporting-view dashboards]

actuals:
  tokens: 7635
  tasks: 2
  commits: 2

tech-stack:
  added: []
  patterns:
    - "Lint checks that need a live server boot the digest-pinned compose service directly (docker compose run -d, overriding --entrypoint and env), rather than adding a new compose service, and always stop it in a trap so --rm removes it even on failure"
    - "ini-section-aware assertion helper (assert_ini_key) is proven against synthetic same-key-different-section fixtures before it is trusted against the real grafana.ini, matching this repository's established self-test-before-real-scan convention"
    - "A bare `VAR=\"$(cmd1 | cmd2)\"` assignment trips `set -e -o pipefail` when cmd1 (e.g. tr reading /dev/urandom) receives SIGPIPE from a bounded consumer (head -c N); wrapping the substitution as an argument to printf avoids this because set -e only inspects printf's own exit status"

key-files:
  created:
    - deploy/provisioning/grafana/grafana.ini
    - deploy/provisioning/grafana/provisioning/datasources/ledger.yaml
    - deploy/provisioning/grafana/provisioning/dashboards/ledger.yaml
    - deploy/provisioning/grafana/provisioning/alerting/contact-points.yaml
    - deploy/provisioning/grafana/provisioning/alerting/notification-policies.yaml
    - deploy/provisioning/grafana/provisioning/alerting/platform-rules.yaml
    - deploy/provisioning/prometheus/prometheus.yml
    - deploy/systemd/grafana-server.service.d/ledger.conf
    - deploy/grafana.env.example
    - build/lint/checks/60-observability.sh
    - docs/monitoring.md
  modified: []

key-decisions:
  - "Reused the grafana and promtool compose services already pre-provisioned in build/lint/compose.yaml (plan 01-02) instead of adding new ones — no compose.yaml or dependabot.yml edit was needed, since Dependabot's docker-compose watcher already covers build/lint"
  - "The ephemeral lint-check Grafana container overrides GF_SERVER_ENFORCE_DOMAIN and GF_PATHS_CONFIG/GF_PATHS_PROVISIONING only at the container's environment, never in the shipped grafana.ini itself, so the assertions still prove the real file that ships to production"
  - "Alert rule and threshold-expression YAML follows Grafana's documented file-provisioning schema (data: [prometheus query node, __expr__ threshold node], condition: C) rather than dashboard-JSON-style alerting, matching what a real Grafana 13.2 instance actually loads (verified live, not assumed)"

patterns-established:
  - "Any future lint check that needs a live service boots the already-pinned compose service directly with docker compose run -d + --entrypoint/env overrides, cleans up via trap, and never adds a redundant compose service or duplicate image pin"

requirements-completed: [DASH-06, DASH-08, SEC-06]

coverage:
  - id: D1
    description: "grafana.ini disables anonymous access, public dashboards, snapshots, sign-up/org-create, sets Viewer auto-assignment, hardens cookies/CSP/HSTS, disables gravatar/analytics/news/update-checks"
    requirement: "SEC-06"
    verification:
      - kind: other
        ref: "build/lint.sh observability (ini-section-aware assertion of all 29 hardening keys, self-tested against synthetic fixtures first)"
        status: pass
    human_judgment: false
  - id: D2
    description: "A live Grafana container booted with the repository's own grafana.ini and provisioning answers /api/search, /api/dashboards/home and /api/snapshots with 401 for anonymous requests"
    requirement: "SEC-06"
    verification:
      - kind: integration
        ref: "build/lint.sh observability (curl against the digest-pinned Grafana container's published loopback port)"
        status: pass
    human_judgment: false
  - id: D3
    description: "Both datasources (ledger-reporting over the Unix socket as grafana_reader, prometheus) provision with no password/secureJsonData anywhere, and an empty dashboards provider reserves the Household Ledger folder"
    requirement: "DASH-06"
    verification:
      - kind: other
        ref: "build/lint.sh observability (admin /api/datasources assertion) plus acceptance-criteria greps in this plan"
        status: pass
    human_judgment: false
  - id: D4
    description: "Eight platform alert rules and the operator-email contact point provision and are queryable through Grafana's own provisioning API"
    requirement: "DASH-08"
    verification:
      - kind: integration
        ref: "build/lint.sh observability (/api/v1/provisioning/alert-rules uid count == 8, /api/v1/provisioning/contact-points contains operator-email)"
        status: pass
    human_judgment: false
  - id: D5
    description: "prometheus.yml scrapes only 127.0.0.1:5081/9100/9090, has no remote_write or alertmanagers section, and passes promtool check config"
    requirement: "DASH-08"
    verification:
      - kind: integration
        ref: "build/lint.sh observability (promtool check config) plus acceptance-criteria greps"
        status: pass
    human_judgment: false
  - id: D6
    description: "Live end-to-end operation against the real LXC (real Grafana viewer accounts, real Traefik route, real Postfix delivery of an alert email) is out of scope for this plan"
    verification: []
    human_judgment: true
    rationale: "Requires the provisioned LXC, real systemd units and a real SMTP relay — explicitly out of scope per this plan's own <verification> section, which only requires build/lint.sh observability to pass locally and in CI."

duration: ~50min
completed: 2026-09-27
status: complete
---

# Phase 01 Plan 08: Grafana and Prometheus Provisioning as Code Summary

**Hardened, durably reinstalled Grafana lockdown (no anonymous access, public dashboards or snapshots), both reporting datasources, eight platform alert rules with a single email contact point, a loopback-only Prometheus scrape config, and a lint check that boots a real digest-pinned Grafana to prove all of it loads and the 401 lockdown holds.**

## Performance

- **Duration:** ~50 min
- **Tasks:** 2
- **Files modified:** 11 (all created)

## Accomplishments

- `grafana.ini` disables anonymous access, public (shared) dashboards, local and external snapshots, sign-up and org-create; auto-assigns Viewer; hardens secure/strict-SameSite cookies, HSTS, CSP, `x_content_type_options`; disables gravatar, analytics reporting, update checks and the news feed — and is reinstalled by the root installer on every deploy, so a UI change can never durably re-enable any of it.
- Both datasources — `ledger-reporting` (PostgreSQL over the Unix socket as `grafana_reader`, no password anywhere) and `prometheus` — provision non-editable, alongside an empty dashboards provider reserving the `Household Ledger` folder for the first English/Dutch dashboard.
- Eight platform alert rules (app down, a platform systemd service down, a failing health check, a stale or failed backup, a failed or rolled-back deploy, a stale deploy poll) provision into one `platform` rule group under the `Platform` folder, all routing to a single `operator-email` contact point whose address is environment-interpolated (`$LEDGER_ALERT_EMAIL`) — titles and summaries name only the rule and service, never a query result.
- `prometheus.yml` scrapes the app (`127.0.0.1:5081`), node exporter (`127.0.0.1:9100`) and Prometheus itself (`127.0.0.1:9090`), all loopback, with no remote write and no Alertmanager section, and passes `promtool check config`.
- `build/lint/checks/60-observability.sh` proves all of the above against a **live, digest-pinned Grafana 13.2.2 container**, not just static file inspection: it self-tests its own ini-hardening assertion helper and its `promtool` wrapper against synthetic fixtures first (proving section-boundary correctness and accept/reject behavior), then asserts all 29 real hardening keys, then boots the real Grafana image with the real `grafana.ini` and provisioning directory, waits for `/api/health`, and asserts anonymous `GET` on `/api/search`, `/api/dashboards/home` and `/api/snapshots` all return 401, and that admin-authenticated calls show both datasource uids, exactly 8 alert-rule uids and the `operator-email` contact point.

## Task Commits

Each task was committed atomically:

1. **Task 1: Hardened grafana.ini, provisioned datasources and dashboards provider, server-side environment** - `e255bc3` (feat)
2. **Task 2: Platform alert rules, email contact point, Prometheus scrape config and the observability lint check** - `77038b8` (feat)

_Note: this SUMMARY and STATE.md are committed separately by the wave orchestrator (worktree mode)._

## Files Created/Modified

- `deploy/provisioning/grafana/grafana.ini` - Hardened Grafana configuration, no server-specific values
- `deploy/provisioning/grafana/provisioning/datasources/ledger.yaml` - PostgreSQL reporting and Prometheus datasources, non-editable
- `deploy/provisioning/grafana/provisioning/dashboards/ledger.yaml` - Empty dashboards provider, `Household Ledger` folder
- `deploy/provisioning/grafana/provisioning/alerting/contact-points.yaml` - `operator-email` contact point, `$LEDGER_ALERT_EMAIL` interpolation
- `deploy/provisioning/grafana/provisioning/alerting/notification-policies.yaml` - Root policy routing everything to `operator-email`
- `deploy/provisioning/grafana/provisioning/alerting/platform-rules.yaml` - Eight platform alert rules in the `platform` group, `Platform` folder
- `deploy/provisioning/prometheus/prometheus.yml` - Loopback-only scrape config for the app, node exporter, Prometheus
- `deploy/systemd/grafana-server.service.d/ledger.conf` - `EnvironmentFile=/etc/ledger/grafana.env` drop-in
- `deploy/grafana.env.example` - Placeholder-only server-side Grafana environment template
- `build/lint/checks/60-observability.sh` - `promtool`, self-tested ini-hardening assertions, live Grafana provisioning smoke test
- `docs/monitoring.md` - What is monitored, the eight alerts and what to do, how to change the alert address/relay

## Decisions Made

- Reused the `grafana` and `promtool` compose services already pre-provisioned in `build/lint/compose.yaml` by plan 01-02, invoking them directly via `docker compose run -d` with `--entrypoint`/environment overrides for the live-boot smoke test, instead of adding a new compose service — no `compose.yaml` or `dependabot.yml` change was needed; Dependabot's `docker-compose` watcher on `/build/lint` already covers both images.
- The lint check's ephemeral Grafana container overrides `GF_SERVER_ENFORCE_DOMAIN`, `GF_PATHS_CONFIG` and `GF_PATHS_PROVISIONING` only in the container's own environment (so `curl` against a loopback IP isn't rejected by domain enforcement, and the container reads the repository's real files without a bind-mount). The shipped `grafana.ini` itself keeps `enforce_domain = true` and `provisioning = /etc/grafana/provisioning`, which the ini-hardening assertions check directly against the file on disk — the live-boot overrides never mask what actually ships.
- Followed Grafana's documented file-provisioning schema for alerting (`data:` array with a Prometheus query node feeding a `__expr__` threshold node, `condition: C`) rather than guessing — verified live against a real Grafana 13.2.2 instance rather than assumed from memory.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `printf '%s' "$secret"` grep on the datasource YAML's own explanatory comment**
- **Found during:** Task 1, acceptance-criteria pass
- **Issue:** The datasource YAML's own comment explaining *why* there is no password used the words "password" and "secureJsonData" in prose, which the plan's own acceptance-criteria grep (`! grep -niE 'password|secureJsonData' ...`) correctly flagged as a false match against its own documentation.
- **Fix:** Reworded the comment to describe the same fact ("nothing secret here... no secret material to hold") without using either literal word.
- **Files modified:** `deploy/provisioning/grafana/provisioning/datasources/ledger.yaml`
- **Verification:** `grep -niE 'password|secureJsonData' deploy/provisioning/grafana/provisioning/datasources/ledger.yaml` now returns nothing.
- **Committed in:** `e255bc3` (Task 1 commit)

**2. [Rule 1 - Bug] `ADMIN_PASSWORD="$(tr ... | head -c 32)"` silently killed the lint check under `set -e -o pipefail`**
- **Found during:** Task 2, first full run of `build/lint.sh observability`
- **Issue:** `head -c 32` closes its input early once it has enough bytes, sending `tr` a SIGPIPE; `tr`'s resulting 128+SIGPIPE exit status becomes the pipeline's exit status under `pipefail`, and because this was a bare `VAR="$(...)"` assignment — not an argument passed to another command — `set -e` treated that as the whole command's failure and the script exited with no further output, right after the ini-hardening assertions and before ever printing "Booting the digest-pinned Grafana image".
- **Fix:** Wrapped the same pipeline inside `printf '%s' "$(...)"`, matching the pattern `build/lint/checks/40-secrets.sh` already uses for its own `fake_github_token` helper — `set -e` only inspects `printf`'s own (successful) exit status, not the inner pipeline's.
- **Files modified:** `build/lint/checks/60-observability.sh`
- **Verification:** `build/lint.sh observability` now runs to completion and passes, including the live Grafana boot and all its API assertions.
- **Committed in:** `77038b8` (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (2 Rule 1 bugs)
**Impact on plan:** Both fixes were necessary for the plan's own acceptance criteria and lint check to actually pass against the real repository and a real Grafana instance. No scope creep.

## Issues Encountered

None beyond the two auto-fixed deviations above.

## User Setup Required

None - no external service configuration required. This plan produces repository files and a lint check only; installing them onto a real LXC (`/etc/grafana`, `/etc/prometheus`, real viewer accounts, a real SMTP relay) is the go-live plan's responsibility, per this plan's own interfaces section.

## Known Stubs

None. Every truth in the plan's `must_haves` is implemented and proven against a real, digest-pinned Grafana 13.2.2 container and a real `promtool`, not just static file inspection. No dashboard JSON ships in this plan by design (per the plan's own flagged assumption): every dashboard must exist in English and Dutch, generated from one source, before it is committed — that is Phase 2's reporting-view dashboard work, not this plan's.

## Next Phase Readiness

- `deploy/provisioning/grafana/**`, `deploy/provisioning/prometheus/prometheus.yml` and `deploy/systemd/grafana-server.service.d/ledger.conf` are ready for the root installer (plan 01-04's `ledger_install_provisioning`) to copy onto a real LXC exactly as it already expects (paths matched with no changes needed to `deploy/lib/deploy.sh`).
- `build/lint.sh observability` is now part of the full lint suite CI runs on every push; a future dashboard-adding plan should extend `60-observability.sh`'s live-boot assertions rather than adding a second Grafana-boot check.
- No blockers. The empty dashboards provider and the `Household Ledger` folder are ready for Phase 2's first data dashboard.

## Self-Check: PASSED

- All 11 created files confirmed present on disk (`deploy/provisioning/grafana/grafana.ini`, `deploy/provisioning/grafana/provisioning/{datasources,dashboards,alerting}/*`, `deploy/provisioning/prometheus/prometheus.yml`, `deploy/systemd/grafana-server.service.d/ledger.conf`, `deploy/grafana.env.example`, `build/lint/checks/60-observability.sh`, `docs/monitoring.md`)
- Commit `e255bc3` — present in `git log`
- Commit `77038b8` — present in `git log`
- `build/lint.sh` (all six checks: repo-rules, workflows, shell, secrets, script-tests, observability) — re-verified exit 0 immediately before writing this summary, no leftover containers afterward (`docker ps -a --filter name=lint-` empty)
- Every acceptance-criteria grep for both tasks re-run and confirmed passing

---
*Phase: 01-secure-platform-release-pipeline*
*Completed: 2026-09-27*
