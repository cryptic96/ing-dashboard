# Phase 2: Automatic ING Sync - Pattern Map

**Mapped:** 2026-09-30
**Files analyzed:** 34 new/modified (grouped)
**Analogs found:** 27 / 34 (7 have no in-repo analog; use RESEARCH.md examples)

Conventions common to every C# file (observed in all existing files): file-scoped namespace, `///` XML summary on every public type and member (`/// <inheritdoc />` on overrides), no `//` comments, primary constructors for DI (`class ApiKeyStore(LedgerDbContext dbContext) : IApiKeyStore`), `Guid.CreateVersion7()` for ids, `DateTimeOffset` for instants, `CancellationToken cancellationToken` last parameter. Never put planning references (decision IDs, phase numbers, planning doc names) in code, tests, dashboards, alert text or docs.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match |
|---|---|---|---|---|
| `Ledger.Domain/Banking/IBankDataProvider.cs` (+ DTOs) | interface | request-response / streaming | `Ledger.Domain/Auth/IApiKeyStore.cs` | role-match |
| `Ledger.Domain/Ingestion/TransactionReconciler.cs`, `BalanceReconciler.cs`, `ConsentState.cs`, `SyncSchedule.cs`, `CallBudget.cs`, `MoneyParser.cs` | domain logic (pure) | transform | `Ledger.Domain/Auth/ApiKeyToken.cs` (pure static helpers) | partial |
| `Ledger.Domain/Ingestion/I*Store.cs` (ledger, connection, authorization, run stores) | interface | CRUD | `Ledger.Domain/Auth/IApiKeyStore.cs`, `Security/IDataProtectionCanaryStore.cs` | exact |
| `Ledger.Repository/Entities/*` (BankConnection, BankAuthorization, LedgerAccount, LedgerTransaction, TransactionRef, TransactionPayload, BalanceSnapshot, SyncRun, ProviderCall) | model | CRUD | `Ledger.Repository/Entities/ApiKeyEntity.cs` | exact |
| `Ledger.Repository/LedgerDbContext.cs` (modify) | config | CRUD | itself (ApiKeyEntity block) | exact |
| `Ledger.Repository/Stores/*Store.cs` | store | CRUD | `Ledger.Repository/Stores/ApiKeyStore.cs` | exact |
| `Ledger.Repository/RepositoryServiceCollectionExtensions.cs` (modify) | config | - | itself | exact |
| `Ledger.Repository/Migrations/*_AddBankSync.cs` (tables, indexes, REVOKE on payloads, `reporting` views) | migration | CRUD | `20260927192231_AddApiKeys.cs`; `20260927183611_InitialCreate.cs` (raw SQL, reporting schema) | exact |
| `Ledger.Service/Ingestion/EnableBanking/EnableBankingClient.cs`, DTOs, error mapper | service (adapter) | request-response, paged | no HTTP-client analog | none |
| `Ledger.Service/Ingestion/EnableBanking/EnableBankingTokenMinter.cs` | utility | transform | RESEARCH.md "Mint the Enable Banking client JWT" | none (use research) |
| `Ledger.Service/Ingestion/EnableBanking/AisOnlyGuardHandler.cs` | middleware (DelegatingHandler) | request-response | RESEARCH.md guard example | none (use research) |
| `Ledger.Service/Ingestion/Synthetic/SyntheticBankDataProvider.cs` | service | streaming | none; must implement `IBankDataProvider` | none |
| `Ledger.Service/Ingestion/SyncOrchestrator.cs` | service | batch / CRUD | `Ledger.Service/Health/DataProtectionCanaryInitializer.cs` (scope-per-run) | partial |
| `Ledger.Service/Ingestion/SyncScheduler.cs` | background service | event-driven / batch | `Ledger.Service/Health/DataProtectionCanaryInitializer.cs` | role-match |
| `Ledger.Service/Ingestion/SyncMetricsRefresher.cs` | background service | batch | `DataProtectionCanaryInitializer.cs` + `Metrics/HealthCheckMetricsPublisher` | role-match |
| `Ledger.Service/Metrics/SyncMetrics.cs` | utility | pub-sub (metrics) | `Ledger.Service/Metrics/LedgerMetrics.cs` | exact |
| `Ledger.Service/Endpoints/BankEndpoints.cs` | route | request-response | `Ledger.Service/Endpoints/StatusEndpoints.cs` | exact |
| `Ledger.Service/Program.cs` (modify) | config | - | itself | exact |
| `Ledger.Service/Hosting/ProductionConfigurationValidator.cs` (modify: EB app id, key path; reject synthetic provider) | config | - | itself | exact |
| `Ledger.Service/appsettings*.json`, `deploy/ledger.env.example` (modify, placeholders only) | config | - | existing files | exact |
| `Ledger.Dashboards/*` (console generator, typed model, translations, `generate`/`check`) | utility (tool) | transform / file-I/O | no console project exists; `Ledger.slnx`, `Directory.Build.props` for wiring | none |
| `deploy/provisioning/grafana/provisioning/dashboards/json/*.json` (generated) + `dashboards/ledger.yaml` (modify path) | config | file-I/O | `dashboards/ledger.yaml` | role-match |
| `deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml` | config | - | `alerting/platform-rules.yaml` | exact |
| `alerting/notification-policies.yaml` (modify: child route) | config | - | itself | exact |
| `build/lint/checks/60-observability.sh` (modify: rule count 8 -> N, dashboards) | script | - | itself | exact |
| `deploy/lib/deploy.sh`, `deploy/bin/ledger-selfcheck` (modify: secret scan, sandboxing) | script | batch | themselves + `deploy/tests/selfcheck-logic-test.sh` | exact |
| `Ledger.UnitTests/Ingestion/*Tests.cs`, `Dashboards/*Tests.cs` | test | - | `Ledger.UnitTests/Metrics/LedgerMetricsTests.cs`, `Auth/ApiKeyTokenTests.cs` | exact |
| `Ledger.IntegrationTests/Ingestion/*`, `Database/DatabaseRoleTests.cs` (modify) | test | CRUD | `DatabaseRoleTests.cs`, `Auth/ApiKeyAuthTests.cs` | exact |
| `docs/bank-link.md`, `docs/rest-api.md`, `docs/monitoring.md`, `.http` file | docs | - | `docs/rest-api.md`, `docs/monitoring.md` | role-match |
| `Ledger.slnx`, `*.csproj`, `packages.lock.json` (new Dashboards project, JWT package, FakeTimeProvider) | config | - | existing csproj + lock files | exact |

