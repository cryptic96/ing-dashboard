---
quick_id: 261008-0av
type: quick
subsystem: deploy
tags: [tls, kestrel, traefik, certificate-pinning, installer, selfcheck]
status: complete
key-files:
  created:
    - deploy/lib/backend-tls.sh
    - deploy/tests/backend-tls-logic-test.sh
    - Ledger.IntegrationTests/Security/BackendTlsTests.cs
    - .planning/todos/pending/2026-10-08-encrypt-the-proxy-to-grafana-hop.md
  modified:
    - Ledger.Service/appsettings.Production.json
    - Ledger.Service/Hosting/ProductionConfigurationValidator.cs
    - deploy/provision.d/20-accounts.sh
    - deploy/lib/deploy.sh
    - deploy/bin/ledger-deploy
    - deploy/bin/ledger-selfcheck
    - deploy/ledger.env.example
    - deploy/traefik/ledger.yml.example
    - deploy/tests/ledger-deploy-logic-test.sh
    - deploy/tests/selfcheck-logic-test.sh
    - Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs
    - Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs
    - Ledger.UnitTests/Configuration/TraefikTemplateTests.cs
    - Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs
    - Ledger.IntegrationTests/Infrastructure/McpTestHost.cs
    - Ledger.IntegrationTests/Infrastructure/TestCertificates.cs
    - docs/lxc-setup.md
    - docs/mcp.md
    - docs/deploy.md
actuals:
  tokens: 17000
  tasks: 2
  commits: 2
---

# Quick 261008-0av: Encrypt the proxy-to-application hop with a pinned certificate

Port 5080 now speaks only TLS, from a long-lived self-signed EC P-256 certificate the ledger host generates for itself (name `ledger-backend`), and Traefik connects over HTTPS through a `ledger-backend` serversTransport that trusts exactly that certificate. Passwords, one-time codes, OAuth tokens, session cookies and every MCP answer no longer cross the server segment in clear text, and nothing but the ledger host can pass as the application.

## What changed

**Task 1 - the application serves HTTPS from a host-generated pair (6b9bf5e)**

- `deploy/lib/backend-tls.sh` holds one idempotent helper, `ledger_ensure_backend_tls`, used by both provisioning (`20-accounts.sh`) and the installer. When neither file exists it creates `/etc/ledger/backend-tls.crt` (root:ledger 0644) and `/etc/ledger/backend-tls.key` (root:ledger 0640): EC P-256, CN and SAN `DNS:ledger-backend` only (no addresses), keyUsage digitalSignature, EKU serverAuth, 3650 days. The key is created under umask 077 in a 0700 work directory, installed under temporary names and renamed into place, then shredded from the work directory. An existing pair is never regenerated; a lone file or a certificate and key that do not match stop the caller with a message naming only the files. It logs the certificate's SHA-256 fingerprint and never prints key material.
- The installer (`ledger_install_verified_release`) calls the helper first, before unpacking, migrating, stopping or restarting anything. The release carrying this change also migrates, which means no automatic rollback, so a start without the certificate must be impossible.
- Production settings: `Ledger.Service/appsettings.Production.json` (the existing production settings file) now sets the `Api` endpoint to `https://0.0.0.0:5080` with `Certificate:Path` and `Certificate:KeyPath` at the two paths above. The `Ops` endpoint stays `http://127.0.0.1:5081`. `ledger.env.example` documents it; no env keys are needed, so an existing host's already-rendered env file keeps working.
- `ProductionConfigurationValidator.AddApiEndpointProblems` (same validator that already requires `ReverseProxy:KnownProxies` with the public address): in Production every Api address must be https and both the certificate and the key path must be readable files; failure names only `Kestrel:Endpoints:Api:Url`, `...:Certificate:Path`, `...:Certificate:KeyPath`.
- `ledger-selfcheck`: all probes of 5080 go through `backend_curl` (`--cacert /etc/ledger/backend-tls.crt --resolve ledger-backend:5080:127.0.0.1`, never `-k`). New checks: the handshake presents exactly the certificate on disk (SHA-256 match), a plain-HTTP request to 5080 gets no application response (status 000), the certificate is not expiring within 30 days, and `/etc/ledger/backend-tls.crt` / `.key` have modes 644 / 640 root:ledger. The loopback check of the protected-resource document that the earlier review fix removed is restored, now over the pinned HTTPS connection (the document needs an https scheme, which the port now provides natively with no forwarded header).
- Tests: `backend-tls-logic-test.sh` (creation, modes, owner, key type, name, lifetime, idempotence, half/mismatched pair refusal, pinned verification accepts the name and rejects another, key never printed, provisioning and installer wiring), additions to `ledger-deploy-logic-test.sh` (installer creates the pair before the unpack step, leaves an existing one alone, stops on half a pair with no systemctl and no release directory), `selfcheck-logic-test.sh` (stubbed curl logs every 5080 request; every TLS request pins the cert and name, none passes `-k`, the only plain request is the refusal probe; fingerprint mismatch, no handshake, plain-HTTP answer, near expiry, broken pin all fail), validator and committed-settings unit tests, and `BackendTlsTests` (a real Kestrel host from a generated PEM pair: a client pinning the certificate gets through, any other certificate is rejected, plain HTTP gets nothing, and the challenge, protected-resource document and sign-in 404 work over TLS with no forwarded header).

**Task 2 - Traefik trusts exactly that certificate (b176adf)**

