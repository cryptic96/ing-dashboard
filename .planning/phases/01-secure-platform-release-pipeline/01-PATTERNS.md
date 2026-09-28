# Phase 1: Secure Platform & Release Pipeline - Pattern Map

**Mapped:** 2026-09-27
**Files analyzed:** 20 (new files; this repo is greenfield — no modifications, only creations)
**Analogs found:** 20 / 20 (all from the read-only reference project `/mnt/Data/repos/quest-board-dnd/`; no analog is a byte-for-byte copy — every one requires adapting SQL Server → PostgreSQL/peer-auth, self-hosted-runner → pull-based deploy, and stripping personal data)

**Greenfield note:** This repository (`ing-dashboard`) has no application code yet — only `.claude/` and `.planning/`. Every "file to create" below is brand new. The only source of concrete patterns is the user's other homelab app, `quest-board-dnd` (read-only reference, never modified). Where that project's pattern is itself the thing this phase must reject (self-hosted runner, mutable tags, SQL Server `sa`), this is called out explicitly as "do not copy."

## File Classification

| New File | Role | Data Flow | Closest Analog (quest-board-dnd) | Match Quality |
|---|---|---|---|---|
| `Ledger.slnx` | config | — | `QuestBoard.slnx` | exact |
| `Ledger.Domain/Ledger.Domain.csproj` | config | — | `QuestBoard.Domain/QuestBoard.Domain.csproj` | exact |
| `Ledger.Repository/Ledger.Repository.csproj` | config | — | `QuestBoard.Repository/QuestBoard.Repository.csproj` | role-match (SQL Server → Npgsql) |
| `Ledger.Repository/LedgerDbContext.cs` | model | CRUD | `QuestBoard.Repository/Entities/QuestBoardContext.cs` | role-match (Identity-based; this project has no ASP.NET Identity, but DbContext/DbSet shape transfers) |
| `Ledger.Repository/Migrations/*InitialCreate.cs` | migration | CRUD | (no analog file read; EF Core `dotnet ef migrations add` convention is standard, no project-specific pattern needed) | no analog |
| `Ledger.Service/Ledger.Service.csproj` | config | — | `QuestBoard.Service/QuestBoard.Service.csproj` | role-match (drop Hangfire/Identity/QRCoder, add Npgsql/DataProtection.EFCore/prometheus-net/Resilience) |
| `Ledger.Service/Program.cs` | config | request-response | `QuestBoard.Service/Program.cs` | role-match (DI bootstrap shape, `AddForwardedHeaders`/`KnownProxies` pattern, `MapHealthChecks`, fail-fast config validation) |
| `Ledger.Service/Middleware/ApiKeyAuthMiddleware.cs` | middleware | request-response | `QuestBoard.Service/Middleware/*` (pattern only; no specific auth middleware file read — GroupSessionMiddleware/MobileDetectionMiddleware establish the middleware-class convention) | role-match |
| `Ledger.Service/Endpoints/HealthEndpoints.cs` | route | request-response | `Program.cs` `AddHealthChecks()` / `MapHealthChecks("/health")` | role-match |
| `Ledger.Service/Metrics/*` | utility | request-response | none (net-new concern; prometheus-net's own docs are the analog, not quest-board-dnd) | no analog |
| `Ledger.UnitTests/Ledger.UnitTests.csproj` | test | — | `QuestBoard.UnitTests/QuestBoard.UnitTests.csproj` | exact (assume xUnit v3/FluentAssertions/NSubstitute/InMemory convention per CONTEXT.md canonical refs; csproj itself not read this session) |
| `Ledger.IntegrationTests/Ledger.IntegrationTests.csproj` + `DatabaseFixture.cs` | test | request-response | `QuestBoard.IntegrationTests/*` (pattern only; specific fixture file not read this session) | role-match |
| `.github/workflows/release.yml` | config | event-driven | `.github/workflows/binary-release.yml` `release` job | role-match (build/publish shape reusable; deploy job is an anti-pattern, see below) |
| `.github/workflows/publish.yml` | config | event-driven | none in reference (reference has no Environment-gated publish split) | no analog |
| `.github/workflows/ci.yml` | config | event-driven | `.github/workflows/dotnet.yml` (not read in detail; standard `dotnet test` shape assumed) | role-match |
| `.github/dependabot.yml` | config | batch | none read this session (standard GitHub-documented shape, no project-specific pattern) | no analog |
| `deploy/provision.sh` | utility | batch | `docs/server-setup.md` §1 "App CT" shell blocks (user/dir/systemd creation) | role-match |
| `deploy/ledger-deploy` (installer) | utility | file-I/O | `docs/server-setup.md` §"Create the deploy script" (`deploy.sh`) | role-match, but this is exactly the shape to expand with attestation verification, backup, migration — reference version is minimal |
| `deploy/ledger-deploy-poll.timer`/`.service`, `deploy/ledger-backup.timer`/`.service` | config | event-driven | `docs/server-setup.md` §"Create the systemd service" (`questboard.service`) | role-match |
| `deploy/grafana/provisioning/datasources/postgres.yaml`, `grafana.ini.template` | config | request-response | none in reference (quest-board-dnd has no Grafana) | no analog — use RESEARCH.md Code Examples directly |
| `docs/lxc-setup.md` | config | — | `docs/server-setup.md` (whole-file structural analog) | exact structural match, content fully rewritten |
| Traefik dynamic config template (e.g. `deploy/traefik/ledger.yml.template`) | config | request-response | `docs/server-setup.md` §3 "Traefik — Add Route" | role-match (reference has no IP allowlist middleware; must add it) |

## Pattern Assignments

### `Ledger.slnx`

**Analog:** `/mnt/Data/repos/quest-board-dnd/QuestBoard.slnx` (full file, 15 lines)

```xml
<Solution>
  <Configurations>
    <Platform Name="Any CPU" />
    <Platform Name="x64" />
    <Platform Name="x86" />
  </Configurations>
  <Folder Name="/Tests/">
    <Project Path="QuestBoard.IntegrationTests/QuestBoard.IntegrationTests.csproj" />
    <Project Path="QuestBoard.UnitTests/QuestBoard.UnitTests.csproj" />
  </Folder>
  <Project Path="QuestBoard.Domain/QuestBoard.Domain.csproj" />
  <Project Path="QuestBoard.Repository/QuestBoard.Repository.csproj" />
  <Project Path="QuestBoard.Service/QuestBoard.Service.csproj" />
</Solution>
```

Copy verbatim, substituting `QuestBoard` → `Ledger` and project names. Keep the `/Tests/` solution folder grouping — matches the project's own layering convention already recorded in CONTEXT.md canonical refs.

---

### `Ledger.Repository/Ledger.Repository.csproj`

**Analog:** `QuestBoard.Repository/QuestBoard.Repository.csproj` (full file, 20 lines, read above)

Keep the shape (TargetFramework, ImplicitUsings, Nullable, EF Core package set, `PrivateAssets`/`IncludeAssets` on `.Design`, `ProjectReference` to `*.Domain` only). **Package substitutions required by D-08:**
- Replace `Microsoft.EntityFrameworkCore.SqlServer` → `Npgsql.EntityFrameworkCore.PostgreSQL` **10.0.3**
- Drop `Microsoft.AspNetCore.Identity.EntityFrameworkCore` entirely — this project has no ASP.NET Identity (per D-21/D-22, auth is Grafana-native Viewer logins + REST API keys, not app-level Identity)
- Add `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` **10.0.12** (for `IDataProtectionKeyContext`)
- Keep `Microsoft.EntityFrameworkCore.Design` **10.0.12** with the identical `PrivateAssets`/`IncludeAssets` block — this is exactly what builds the `efbundle` (Pattern 3, RESEARCH.md)

---

### `Ledger.Repository/LedgerDbContext.cs`

**Analog:** `QuestBoard.Repository/Entities/QuestBoardContext.cs` (lines 1-59 read)

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using QuestBoard.Domain.Interfaces;

namespace QuestBoard.Repository.Entities;

public class QuestBoardContext(
    DbContextOptions<QuestBoardContext> options,
    IActiveGroupContext activeGroupContext)
    : IdentityDbContext<UserEntity, IdentityRole<int>, int>(options)
{
    public DbSet<QuestEntity> Quests { get; set; }
    /// ...one DbSet property per entity...
    protected override void OnModelCreating(ModelBuilder modelBuilder) { ... }
}
```

**Copy the shape, not the base class:** primary-constructor DI injection into the context, one `DbSet<T>` property per aggregate, `OnModelCreating` override for fluent config. **Do NOT inherit `IdentityDbContext`** — this project has no ASP.NET Identity. Instead implement `IDataProtectionKeyContext` (per RESEARCH.md Pattern 4) directly:

```csharp
/// <summary>EF Core context for the ledger database, including the Data Protection key ring.</summary>
public class LedgerDbContext(DbContextOptions<LedgerDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    /// reporting schema entities go here in later phases
}
```

Wave 0 migration must also create the empty `reporting` schema and its grants per D-10/decisions — this has no analog in the reference project (SQL Server, no schema-level grant story) and must be built from RESEARCH.md Pattern 2 + the phase decisions directly.

---

### `Ledger.Service/Program.cs`

**Analog:** `QuestBoard.Service/Program.cs` (full file, 434 lines, read above)

**Reusable shape (copy these patterns, drop everything Identity/Hangfire/session-specific):**

Forwarded-headers / KnownProxies pattern (lines 98-112) — copy near-verbatim, this is exactly D-20's "trust `X-Forwarded-For` only from Traefik" requirement:
```csharp
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

    var knownProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
    foreach (var proxy in knownProxies)
    {
        if (IPAddress.TryParse(proxy, out var ip))
            options.KnownProxies.Add(ip);
    }
});
```

Health checks + fail-fast config validation (lines 41-42, 373, 414-432) — copy the *pattern* of failing loudly in Production if required config is missing; apply it to the Data Protection certificate path/password and the DB connection string instead of email settings:
```csharp
builder.Services.AddHealthChecks();
...
app.MapHealthChecks("/health");
...
if (app.Environment.IsProduction())
{
    var missing = new List<string>();
    if (string.IsNullOrWhiteSpace(app.Configuration["DataProtection:CertificateThumbprint"]))
        missing.Add("DataProtection:CertificateThumbprint");
    if (missing.Count > 0)
        throw new InvalidOperationException($"Missing required configuration: {string.Join(", ", missing)}");
}
```

Pipeline ordering pattern (lines 309-334): `UseForwardedHeaders()` first, then `UseExceptionHandler`/`UseHsts` outside Development, then routing/auth middleware in order — mirror this ordering but insert the API-key middleware (API-02) where `UseAuthentication()`/`UseAuthorization()` sit in the reference.

**Net-new, no analog — build from RESEARCH.md directly:**
- `AddDataProtection().PersistKeysToDbContext<LedgerDbContext>().ProtectKeysWithCertificate(...).SetApplicationName("HouseholdLedger")` (RESEARCH.md Pattern 4)
- `prometheus-net.AspNetCore` `/metrics` endpoint bound to a second Kestrel endpoint on `127.0.0.1` only (D-23) — the reference app has a single Kestrel endpoint and no separate metrics port; there is no analog to copy, only the requirement to satisfy.
- Npgsql `AddDbContext` registration — analog uses `UseSqlServer(...)`; substitute `UseNpgsql(...)` with a Unix-socket connection string (`Host=/var/run/postgresql;Database=ledger;Username=ledger_runtime`), no password (D-09).

---

### `Ledger.Service/Middleware/ApiKeyAuthMiddleware.cs`

**Analog:** middleware-class *convention* only (no specific auth middleware body was read this session — `GroupSessionMiddleware`/`MobileDetectionMiddleware`/`CrossBoardDeepLinkMiddleware` establish that this project writes custom middleware as plain classes registered via `app.UseMiddleware<T>()`, per `Program.cs` lines 321, 327, 332).

**Core pattern to follow (no financial/session logic — write fresh against API-02 + D-22):**
```csharp
/// <summary>Rejects any request without a valid, hashed API key in the configured header.</summary>
public class ApiKeyAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IApiKeyStore store)
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var key) ||
            !await store.IsValidAsync(key.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(context);
    }
}
```
Registration mirrors `app.UseMiddleware<GroupSessionMiddleware>();` (Program.cs line 327) — place before `UseAuthorization()`/route mapping, after `UseRouting()`.

---

### `.github/workflows/release.yml`

**Analog:** `.github/workflows/binary-release.yml` `release` job only (lines 12-41 of the file read above)

**Reusable shape** (checkout → setup-dotnet → publish → zip → create release):
```yaml
- name: Checkout repository
  uses: actions/checkout@v4