## Pattern Assignments

### Domain interfaces (`IBankDataProvider`, ledger stores)

**Analog:** `Ledger.Domain/Auth/IApiKeyStore.cs`. Interface plus records and a domain exception in the same file; every member has a `///` summary; DTOs are positional `record`s that never carry secrets.

```csharp
namespace Ledger.Domain.Auth;

/// <summary>Creates, lists, revokes and validates named per-client API keys.</summary>
public interface IApiKeyStore
{
    /// <summary>Lists every key ... Never includes a secret.</summary>
    Task<IReadOnlyList<ApiKeySummary>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>A key's public metadata. Never carries a secret.</summary>
public record ApiKeySummary(string Name, string KeyId, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>Thrown when ...</summary>
public class ApiKeyOperationException(string message) : Exception(message);
```

Domain has no EF or HTTP references. Provider DTO shape (statuses, dates as `DateOnly?`, signed `decimal`) is specified in RESEARCH.md "Pattern 1"; copy that interface verbatim. Secret storage uses `Ledger.Domain/Security/ISecretProtector.cs` (`string Protect(string)`, `string Unprotect(string)`).

### Entities (`Ledger.Repository/Entities/*`)

**Analog:** `Ledger.Repository/Entities/ApiKeyEntity.cs`. Plain class, `///` on every property, defaults `= string.Empty` / `= []`, nullable `DateTimeOffset?` for optional instants.

```csharp
namespace Ledger.Repository.Entities;

/// <summary>A named per-client API key. Only the key id and the SHA-256 of its secret are stored, never the secret.</summary>
public class ApiKeyEntity
{
    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[] SecretSha256 { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
```

Use `DateOnly` for bank calendar dates, `decimal` with `HasColumnType("numeric(19,4)")` for money, text plus check constraint for status columns.

### `LedgerDbContext.cs` (modify)

**Analog:** the existing file. Add one `DbSet<T>` with `///` per entity and one `modelBuilder.Entity<T>` block each, then keep `SnakeCaseNaming.Apply(modelBuilder);` last.

```csharp
modelBuilder.Entity<ApiKeyEntity>(entity =>
{
    entity.HasKey(key => key.Id);
    entity.Property(key => key.Name).HasMaxLength(32).IsRequired();
    entity.HasIndex(key => key.KeyId).IsUnique();
    entity.HasIndex(key => key.Name).IsUnique().HasFilter("revoked_at IS NULL");
});
```
Partial unique indexes use `.HasFilter("...")` (reuse for `sync_runs ... WHERE finished_at IS NULL`). Check constraints: `entity.ToTable(table => table.HasCheckConstraint("ck_data_protection_canary_id", "id = 1"));`.

