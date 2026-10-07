---
phase: 03-claude-reads-the-ledger
plan: 06
subsystem: exposure
tags: [traefik, exposure, selfcheck, sudo, anthropic-ranges, provisioning, docs]
status: complete

requires:
  - phase: 03-02
    provides: PublicHostGuard allow-list and sign-in networks the router paths must equal
  - phase: 03-05
    provides: ledger-login, ledger-grants and the Claude access section of the monitoring guide
provides:
  - Reverse-proxy template with ledger-mcp-allow, ledger-mcp-public (priority 100, five exact Path values) and ledger-mcp-signin (priority 110, home and VPN only)
  - TraefikTemplateTests (Category=Configuration) pinning every template rule
  - Provisioning keys LEDGER_MCP_DOMAIN and LEDGER_SUDO_ALLOWED_USERS, and OAuth env lines rendered at provisioning
  - ledger-selfcheck check_sudo_logins, check_mcp_endpoint and log-scan patterns for bearer tokens, token and verifier parameters and enrolment URIs
  - build/check-exposure.sh with outside and inside status matrices for the MCP, REST and Grafana hostnames
  - build/tests/anthropic-ranges-network-test.sh (opt-in) comparing the template range with Anthropic's published page
  - docs/mcp.md connection guide, with docs/lxc-setup.md and docs/rest-api.md updates
affects: [03-07, 03-08]

tech-stack:
  added: []
  patterns:
    - "Template rules enforced by a unit test that reads the committed file as text and cuts router blocks by indentation, no YAML package"
    - "Host checks send requests to the app's loopback port with a Host header and X-Forwarded-Proto, exactly as the proxy would forward them"
    - "Operator tool tested with a curl stand-in that answers from a URL-to-status fixture, so no test touches a network"

key-files:
  created:
    - Ledger.UnitTests/Configuration/TraefikTemplateTests.cs
    - build/check-exposure.sh
    - build/tests/check-exposure-logic-test.sh
    - build/tests/anthropic-ranges-network-test.sh
    - docs/mcp.md
  modified:
    - deploy/traefik/ledger.yml.example
    - deploy/provision.d/20-accounts.sh
    - deploy/provision.conf.example
    - deploy/provision.sh
    - deploy/tests/provision-logic-test.sh
    - deploy/bin/ledger-selfcheck
    - deploy/tests/selfcheck-logic-test.sh
    - docs/lxc-setup.md
    - docs/rest-api.md

key-decisions:
  - "The sudo check treats only sudo's explicit 'is not allowed to run sudo' answer as a pass, so an unexpected answer or an error also fails and names the account"
  - "MCP selfcheck probes are skipped under --pre-deploy (no release to answer) and when OAuth__PublicBaseUrl is absent"
  - "The bearer-token pattern needs 20 or more token characters after Bearer and the parameter patterns need 16 or more, so a challenge such as Bearer resource_metadata= or a redacted placeholder is not reported"
  - "LEDGER_SUDO_ALLOWED_USERS is split on whitespace and commas, as provision.conf.example documents it space-separated"
  - "The range-freshness test checks the template's non-placeholder ranges against the published page by text match; the phased-out 34.162.x.x rule stays in the offline unit test"

patterns-established:
  - "Every public-exposure rule has an offline test (template unit test, selfcheck logic test, exposure logic test) and one opt-in network test"

requirements-completed: [SEC-04, ADV-10]

duration: 2 sessions
completed: 2026-10-07
actuals:
  tokens: 17000
  tasks: 3
  commits: 3
---

# Phase 3 Plan 06: Exposure as tested configuration Summary

**The public MCP exposure is now code: a proxy template with exact-path routers, provisioning keys, selfchecks that prove the boundary and the end of temporary sudo logins, an outside-in check and a connection guide, all pinned by offline tests.**

## Performance

- **Duration:** 2 sessions (the tracer gate sat between them)
- **Completed:** 2026-10-07
- **Tasks:** 3 (tracer plus two)
- **Files:** 5 created, 9 modified

## Accomplishments

