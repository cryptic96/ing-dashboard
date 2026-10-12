---
created: 2026-10-12T20:00:00.000Z
title: Internal, unauthenticated version endpoint so a deploy can be confirmed from the workstation
area: operations
severity: minor
files:
  - Ledger.Service/Endpoints/
  - Ledger.Service/Hosting/PublicHostGuard.cs
  - deploy/traefik/ledger.yml.example
  - build/check-exposure.sh
  - docs/rest-api.md
---

## Problem

Since Claude's host access was removed, confirming that a release is installed needs the operator to log in to the ledger host (`readlink -f /opt/ledger/current`). Claude runs on the operator's workstation on the home network and can already reach the REST hostname through the reverse proxy, but every REST path requires an API key, which Claude should not hold, and the build-info metric lives on the loopback-only ops port.

## Solution

Add `GET /api/v1/version` on the REST hostname, unauthenticated, answering only `{"version":"x.y.z","commit":"<sha>"}` (no data, accounts, configuration or uptime).

- Reachable from home and VPN only, like the rest of the REST hostname. Not on the MCP hostname and never internet-facing (the public-exposure rule allows only `/mcp` and its OAuth endpoints; a public version string would also fingerprint the exact release of a public codebase).
- `PublicHostGuard`: the path answers 404 on the MCP hostname; tests on both hosts.
- The REST fallback authorization policy needs an explicit anonymous exception for this one path; test that every other REST path still requires a key.
- `build/check-exposure.sh --from inside`: expect 200 with only those two fields; `--from outside`: 403 like the rest of the REST hostname.
- Document it in `docs/rest-api.md` and use it in the release steps ("confirm the installed version").

## Origin

Operator request during the v0.3.1 rollout (2026-10-12), adjusted from "public" to "home/VPN only" for the exposure rule and fingerprinting.