### Stores (`Ledger.Repository/Stores/*Store.cs`)

**Analog:** `Ledger.Repository/Stores/ApiKeyStore.cs`. Primary-constructor `LedgerDbContext`, `AsNoTracking()` for reads, unique-violation mapped to a domain exception, `Guid.CreateVersion7()`.

```csharp
try
{
    await dbContext.SaveChangesAsync(cancellationToken);
}
catch (DbUpdateException exception)
    when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
{
    throw new ApiKeyOperationException($"An active key named '{name}' already exists.");
}
```
For the one-time `state` consumption use an atomic `ExecuteUpdateAsync`/raw `UPDATE ... WHERE consumed_at IS NULL AND expires_at > now()` and check rows affected (no analog; described in RESEARCH.md "Data Model"). Register each store in `RepositoryServiceCollectionExtensions.AddLedgerRepository` next to `services.AddScoped<IApiKeyStore, ApiKeyStore>();`. Stores are scoped; hosted services must create a scope (see scheduler below).

### Migration

**Analog:** `Migrations/20260927192231_AddApiKeys.cs` (generated `CreateTable`, `CreateIndex` with `unique`/`filter`, `Down` drops) and `20260927183611_InitialCreate.cs` for raw SQL via `migrationBuilder.Sql`.

Existing reporting grant (InitialCreate lines ~44-60), so views created by `ledger_migrator` in schema `reporting` inherit SELECT for `grafana_reader` automatically:
```csharp
migrationBuilder.Sql("CREATE SCHEMA reporting;");
migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES FOR ROLE ledger_migrator IN SCHEMA reporting GRANT SELECT ON TABLES TO grafana_reader;");
migrationBuilder.Sql("REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE \"__EFMigrationsHistory\" FROM ledger_runtime;");
```
Mirror the REVOKE line for `transaction_payloads` (append-only). Add `CREATE VIEW reporting.*` with `migrationBuilder.Sql` and matching `DROP VIEW` in `Down`. Generate with `dotnet ef`, then hand-edit to add raw SQL; also update `LedgerDbContextModelSnapshot.cs`/Designer via the tool.

### `BankEndpoints.cs`

**Analog:** `Ledger.Service/Endpoints/StatusEndpoints.cs`. Static class, extension on `IEndpointRouteBuilder` returning `endpoints`, `/api/v1/...` route, `Results.Ok(new XResponse(...))` with a private sealed record; authenticated by default via the fallback policy.

```csharp
public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder endpoints)
{
    endpoints.MapGet("/api/v1/status", (HttpContext context) =>
    {
        var client = context.User.Identity?.Name ?? string.Empty;
        return Results.Ok(new StatusResponse(version, client));
    });
    return endpoints;
}
private sealed record StatusResponse(string Version, string Client);
```
The callback is the single exception: `endpoints.MapGet("/api/v1/bank/callback", ...).AllowAnonymous();` (RESEARCH.md "Callback opts out"). Wire in `Program.cs` after `app.MapStatusEndpoints();` with `app.MapBankEndpoints();`. Add responses with `Cache-Control: no-store`, `Referrer-Policy: no-referrer`; never log query string.

### `Program.cs` (modify)

**Analog:** itself. Register in the services block before `builder.Build()`: `builder.Services.AddHostedService<...>()`, options binding, `IHttpClientFactory` typed client for Enable Banking (NO `AddStandardResilienceHandler`), `TimeProvider.System` singleton. Auth stays as is:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
```
Production-only fail-fast config check goes through `ProductionConfigurationValidator.ThrowIfInvalid(builder.Configuration)` (top of file); add EB application id / key path checks and refuse the synthetic provider there.

### `SyncScheduler.cs`, `SyncMetricsRefresher.cs`, `SyncOrchestrator.cs`

**Analog:** `Ledger.Service/Health/DataProtectionCanaryInitializer.cs`. `BackgroundService` with `IServiceScopeFactory`, scope per iteration, cancellation handled separately from generic failure, log warning without secrets, delay before retry.

```csharp
public class DataProtectionCanaryInitializer(IServiceScopeFactory scopeFactory, ILogger<DataProtectionCanaryInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IDataProtectionCanaryStore>();
                ...
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not initialize the Data Protection canary; retrying.");
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}
```
Differences: scheduler ticks with `PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider)` and injected `TimeProvider` (RESEARCH.md Pattern 5); loop never returns on success. Since `.editorconfig`/code style forbids `//`, express intent via method names.

