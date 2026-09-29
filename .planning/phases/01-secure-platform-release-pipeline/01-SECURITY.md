---
phase: 01
slug: secure-platform-release-pipeline
status: verified
# threats_open = count of OPEN threats at or above workflow.security_block_on severity (the blocking gate)
threats_open: 0
asvs_level: 2
block_on: high
created: 2026-09-29
---

# Phase 01 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

The register below is the plan-time threat model of all twelve plans (91 threats), verified against the code at ASVS level 2: each mitigation must exist and sit at the trust boundary it protects. Host and network behaviour was confirmed on the real system during go-live (plans 01-11 and 01-12). Three gaps found by this audit were fixed before sign-off.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| Internet → home network | Nothing from this phase is internet-facing; the only future public path is the Claude endpoint in a later phase | none |
| LAN/VPN → reverse proxy → host | The reverse proxy admits only LAN and VPN source ranges and routes only Grafana and the REST API's /api/ prefix; the host firewall admits those ports only from the proxy | dashboards (future financial aggregates), REST calls with API keys |
| GitHub → host | The host pulls published releases over unauthenticated HTTPS and verifies each attestation (workflow, tag, commit on main) before unpacking; no GitHub-executed code runs on the host | release artifacts, Sigstore bundles |
| Operator approval → publish | A release is published only after the owner approves the deploy environment; the publish job re-verifies checksum and attestation | release metadata |
| App → database | Unix socket only, peer authentication, one least-privilege role per OS user (runtime, migrator, reporting reader, backup) | all ledger data |
| Host → mail relay | Alert and deploy-outcome emails over the LAN, plaintext by decision | alert names, versions and timestamps only (no financial data or secrets) |
| Host → operator password manager | Data Protection certificate and password, the backup identity and Grafana credentials leave the host only by the operator's hand; the backup identity never lives on the host | secrets |
| Repository (public) | No personal data, secrets or real infrastructure values; enforced by the secret scan (default rules plus IBAN, private-address and email rules) and review | source, docs, planning |

---

## Threat Register

