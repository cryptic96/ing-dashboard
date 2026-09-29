---
phase: 01-secure-platform-release-pipeline
reviewed: 2026-09-29
depth: standard
files_reviewed: 134
status: issues_resolved
findings:
  critical: 3
  warning: 11
  info: 7
  total: 21
  fixed: 10
  deferred: 3
  false_positive: 1
  accepted: 7
---

# Phase 1 Code Review

Standard-depth review of every file the phase changed (134 files, generated EF migrations and lock files excluded), split three ways: the .NET application, the host scripts under deploy/, and the build/CI pipeline with docs. The three original reports follow the disposition table unchanged, except that one realistic-looking placeholder token in the pipeline report was replaced with a description, so the repository's own secret scan stays clean.

## Disposition

| Area | ID | Finding | Outcome |
|---|---|---|---|
| App | CR-01 | apikey CLI skipped the production configuration check | Fixed — 719cc8e |
| App | WR-01 | duplicate-name create race crashed the CLI | Fixed — 719cc8e |
| App | WR-02 | whitespace-only certificate password passed validation | Fixed — 719cc8e (unit test added) |
| App | IN-01 | test host passes certificates via process-wide environment variables | Accepted — safe while all test classes share one collection; noted |
| App | IN-02 | forwarded-header trust has no startup validation | Accepted — fails closed to loopback; revisit when IP-based logic is added |
| Deploy | CR-01 | backup recipients check could log a pasted private identity | Fixed — 645281a (tests prove the key never appears; mutation-checked) |
| Deploy | WR-01 | grafana_reader has no grants under deploy/ | False positive — grants come from the InitialCreate migration; verified on the host (USAGE on reporting, default SELECT, no access to public) |
| Deploy | WR-02 | deploy poll service lacks systemd sandboxing | Deferred — todo: harden privileged units and scan logs for secrets |
| Deploy | WR-03 | apikey CLI transient unit lacks sandboxing | Deferred — same todo |
| Deploy | WR-04 | concurrent backups could clobber each other | Fixed — 645281a (flock) |
| Deploy | WR-05 | selfcheck does not scan logs or the journal for secrets | Deferred — same todo |
| Deploy | WR-06 | attestation helper claimed to be fully offline | Fixed — 645281a (comment now accurate) |
| Deploy | WR-07 | inconsistent Host-header escaping | Fixed — 645281a |
| Deploy | IN-01 | swallowed chown failure delays a clear error | Accepted — failure surfaces at the next step with its own error |
| Deploy | IN-02 | age public-key validation is shape-only | Accepted — age itself rejects a malformed recipient on the first backup, which alerts |
| Deploy | IN-03 | textfile metrics drop HELP/TYPE for series not in the current call | Accepted — cosmetic for Prometheus |
| Pipeline | CR-01 | // comment check missed most placements | Fixed — b506378 (self-test covers each placement) |
| Pipeline | WR-01 | gitleaks allowlisted the whole planning directory for the generic key rule | Fixed — 36943c2 (one exact phrase allowed; a planted token is caught) |
| Pipeline | WR-02 | planning-reference checks were case-sensitive | Fixed — b506378 |
| Pipeline | IN-01 | zizmor pedantic persona style findings | Accepted — style only |
| Pipeline | IN-02 | actor_id 5 unexplained in check-github-settings.sh | Accepted — documented in docs/github-repository-settings.md |

All fixes were verified with `dotnet test` (unit and integration, against a real PostgreSQL), `build/lint.sh` (all six checks) and, where a test was added, a mutation run showing the test catches the original defect.

---

## Original report — Application (.NET)

# Phase 01: Code Review Report — application (.NET)

**Reviewed:** 2026-09-28T00:00:00Z
**Depth:** standard
**Files Reviewed:** 54
**Status:** issues_found

## Summary

This slice of the split review covers the .NET solution: `Ledger.Domain`, `Ledger.Repository`, `Ledger.Service` and their test projects. Overall the security-sensitive plumbing is well built: the API key scheme uses a high-entropy secret, a strict regex parse, a SHA-256 hash at rest and a fixed-time comparison; the Data Protection key ring is certificate-protected in Production with a canary health check that never rewrites on failure; the connection-string rule set and the Production configuration validator name only offending *keys*, never values; health/metrics are wired to a loopback-only ops port kept off the routing/authorization pipeline; and there is no `//`-comment, hardcoded-secret, or planning-reference violation anywhere in the reviewed set (checked by direct read and by grep across all 54 files).