### `SyncMetrics.cs`

**Analog:** `Ledger.Service/Metrics/LedgerMetrics.cs`. Static class, private static readonly `Gauge`/`Counter` created with `Prometheus.Metrics.CreateGauge(name, help, labelNames)`, small `Record*` methods using `WithLabels(...).Set(...)`. Labels are opaque `account_key`/`connection` values only.

```csharp
private static readonly Gauge HealthCheckStatus = Prometheus.Metrics.CreateGauge(
    "ledger_health_check_status",
    "1 when the named health check is healthy, 0 otherwise.",
    "check");

public static void RecordHealthCheckStatus(string checkName, bool isHealthy)
{
    HealthCheckStatus.WithLabels(checkName).Set(isHealthy ? 1 : 0);
}
```
Note the `Prometheus.Metrics` fully-qualified use (the namespace `Ledger.Service.Metrics` collides). In `Program.cs` the class is aliased: `using LedgerMetrics = Ledger.Service.Metrics.LedgerMetrics;`, do the same for `SyncMetrics`. Metric names and labels: RESEARCH.md "Metrics, Alerts and Notification Cadence". Pre-initialise counters for each reason label.

### Alert rules `household-rules.yaml`

**Analog:** `alerting/platform-rules.yaml`. Copy the structure exactly: header comment (no planning refs, "never a query result"), `apiVersion: 1`, `groups` with `orgId: 1`, `interval: 1m`, per rule `uid`, `title`, `condition: C`, Prometheus query node `A` (`datasourceUid: prometheus`, `instant: true`), threshold expression node `C` (`datasourceUid: __expr__`, reducer `last`), `for`, `noDataState`, `execErrState: Alerting`, `annotations.summary` naming only the rule, `labels: {}`. Use `noDataState: OK` for household rules (no series before first link). The stale-sync threshold `params: [93600]` already exists in the platform backup-stale rule (same 26 h). Folder `Household`, group `household`.

### `notification-policies.yaml` (modify)

Single provisioned tree, so add a child `routes:` entry under the existing root (do not add a second file). Existing root:
```yaml
policies:
  - orgId: 1
    receiver: operator-email
    group_by: [grafana_folder]
    group_wait: 1m
    group_interval: 1h
    repeat_interval: 12h
```
Child route matches household consent rules with `repeat_interval: 24h`.

### `dashboards/ledger.yaml` (modify) and generated JSON

**Analog:** the existing provider. Change `options.path` from `/var/lib/grafana/dashboards/ledger` to `/etc/grafana/provisioning/dashboards/json` (installer copies the whole `provisioning` tree, `deploy/lib/deploy.sh` around lines 394-395) and rewrite the header comment (it currently states no dashboards ship). Keep `folder: Household Ledger`, `disableDeletion: true`, `allowUiUpdates: false`. Datasource reference in JSON: `{"type":"grafana-postgresql-datasource","uid":"ledger-reporting"}`.

### `Ledger.Dashboards` project (no analog)

Wire like existing projects: add to `Ledger.slnx` (`<Project Path="Ledger.Dashboards/Ledger.Dashboards.csproj" />`), inherit `Directory.Build.props` (nullable, warnings as errors, `RestorePackagesWithLockFile`), commit its `packages.lock.json`. Design per RESEARCH.md "Dashboard Generator". Tests go in `Ledger.UnitTests` referencing the generator class (drift byte-compare, translation completeness, only `reporting.` SQL).

### Tests

**Unit analog:** `Ledger.UnitTests/Metrics/LedgerMetricsTests.cs` (FluentAssertions, `[Fact]`/`[Theory]`, summary comment on class, no `//`). Use `FakeTimeProvider` for scheduler/quota tests.

**Integration analog:** `Ledger.IntegrationTests/Database/DatabaseRoleTests.cs` and `Auth/ApiKeyAuthTests.cs`. `[Collection("Database")]`, primary-constructor `DatabaseFixture fixture`, `[Trait("Category", "...")]` (CI filter), helpers `AssertDeniedAsync("role", sql, "42501")` / `AssertSucceedsAsync`, `new LedgerWebApplicationFactory(fixture.ConnectionStringFor("ledger_runtime"))`, `factory.CreateApiClient()`.

