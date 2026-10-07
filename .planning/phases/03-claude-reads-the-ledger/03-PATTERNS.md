# Phase 3: Claude Reads the Ledger - Pattern Map

**Mapped:** 2026-10-07
**Files analyzed:** 31 new/modified
**Analogs found:** 27 / 31 (4 have no in-repo analog; use RESEARCH.md skeletons)

Conventions that apply to every file (from CLAUDE.md, enforced by `build/lint/checks/10-repo-rules.sh`): `///` XML docs only, no `//`; no planning references (requirement keys, decision IDs, phase numbers, planning doc names) in code, strings, test names, tool descriptions, alert texts; synthetic data only; `example.com` and RFC 5737 addresses in templates. Primary-constructor DI, file-scoped namespaces, `<inheritdoc />` on implementations, 4-space indent.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match |
|---|---|---|---|---|
| `Ledger.Domain/Ledger/PeriodResolver.cs` (+ `DateRange`, `RelativePeriod`) | utility | transform | `Ledger.Domain/Ingestion/SyncSchedule.cs` | role-match |
| `Ledger.Domain/Ledger/IbanMask.cs`, `CounterpartyRef.cs` | utility | transform | `Ledger.Domain/Ingestion/TextNormalizer.cs`, `OpaqueKey.cs` | role-match |
| `Ledger.Domain/Ledger/ILedgerQueryStore.cs` + result records | model/interface | request-response | `Ledger.Domain/Ingestion/IIngestionStatusStore.cs`, `ILedgerStore.cs` | exact |
| `Ledger.Domain/Ledger/TotalsAggregator` (period bucketing, rollup) | service (pure) | transform | `Ledger.Domain/Ingestion/BalanceReconciler.cs` | role-match |
| `Ledger.Repository/Stores/LedgerQueryStore.cs` | store | CRUD read/aggregate | `Ledger.Repository/Stores/IngestionStatusStore.cs` | exact |
| `Ledger.Repository/RepositoryServiceCollectionExtensions.cs` (modify) | config | DI | itself | exact |
| `Ledger.Repository/LedgerDbContext.cs` (modify: `UseOpenIddict`, Identity) + entity `LedgerUserEntity.cs` | model | CRUD | `Entities/ApiKeyEntity.cs` + `LedgerDbContext.OnModelCreating` | role-match |
| `Ledger.Repository/Migrations/<ts>_AddOAuthAndIdentity.cs` | migration | batch | `Migrations/20260927192231_AddApiKeys.cs` | exact |
| `Ledger.Service/OAuth/*` (OpenIddict wiring, client seeder, authorize/login/totp/consent endpoints, grant revocation service) | config / controller / service | request-response | `Endpoints/BankEndpoints.cs`, `Ingestion/IngestionServiceCollectionExtensions.cs` | role-match |
| `Ledger.Service/Mcp/*Tools.cs` (four tools) | controller (thin adapter) | request-response | `Endpoints/StatusEndpoints.cs` / `BankEndpoints.cs` | role-match |
| `Ledger.Service/Mcp/McpMetrics.cs` | metrics | event-driven | `Ledger.Service/Metrics/SyncMetrics.cs` | exact |
| `Ledger.Service/Mcp/McpHostGuard` (host/path + LAN guard middleware) | middleware | request-response | `Hosting/OpsEndpoint.cs`, forwarded-headers block in `Program.cs` | partial |
| `Ledger.Service/Cli/LoginCommand.cs`, `GrantsCommand.cs` | CLI | request-response | `Ledger.Service/Cli/ApiKeyCommand.cs` | exact |
| `deploy/bin/ledger-login`, `deploy/bin/ledger-grants` | script | request-response | `deploy/bin/ledger-apikey` | exact |
| `Ledger.Service/Program.cs` (modify) | config | wiring | itself | exact |
| `Ledger.Service/Hosting/ProductionConfigurationValidator.cs` (modify) | config | validation | itself | exact |
| `deploy/traefik/ledger.yml.example` (modify) | config | routing | itself | exact |
| `deploy/ledger.env.example` (modify) | config | env | itself | exact |
| `deploy/provisioning/grafana/provisioning/alerting/platform-rules.yaml` (modify) | config | alert rules | itself | exact |
| `deploy/bin/ledger-selfcheck` (modify), new exposure-check script | script | batch | existing selfcheck + `deploy/tests/selfcheck-logic-test.sh` | exact |
| `docs/rest-api.md`, `docs/monitoring.md`, new MCP doc | docs | - | existing docs | role-match |
| `Ledger.UnitTests/Mcp/PeriodResolverTests.cs`, `ToolCatalogTests.cs` | test | transform | `Ledger.UnitTests/Ingestion/SyncScheduleTests.cs`, `Metrics/LedgerMetricsTests.cs` | exact |
| `Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs` (extend: Traefik template) | test | file-I/O | itself | exact |
| `Ledger.IntegrationTests/Mcp/TotalsQueryTests.cs`, `SearchPaginationTests.cs` | test | CRUD | `Ingestion/ReconciliationPipelineTests.cs`, `Dashboards/DashboardQueryTests.cs` | role-match |
| `Ledger.IntegrationTests/Mcp/OAuthFlowTests.cs` | test | request-response | `Auth/ApiKeyAuthTests.cs` + `Infrastructure/LedgerWebApplicationFactory.cs` | role-match |
| `Ledger.IntegrationTests/Security/LogRedactionTests.cs` (extend) | test | event | itself | exact |
| `packages.lock.json` in every touched project | config | - | existing locks | exact |