The one finding that must be fixed before shipping is a real gap in the "no unsafe config reaches Production" guarantee: the `apikey` CLI subcommand — the one code path that creates and revokes the household's authentication secrets — never runs `ProductionConfigurationValidator`, so it can silently use an unsafe connection string (wrong role, embedded password, `Include Error Detail=true`) that the web host itself would have refused to start with. Two further issues degrade robustness (an unhandled-exception path in `ApiKeyStore.CreateAsync` under a race, and an inconsistent null/whitespace check in the validator), and two informational items note fragile-but-currently-safe patterns worth hardening later.

## Critical Issues

### CR-01: The `apikey` CLI subcommand never runs the Production configuration safety checks

**File:** `Ledger.Service/Program.cs:23-34`
**Issue:** `Program.cs` branches into `ApiKeyCommand.RunAsync` and returns *before* the block that runs `ProductionConfigurationValidator.ThrowIfInvalid(builder.Configuration)` and switches to hardened systemd logging:

```csharp
if (args.Length > 0 && args[0] == "apikey")
{
    return await ApiKeyCommand.RunAsync(args[1..], builder.Configuration);
}

if (builder.Environment.IsProduction())
{
    ProductionConfigurationValidator.ThrowIfInvalid(builder.Configuration);
    ...
}
```

`ApiKeyCommand.RunAsync` (`Ledger.Service/Cli/ApiKeyCommand.cs:23-29`) builds its own mini host from the *same* `ConnectionStrings:Ledger` configuration and calls `AddLedgerRepository` directly, with no equivalent call to `LedgerConnectionStringRules.Problems(...)`. This is exactly the check that exists specifically to stop the app from ever running against a connection string that: uses a password instead of the Unix-socket/peer-auth pattern, targets the wrong PostgreSQL role, or enables `Include Error Detail` (which would make Npgsql exceptions echo parameter values). The web host enforces this before it will bind a socket; the tool that mints and revokes the household's API key secrets against the very same database does not enforce it at all. In practice the biggest exposure is the ordering: a misconfigured connection string that has never yet been used to start the web host (e.g. right after a fresh deploy, before the systemd unit's first successful start) would be used *unvalidated* by an operator's first `apikey create` invocation, with no equivalent test ever exercising this path (`ApiKeyCommandTests` always builds its `IConfiguration` directly with a known-good connection string, so this gap has no test coverage either).

