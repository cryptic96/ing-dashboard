---
phase: 03-claude-reads-the-ledger
plan: 07
subsystem: release
tags: [release, deploy, traefik, oauth, claude-code, home-network]
status: complete
requires: [03-04, 03-05, 03-06]
provides:
  - Release v0.3.0 merged, tagged, built, attested, published and installed on the household host
  - Host configured for the MCP endpoint on the home-network/VPN allowlist only (public route not yet open)
  - One enrolled login with a confirmed authenticator and one Claude Code grant
  - First real-client proof of discovery, PKCE, consent and all three query tools against the real ledger
affects: [03-08]
tech-stack:
  added: []
  patterns:
    - "Host changes on the live servers are run by the operator from exact commands; Claude verifies read-only afterwards"
    - "Upgrade with a new installer prerequisite: provision from the release tag before approving the deploy"
key-files:
  created: []
  modified: []
key-decisions:
  - "Code review and the proxy-hop TLS fix ran before the release (operator decisions), so v0.3.0 shipped with them"
  - "Claude Code was connected through the desktop app's Code tab with a project .mcp.json (oauth.clientId + oauth.callbackPort) because the CLI is not installed; equivalent to claude mcp add --client-id --callback-port"
  - "MCP hostname: a dedicated subdomain beside the existing ledger hostnames (recorded only in local, untracked notes)"
requirements-completed: []
duration: "~2.5h wall clock across the operator steps"
completed: 2026-10-08
actuals:
  tasks: 2
  commits: 0
  files_changed: 0
---

# Plan 03-07: Release v0.3.0 and prove it from the home network

Claude Code on the home network answered an overview, a September spending question and a last-week search from the household's real transactions through the deployed endpoint, and the operator confirmed all three against the bank's app. Nothing is reachable from the internet yet.

## Performance

- Release candidate: packaged in about 25 s; full suite with the packaged migration bundle 1139 tests, 0 failed, 1 skipped (needs real bank captures); the two bundle tests that skip without a bundle ran and passed.
- Release build on GitHub: first attempt failed on one integration test (test-host free-port race, unrelated to the release); the failed job was re-run on the same tagged commit and passed. Fixed afterwards in quick task 261008-evv.

## Accomplishments