- name: Setup .NET
  uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '10.0.x'
- name: Publish
  run: dotnet publish Ledger.Service/Ledger.Service.csproj -c Release -o ./publish
- name: Zip artifact
  run: cd publish && zip -r ../ledger-${{ github.ref_name }}.zip .
- name: Create GitHub Release
  uses: softprops/action-gh-release@v2
  with:
    files: ledger-${{ github.ref_name }}.zip
```
**Required changes per D-01/D-07/SEC-09:** pin every `uses:` to a commit SHA (not `@v4`/`@v2`), create the release as `draft: true` (reference publishes immediately — this project's D-02 requires draft-then-Environment-gated-publish), add the semver+reachable-from-`main` validation step (RESEARCH.md Pitfall 6 code block) with `TAG` passed via `env:` only, add `actions/attest-build-provenance` + bundle-asset upload (RESEARCH.md Pattern 1), add the `dotnet ef migrations bundle --self-contained` step (RESEARCH.md Pattern 3).

**Anti-pattern — do NOT copy the `deploy` job** (lines 43-52 of `binary-release.yml`):
```yaml
  deploy:
    if: ${{ always() && (github.event_name == 'workflow_dispatch' || needs.release.result == 'success') }}
    needs: [release]
    runs-on: self-hosted          # <-- REJECT: no self-hosted runner anywhere (D-02)
    permissions: {}
    steps:
      - name: Deploy
        run: /home/questboard/deploy.sh ${{ github.event.inputs.tag || github.ref_name }}   # <-- REJECT: unpinned inline shell, and a self-hosted runner executing untrusted-adjacent code on the app host