**Fix:** Run the same validator for the CLI path (skipping only the parts that don't apply, e.g. logging), or factor connection-string validation out so both entry points call it unconditionally in Production:

```csharp
if (args.Length > 0 && args[0] == "apikey")
{
    if (builder.Environment.IsProduction())
    {
        ProductionConfigurationValidator.ThrowIfInvalidConnectionStringOnly(builder.Configuration);
    }

    return await ApiKeyCommand.RunAsync(args[1..], builder.Configuration);
}
```

## Warnings

### WR-01: `ApiKeyStore.CreateAsync` has a check-then-act race that surfaces as an unhandled exception instead of the documented error

**File:** `Ledger.Repository/Stores/ApiKeyStore.cs:11-40`, `Ledger.Service/Cli/ApiKeyCommand.cs:31-46`
**Issue:** `CreateAsync` checks `hasActiveKey` via a separate `AnyAsync` query and only *then* inserts. The DB does enforce the invariant with a partial unique index (`ix_api_keys_name` filtered on `revoked_at IS NULL`), but the interface contract promises: *"throwing `ApiKeyOperationException` if the name is invalid or already active"* (`Ledger.Domain/Auth/IApiKeyStore.cs:8`). Under a genuine race (two `apikey create SAME_NAME` invocations close together — plausible from an automation/redeploy script, not just a hypothetical), the second call's `AnyAsync` check can pass, and the later `SaveChangesAsync` throws a raw `DbUpdateException`/`PostgresException` for the unique-constraint violation instead. `ApiKeyCommand.RunAsync`'s `catch` clause only catches `ApiKeyOperationException` (`ApiKeyCommand.cs:41-45`), so this exception is not caught at all — it propagates out of `RunAsync`, out of the `apikey` branch in `Program.cs`, and crashes the process with a raw stack trace instead of the tool's normal exit-code-1 error reporting.
**Fix:** Catch the unique-violation case and translate it to the documented exception:

```csharp
try
{
    await dbContext.SaveChangesAsync(cancellationToken);
}
catch (DbUpdateException exception) when (IsUniqueViolation(exception))
{
    throw new ApiKeyOperationException($"An active key named '{name}' already exists.");
}
```

### WR-02: Production configuration validator is inconsistent about whitespace-only values

**File:** `Ledger.Service/Hosting/ProductionConfigurationValidator.cs:18-28`
**Issue:** The certificate *path* check uses `string.IsNullOrWhiteSpace(certificatePath)`, correctly rejecting a whitespace-only path, but the certificate *password* check two lines later uses `string.IsNullOrEmpty(certificatePassword)`, which lets a whitespace-only password (e.g. a single space from a misconfigured secret store) pass startup validation. That value is then handed straight to `X509CertificateLoader.LoadPkcs12FromFile` in `DataProtectionSetup.LoadCertificate`, which will fail with a generic, harder-to-diagnose crypto error instead of the clear, named-key `InvalidOperationException` the validator is supposed to give operators.
**Fix:** Use the same whitespace-aware check for both:

```csharp
var certificatePassword = configuration[CertificatePasswordKey];
if (string.IsNullOrWhiteSpace(certificatePassword))
{
    offendingKeys.Add(CertificatePasswordKey);
}
```

## Info

### IN-01: Test host wires certificates through process-wide environment variables

**File:** `Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs:143-148`
**Issue:** `ApplyCertificateEnvironmentVariables` sets `DataProtection__CertificatePath`/`DataProtection__CertificatePassword` as *process-level* environment variables in the constructor, because `Program.cs` reads configuration (including environment variables) eagerly before the factory's own `ConfigureAppConfiguration` override can inject an in-memory value for the same keys. This only stays race-free today because every integration test class in the project shares a single `[Collection("Database")]`, which serializes all of them against one `ICollectionFixture<DatabaseFixture>`. If a future test class in this project is added without that collection attribute (or a second collection is introduced), two `LedgerWebApplicationFactory` instances constructed concurrently would stomp on each other's environment variables mid-startup, and a host could silently come up with the wrong certificate — a source of flaky, hard-to-diagnose failures rather than a clean test failure.
**Fix:** Document the invariant at the top of `LedgerWebApplicationFactory` (e.g. `/// Every test using this factory must belong to the "Database" collection; these env vars are process-wide.`), or pass the certificate path/password through `IWebHostBuilder.UseSetting`/`ConfigureAppConfiguration` only, and change `Program.cs` to read configuration lazily enough that the factory's in-memory override always wins without relying on process env vars at all.

### IN-02: Forwarded-header trust in Production has no explicit validation or fallback documentation

**File:** `Ledger.Service/Program.cs:59-72`
**Issue:** `ForwardedHeadersOptions.KnownProxies` is populated only from `ReverseProxy:KnownProxies` configuration, appended to (not replacing) ASP.NET Core's own defaults (`KnownNetworks` pre-populated with the loopback network). Today this is safe-by-default: nothing in the reviewed code currently branches on `HttpContext.Connection.RemoteIpAddress` or a forwarded value for a security decision, and if `ReverseProxy:KnownProxies` is left unset in Production, the middleware simply falls back to trusting only loopback peers (fail-closed). However, there is no startup check (unlike the certificate and connection-string checks) confirming this key is actually set to the real reverse-proxy address when one is needed, and no test exercises the "misconfigured/missing known-proxies" case. If IP-derived logic (e.g. audit logging of a caller's real address, or a future rate limiter) is added later without revisiting this, a missing or wrong `KnownProxies` entry would silently record the reverse proxy's own address instead of the real client's, with no signal that anything is wrong.
**Fix:** Add this key to `ProductionConfigurationValidator` when (and only when) something in the app starts depending on the forwarded client address, and add a test asserting the configured proxy address is actually honored end-to-end.

---

_Reviewed: 2026-09-28T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

---

## Original report — Host scripts (deploy/)

# Phase 01: Code Review Report (deploy/ host scripts)

**Reviewed:** 2026-09-28T00:00:00Z
**Depth:** standard
**Files Reviewed:** 50
**Status:** issues_found

## Summary