```csharp
[Fact]
[Trait("Category", "DatabaseRoles")]
public async Task Grafana_reader_role_can_only_read_reporting()
{
    await AssertDeniedAsync("grafana_reader", "SELECT * FROM public.data_protection_keys", "42501");
    await ExecuteAsync("ledger_migrator", "CREATE VIEW reporting.role_test_view AS SELECT 1 AS value");
    await AssertSucceedsAsync("grafana_reader", "SELECT * FROM reporting.role_test_view");
}
```
Extend with a data-driven check: every object in `reporting` is SELECT-only for `grafana_reader`, `transaction_payloads` cannot be UPDATEd/DELETEd by `ledger_runtime`, and `grafana_reader` cannot read `public.transactions`.

Endpoint inventory (existing test enumerates `EndpointDataSource` and expects 401 on every route without a key). That test will now fail on the callback: change it to skip the single allow-listed anonymous route and add the "only the callback allows anonymous" test from RESEARCH.md:
```csharp
var endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
var routeEndpoints = endpointDataSource.Endpoints.OfType<RouteEndpoint>().ToList();
```
Log redaction: extend `Ledger.IntegrationTests/Security/LogRedactionTests.cs` to cover `state`, `code`, session id and the EB key.

## Shared Patterns

### Secrets at rest
**Source:** `Ledger.Domain/Security/ISecretProtector.cs` + `Ledger.Service/Security/DataProtectionSecretProtector.cs` (registered singleton in `Program.cs`).
**Apply to:** bank session id storage (`session_id_protected`). Never log, never in metric labels or exception messages.

### Authentication
**Source:** `Ledger.Service/Auth/ApiKeyAuthenticationHandler.cs` + fallback policy in `Program.cs`.
**Apply to:** all `BankEndpoints` except the callback (`AllowAnonymous`, guarded by hashed single-use state).

### Error handling in stores and services
**Source:** `ApiKeyStore.CreateAsync` (unique violation to domain exception) and `DataProtectionCanaryInitializer` (log, do not swallow cancellation). Errors and log messages carry no amounts, IBANs, counterparties or tokens.

### Time
Inject `TimeProvider` (register `TimeProvider.System`); existing stores use `DateTimeOffset.UtcNow` directly, new time-sensitive code (scheduler, quota, state TTL, consent state) must use the injected provider for `FakeTimeProvider` tests.

### Test traits and CI filters
`[Trait("Category", "...")]` on integration tests (see `DatabaseRoles`, `ApiAuth`). Keep traits working through the test-platform migration.

### Lock files
Every project has `packages.lock.json`; adding `Microsoft.IdentityModel.JsonWebTokens` (Service) and `Microsoft.Extensions.TimeProvider.Testing` (test projects) and the new project require regenerated, committed lock files (CI restores in locked mode).

### Host scripts
New host-side logic follows the `deploy/tests/*-logic-test.sh` offline-test pattern and must pass `build/lint.sh` (shellcheck, gitleaks). `60-observability.sh` hard-codes exactly 8 alert rules (~line 266); update the count with the new rules.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `EnableBankingClient` (typed HttpClient, paging, error mapping) | service | request-response | No outbound HTTP client exists yet; follow RESEARCH.md continuation-loop and error tables. Do not add standard resilience handler (retries 429) |
| `EnableBankingTokenMinter` | utility | transform | Use RESEARCH.md verified example |
| `AisOnlyGuardHandler` | middleware | request-response | Use RESEARCH.md example plus recording-handler test |
| `SyntheticBankDataProvider` + scenario builder | service | streaming | Implement the domain interface; fixtures fully synthetic, provider refused in Production by the validator |
| `TransactionReconciler` / `BalanceReconciler` / `SyncSchedule` | domain | transform | No comparable pure logic; RESEARCH.md Patterns 3, 5, 6 and the `SyncSchedule` example |
| `Ledger.Dashboards` console tool | tool | transform | No console/tool project yet |
| Migration-level `reporting` views | migration | CRUD | Only schema and default grants exist; write views as raw SQL |

## Metadata

**Analog search scope:** whole repository via `git ls-files` (Ledger.Domain, Ledger.Repository, Ledger.Service, both test projects, deploy/, build/, docs/)
**Files read:** StatusEndpoints, LedgerMetrics, ISecretProtector, IApiKeyStore, ApiKeyEntity, LedgerDbContext, ApiKeyStore, RepositoryServiceCollectionExtensions, Program, DataProtectionCanaryInitializer, AddApiKeys migration, InitialCreate (grep), DatabaseRoleTests, ApiKeyAuthTests, platform-rules.yaml, notification-policies.yaml, dashboards/ledger.yaml, LedgerMetricsTests, Ledger.slnx
**Pattern extraction date:** 2026-09-30