- `deploy/traefik/ledger.yml.example`: `ledger-api` uses `https://192.0.2.20:5080` and `serversTransport: ledger-backend`; `serversTransports.ledger-backend` has `serverName: "ledger-backend"` and `rootCAs` with one inline PEM placeholder (two during rotation). No `insecureSkipVerify`. Grafana (3000) is unchanged.
- `TraefikTemplateTests`: every service on :5080 is https and names the transport; the transport has the expected `serverName` and only inline `BEGIN CERTIFICATE` entries (one, or two for rotation, no file paths, no private key); `insecureSkipVerify` appears nowhere (case-insensitive); eleven weakened variants (http URL, extra http server, missing/unknown transport, wrong or missing serverName, skip-verify added in two places, non-certificate entry, file-path entry, three entries) must each be reported.
- Docs: `docs/lxc-setup.md` (install the PUBLIC certificate, compare fingerprints from the proxy host, rotation with both certificates trusted before the app restarts, cutover for a host that has no certificate yet, Grafana hop still plain HTTP, selfcheck description), `docs/mcp.md` (route file pins the certificate, bad-gateway troubleshooting entry), `docs/deploy.md` (installer ensures the certificate first).
- Todo `2026-10-08-encrypt-the-proxy-to-grafana-hop.md` records the remaining Grafana hop.

## Traefik `rootCAs` forms (source)

Verified against the Traefik v3 documentation before choosing: the routing/services reference for `serversTransport` (`docs/content/routing/services/index.md`, v3.1, fetched 2026-10-08) says `rootCAs` "defines the set of root certificate authorities (as file paths, or data bytes) to use when verifying server certificates", and the file-provider type in `pkg/config/dynamic/http_config.go` (v3.1) is `RootCAs []types.FileOrContent`. Both a path and inline PEM content are therefore accepted; the template uses inline PEM so no extra file is needed in the proxy's dynamic directory.

## Deviations from Plan

### Auto-fixed Issues

None needed in the Rule 1 sense. Choices and additions worth knowing:

**1. [Design choice] Production values live in the existing `appsettings.Production.json`**
- The plan named `appsettings.json` / `appsettings.Development.json`. `appsettings.Production.json` already exists and only applies when `ASPNETCORE_ENVIRONMENT=Production` (which the env file sets and the validator keys off), so local runs and the integration tests (environment `Testing`) keep plain HTTP with no Development override, and an existing host picks the new endpoint up from the release itself with no env-file edit.

**2. [Rule 2 - Critical] Extra safety around the helper and selfcheck**
- The helper also verifies that an existing certificate and key belong together (refusing a mismatch), and the selfcheck adds the 30-day expiry warning for a ten-year certificate. Both are small and fail closed.

**3. [Rule 2 - Verification] Real-host evidence for the restored selfcheck probe**
- Added `TestBackendCertificate` and an optional `backendCertificate` parameter on `LedgerWebApplicationFactory` / `McpTestHost` so a real Kestrel host can serve HTTPS in tests (test infrastructure only), plus `BackendTlsTests`. This is what shows the protected-resource probe works over native HTTPS without any forwarded header.

**4. [Scope] `docs/deploy.md` also touched** (not in the plan's file list) to state that the installer creates the certificate before it starts a release.

## Verification

- `dotnet test --solution Ledger.slnx` (without `--no-restore`): 1139 total, 1137 passed, 2 skipped (both skipped before these changes: the packaged migration bundle), 0 failed.
- `bash build/lint.sh`: repo-rules, workflows, shell, secrets, script-tests and observability all pass (an initial shellcheck SC2016 in the new test was fixed first).
- Deploy logic tests pass: `backend-tls-logic-test.sh`, `ledger-deploy-logic-test.sh`, `selfcheck-logic-test.sh`, `provision-logic-test.sh`, `render-templates-test.sh`.
- Checked by hand with real tools: the generated certificate verifies under `ledger-backend`, fails under any other name, and `curl --cacert --resolve` against `openssl s_server` succeeds only with the pin and name (no pin, wrong name and plain HTTP all gave status 000).

## Rollout notes (for the operator)

- **The new installer must be on the host before the release is approved.** The installer that ships with a release is only installed by provisioning, not by the release. If the currently installed installer starts this release, the app has no certificate, refuses to start, and (because the release migrates) is not rolled back. Run `./deploy/provision.sh` from a checkout of the release tag first; the exact order, including when to save the Traefik change (the REST API and MCP answer with a bad gateway for the minutes between the release going live and the route file being saved), is in `docs/lxc-setup.md`.
- Not verified against a live Traefik (out of scope: no live host touched). Go's verification of a self-signed leaf placed in `rootCAs` is the standard pinning pattern and the certificate carries the right SAN/EKU, but the first rollout should confirm the proxy-side fingerprint comparison from the guide and a successful `/mcp` challenge through the proxy.
- Rolling back to a release from before this change makes the app answer plain HTTP again; the Traefik route file has to be reverted at the same time.

## Known Stubs

None. The Traefik template's `REPLACE-WITH-THE-PUBLIC-CERTIFICATE-FROM-THE-LEDGER-HOST` line is a deliberate operator placeholder like the template's other example values, and the template tests only require its structure.

## Threat Flags

None beyond what the task introduces on purpose (TLS on the proxy-facing port); no new endpoint, auth path or schema.

## Self-Check: PASSED

Created and modified files exist, commits 6b9bf5e and b176adf exist on the branch.