This is a scoped review of `deploy/` only (installer, provisioning modules, backup/restore, systemd units, Postgres/Grafana/Prometheus provisioning, and the offline test suite); the .NET app and CI/CD pipeline are reviewed separately. The overall design is careful: attestation verification always precedes unpacking, releases are activated by an atomic symlink swap, config files are parsed with an allow-list (never sourced), secrets are handled through `LoadCredential=`/config-stream `curl`, and the offline test suite includes a `PATH`-guard so tests can never call the real `systemctl`. The previously-noted host fixes (backup `LoadCredential`, installer retry of a non-activated version, msmtp account naming, Grafana STARTTLS policy derivation, reading EF's own `"MigrationId"` history column, pipefail-safe `nft`/`grep` matching in `ledger-selfcheck`, and the offline-test `PATH` guard) all check out correctly against the code as written.

One genuine secret-leak path was found in the backup recipients validator (see CR-01), which is a direct violation of the project's "secrets never appear in logs" rule and is realistically triggerable by an ordinary operator mistake (a stray leading character on a pasted line). The remaining findings are hardening gaps, a permissions gap that spans outside this review's own scope, and small correctness/documentation issues.

## Critical Issues

### CR-01: A mistakenly-pasted age private identity can be logged verbatim by the backup recipients validator

**File:** `deploy/lib/backup.sh:16-41` (specifically lines 27-33)
**Issue:** `ledger_backup_validate_recipients` is the safety net against an operator accidentally putting the *private* age identity (instead of the public recipient key) into `/etc/ledger/backup-recipients.txt`. It only recognizes a private identity if the line matches `^AGE-SECRET-KEY-1` **from the very first character**:

```sh
if [[ "${line^^}" =~ ^AGE-SECRET-KEY-1 ]]; then
  ledger_die "recipients file $file contains what looks like a private age identity, refusing"
fi

if [[ ! "$line" =~ ^age1[0-9a-z]+$ ]]; then
  ledger_die "recipients file $file contains a line that is not an age public key: $line"
fi
```

The line is read with `IFS= read -r line`, so any leading whitespace, a stray character, a copy-paste artifact, or a BOM is preserved verbatim. Such a line fails the anchored private-key check (no leading-whitespace tolerance) *and* fails the public-key check, and falls through to the second `ledger_die`, which **echoes the full raw line — the private key itself — into the error message**. This message goes to `ledger_log`/stderr, which under the `ledger-backup@.service` unit is captured by journald (and, if run manually by an operator, appears on the terminal and any redirected log). This directly violates the project's hard rule that secrets never appear in logs, exceptions, or output, and it defeats the very check that exists to catch this exact operator mistake.

**Fix:** Never echo the raw line in the fallback branch; only ever say a line was rejected, or redact it before logging (e.g. print only length/hash), and additionally trim leading/trailing whitespace before the private-key-shape check so genuine mis-pastes are still caught by the safe branch:

```sh
while IFS= read -r line || [ -n "$line" ]; do
  local trimmed="${line#"${line%%[![:space:]]*}"}"
  trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
  [ -z "$trimmed" ] && continue

  if [[ "${trimmed^^}" =~ AGE-SECRET-KEY-1 ]]; then
    ledger_die "recipients file $file contains what looks like a private age identity, refusing"
  fi

  if [[ ! "$trimmed" =~ ^age1[0-9a-z]+$ ]]; then
    ledger_die "recipients file $file contains a line that is not a valid age public key (line redacted)"
  fi
  found=1
done < "$file"
```

## Warnings

### WR-01: `grafana_reader` has no schema or table privileges anywhere in `deploy/`

**File:** `deploy/sql/bootstrap-roles.sql`, `deploy/sql/bootstrap-database.sql`, `deploy/provisioning/grafana/provisioning/datasources/ledger.yaml`
**Issue:** `bootstrap-roles.sql` grants `grafana_reader` only `CONNECT` on the database. `bootstrap-database.sql` explicitly does `REVOKE ALL ON SCHEMA public FROM PUBLIC` and then grants `USAGE ON SCHEMA public` to `ledger_runtime` only — `grafana_reader` is never granted `USAGE` on any schema, let alone `SELECT` on any table or view, and no `reporting`/other schema is created here at all. Yet the provisioned Grafana Postgres datasource (`ledger.yaml`) connects as `grafana_reader` and is expected to serve dashboards, and `ledger-selfcheck --grafana-admin` asserts a `ledger-reporting` datasource health check must report `OK`. As reviewed, nothing under `deploy/` ever grants `grafana_reader` enough privilege to run a single query — even a hypothetical `GRANT SELECT` on a future view would be useless without a matching `GRANT USAGE ON SCHEMA ...`, which also doesn't exist here.
**Fix:** If this is intentionally deferred to the application's own EF Core migrations (run under `ledger_migrator`), confirm those migrations issue an explicit `GRANT USAGE ON SCHEMA <x> TO grafana_reader` in addition to any `SELECT` grants, and record that cross-repo dependency somewhere discoverable (e.g. in `bootstrap-database.sql`'s header comment) so a future change to either repo doesn't silently break Grafana. If it is not yet handled anywhere, this is a functional blocker for the dashboards feature, not just a hardening note.

### WR-02: `ledger-deploy-poll.service` has almost no systemd sandboxing despite running fully-privileged, network-facing, artifact-unpacking code