```
This job is the exact shape SEC-08/D-02 forbid: a self-hosted runner on the app host (`runs-on: self-hosted`), executing on every tag push with no human gate, and interpolating a ref-derived value straight into a shell command. Replace entirely with the pull-based model: GitHub Actions ends at "release published" (D-05); there is no `deploy` job in this project's workflows at all.

Also flag `- name: Determine version` (`binary-release.yml` line 28-30):
```yaml
run: echo "version=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"
```
`GITHUB_REF_NAME` is read directly in a `run:` shell expansion here — for this project's stricter D-01 requirement, the raw tag must first be validated against the strict semver regex (RESEARCH.md Pitfall 6) via `env:` before any further shell use, even though this particular line itself is low-risk (bash parameter expansion, not `eval`).

---

### `docs/lxc-setup.md`

**Analog:** `docs/server-setup.md` (full file, 268 lines, read above) — structural analog only; content is fully rewritten.

**Structure to mirror:** numbered sections (App CT → DB → Reverse proxy → DNS → Deploying → Checking logs), each with copy-pasteable shell blocks, a "Do not expose port X to the internet" callout, and a final `journalctl -u <service> -f` logs section. Reuse this skeleton for `docs/lxc-setup.md`'s sections: LXC creation (`pct create`) → `provision.sh` walkthrough → PostgreSQL role verification → Grafana viewer account creation → Traefik route template → backup/restore drill → `ledger-deploy` manual invocation → logs.

**Do not copy these specific lines (personal-data / anti-pattern risk):**
- `ConnectionStrings__DefaultConnection=Server=<SQL_SERVER_CT_IP>;...User Id=sa;Password=<SA_PASSWORD>;TrustServerCertificate=true;` (line 61) — `sa` login and `TrustServerCertificate=true` are exactly the weaknesses D-08/D-09 remove; this project's env file has no DB password at all (peer auth).
- The GitHub Actions self-hosted runner install section (lines 134-147) — no runner exists in this project (D-02).
- Any real hostname/IP placeholder text — reference uses `<SQL_SERVER_CT_IP>`, `yourdomain.com`, `<GMAIL_ADDRESS>` etc. as placeholders already; keep that placeholder convention (`example.com`-style) in the new doc, never fill in real values.

**Reusable verbatim-shape sections:**
- Env file creation with `chmod 600` + `chown <user>:<user>` (lines 53-70) — same shape, but the ledger project's env file needs no DB password (D-09); it does need the Data Protection certificate path/password (RESEARCH.md Pattern 4) and the `age` public key path (D-16).
- systemd unit shape (lines 110-132): `User=`, `WorkingDirectory=`, `ExecStart=/usr/bin/dotnet ...`, `Restart=always`, `EnvironmentFile=` — copy directly for the `ledger.service` unit; add the `deploy/ledger-deploy-poll.timer`/`.service` and `deploy/ledger-backup.timer`/`.service` pairs alongside it using the same `[Unit]`/`[Service]`/`[Install]` shape.
- Traefik dynamic config shape (lines 190-207) — copy the `http.routers`/`http.services` shape, but add the `ipAllowList` middleware block from RESEARCH.md's Code Examples (D-20) — the reference has no LAN/VPN allowlist at all (its app is intentionally internet-facing), which is the one thing that must NOT be copied unchanged.

---

## Shared Patterns

### Forwarded-headers / trusted-proxy configuration
**Source:** `QuestBoard.Service/Program.cs` lines 98-112, `docs/server-setup.md` line 213 (note)
**Apply to:** `Ledger.Service/Program.cs`, `docs/lxc-setup.md` Traefik section, `deploy/traefik/*.yml.template`
Every route behind Traefik must configure `ReverseProxy:KnownProxies` to the Traefik CT's address (placeholder in repo) and call `UseForwardedHeaders()` before any middleware reads `RemoteIpAddress` — otherwise IP-based logic (allowlists, rate limiting, audit logging) silently treats every request as coming from Traefik itself.

### Fail-fast Production config validation
**Source:** `QuestBoard.Service/Program.cs` lines 414-432
**Apply to:** `Ledger.Service/Program.cs` — apply the same "collect missing required keys into a list, throw one `InvalidOperationException` naming all of them" pattern to: DB connection availability, Data Protection certificate thumbprint/password, and the `age` backup public-key path. Exempt Development/Testing environments exactly as the reference does.

### systemd unit shape (EnvironmentFile + Restart=always)
**Source:** `docs/server-setup.md` lines 110-132
**Apply to:** `ledger.service`, `ledger-deploy-poll.service`/`.timer`, `ledger-backup.service`/`.timer` in `deploy/`
`User=`, `WorkingDirectory=`, `ExecStart=`, `Restart=always`, `RestartSec=10`, `EnvironmentFile=/etc/<app>/env` — consistent shape across all systemd units this phase creates.

### Environment file custody (root:group, 600/640 perms)
**Source:** `docs/server-setup.md` lines 53-70
**Apply to:** `provision.sh`, `docs/lxc-setup.md` — same `chmod`/`chown` discipline, but per D-07 the file is `640 root:ledger` (readable by the app's group, not owned by the app user directly) rather than `600 questboard:questboard` — tighten this from the reference's shape, don't copy the exact perms.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `Ledger.Repository/Migrations/*_InitialCreate.cs` | migration | CRUD | Standard `dotnet ef migrations add` output; no project-specific analog needed — follow RESEARCH.md Pattern 3 and D-10 for the `reporting` schema grants |
| `Ledger.Service/Metrics/*` (prometheus-net wiring) | utility | request-response | Reference project has no metrics endpoint at all; build directly from RESEARCH.md's `prometheus-net.AspNetCore` recommendation |
| `.github/workflows/publish.yml` | config | event-driven | Reference has no Environment-gated publish split (its deploy job runs unconditionally on a self-hosted runner); build from RESEARCH.md Architecture Diagram + Pattern 1 |
| `.github/dependabot.yml` | config | batch | Not present in reference repo checkout this session; use RESEARCH.md's Code Examples block directly (already a verified GitHub-docs shape) |
| `deploy/grafana/provisioning/datasources/postgres.yaml`, `grafana.ini.template` | config | request-response | Reference project has no Grafana; use RESEARCH.md Code Examples (Unix-socket datasource, hardening `.ini` keys) verbatim as the starting point |
| `deploy/ledger-deploy` (attestation verify, backup, migrate, rollback logic) | utility | file-I/O | Reference's `deploy.sh` is a 15-line unzip-and-restart script with none of D-03's required steps (attestation, pre-migration backup, rollback-unless-migrated); use it only for the outermost shell-script shape (`set -e`, `TAG=$1` argument handling) and build the rest from RESEARCH.md Patterns 1 and 3 plus decisions D-03/D-04 |
| `Ledger.UnitTests`/`Ledger.IntegrationTests` fixture bodies | test | request-response | csproj and fixture file contents were not read this session (out of budget for this pass); CONTEXT.md canonical refs already establish the xUnit v3/FluentAssertions/NSubstitute/InMemory/Mvc.Testing convention — planner should have the phase's Wave 0 task read `QuestBoard.UnitTests/QuestBoard.UnitTests.csproj` and one existing test file directly if exact syntax is needed |

## Metadata

**Analog search scope:** `/mnt/Data/repos/quest-board-dnd/` top-level tree, `.github/workflows/`, `docs/server-setup.md`, `QuestBoard.slnx`, `*.csproj` for Repository/Service, `QuestBoard.Service/Program.cs`, `QuestBoard.Repository/Entities/QuestBoardContext.cs` (read-only; no reference-repo files modified)
**Files scanned:** ~15 files/directories directly read or listed in the reference repo; `ing-dashboard` repo confirmed greenfield (only `.claude/`, `.planning/`, `.gitignore`)
**Pattern extraction date:** 2026-09-27
**Personal-data scrubbing:** All excerpts above already used placeholder-style values in the source (`<SQL_SERVER_CT_IP>`, `yourdomain.com`, `<GMAIL_ADDRESS>`, `<APP_CT_IP>`) — none were replaced because none were real; verified no real hostnames/IPs/names were copied into this document.