- The template routes the MCP hostname with exactly five public Path values behind Anthropic's range plus home and VPN, and the sign-in paths (`/connect/authorize`, the `/account/` prefix) behind the home and VPN list only. Grafana and REST routers are unchanged. No ipStrategy and no buffering; a unit test with 11 weakened-template cases enforces it.
- A freshly provisioned host gets `OAuth__PublicBaseUrl` and one `OAuth__SignInNetworks__N` per range from `LEDGER_MCP_DOMAIN` and `LEDGER_ADMIN_SSH_SOURCES`.
- `ledger-selfcheck` fails when any account other than root and the allowed list can use sudo or any other account has UID 0 (the SSH-removal todo's optional check, now in place), and proves on the host that `/mcp` answers 401 with the expected `resource_metadata`, that the protected-resource document names the configured resource, that the REST status path answers 404 on the MCP hostname and that the sign-in page answers 404 to a non-home address.
- The selfcheck log scan also looks for bearer tokens, `access_token=` and `refresh_token=` parameters, `code_verifier=` and `otpauth://` links, reporting kind and place only, never the value.
- `build/check-exposure.sh --from outside|inside` prints one PASS or FAIL line per request with expected and actual status and exits 1 on any failure; a connection failure on the MCP host carries the hint that Anthropic could not reach it either.
- `docs/mcp.md` walks through hostname and DNS, the staged rollout, login enrolment, Claude Code, claude.ai (Use your own OAuth client, `ledger-claude-hosted`), Desktop and mobile, re-authorisation, day-to-day use and troubleshooting.

## Task Commits

1. **Task 1 (tracer): Reverse-proxy template and provisioning expose exactly the MCP surface, sign-in kept to home and VPN** - `20a3612` (feat). The tracer gate was approved by the user, including the router and allow-list layout as built.
2. **Task 2: Host proves the MCP boundary and sudo logins; operator exposure check** - `3ac4538` (feat)
3. **Task 3: Connection guide and host setup updates** - `6da003e` (docs)

## Verification Evidence

- Configuration category unit tests: 124/124 at the tracer; the full solution run ends with 1003 total, 1001 passed, 2 skipped, 0 failed (unit and integration, `dotnet test --solution Ledger.slnx`).
- `deploy/tests/provision-logic-test.sh` 38/38 and `deploy/tests/render-templates-test.sh` 21/21 at the tracer.
- `deploy/tests/selfcheck-logic-test.sh` and `build/tests/check-exposure-logic-test.sh` pass; the exposure suite covers both matrices with every deviation named in the plan plus malformed arguments.
- `build/lint.sh` (all of repo-rules, workflows, shell, secrets, script-tests, observability) passes after the last commit.
- `LEDGER_LINT_NETWORK=1`-style run of `build/tests/anthropic-ranges-network-test.sh` against Anthropic's real IP address page passed: `160.79.104.0/21` is listed. This request went to Anthropic's public documentation only; no homelab host, proxy or router was contacted and `check-exposure.sh` was never run against any real hostname.

## Decisions Made

See key-decisions above. Facts the docs state but this plan cannot verify from the repository (no source address translation in front of the proxy, WAN address not CGNAT, the certificate resolver issuing the new hostname's certificate) are written as operator confirmations; the go-live plans confirm them.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `LEDGER_MCP_DOMAIN` and `LEDGER_SUDO_ALLOWED_USERS` added to the provisioning key allow-list**
- **Found during:** Task 1
- **Issue:** `deploy/provision.sh` rejects configuration keys it does not know, so the new keys in `provision.conf.example` would have stopped provisioning. The file is not in the plan's files list.
- **Fix:** Added both keys to `PROVISION_CONF_ALLOWED_KEYS`.
- **Files modified:** deploy/provision.sh
- **Commit:** 20a3612

**2. [Rule 2 - Missing critical functionality] MCP hostname validation at provisioning**
- **Found during:** Task 1
- **Issue:** The domain flows into the env file and the router rule; a value with spaces, upper case or shell characters would write a bad public address.
- **Fix:** `accounts_valid_mcp_domain` refuses anything that is not a plain lower-case hostname, with unit coverage.
- **Files modified:** deploy/provision.d/20-accounts.sh, deploy/tests/provision-logic-test.sh
- **Commit:** 20a3612

### Notes (not deviations)

- The selfcheck's MCP probes are skipped under `--pre-deploy`. The plan did not say, and with no release installed the probes would only report a missing app.
- The parallel quick fix that encrypts authenticator secrets at rest touches other files; the guide describes enrolment through `ledger-login` and makes no statement about how the secret is stored.

## Issues Encountered

None that remain open. The test stub for the host's `curl` had to learn to read the Host header and write headers and body to files, because the MCP probes use `--dump-header` and `--output`.

## Known Stubs

None.

## Threat Flags

None. The plan adds no new endpoint; it only restates and tests the exposure the application already enforces. Threat register status: T-03-06-01, -02, -04, -05, -06 mitigated by tests (template unit test, no ipStrategy rule, 34.162 rule plus network test, log patterns, placeholders only); T-03-06-03 mitigated by the sudo and UID 0 selfcheck; T-03-06-07 mitigated by the DNS-only and no-AAAA guidance and troubleshooting entry.

## Next Phase Readiness

The go-live plans can apply the template, run `build/check-exposure.sh` from inside and outside, run the selfcheck with zero failures after removing the temporary sudo logins, and follow `docs/mcp.md` to connect claude.ai.

## Self-Check: PASSED

- Files present: build/check-exposure.sh, build/tests/check-exposure-logic-test.sh, build/tests/anthropic-ranges-network-test.sh, docs/mcp.md, Ledger.UnitTests/Configuration/TraefikTemplateTests.cs
- Commits present: 20a3612, 3ac4538, 6da003e