| Threat ID | Category | Component | Severity | Disposition | Mitigation | Status |
|-----------|----------|-----------|----------|-------------|------------|--------|
| T-01-01 | Elevation of Privilege | ledger_runtime role | high | mitigate | Default privileges grant DML only; DatabaseRoleTests assert 42501 on CREATE/ALTER/DROP/CREATE SCHEMA; history table DML revoked in InitialCreate | closed |
| T-01-02 | Information Disclosure | grafana_reader role | high | mitigate | Only USAGE on reporting plus default SELECT on reporting objects; tests assert 42501 on public tables and success only on a reporting view | closed |
| T-01-03 | Denial of Service | Data Protection key ring | high | mitigate | Keys persisted in data_protection_keys, SetApplicationName("HouseholdLedger"), canary health check; restart/redeploy test with a different content ro… | closed |
| T-01-04 | Information Disclosure | ops endpoint | high | mitigate | OpsEndpoint refuses non-loopback URLs at startup (unit tests); health and metrics filtered by connection local port via UseHealthChecks(path, port) a… | closed |
| T-01-05 | Spoofing | Host header on API port | medium | mitigate | No Host-header-based routing for ops paths; integration test requests /health and /metrics on the API port and expects non-200 | closed |
| T-01-06 | Information Disclosure | test admin connection string | medium | mitigate | Only in dotnet user-secrets (local) or CI environment variable; fixture error message names the key, never the value; checkpoint keeps the password o… | closed |
| T-01-07 | Tampering | schema changes from the running app | medium | mitigate | Service contains no migration call (acceptance grep); runtime role cannot run DDL (database-enforced) | closed |
| T-01-08 | Tampering | user's local container state | low | accept | Tests create passwordless roles and throwaway databases only with the user's explicit consent in Task 1; databases are dropped on dispose | closed |
| T-01-SC | Tampering | NuGet restore | high | mitigate | Exact versions from the research legitimacy audit; RestorePackagesWithLockFile with committed packages.lock.json and restore --locked-mode in the pac… | closed |
| T-02-01 | Tampering | third-party actions | high | mitigate | zizmor hash-pin policy for every action, self-tests prove short SHAs and tags fail, Dependabot keeps SHAs current | closed |
| T-02-02 | Elevation of Privilege | run: steps | high | mitigate | zizmor template-injection and actionlint; self-test fixture proves an interpolated expression fails; values reach shell only via env: | closed |
| T-02-03 | Information Disclosure | repository history | high | mitigate | gitleaks over full history and working tree with default rules; shallow clones refused; planted-secret self-test | closed |
| T-02-04 | Information Disclosure | personal data in repo | high | mitigate | Custom rules for Dutch IBANs, RFC 1918 addresses and non-example email addresses; self-tests prove adjacency (RFC 5737 and example.com pass, private… | closed (fixed during audit, 66c07e5) |
| T-02-05 | Elevation of Privilege | GITHUB_TOKEN | medium | mitigate | Top-level permissions {}, jobs contents: read, persist-credentials false | closed |
| T-02-06 | Tampering | scanner images | medium | mitigate | Images pinned by digest in compose.yaml, updated by Dependabot docker-compose ecosystem | closed |
| T-02-07 | Tampering | CI PostgreSQL service image | low | accept | Test-only container with no secrets and no network exposure beyond the job; pinned by digest, bumped manually because Dependabot does not update work… | closed |
| T-02-SC | Tampering | NuGet restore in CI | high | mitigate | restore --locked-mode through the package script against committed lock files | closed |
| T-03-01 | Elevation of Privilege | tag string in shell | high | mitigate | Tag via env only; strict regex before any other use; no expression in run: (lint + acceptance grep) | closed |
| T-03-02 | Tampering | release from a non-main commit | high | mitigate | validate-release-tag.sh checks reachability from origin/main and tag commit equals GITHUB_SHA; tag ruleset restricts creation (documented, set in go-… | closed |
| T-03-03 | Tampering | artifact swapped before publish | high | mitigate | Publish job re-verifies the draft zip against its bundle with signer workflow, tag source ref and GitHub-hosted runner checks; immutable releases loc… | closed |
| T-03-04 | Spoofing | unapproved publish | high | mitigate | publish job bound to environment deploy with the operator as required reviewer and a v-tag deployment policy | closed |
| T-03-05 | Elevation of Privilege | job token scope | medium | mitigate | Workflow permissions {}; build gets only contents/id-token/attestations write; publish only contents write; persist-credentials false | closed |
| T-03-06 | Tampering | cache poisoning | medium | mitigate | No caches in the release workflow | closed |
| T-03-07 | Repudiation | who released what | low | accept | Environment approvals and releases are recorded by GitHub; the attestation records workflow, ref and commit; no extra audit log needed at household s… | closed |
| T-04-01 | Tampering | downloaded release zip | critical | mitigate | Offline gh attestation verify --bundle with repo, signer workflow, tag source ref and GitHub-hosted runner checks before unpacking; network test prov… | closed |
| T-04-02 | Spoofing | release built from an off-main commit | high | mitigate | Attested sourceRepositoryDigest must be identical to or behind main via the compare API; unreachable API refuses | closed |
| T-04-03 | Elevation of Privilege | verification bypass | critical | mitigate | No flag, env var or fallback continues after failed verification (acceptance grep + review); LEDGER_DEPLOY_ROOT only relocates paths | closed |
| T-04-04 | Information Disclosure | GitHub credentials on the LXC | high | mitigate | gh runs with tokens unset and an empty config dir; curl sends no Authorization header (acceptance grep) | closed |
| T-04-05 | Tampering | config injection via deploy.conf | medium | mitigate | Config parsed with an allow-list, never sourced; refused if not root-owned or group/world-writable | closed |
| T-04-06 | Denial of Service | failed deploy leaves app down | high | mitigate | Health and version checks with automatic rollback when no migration ran; failure after a migration stops loudly and alerts (email + metric) | closed |
| T-04-07 | Tampering | downgrade to a vulnerable release | medium | mitigate | Versions not newer than the active one are refused except via the explicit rollback subcommand with a migration-subset check | closed |
| T-04-08 | Denial of Service | concurrent or interrupted installs | medium | mitigate | flock serialisation; staging directory renamed into place; atomic symlink swap | closed |
| T-04-09 | Elevation of Privilege | app process compromise | medium | mitigate | ledger.service sandboxing (ProtectSystem=strict, empty capability set, NoNewPrivileges, restricted address families); bundle runs as ledger_migrator,… | closed |
| T-04-10 | Information Disclosure | deploy email | medium | mitigate | Body limited to version, result and timestamps (logic test asserts content) | closed |
| T-04-11 | Denial of Service | GitHub unauthenticated rate limit | low | accept | One releases/latest call per 5 minutes plus one compare call per install stays far below 60 per hour; failures are retried on the next poll and visib… | closed |
| T-05-01 | Information Disclosure | backup files | high | mitigate | age encryption to public keys only; recipients file validated to contain only age1 keys; directory 700 ledger_backup, files umask 077 | closed |
| T-05-02 | Information Disclosure | secrets inside backups | high | mitigate | Backup reads only pg_dump output; never reads the env file, certificate or identities (acceptance grep) | closed |
| T-05-03 | Information Disclosure | identity during restore | medium | mitigate | Identity passed via stdin or tmpfs straight to age, never copied; script warns about on-disk identity files | closed |
| T-05-04 | Elevation of Privilege | backup role | medium | mitigate | ledger_backup has pg_read_all_data only (no write); unit runs sandboxed with write access to two directories | closed |
| T-05-05 | Denial of Service | silent backup failure or staleness | high | mitigate | pipefail, size and header checks; per-reason success/failure/timestamp metrics feed the stale (about 26 h) and failed alerts | closed (fixed during audit, 9a5877b) |
| T-05-06 | Denial of Service | disk loss destroys data and backups | high | accept | User decision: backups local only; offsite copies deferred; documented as accepted risk | closed |
| T-05-07 | Tampering | live restore overwriting good data | medium | mitigate | Typed confirmation; previous database kept under a timestamped name; drill mode rehearses without touching live data | closed |
| T-06-01 | Spoofing | REST endpoints | high | mitigate | Fallback policy requires an authenticated caller; endpoint-enumerating test proves 401 on every endpoint; no AllowAnonymous anywhere | closed |
| T-06-02 | Information Disclosure | stored keys | high | mitigate | Only key id + SHA-256 of a 256-bit random secret stored; test proves no secret substring in any column; token shown once | closed |
| T-06-03 | Information Disclosure | keys in logs | high | mitigate | Handler never logs presented values; log-capture test asserts absence | closed |
| T-06-04 | Elevation of Privilege | revoked or leaked key | medium | mitigate | Per-request database validation; revocation effective on the next request (test) | closed |
| T-06-05 | Spoofing | forwarded client address | medium | mitigate | X-Forwarded-For honoured only from KnownProxies (Traefik), ForwardLimit 1 | closed |
| T-06-06 | Denial of Service | malformed or oversized headers | low | mitigate | Strict parse before any database call; tests assert 401, not 500 | closed |
| T-06-07 | Spoofing | online guessing of keys | low | accept | 256-bit secrets make guessing infeasible; the API is LAN/VPN-only behind the Traefik allowlist; rate limiting is not added at household scale | closed |
| T-06-08 | Tampering | CLI argument injection via wrapper | low | mitigate | Wrapper accepts only three verbs and names matching ^[a-z][a-z0-9-]{1,31}$, passes arguments as argv via systemd-run | closed |
| T-07-01 | Information Disclosure | data_protection_keys at rest | high | mitigate | ProtectKeysWithCertificate; test asserts encryptedSecret present and no plaintext master key | closed |
| T-07-02 | Denial of Service | key ring unreadable after restart on Linux | high | mitigate | UnprotectKeysWithAnyCertificate with the same certificate; restart/redeploy test with a different content root | closed |
| T-07-03 | Information Disclosure | secrets in logs | high | mitigate | Hosting diagnostics at Warning, no HTTP logging middleware, no EF sensitive logging; sentinel test over raw/URL-encoded/base64 forms | closed |
| T-07-04 | Information Disclosure | secrets in exception messages and error responses | high | mitigate | Validator and setup name keys only; problem details without exception details outside Development; sentinel tests | closed |
| T-07-05 | Information Disclosure | secrets in metric labels | medium | mitigate | Route-pattern labels only; sentinel test over /metrics | closed |
| T-07-06 | Elevation of Privilege | app connecting as superuser or over TCP | high | mitigate | Production validator requires socket host, no password, ledger_runtime username | closed |
| T-07-07 | Tampering | canary healed after key loss | medium | mitigate | Health check read-only; wrong-certificate test asserts Unhealthy and unchanged row | closed |
| T-07-08 | Information Disclosure | certificate expiry | low | accept | Data Protection uses the certificate's key pair for unprotect regardless of expiry; a long validity self-signed certificate is generated by provision… | closed |
| T-08-01 | Information Disclosure | anonymous access, snapshots, public dashboards | high | mitigate | Disabled in grafana.ini; container smoke test asserts 401 for anonymous API calls; hardening re-installed on every deploy | closed |
| T-08-02 | Elevation of Privilege | viewer edits or arbitrary queries | medium | mitigate | auto_assign_org_role Viewer, viewers_can_edit false, datasources not editable; database role limits any query to reporting views | closed |
| T-08-03 | Information Disclosure | datasource credentials | medium | mitigate | No password anywhere; peer authentication maps OS user grafana to grafana_reader | closed |
| T-08-04 | Information Disclosure | alert email content | medium | mitigate | Static titles/annotations naming rule and service only; no financial queries in alert rules | closed |
| T-08-05 | Denial of Service | silent monitoring failure | medium | mitigate | noData and execError count as alerting for liveness rules, so a dead Prometheus or node_exporter alerts | closed |
| T-08-06 | Information Disclosure | Prometheus exposed | medium | mitigate | Scrape targets and listeners loopback only (listener flags set in provisioning, plan 01-10); no Traefik route | closed |
| T-08-07 | Spoofing | Grafana login brute force | low | accept | Brute-force protection stays enabled; Grafana is reachable only from LAN/VPN via the Traefik allowlist | closed |
| T-09-SC | Tampering | apt repositories and Prometheus tarball | high | mitigate | Key fingerprints pinned in versions.env and checked before trusting a repository; signed-by keyrings; Prometheus SHA-256 checked before extraction; n… | closed |
| T-09-01 | Information Disclosure | database over the network | high | mitigate | listen_addresses empty; self-check fails if any TCP socket belongs to postgres; pg_hba has no host lines | closed |
| T-09-02 | Elevation of Privilege | cross-role access | high | mitigate | pg_hba per-role peer lines with explicit ident maps; self-check proves each OS user reaches only its own role and not the migrator | closed |
| T-09-03 | Information Disclosure | env file and certificate | high | mitigate | 640 root:ledger; password generated locally, passed via environment, never printed; files never overwritten on re-run | closed |
| T-09-04 | Elevation of Privilege | service accounts | medium | mitigate | System users without shells or homes | closed |
| T-09-05 | Tampering | provision.conf injection | medium | mitigate | Allow-list parser, no evaluation, root-owned file required | closed |
| T-09-06 | Denial of Service | unattended major upgrades | low | mitigate | Grafana pinned to its minor line; PostgreSQL major is a separate package; Ubuntu security updates stay automatic | closed |
| T-10-01 | Information Disclosure | Grafana/API reachable beyond LAN/VPN | high | mitigate | Traefik ipAllowList template; nftables admits 5080/3000 only from the Traefik address; no public route; external reachability step in the guide and g… | closed |
| T-10-02 | Information Disclosure | Prometheus, node_exporter, ops endpoint | medium | mitigate | Loopback listeners; no Traefik route; selfcheck fails on non-loopback binding | closed |
| T-10-03 | Denial of Service | firewall misconfiguration or partial reload | medium | mitigate | nft -c before applying; single-transaction load keeps the old ruleset on failure; pct enter documented as recovery | closed |
| T-10-04 | Information Disclosure | Grafana passwords in process list or logs | medium | mitigate | curl --config - and --data @- on stdin; hidden prompts; acceptance grep forbids -u/--user | closed (fixed during audit, 71753dc) |
| T-10-05 | Elevation of Privilege | default Grafana admin or elevated partner account | high | mitigate | Admin renamed with a strong password; viewers forced to Viewer; selfcheck verifies roles | closed |
| T-10-06 | Information Disclosure | age identity or GitHub credential left on the LXC | high | mitigate | Provisioning accepts only public keys; selfcheck scans for identities, gh logins, tokens and runners | closed |
| T-10-07 | Spoofing | forged X-Forwarded-For | medium | mitigate | App trusts forwarded headers only from ReverseProxy__KnownProxies__0 (the Traefik address from provision.conf) | closed |
| T-10-08 | Tampering | unattended OS updates breaking services | low | accept | Only Ubuntu security updates are automatic; selfcheck and alerts detect a broken service | closed |
| T-11-01 | Spoofing | unauthorised tag creation or publish | high | mitigate | Tag ruleset plus deploy environment reviewer; verified by build/check-github-settings.sh | closed |
| T-11-02 | Elevation of Privilege | outside-contributor workflow runs | high | mitigate | Approval required for all outside contributors; verified by the script; no runner can execute on the LXC because none exists (runner count 0 plus sel… | closed |
| T-11-03 | Tampering | release assets changed after publish | medium | mitigate | Immutable releases enabled and verified | closed |
| T-11-04 | Information Disclosure | secrets pushed to the public repository | high | mitigate | Secret scanning and push protection enabled and verified; gitleaks in CI | closed |
| T-11-05 | Information Disclosure | secrets lost or left on the LXC during setup | high | mitigate | Guide steps store certificate, password, identity and admin credentials in the password manager; selfcheck scans for identities and GitHub credentials | closed |
| T-11-06 | Information Disclosure | real infrastructure identifiers committed | medium | mitigate | Real values go only into server-side files; acceptance checks git status; gitleaks custom rules in CI | closed |
| T-12-01 | Tampering | first published artifact | high | mitigate | Workstation verification with the installer's own functions plus the LXC's own verification; modified-copy refusal observed on both | closed |
| T-12-02 | Spoofing | unapproved release reaching the LXC | high | mitigate | Deploy environment approval observed in the run; LXC installs only published releases | closed |
| T-12-03 | Information Disclosure | identity exposure during the drill | medium | mitigate | Identity pasted to stdin only; restore script never copies it | closed |
| T-12-04 | Information Disclosure | services reachable from the internet | high | mitigate | External reachability check from mobile data; firewall and Traefik allowlist already verified by selfcheck | closed |
| T-12-05 | Repudiation | unrecorded acceptance | low | mitigate | Every check's output recorded in the summary with its date | closed |

*Status: open · closed · open — below high threshold (non-blocking)*
*Severity: critical > high > medium > low — only open threats at or above workflow.security_block_on count toward threats_open*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Gaps Closed During This Audit

| Threat | Severity | Gap | Fix |
|---|---|---|---|
| T-05-05 | high | Textfile metrics were written 0600, so the node exporter could not read the backup, deploy and restore-drill metrics; the backup and poll-staleness alerts sat in NoData and fired continuously, and a real failed backup would have looked the same | 9a5877b: files written 0644 (no secrets in them), test under the backup unit's strict umask; confirmed on the host: scrape error 0 and all series present in Prometheus |
| T-10-04 | medium | Grafana admin and viewer passwords were passed to jq as command-line arguments, readable from the process table while jq ran | 71753dc: passwords reach jq through its environment; a jq wrapper test confirms they are absent from its arguments |
| T-02-04 | high | The Dutch IBAN rule matched only the compact uppercase form | 66c07e5: grouped and lowercase forms detected; self-test covers all three; mutation run confirms the old rule fails it |

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-01-01 | T-01-08 | Tests create passwordless roles and throwaway databases in the operator's own local PostgreSQL only with the operator's explicit consent; the databases are dropped on dispose. | operator (plan threat model) | 2026-09-29 |
| AR-01-02 | T-02-07 | The CI PostgreSQL service image is test-only, holds no secrets and is not exposed beyond the job; it is pinned by digest but updated by hand, because Dependabot does not update workflow service images. | operator (plan threat model) | 2026-09-29 |
| AR-01-03 | T-03-07 | GitHub records environment approvals and releases, and the attestation records workflow, ref and commit; no extra audit log is needed at household scale. | operator (plan threat model) | 2026-09-29 |
| AR-01-04 | T-04-11 | One unauthenticated releases/latest call every 5 minutes plus one compare call per install stays far below the 60-per-hour limit; failures retry on the next poll and show in the poll metrics. | operator (plan threat model) | 2026-09-29 |
| AR-01-05 | T-05-06 | Backups stay on the host's own disk, so losing the disk, container or host loses data and backups together; the operator chose local-only backups, and offsite copies are a later decision (docs/backup-restore.md). | operator (plan threat model) | 2026-09-29 |
| AR-01-06 | T-06-07 | API keys carry 256-bit random secrets, which makes online guessing infeasible, and the API is reachable only from the LAN/VPN through the reverse-proxy allow-list, so no rate limiting is needed at household scale. | operator (plan threat model) | 2026-09-29 |
| AR-01-07 | T-07-08 | Unprotecting uses the certificate's key pair whatever its expiry; provisioning creates a 10-year self-signed certificate, and rotation works because UnprotectKeysWithAnyCertificate can hold the old and new certificates. | operator (plan threat model) | 2026-09-29 |
| AR-01-08 | T-08-07 | Grafana's own brute-force protection stays on, and Grafana is reachable only from the LAN/VPN through the reverse-proxy allow-list. | operator (plan threat model) | 2026-09-29 |
| AR-01-09 | T-10-08 | Only Ubuntu security updates install automatically (third-party repositories are not allowed origins, and Grafana is version-pinned); a broken service is caught by the selfcheck and by the service-down and health alerts. | operator (plan threat model) | 2026-09-29 |

*Accepted risks do not resurface in future audit runs.*

---

## Non-Blocking Hardening Notes

Observations from the audit that are not threats in the register and do not block the phase:

- The deploy poll unit and the apikey transient unit lack systemd sandboxing, and the selfcheck's secret scan does not cover logs or the journal (tracked as a todo).
- The orchestrator's temporary SSH access to the host and reverse proxy must be removed before real bank data arrives (tracked as a todo).
- A promtool or provisioning failure after activation, with no migration, exits without the installer's own metric or email; the service-down alerts do not cover a release that was activated but never started.
- The backup script exits through its error path without writing failure metrics when the recipients check fails or another backup holds the lock; only the 26-hour staleness alert catches those cases.
- The socket-only connection-string rule checks that the host starts with a slash; a comma-separated multi-host value could pass (the socket-only listener, the peer-only pg_hba and the root-owned env file limit the impact).
- The Grafana apt pin is written only when missing, so a later version bump will not refresh it.
- The `pct enter` firewall-recovery step is printed by provisioning but is not in the setup guide.
- The reverse-proxy allow-list is proven by a live 403 observation only; there is no repeatable drift check from the host.
- The secret scan has no rule for real hostnames or domains; those rely on review.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-09-29 | 91 | 88 | 3 | gsd-security-auditor ×3 (plans 01-01–01-04, 01-05–01-08, 01-09–01-12) |
| 2026-09-29 | 91 | 91 | 0 | orchestrator, after fixing T-05-05, T-10-04 and T-02-04 |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-09-29