## Pattern Assignments

### Domain interface and records: `Ledger.Domain/Ledger/ILedgerQueryStore.cs` (interface, request-response)

**Analog:** `Ledger.Domain/Ingestion/ILedgerStore.cs` (and `IIngestionStatusStore.cs`). Interface with `///` summary per member, async with `CancellationToken cancellationToken` last, plain `record` types declared below the interface in the same file, no EF or HTTP types.

```csharp
namespace Ledger.Domain.Ingestion;

/// <summary>Reads and writes the ledger's transactions for one account at a time.</summary>
public interface ILedgerStore
{
    Task<FetchWindow> GetFetchWindowAsync(Guid accountId, TimeZoneInfo zone, CancellationToken cancellationToken);
    ...
}

/// <summary>What an account already holds: ...</summary>
public record FetchWindow(bool HasTransactions, DateOnly? LatestEffectiveDate, DateOnly? OldestPendingDate);
```
Apply: tools depend only on `ILedgerQueryStore` or a Service-layer query service, never on `LedgerDbContext`.

### Period resolver: `Ledger.Domain/Ledger/PeriodResolver.cs` (utility, transform)

**Analog:** `Ledger.Domain/Ingestion/SyncSchedule.cs` lines 36-43. Static zone helper to reuse directly for "now" and for pending rows with no booking date:

```csharp
public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone)
{
    var local = TimeZoneInfo.ConvertTime(instant, zone);
    return DateOnly.FromDateTime(local.DateTime);
}
```
Zone source: `IngestionOptions.TimeZone = "Europe/Amsterdam"` with `ResolveTimeZone()` (`Ledger.Service/Ingestion/IngestionOptions.cs`); inject `TimeProvider` (as `SyncScheduler` does). Do not reuse `TransactionReconciler.EffectiveDate` (transaction date first, lines ~259-267); name the new function `PeriodDate` (booking date, then value date, then transaction date, pending-undated falls to `LocalDate(FirstSeenAt, zone)`) and explain the difference in a `///` summary. Tests: `Microsoft.Extensions.TimeProvider.Testing` `FakeTimeProvider` (already referenced).

### Text and IBAN helpers: `IbanMask.cs`, `CounterpartyRef.cs` (utility, transform)

**Analog:** `Ledger.Domain/Ingestion/TextNormalizer.cs` (static class, `///` on every member, null-in null-out). Use `TextNormalizer.ForMatching` for counterparty comparison. IBAN normalisation for the own-account rule: `upper(replace(iban, ' ', ''))` on both sides; mask to last four (`NL••••1234`). For the opaque ref, see `Ledger.Domain/Ingestion/OpaqueKey.cs` for the existing opaque-key style.

### Query store: `Ledger.Repository/Stores/LedgerQueryStore.cs` (store, read/aggregate)