**File:** `deploy/systemd/ledger-deploy-poll.service`
**Issue:** This unit runs as root (no `User=`) and is the entry point that fetches, verifies and unpacks release artifacts and restarts services. Compared to `ledger.service` and `ledger-backup@.service` — both of which set `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectKernelTunables`, `ProtectKernelModules`, `ProtectControlGroups`, `RestrictNamespaces`, `LockPersonality`, `RestrictAddressFamilies`, and an empty `CapabilityBoundingSet` — this unit only sets `PrivateTmp=yes` and `ProtectHome=read-only`. Several of the hardening options used elsewhere (`NoNewPrivileges`, `LockPersonality`, `RestrictNamespaces`, `ProtectKernelTunables`, `ProtectKernelModules`, `ProtectControlGroups`, a trimmed `CapabilityBoundingSet`) would not interfere with its filesystem writes, `curl`, `gh`, or `systemctl` D-Bus calls, and would meaningfully reduce the blast radius of this specific, most-privileged unit in the whole platform.
**Fix:** Add the options above (leaving `ProtectSystem=` off or as `ProtectSystem=full` with explicit `ReadWritePaths=` if `strict` proves too invasive to test against safely), e.g.:

```ini
NoNewPrivileges=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictNamespaces=yes
LockPersonality=yes
SystemCallArchitectures=native
```

### WR-03: `ledger-apikey`'s transient unit carries none of `ledger.service`'s sandboxing, despite reading the same secret-bearing env file