1. **Release candidate proven** (Task 1): `build/package-release.sh` for 0.3.0, full suite against the packaged migrations, `Ledger.Dashboards -- check` up to date, `build/lint.sh` all six checks. Manifest lists 11 migrations including `AddOAuthAndLogins` and `TrackLastTotpTimeStep`.
2. **Pre-push personal-data scan** of the 72 unpushed commits found the real server subnet in a pause handoff file; that one unpushed commit was rewritten (operator-approved approach) before anything was pushed. All pushed commits scan clean; the secrets lint passes on the full history.
3. **PR and release**: PR to main opened and, at the operator's request, merged with a merge commit; annotated tag `v0.3.0` on the merge commit; release workflow built, attested and (after operator approval) published.
4. **Upgrade order honoured**: the operator re-ran provisioning from the `v0.3.0` checkout before approving the deploy, creating the backend certificate (CN `ledger-backend`, EC P-256, ten years) and installing the new installer, selfcheck, `ledger-login` and `ledger-grants`. The installer then applied both migrations with the migrator role and the app came up healthy serving HTTPS on 5080.
5. **Proxy route**: the route file (prepared by Claude outside the repo, saved by the operator) moved the REST/MCP service to HTTPS with the pinned `ledger-backend` serversTransport (embedded certificate fingerprint matched the host's) and added the two MCP routers on the home/VPN allowlist only. Grafana unchanged.
6. **Env keys**: the operator appended `OAuth__PublicBaseUrl` and two `OAuth__SignInNetworks__N` lines (non-secret) and restarted the app; `ReverseProxy__KnownProxies` was already present.
7. **Login**: the operator enrolled one login in their own terminal (password and authenticator secret never passed through Claude; secret stored in their password manager) and confirmed it.
8. **Claude Code**: connected from the desktop app's Code tab in a separate folder; sign-in with password and one-time code, consent approved; four tools listed; the operator confirmed the overview, September totals by counterparty and last week's transactions look right compared to the bank's app.

## Task Commits

None — this plan changes no repository files. Related commits made while executing it are recorded with their quick tasks and the review fix report.

## Verification Evidence

- On the host (read-only): current release `0.3.0`; migrations `AddOAuthAndLogins` and `TrackLastTotpTimeStep` applied; `/health` Healthy; env key counts `OAuth__PublicBaseUrl` 1, `OAuth__SignInNetworks__` 2, `ReverseProxy__KnownProxies__` 1 (values never read).
- Through the proxy from the home network: REST status without a key 401 (pinned TLS hop works); Grafana 200; `POST /mcp` 401 with the Bearer `resource_metadata` challenge; protected-resource document names the canonical resource; authorization-server metadata: canonical issuer, PKCE `S256` only, no registration endpoint; REST path on the MCP host 404; sign-in page 200 from the LAN.
- `build/check-exposure.sh --from inside` with the real hostnames (passed as arguments, never committed): 12 PASS, 0 FAIL.
- Metrics after the client checks: `ledger_mcp_tool_calls_total` ledger_overview 1, money_totals 3, search_transactions 1; `ledger_oauth_grants_created_total` 1; no rejected-token series above zero.
- `ledger-grants list`: exactly one valid grant, client `ledger-claude-code`, two live tokens. `ledger-login list`: one login, second factor yes, one active grant.
- `ledger-selfcheck` (no restart option): 61 PASS, 1 FAIL — the expected FAIL is Claude's temporary sudo-capable login, removed at the start of the go-live plan. The log scan found no secret-shaped text in the journal or log directories; the TLS handshake on 5080 presents exactly the certificate on disk.
- Real-client compatibility (research assumptions on scope handling, issuer and resource forms, stateless mode): Claude Code accepted the issuer and resource forms and the stateless server without any fallback; no rejected tokens.

## Decisions Made

See key-decisions above. The operator also asked Claude to merge the PR and push the tag (the plan had reserved both for the operator).

## Deviations from Plan

1. **Operator steps executed differently** — Claude's attempt to run provisioning over SSH was blocked by the auto-mode classifier as a production deploy; from then on every change on the live hosts (provisioning, route file, env keys, restart) was run by the operator from exact commands, and Claude only verified read-only. This also covered steps the plan had assigned to Claude (route file, env keys).
2. **Claude Code via the desktop Code tab** instead of the CLI: a project `.mcp.json` in a separate local folder with `oauth.clientId` and `oauth.callbackPort`; same client and callback port as planned.
3. **Public DNS**: a public record for the MCP hostname already exists but resolves to the internal proxy address (matching the other ledger hostnames). Harmless while the endpoint is internal; it must point at the home IPv4 address (DNS-only, no AAAA) in the go-live plan.
4. **Release build re-run** after a flaky test (see Performance); fixed in quick task 261008-evv after the release.

## Issues Encountered

- Minor: the protected-resource document lists `"header"` twice in `bearer_methods_supported`. Harmless; fix in the next release.
- Two personal-data leaks into unpushed history (an example account number quoted in the code review, the real subnet in a handoff file) were caught by pre-push scans and scrubbed by rewriting unpushed commits. The secrets lint's private-IPv4 rule does not flag `.0` network addresses.

## Known Stubs

None.

## Threat Flags

- Claude's temporary SSH logins on the ledger host and the reverse proxy still exist (by design until the go-live plan's first step); the selfcheck flags the ledger-host login.

## Next Phase Readiness

Ready for the go-live plan: remove Claude's temporary access first, re-check Anthropic's published range, point the public DNS record at the home IPv4 address, add the Anthropic range to the public MCP router, verify from outside, then connect claude.ai, Desktop and mobile.

## Self-Check: PASSED