**Analog:** `Ledger.Repository/Stores/IngestionStatusStore.cs`

Header and read style (lines 1-22): primary-constructor `LedgerDbContext`, `AsNoTracking()`, project to anonymous/records in `Select`, `ToListAsync(cancellationToken)`:
```csharp
public class IngestionStatusStore(LedgerDbContext dbContext) : IIngestionStatusStore
{
    /// <inheritdoc />
    public async Task<IngestionStatus> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.SyncEnabled && connectionIds.Contains(account.BankConnectionId))
            .OrderBy(account => account.CreatedAt)
            .Select(account => new { account.Id, account.AccountKey, account.BankConnectionId })
            .ToListAsync(cancellationToken);
```
Group-and-sum shape (lines 44-50, 99-104): `.GroupBy(...).Select(group => new { Key = group.Key, Max = group.Max(...) })` then materialise. Use `Sum(x => x.Amount)` on `decimal`, never double. Status enum filter: `transaction.Status == LedgerTransactionStatus.Pending` (line 77). Reuse the existing overview inputs: last success per connection (lines 44-50), latest reconciliation verdict via `ReadFlaggedReconciliationAsync` (lines 127-163; extract or call the same logic rather than redefining "reconciles"). Entities: `LedgerTransactionEntity` (`BookingDate`, `ValueDate`, `TransactionDate`, `Amount` signed negative-out, `Currency`, `CounterpartyName`, `CounterpartyIban`, `Description`, `Status`, `FirstSeenAt`) and `LedgerAccountEntity` (`Iban`, `DisplayName`, `AccountKey`, `SyncEnabled`).
Text filter: `EF.Functions.ILike(column, escapedPattern)` with `%`, `_`, `\` escaped (filter text is LLM-supplied). Registration: add `services.AddScoped<ILedgerQueryStore, LedgerQueryStore>();` beside line 31 of `RepositoryServiceCollectionExtensions.cs`. Never read `reporting.*` views.

### Tools: `Ledger.Service/Mcp/LedgerTools.cs` (thin adapters, request-response)

**Analog:** `Ledger.Service/Endpoints/StatusEndpoints.cs` and `BankEndpoints.cs` (handlers only translate transport to a service call; static class docs say so). No MCP analog exists; use the RESEARCH.md "Tool shape" skeleton:
```csharp
[McpServerToolType]
public class LedgerTools(ILedgerQueryService ledger)
{
    [McpServerTool(Name = "money_totals", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("...")]
    public async Task<MoneyTotalsResult> MoneyTotals(MoneyTotalsRequest request, CancellationToken cancellationToken) => ...
}
```
Error rule: only `McpException` text reaches Claude; messages must be static and never contain counterparty, amount, IBAN or filter text. Descriptions use generic examples ("a supermarket") and no planning references. Do not add untrusted-data labels or sanitising; keep bank text in clearly named fields so a later envelope is additive.

### Metrics: `Ledger.Service/Mcp/McpMetrics.cs` (event-driven)

**Analog:** `Ledger.Service/Metrics/SyncMetrics.cs`. Static class, `Prometheus.Metrics.CreateCounter(name, help, labels...)` (lines 36-39), fixed-word labels only, and pre-create every label value at zero at startup (lines 58-65):
```csharp
private static readonly Counter Errors = Prometheus.Metrics.CreateCounter(
    "ledger_sync_errors_total", "Finished sync runs that failed, by reason, counted over all history.", "reason");

public static void InitialiseCounters()
{
    foreach (var reason in SyncHealth.Reasons) { Errors.WithLabels(reason); }
}
```
Names: `ledger_mcp_tool_calls_total{tool}`, `ledger_mcp_rejected_tokens_total{reason}` with reasons `expired|wrong_audience|invalid`, `ledger_oauth_grants_created_total`. Also add a unit test following `Ledger.UnitTests/Metrics/LedgerMetricsTests.cs`. Call `InitialiseCounters` from `Program.cs` next to the existing `LedgerMetrics.RecordBuildInfo` call (line 95).

### Alert rules: `platform-rules.yaml` (modify)

**Analog:** same file, rule `ledger-app-down` (lines 12-50): Prometheus datasource uid `prometheus`, refId A query, refId C threshold expression, `for`, `noDataState`, `execErrState`, annotation summary naming only the rule, never query results. Add: rejected-token burst (increase over 10m greater than 10) and new-grant alert. Alert text must carry no financial detail and no planning refs.

### CLI: `Ledger.Service/Cli/LoginCommand.cs`, `GrantsCommand.cs` (CLI)

**Analog:** `Ledger.Service/Cli/ApiKeyCommand.cs`. Static class, `RunAsync(string[] args, IConfiguration configuration)` returning 0/1/2, list-pattern `switch` on args, `Host.CreateApplicationBuilder()` with `Logging.ClearProviders()` and `AddLedgerRepository`, secret shown once on stdout with hint on stderr:
```csharp
var hostBuilder = Host.CreateApplicationBuilder();
hostBuilder.Configuration.AddConfiguration(configuration);
hostBuilder.Logging.ClearProviders();
hostBuilder.Services.AddLedgerRepository(hostBuilder.Configuration);
using var host = hostBuilder.Build();
...
return args switch
{
    ["create", var name] => await CreateAsync(store, name),
    ["list"] => await ListAsync(store),
    ["revoke", var name] => await RevokeAsync(store, name),
    _ => await UsageErrorAsync()
};
```
Dispatch from `Program.cs` before the host starts, next to lines 29-32 (`if (args.Length > 0 && args[0] == "apikey")`). Grants command: `list | revoke-all | revoke GRANT_ID`; the revoke logic lives in a service reused by the web host (OpenIddict token manager `RevokeByAuthorizationIdAsync` is verified; manager wiring needs `AddOpenIddict().AddCore(...)` in the CLI host too). Login command prints the `otpauth://` URI and Base32 secret (no QR library). Tests: copy structure of `Ledger.IntegrationTests/Auth/ApiKeyCommandTests.cs`.

### Wrapper scripts: `deploy/bin/ledger-login`, `deploy/bin/ledger-grants`

**Analog:** `deploy/bin/ledger-apikey` (full file): `set -euo pipefail`, root check, argument whitelist via regex (`NAME_PATTERN='^[a-z][a-z0-9-]{1,31}$'`), `exec systemd-run --quiet --pipe --wait --collect --uid=ledger --gid=ledger --property=EnvironmentFile=/etc/ledger/ledger.env ...` with the same hardening properties, `dotnet /opt/ledger/current/app/Ledger.Service.dll <subcommand> "$@"`. Keep `RestrictAddressFamilies=AF_UNIX`. Add offline logic tests under `deploy/tests/` and register in lint (`shellcheck`).

### Program.cs wiring (modify)

**Analog:** itself. Insert points:
- CLI dispatch: lines 29-32.
- Auth: lines 80-89. Keep `AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)` as default and the fallback policy; chain `.AddMcp(...)` and `.AddCookie(...)` for sign-in, add the named `McpPolicy` in the same `AddAuthorization` block. Use `AddIdentityCore` (not `AddIdentity`) so the default scheme stays ApiKey.
- Pipeline: lines 97-120. Host/path guard goes after `UseForwardedHeaders()` (line 97); keep `UseRouting/UseAuthentication/UseAuthorization` order; `app.MapMcp("/mcp").RequireAuthorization("McpPolicy")` after `MapBankEndpoints()`; OAuth pages use `.AllowAnonymous()` (see `bank.MapGet("/callback", CallbackAsync).AllowAnonymous();`, BankEndpoints.cs line 26).
- Forwarded headers (lines 65-78) stay unchanged; issuer, PRM resource and audience come from configuration, never from `Request`.
- Rate limiter (`AddRateLimiter`) and Kestrel `MaxRequestBodySize` go next to line 40 (`builder.Services.Configure<KestrelServerOptions>`).

Keep `public partial class Program;` at the end.

### Production validator (modify)

**Analog:** `Ledger.Service/Hosting/ProductionConfigurationValidator.cs` with `Ledger.UnitTests/Hosting/ProductionConfigurationValidatorTests.cs`. Add checks for the public MCP base URL (https, no trailing slash, resource path exactly `/mcp`) and issuer; extend the unit tests in the same style.

### Authentication handler: bearer scheme and rejection metrics

**Analog:** `Ledger.Service/Auth/ApiKeyAuthenticationHandler.cs` (lines 10-71): handler never logs or echoes the presented credential; failed validation logs only a key id at Debug; `HandleChallengeAsync` sets `401` plus `WWW-Authenticate`. For `/mcp`, the SDK's `McpAuthenticationHandler` supplies the challenge; set `ForwardAuthenticate = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme` (default `"Bearer"` does not exist here) and set `ResourceMetadata.Resource` explicitly. Count rejections by reason through an OpenIddict validation event handler, incrementing `McpMetrics` with fixed reason words only. The ApiKey scheme must not be listed in `McpPolicy`, and `/api/*` must keep rejecting bearer tokens (test both directions, see below).

### Migration: `<ts>_AddOAuthAndIdentity.cs`

**Analog:** `Ledger.Repository/Migrations/20260927192231_AddApiKeys.cs` (read it for the table-plus-grant shape; runtime role DML on new tables comes via default privileges, nothing to grant manually) and `LedgerDbContext.OnModelCreating` / `Conventions/SnakeCaseNaming.cs` (research assumption A2: check OpenIddict and Identity entities against the snake-case convention; generated migrations are exempt from the `//` lint). Generate with the existing design-time factory `LedgerDbContextDesignTimeFactory.cs`; `Migrations/` is generated code.

### Traefik template (modify)

**Analog:** `deploy/traefik/ledger.yml.example`. Reuse middlewares `ledger-lan-vpn-only` and `ledger-security-headers` (lines 22-40) and the router shape (lines 43-62: `rule`, `entryPoints: [websecure]`, `middlewares`, `service`, `tls.certResolver: letsencrypt`) and service `ledger-api` (port 5080). Add `ledger-mcp-allow` (`160.79.104.0/21` plus the LAN/VPN placeholders), router `ledger-mcp-public` (exact `Path()` list from RESEARCH.md "Network exposure") and `ledger-mcp-signin` (`/connect/authorize`, `PathPrefix(/account/)`, LAN/VPN only). Placeholders only (`mcp.example.com`, 192.0.2.0/24, 198.51.100.0/24); none of the five phased-out `34.162.x.x/32` addresses may appear. Keep the header comment accurate (it currently says only Grafana and `/api/` are routed; update it to describe the MCP routers).

### Config test for the template

**Analog:** `Ledger.UnitTests/Configuration/CommittedConfigurationTests.cs` (`[Trait("Category", "Configuration")]`, `FindRepositoryRoot()`, reads committed files, FluentAssertions with a reason string). Add tests: MCP host uses only exact `Path()` plus the one `PathPrefix('/account/')`, allowlist contains `160.79.104.0/21`, no phased-out addresses, sign-in router uses the LAN/VPN middleware.

### Selfcheck and exposure script

**Analog:** `deploy/bin/ledger-selfcheck` and `deploy/tests/selfcheck-logic-test.sh` (offline logic test pattern with `deploy/tests/lib` and `deploy/tests/fixtures`). Add the "no unexpected sudo login" check there; a new outside-in exposure script gets a matching `deploy/tests/exposure-check-logic-test.sh`. All pass `build/lint.sh` (shellcheck, gitleaks). No personal hostnames or account names in the repo; the real account list lives only in the operator's local notes.

### Tests

**Unit (`Ledger.UnitTests/Mcp/*`):** `[Fact]` plus `[Trait("Category", "<X>")]` (categories `Periods`, `McpTools`), FluentAssertions, `FakeTimeProvider`; flat class per subject like `Ledger.UnitTests/Ingestion/SyncScheduleTests.cs`. Test names must not contain planning references.

**Integration (`Ledger.IntegrationTests/Mcp/*`):** copy the `ApiKeyAuthTests` structure (lines 1-45 read):
```csharp
[Collection("Database")]
public partial class ApiKeyAuthTests(DatabaseFixture fixture)
{
    [Fact]
    [Trait("Category", "ApiAuth")]
    public async Task ...()
    {
        await using var factory = new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"));
        using var client = factory.CreateApiClient();
        await Wait.UntilReadyAsync(factory);
```
Use `fixture.ConnectionStringFor("ledger_runtime")` (least-privilege role) for host tests; `LedgerWebApplicationFactory` boots real Kestrel sockets, exposes `ApiPort`/`OpsPort`, accepts `additionalConfiguration` (use it to set MCP base URL, issuer, client seed values) and `configureTestServices` (replace `TimeProvider` with `FakeTimeProvider`). For seeding use the style in `Ingestion/IngestionTestSupport.cs`. `Ledger.IntegrationTests/Auth/ApiKeyAuthTests.cs` "Every_mapped_endpoint_returns_401_without_a_key" enumerates `EndpointDataSource`; it will now meet `/mcp` and the OAuth pages, so update its exclusion list (the anonymous callback route is excluded via `AnonymousBankCallbackRoute`) and assert the `/mcp` 401 challenge is `Bearer resource_metadata=...`. Add the OAuth driver (password, computed TOTP, PKCE) in `Infrastructure/`. Extend `Security/LogRedactionTests.cs` with a tool call carrying a synthetic counterparty and a bearer token.

## Shared Patterns

### No secrets or financial values in logs, exceptions, labels
**Source:** `ApiKeyAuthenticationHandler.cs` (never echoes the credential) and `SyncMetrics.cs` class summary ("every label is an opaque key or a fixed reason word"). **Apply to:** all new Service code, tools, metrics, CLI output (except the one-time shown secret). Set `ModelContextProtocol` and `OpenIddict` log categories to Warning in `appsettings.json`.

### Layering
**Source:** `RepositoryServiceCollectionExtensions.cs`, `Ledger.Domain` has no EF/HTTP. EF packages stay in `Ledger.Repository`; `Ledger.Service` references only OpenIddict ASP.NET Core packages and the MCP SDK. **Apply to:** every new file; add packages with lock-file regeneration (`dotnet restore --force-evaluate`), as CI restores in locked mode.

### Authorization posture
**Source:** `Program.cs` lines 80-89 (default scheme ApiKey, fallback policy requires authenticated user). **Apply to:** `.AllowAnonymous()` only on OAuth/sign-in/discovery handlers; `/mcp` uses its own named policy; REST never accepts bearer, `/mcp` never accepts X-Api-Key.

### Thin transport handlers
**Source:** `BankEndpoints.cs` class summary ("Every handler only translates HTTP to a service call"). **Apply to:** tools and OAuth endpoints; business rules in Domain, queries in Repository.

### Money
**Source:** `LedgerTransactionEntity.Amount` is `decimal`, signed negative-out. **Apply to:** all totals; serialise decimals as strings with currency, report `money_out`/`money_in` as non-negative magnitudes and `net = in - out`, never sum across currencies.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `Ledger.Service/OAuth/OpenIddictSetup.cs` (AddOpenIddict server and validation) | config | request-response | No OAuth server exists; use RESEARCH.md Pattern 2 (verify EF `UseOpenIddict` and data-protection token format per the research's assumptions) |
| `Ledger.Service/OAuth/ClientSeeder` (hosted service creating the two pre-registered clients) | service | batch | Closest style: `Health/DataProtectionCanaryInitializer.cs` (startup hosted service); contents from RESEARCH.md Pattern 3 |
| Sign-in pages (login, TOTP, consent) | controller/view | request-response | No server-rendered pages exist; follow RESEARCH.md Pitfall 16 (antiforgery, `no-store`, strict CSP, local-only returnUrl, no inline JS) and the project's `senior-frontend` skill |
| `Ledger.Service/Mcp/McpHostGuard` | middleware | request-response | No custom middleware exists; use `OpsEndpoint.cs` for the configuration-reading convention and RESEARCH.md "Network exposure" for the rule set |

## Metadata

**Analog search scope:** `Ledger.Domain`, `Ledger.Repository`, `Ledger.Service`, `Ledger.UnitTests`, `Ledger.IntegrationTests`, `deploy/` (bin, traefik, provisioning, tests)
**Files scanned:** about 15 read in full or in part, 200 listed
**Pattern extraction date:** 2026-10-07