**File:** `deploy/bin/ledger-apikey:18-24`
**Issue:** `run_apikey_command` launches the app via `systemd-run --uid=ledger --gid=ledger --property=EnvironmentFile=/etc/ledger/ledger.env ...` with no other `--property=` hardening. `ledger.env` contains `DataProtection__CertificatePassword`. The equivalent long-running unit (`ledger.service`) applies a long list of sandboxing directives to the same binary/environment; this one-off invocation of the same binary gets none of them.
**Fix:** Pass the same hardening as additional `--property=` flags, e.g. `--property=NoNewPrivileges=yes --property=ProtectSystem=strict --property=ProtectHome=yes --property=RestrictAddressFamilies=AF_UNIX` (loopback HTTP is not needed for the `apikey` subcommand if it only talks to the database over the socket — verify against the app's actual `apikey` implementation).

### WR-04: `ledger-backup` has no locking, so two concurrent runs for the same reason can clobber each other

**File:** `deploy/bin/ledger-backup`, `deploy/lib/backup.sh`
**Issue:** Unlike `ledger-deploy` (which takes a `flock` via `acquire_lock`), `ledger-backup` has no locking at all. `TARGET_NAME` is derived from `date -u +%s` (second granularity). If a nightly run is manually re-triggered while the timer-fired instance is still running (or any other overlap of two runs with the *same* reason), both processes read/write the same `STATE_FILE` (`write_metrics`'s read-then-write of `last-success-${REASON}` is not atomic) and — if they land in the same second — the same `FINAL_PATH`, so the second `mv -f` silently overwrites the first's completed backup file without either run reporting an error.
**Fix:** Acquire a `flock` on a per-reason lock file (mirroring `ledger-deploy`'s `acquire_lock`) at the top of `ledger-backup`, before computing `TARGET_NAME`.

### WR-05: `ledger-selfcheck`'s secrets-hygiene scan does not cover `/var/log` or journal storage

**File:** `deploy/bin/ledger-selfcheck:214-216`
**Issue:** `check_secrets_hygiene` greps for an age private-identity pattern under `/etc /root /home /opt /var/backups /var/lib/ledger /var/lib/ledger-deploy /tmp`, but not `/var/log` (where `msmtp.log` and, on hosts with persistent journald storage enabled, `/var/log/journal` live) nor `/run/log/journal` (the default volatile journal location). If a private identity is ever logged (see CR-01, or any future regression of the same kind), this platform self-check — whose entire job is to catch exactly that — would not detect it.
**Fix:** Add `/var/log` and the journal storage directories to the scanned path list (grepping binary journal files won't work directly with `grep -l`, so consider also running the same pattern through `journalctl -o cat` output, bounded to a reasonable time window, as an additional check).

### WR-06: `ledger_verify_attestation`'s "fully offline" doc comment is contradicted by its own implementation

**File:** `deploy/lib/deploy.sh:10-16`
**Issue:** The docstring states: "Verifies a downloaded release artifact against its Sigstore bundle, fully offline: no GitHub API call is made and no GitHub credential is read or used." The function creates a **fresh, empty** `GH_CONFIG_DIR` on every single call (`gh_config_dir="$(mktemp -d)"`), which means any cached Sigstore TUF trust root `gh` might otherwise reuse is never available — every invocation must re-bootstrap trust material from the network. This is consistent with the fact that the only test exercising this function (`deploy/tests/verify-rejects-tampered-artifact-network-test.sh`) is explicitly named and documented as a network test specifically because of this function (in addition to downloading its fixture over HTTPS). The "no GitHub credential is used" half of the claim is correctly proven by that same test, but "fully offline" as stated is misleading: this function very likely depends on reaching Sigstore's infrastructure over the network on every single verification, which matters for anyone reasoning about this as an offline/air-gapped security control or doing firewall/dependency planning.
**Fix:** Either pass `--offline` explicitly if `gh attestation verify --bundle` genuinely supports fully-offline verification from a self-contained bundle (and confirm this with a test that runs with network access blocked), or correct the docstring to describe the actual network dependency (Sigstore only, never GitHub's API/credentials).

### WR-07: Inconsistent curl-config escaping of the Grafana `Host` header between `ledger-selfcheck` and `60-grafana-accounts.sh`

**File:** `deploy/bin/ledger-selfcheck:441-459` vs. `deploy/provision.d/60-grafana-accounts.sh:40-67`
**Issue:** `60-grafana-accounts.sh`'s `grafana_api()` escapes the Grafana domain before embedding it in the curl config's `header = "Host: %s"` line (`printf 'header = "Host: %s"\n' "$(grafana_config_escape "$GRAFANA_HOST")"`). `ledger-selfcheck`'s `grafana_admin_get()` builds the same kind of curl config line but interpolates `${domain:-127.0.0.1}` **unescaped**: `printf 'header = "Host: %s"\n' "${domain:-127.0.0.1}"`. Today `domain` only ever comes from `LEDGER_GRAFANA_DOMAIN` in the root-owned `/etc/ledger/provision.conf`, so this isn't exploitable by an unprivileged party today, but it is an inconsistency in a security-relevant helper (one script defends against embedded quotes, the sibling script doesn't), and `provision_load_conf`/`_provision_parse_kv_file` do not constrain the *shape* of `LEDGER_GRAFANA_DOMAIN`'s value beyond rejecting backticks/`$(`.
**Fix:** Reuse the same escaping approach (or share one helper) in `ledger-selfcheck`'s `grafana_admin_get`.

## Info

### IN-01: Silently swallowed `chown` failure delays a clear error during migration

**File:** `deploy/lib/deploy.sh:487`
**Issue:** `chown ledger_migrator:ledger_migrator "$extract_dir" 2>/dev/null || true` discards any failure. If this ever fails (e.g. the `ledger_migrator` user is somehow missing), the script proceeds to run the migration bundle anyway, which will fail later with a much less clear "migration bundle failed" error instead of an immediate, specific one. Not unsafe (the deploy still fails closed), just harder to diagnose.
**Fix:** Log a warning when the `chown` fails, e.g. `chown ... "$extract_dir" 2>/dev/null || ledger_log "WARNING: could not chown ${extract_dir} to ledger_migrator"`.

### IN-02: age public-key validation accepts syntactically-plausible garbage

**File:** `deploy/provision.sh:272-278`, `deploy/lib/backup.sh:31`
**Issue:** Both `provision_validate_age_public_key` and `ledger_backup_validate_recipients` accept any string matching `^age1[0-9a-z]+$` regardless of length or bech32 checksum validity. A typo made while pasting the key at provisioning time is accepted silently and is only discovered when the first nightly backup fails to encrypt against it.
**Fix:** Consider a minimum/maximum length check (real age X25519 recipients are a fixed length) as a cheap additional guard, since the actual checksum validation will happen for free the first time `age --encrypt` is run against it anyway — but failing at provisioning time (interactive, operator present) is much cheaper to recover from than failing at 02:30 during the nightly backup.

### IN-03: `ledger_write_textfile_metrics` drops HELP/TYPE documentation for metrics not present in the current call, across writers sharing one file

**File:** `deploy/lib/common.sh:99-135`
**Issue:** The merge logic re-emits old metric *value* lines that aren't in the new content, but unconditionally skips **all** old comment (`#`) lines, relying entirely on the current call's content to supply `# HELP`/`# TYPE` for whatever it's writing. `ledger_deploy.prom` is written by three different call sites (`ledger_write_deploy_poll_metrics`, `ledger_write_deploy_run_metrics` used for both installs and rollbacks) sharing the same file; after the first time one writes over the other, the untouched metric family's `HELP`/`TYPE` comments are gone from the file, even though its value line is preserved. This is harmless for scraping (Prometheus treats `HELP`/`TYPE` as optional) but is a minor documentation-completeness regression in the exposed metrics.
**Fix:** Either always regenerate the full fixed set of `HELP`/`TYPE` lines for all metrics ever written to a given file (a static list), or track and preserve comment lines associated with preserved value lines during the merge.

---

_Reviewed: 2026-09-28T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

---

## Original report — Build, CI and docs

# Phase 01: Code Review Report — build/CI/docs slice

**Reviewed:** 2026-09-28T21:50:00Z
**Depth:** standard
**Files Reviewed:** 30
**Status:** issues_found

## Summary

This slice covers the GitHub Actions workflows, the local lint harness (`build/lint.sh` and its checks), the release/tag-validation/verification scripts, Dependabot/zizmor/gitleaks configuration, `.gitignore`, and the operator-facing docs.

The supply-chain posture is generally strong and was spot-checked, not just read: every `uses:` reference in both workflows and in `build/lint/compose.yaml` is pinned to a full 40-character commit SHA or a full 64-character image digest (verified by extracting and measuring every pin in the tree, not just eyeballing them); `persist-credentials: false` is set on every checkout; no untrusted `${{ }}` expression is ever interpolated directly into a `run:` block (everything goes through `env:` indirection, and the repo even ships a fixture — `run-interpolation.yml` — whose entire purpose is to prove the lint suite still catches that pattern if it ever reappears); `pull_request_target` is not used anywhere; the release tag/commit/reachability checks in `validate-release-tag.sh` are exercised by a dedicated test script with good coverage of injection-shaped tag strings; and `verify-published-release.sh` does genuinely fetch the release archive and attestation bundle with a bare, credential-free `curl` as required.

Two real gaps were found by actually running the tools this pipeline depends on (gitleaks and the repo's own lint self-tests via Docker), not just reading the scripts: the project's sole automated enforcement of the "no `//` comments" hard rule has a blind spot that lets a very common C# comment placement through silently, and the gitleaks allowlist for `.planning/` is broader than the "prose only" justification given for it. Both are documented below with the exact reproduction used to confirm them.

## Critical Issues

### CR-01: The "no `//` comments" lint check silently misses comments after `}`, `,`, `]` and most other tokens

**File:** `build/lint/checks/10-repo-rules.sh:18`
**Issue:** `CS_LINE_COMMENT_PATTERN` is meant to be the sole automated enforcement of the project's hard rule ("No `//` comments. Only `///` XML doc summaries" — there is no `.editorconfig`, Roslyn analyzer, or other backstop in the repo for this rule). The pattern is:

```
CS_LINE_COMMENT_PATTERN='^[[:space:]]*//([^/]|$)|[;{)][[:space:]]*//([^/]|$)'
```

It only catches a `//` comment when it is either the first token on a line, or immediately preceded by `;`, `{`, or `)`. It does **not** cover `//` immediately preceded by `}` (a closing brace), `,`, `]`, or any other common C# token. A comment placed after a closing brace — one of the most idiomatic C# comment placements (`} // end of using block`, `} // TODO`) — passes through completely undetected.

Verified directly (not just read):

```
$ printf '        } // trailing note\n' | grep -nE '^[[:space:]]*//([^/]|$)|[;{)][[:space:]]*//([^/]|$)'
(no output — not matched)
```

Also confirmed the check's own self-test suite (`self_test()` in the same file) only exercises a `//` comment at the very start of a line and never exercises any of these other placements, so this gap would not be caught by the check's own regression tests either. Ran the check against the current tree (`build/lint/checks/10-repo-rules.sh` via the pinned toolchain) and confirmed it currently reports clean — meaning the gap is latent, not yet triggered by existing code, but any future `.cs` file using `} // ...` style comments will pass CI without a single warning.

**Fix:** Broaden the post-token alternative to cover the realistic set of C# line-enders, e.g.:

```bash
CS_LINE_COMMENT_PATTERN='^[[:space:]]*//([^/]|$)|[;{}()\[\],>]([[:space:]]*//([^/]|$))'
```

and add self-test cases for at least `} // ...`, `, // ...`, and `] // ...` so a future regression in the pattern itself is caught by `self_test()` rather than relying on manual review.

## Warnings

### WR-01: The gitleaks `.planning/` allowlist suppresses realistic secrets anywhere in that (tracked, non-ignored) directory, not just prose mentions

**File:** `.gitleaks.toml:54-58`
**Issue:** The `generic-api-key` rule is extended with:

```toml
[[rules.allowlists]]
description = "planning documentation discusses APIs in prose without embedding real keys"
paths = [
  '''\.plannin[g]/''',
]
```

This allowlists the entire `.planning/` directory tree for the `generic-api-key` rule, not just specific known-safe patterns. `.planning/` is tracked in git (only `.planning/research/.cache/` is gitignored, per `.gitignore:2`), so anything pasted into a plan, spec, or research note under `.planning/` — including a real secret accidentally copy-pasted while drafting a plan — will not be flagged by this rule.

Verified directly with the pinned gitleaks image (`ghcr.io/gitleaks/gitleaks:v8.30.1`) and the repo's actual `.gitleaks.toml`:

```
# same realistic secret, outside .planning/
$ echo 'token = "<a realistic-looking secret value>"' > secret.txt
→ gitleaks detect ... → 1 leak found (generic-api-key), exit 1

# identical content, moved into .planning/notes.md
$ mkdir .planning && mv secret.txt .planning/notes.md
→ gitleaks detect ... → no leaks found, exit 0
```

Other rules (IBAN, private IPv4, email, specific vendor-token patterns) are unaffected and still fire inside `.planning/` — this is scoped to `generic-api-key` only — but that is precisely the rule most likely to catch an accidentally-pasted real credential of an otherwise-unknown shape, so the gap is meaningful for the rule that matters most here.

**Fix:** Narrow the allowlist instead of exempting the whole tree, for example by requiring an additional signal that the match is prose rather than a value (e.g. `regexTarget = "line"` combined with a requirement that the key/token appears inside a sentence, or simply excluding only specific glob-safe subpaths that are known to be templates/examples rather than free-form research notes). At minimum, note this as an accepted, scoped risk next to the rule rather than only in the allowlist's one-line description.

### WR-02: Requirement-key and decision-ID planning-reference checks are case-sensitive

**File:** `build/lint/checks/10-repo-rules.sh:10-11`
**Issue:** `REQUIREMENT_KEY_PATTERN` and `DECISION_ID_PATTERN` are matched with plain `grep -E` (no `-i`), so a lowercase planning reference bypasses the same hard-rule check CR-01 undermines from the other direction:

```
$ printf 'referenced under sec-01 in the notes\n' | grep -nE '\b(SEC|OPS|API|DASH|INGEST|CAT|PLAN|ADV|WEB|REF)-[0-9]{2}\b'
(no output — not matched)
$ printf 'see d-08 for context\n' | grep -nE '\bD-[0-9]{2}\b'
(no output — not matched)
```

Existing convention is uppercase, so this is lower-likelihood than CR-01, but it is the same class of gap in the same enforcement mechanism.

**Fix:** Add `-i` to both `grep -E` calls (or fold case-insensitivity into the patterns), and add a lowercase case to `self_test()` alongside the existing uppercase ones.

## Info

### IN-01: zizmor's pedantic persona flags unnamed jobs and undocumented broad permissions in `release.yml`

**File:** `.github/workflows/release.yml:15,19-21,108,114`
**Issue:** Running the pinned zizmor image with `--persona=pedantic` (the repo's own `.github/zizmor.yml` only configures the `unpinned-uses` policy, so the lint check runs with zizmor's default `regular` persona and these are silently suppressed — the lint output literally says `No findings to report. Good job! (6 suppressed)`) surfaces:
- `build`, `publish`, `build-test` and `lint` jobs have no `name:` (informational).
- The `build` job's `contents: write` / `id-token: write` / `attestations: write` and the `publish` job's `contents: write` have no explanatory comment for why each is needed (zizmor's `undocumented-permissions` audit).

These are style-only in a project that otherwise cares a lot about security legibility, and GitHub Actions has no way to scope permissions more finely than per-job, so this isn't a fixable security gap — but for a repo whose CLAUDE.md explicitly asks for security to be treated as a first-class, proactively-flagged concern, a one-line `#` comment above each permission explaining why it's needed would cost little and match that stated bar.
**Fix:** Add short `# comment` lines above each non-trivial `permissions:` block (YAML comments, not covered by the "no `//`" C#-only rule) and/or add `name:` to each job.

### IN-02: `actor_id == 5` in `check-github-settings.sh` has no inline explanation

**File:** `build/check-github-settings.sh:46`
**Issue:** `check_tag_ruleset` asserts `(.bypass_actors // [])[0].actor_id == 5` with no comment explaining what role `5` refers to. The meaning ("5 = Admin") is documented, but only in a separate file (`docs/github-repository-settings.md:35`), so anyone reading or modifying this script in isolation has to go find that doc to understand the assertion.
**Fix:** Add a one-line comment next to the check itself, e.g. `# actor_id 5 is the built-in "Admin" repository role`.

---

_Reviewed: 2026-09-28T21:50:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

