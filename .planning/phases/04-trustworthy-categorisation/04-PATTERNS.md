# Phase 4: Trustworthy Categorisation - Pattern Map

**Mapped:** 2026-10-13
**Files analyzed:** 34 new or modified
**Analogs found:** 31 / 34

Repo-wide rules for every new file (CLAUDE.md, lint in `build/lint/checks/10-repo-rules.sh`): `///` comments only, no requirement keys, decision ids, "Phase N" or planning file names in code, strings, test names, dashboards or docs, no personal data, synthetic fixtures only. Primary constructors, file-scoped namespaces and 4-space indent are the existing style.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `Ledger.Repository/Entities/Category*.cs`, `CategorisationRuleEntity`, `TransactionCategorisationEntity`, `CategoryProposalEntity`, `TransactionNoteEntity`, `IntermediaryCounterpartyEntity`, `OwnAccountEntity`, `TransactionTextFlagEntity`, `PendingChangeEntity`, `AuditOperationEntity`, `AuditItemEntity` | model | CRUD | `Ledger.Repository/Entities/BankAuthorizationEntity.cs` (one-time hash, expiry, consumed) and `ProviderCallEntity.cs` (append-only) | exact |
| `Ledger.Repository/LedgerDbContext.cs` (modify) | config | CRUD | `ConfigureProviderCalls` in same file | exact |
| `Ledger.Repository/Migrations/<new>_AddCategorisation.cs` (tables, grants, views, starter tree) | migration | batch | `Migrations/20260930181840_AddLedgerIngestion.cs` | exact |
| `Ledger.Repository/Stores/CategoryStore`, `RuleStore`, `AssignmentStore`, `AuditStore`, `PendingChangeStore`, `CategoryQueryStore` | store | CRUD | `Stores/BankAuthorizationStore.cs` (atomic consume) and `Stores/LedgerStore.cs` | role-match |
| `Ledger.Repository/Stores/LedgerQueryStore.cs` (modify: kind exclusion, category filter and grouping) | store | request-response | itself, lines 296-352 | exact |
| `Ledger.Domain/Categorisation/RuleMatcher`, `RuleSpecificity`, `RuleCriteria`, `CategoryTree`, `UndoPlanner`, `ComparisonCalculator`, `ChangeSetFingerprint`, `UntrustedText`, `InstructionTextDetector` | utility | transform | `Ledger.Domain/Ingestion/TextNormalizer.cs`, `Ingestion/TransactionReconciler.cs`, `Queries/TotalsAggregator.cs`, `Queries/IbanText.cs` | role-match |
| `Ledger.Domain/Categorisation/I*Store.cs` | interface | CRUD | `Ledger.Domain/Ingestion/IBankAuthorizationStore.cs` | exact |
| `Ledger.Service/Categorisation/CategorisationService`, `RuleService`, `CategoryTreeService`, `ConfirmationService`, `WriteGuard` | service | CRUD plus request-response | `Ledger.Service/Queries/LedgerQueryService.cs`, `Ingestion/BankLinkService.cs` | role-match |
| `Ledger.Service/Categorisation/CategorisationEngine.cs` | service | batch | `Ledger.Service/Ingestion/SyncOrchestrator.cs` (hook at line 141) | role-match |
| `Ledger.Service/Categorisation/ReviewMetricsRefresher.cs` | service (hosted) | event-driven | `Ledger.Service/Ingestion/SyncMetricsRefresher.cs` | exact |
| `Ledger.Service/Metrics/CategorisationMetrics.cs` and write counters | utility | pub-sub | `Ledger.Service/Mcp/McpMetrics.cs`, `Metrics/SyncMetrics.cs` | exact |
| `Ledger.Service/Mcp/ReviewTools`, `CategoryTools`, `CompareTools`, `ChangeTools` (write), `ApplyTools` | controller (MCP tool) | request-response | `Ledger.Service/Mcp/LedgerTools.cs` | exact |
| `Ledger.Service/Mcp/LedgerTools.cs`, `ServerInstructions.cs`, `McpEndpoint.cs` (modify) | controller/config | request-response | themselves | exact |
| `Ledger.Service/Endpoints/AuditEndpoints.cs` | route | request-response | `Ledger.Service/Endpoints/StatusEndpoints.cs`, `BankEndpoints.cs` | exact |
| `Ledger.Service/Queries/*Result.cs` (modify: untrusted wrapper, category fields) | model | request-response | `Queries/SearchResult.cs`, `TotalsResult.cs` | exact |
| `Ledger.Dashboards/Definitions/SpendingDashboard.cs` | config | transform | `Definitions/SyncDashboard.cs` | exact |
| `Ledger.Dashboards/Model/Panels.cs`, `FieldConfig.cs` (modify: Stat, TimeSeries, BarChart) | model | transform | `TablePanel` in `Panels.cs` | exact |
| `Ledger.Dashboards/DashboardGenerator.cs`, `translations.json` (modify) | utility | transform | themselves | exact |
| `deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml` (modify) | config | event-driven | existing rules in same file | exact |
| `docs/mcp.md`, `docs/rest-api.md`, `docs/monitoring.md` (modify) | docs | n/a | existing docs | exact |
| Unit tests under `Ledger.UnitTests/Categorisation/` | test | transform | `Ledger.UnitTests/Auth/TotpCodesTests.cs`, `Dashboards/DashboardGeneratorTests.cs` | role-match |
| Integration tests under `Ledger.IntegrationTests/Categorisation/` and `Mcp/` | test | request-response | `Mcp/ToolSurfaceTests.cs`, `Mcp/LedgerQuerySeed.cs`, `Database/DatabaseRoleTests.cs` | exact |
| `DatabaseRoleTests.cs`, `DashboardQueryTests.cs`, `DashboardGeneratorTests.cs` (modify) | test | CRUD | themselves | exact |
| `ConfirmationService` MRTR elicitation | service | request-response | none in repo (see No Analog) | none |
| Audited write pipeline (`AuditContext`, `IUndoHandler`) | service | CRUD | none in repo | none |
| Architecture test (no store or DbContext in `Ledger.Service.Mcp`) | test | n/a | none in repo | none |

## Pattern Assignments

### New entities (`Ledger.Repository/Entities/`)

**Analog:** `Ledger.Repository/Entities/BankAuthorizationEntity.cs` (lines 1-29)

Plain POCO, `///` summary on class and every property, `Guid Id`, `DateTimeOffset` times, nullable `ConsumedAt`, non-null strings default to `string.Empty`, byte arrays default `[]`.

```csharp
namespace Ledger.Repository.Entities;

/// <summary>A pending bank authorisation. Only the SHA-256 of its one-time state is stored, never the state itself.</summary>
public class BankAuthorizationEntity
{
    /// <summary>The row's primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The SHA-256 of the one-time state, unique across rows.</summary>
    public byte[] StateSha256 { get; set; } = [];
    ...
    /// <summary>When the state stops being accepted.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the state was used, or null while it is unused.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
}
```

Use for `PendingChangeEntity` (`token_sha256`, `expires_at`, `consumed_at`). Audit tables copy `ProviderCallEntity` (append-only).

### `LedgerDbContext.cs` configuration

**Analog:** `ConfigureProviderCalls`, `Ledger.Repository/LedgerDbContext.cs` lines 122-141. One private static `Configure*` per entity, registered from `OnModelCreating` (near line 90), enums via `SnakeCaseEnumConverter<T>`, CHECK from `EnumText.CheckList<T>()`, `DeleteBehavior.Restrict`.

```csharp
entity.Property(call => call.Kind).HasConversion(new SnakeCaseEnumConverter<ProviderCallKind>()).IsRequired();
entity.HasOne<LedgerAccountEntity>().WithMany().HasForeignKey(call => call.AccountId).OnDelete(DeleteBehavior.Restrict);
entity.ToTable(table => table.HasCheckConstraint(
    "ck_provider_calls_kind",
    $"kind IN ({EnumText.CheckList<ProviderCallKind>()})"));
```

Add a `DbSet<...> X { get; set; } = null!;` per entity (see line 52).

### Migration (tables, grants, views, starter tree)

**Analog:** `Ledger.Repository/Migrations/20260930181840_AddLedgerIngestion.cs`. Constants of raw SQL (`private const string ... = """ ... """;`) executed via `migrationBuilder.Sql`. Grants are explicit per table (line 12 shows a column-level `GRANT UPDATE (...) ON TABLE transactions TO ledger_runtime;`). Views live in `reporting` and use `public.` qualified tables:

```sql
CREATE VIEW reporting.transactions AS
SELECT t.id AS transaction_id, a.account_key, ...
FROM public.transactions t
JOIN public.accounts a ON a.id = t.account_id
WHERE t.status <> 'dropped'
```

Rules for this migration:
- Runtime privileges per table as listed in the research data model (append-only audit tables: SELECT, INSERT only).
- New `reporting.*` views inherit SELECT for `grafana_reader` via default privileges set in `20260927183611_InitialCreate.cs:47`; runtime gets none.
- No SQL functions, triggers or extensions in `public` or `reporting` (DatabaseRoleTests fails on any `pg_proc` row there). Recursive CTE views only.
- Date fallback in new views must be the query store order `COALESCE(booking_date, value_date, transaction_date, first-seen day)`, not the existing view's `booking, transaction, value` order.
- Seed the starter tree with `INSERT ... SELECT` resolving parents by key; names generic, no merchants. Backfill the built-in synced-IBAN transfer assignment for existing rows in the same migration.
- Migration files are exempt from the `//` lint, but do not add planning references.

### Stores (`Ledger.Repository/Stores/`)

**Analog:** `Ledger.Repository/Stores/BankAuthorizationStore.cs` lines 8-52. Primary constructor over `LedgerDbContext`, `Guid.CreateVersion7()` ids, `/// <inheritdoc />`, one atomic consume via `ExecuteUpdateAsync` with the `consumed_at IS NULL AND expires_at > now` predicate:

```csharp
var changed = await dbContext.BankAuthorizations
    .Where(authorization => authorization.StateSha256 == stateSha256
        && authorization.ConsumedAt == null
        && authorization.ExpiresAt > now)
    .ExecuteUpdateAsync(
        setters => setters.SetProperty(authorization => authorization.ConsumedAt, (DateTimeOffset?)now),
        cancellationToken);

if (changed != 1) { return null; }
```

Use for `PendingChangeStore.TryConsumeAsync` (add principal and client to the predicate). Raw SQL, where needed (`UPDATE ... RETURNING`, `INSERT ... ON CONFLICT (transaction_id) DO NOTHING`), uses `dbContext.Database.SqlQuery<T>($"...")` with interpolation so values become parameters; array parameters are cast `{x}::text[]`. Never `FromSqlRaw` with concatenation.

Register each store in `Ledger.Repository/RepositoryServiceCollectionExtensions.cs` the way the existing stores are.

### `LedgerQueryStore.cs` (modify: transfers by category kind, category filter and grouping)

**Analog:** itself, `ReadGroupedRowsAsync` lines 296-352. Replace the `own_accounts` CTE and `is_transfer` expression (lines 303-307 and 319-322; same IBAN test again in `ReadSearchRowsAsync` around 413-416 and the counterparty read around 489-491) with joins:

```sql
LEFT JOIN transaction_categorisations tc ON tc.transaction_id = t.id
LEFT JOIN categories c ON c.id = tc.category_id
...
(t.status = 'booked' AND c.kind = 'transfer') AS is_transfer
```

Keep: the `CASE WHEN status = 'pending' THEN 'pending' WHEN is_transfer THEN 'transfer' ELSE 'booked' END` precedence (line 340), the `period_date` fallback (lines 315-318), `{zoneId}::text`, the filter predicate style and interpolated parameters. Add category filter by closure (`descendant_id`), `uncategorised` filter and `groupBy = category`; include the new filters in `SearchCursor.FilterHash`.

### Domain pure logic (`Ledger.Domain/Categorisation/`)

**Analogs:** `Ledger.Domain/Ingestion/TextNormalizer.cs` (static class, one `ForMatching` method, `///` with `<summary>`), `Ledger.Domain/Ingestion/TransactionReconciler.cs` (`Plan(...)` pure function returning a plan record), `Ledger.Domain/Queries/IbanText.cs` (`Normalize`, `MaskInText`), `Ledger.Domain/Queries/TotalsAggregator.cs`, `PeriodResolver.cs`, `PeriodDate.cs`.

```csharp
public static string? ForMatching(string? text)
{
    if (text is null) { return null; }
    var composed = text.Normalize(NormalizationForm.FormC);
    ...
    return builder.ToString().ToUpperInvariant();
}
```

- `RuleMatcher.Decide(...)` returns an `Assign`/`Queue` record; normalise names and description words with `TextNormalizer.ForMatching`, IBAN with `IbanText.Normalize`. No regex.
- `UntrustedText.Clean` strips Control/Format/PrivateUse characters, then `IbanText.MaskInText`, then caps.
- `ComparisonCalculator` and month-to-date windows build on `PeriodResolver`/`PeriodDate`/`QueryTimeZone` (`Ledger.Service/Queries/QueryTimeZone.cs`), never `DateTime.Now`; inject `TimeProvider`.
- Store interfaces go in `Ledger.Domain/Categorisation/` like `IBankAuthorizationStore.cs`. No EF or HTTP references.

### Audited write services (`Ledger.Service/Categorisation/`)

**Analog:** `Ledger.Service/Queries/LedgerQueryService.cs` (orchestration over domain interfaces, throws `LedgerQueryException` for caller-visible problems, `TimeProvider` injected) and `Ledger.Service/Ingestion/BankLinkService.cs` (one-time state creation and consumption). Thin tools call these; the service builds the plan, the store applies change plus audit rows in one transaction. Exceptions and logs carry type names only, never bank text (`SyncMetricsRefresher` line 78 shows the logging idiom).

### Engine hook after ingestion

**Analog:** `Ledger.Service/Ingestion/SyncOrchestrator.cs` lines 141-153 (`ledgerStore.ApplyAsync(...)` then progress counters, then `SnapshotBalancesAsync`). Call the engine right after `ApplyAsync`, wrapped in try/catch that logs `exception.GetType().Name`, increments `ledger_categorisation_errors_total` and never fails the sync. Candidate rows: unassigned, non-dropped, `updated_at >= run start`.

### Metrics

**Analog:** `Ledger.Service/Mcp/McpMetrics.cs` lines 11-86. Static class; counters made with `Prometheus.Metrics.CreateCounter(name, help, labelNames)`; `InitialiseCounters()` creates every label series at zero; fixed-word labels only:

```csharp
private static readonly Counter ToolCalls = Prometheus.Metrics.CreateCounter(
    "ledger_mcp_tool_calls_total", "MCP tool calls received, by tool.", "tool");
public static void ToolCalled(string tool) { ToolCalls.WithLabels(tool).Inc(); }
```

Note `ToolNames()` (line 88) reflects only over `typeof(LedgerTools)`; extend it to every tool type so the new tools get zero-initialised series. Add gauges (`ledger_review_queue_size`, `ledger_review_proposals_waiting`) and counters from the research metrics table; unknown reasons collapse to a fixed label as in `TokenRejected` (line 71).

### Review metrics refresher (hosted service)

**Analog:** `Ledger.Service/Ingestion/SyncMetricsRefresher.cs` lines 11-81. Copy whole structure: `BackgroundService`, `IServiceScopeFactory`, `TimeProvider`, `RefreshInterval = 60s`, `SemaphoreSlim` guarded `RefreshOnceAsync`, startup refresh then `PeriodicTimer(RefreshInterval, timeProvider)`, `TryRefreshAsync` swallowing non-cancel exceptions with a type-only warning. Register like the existing refresher.

### MCP tools (read and write adapters)

**Analog:** `Ledger.Service/Mcp/LedgerTools.cs` lines 9-81. `[McpServerToolType]` class with primary-constructor service dependency, one `[McpServerTool(Name, Title, ReadOnly, Destructive, Idempotent, OpenWorld)]` plus `[Description]` per method, per-parameter `[Description]`, `McpMetrics.ToolCalled("<name>")` first line, `try`/`catch (LedgerQueryException)` rethrown as `McpException(message)`.

```csharp
[McpServerTool(Name = "money_totals", Title = "Money totals", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
...
McpMetrics.ToolCalled("money_totals");
try { return await queries.TotalsAsync(new TotalsRequest(...), cancellationToken); }
catch (LedgerQueryException exception) { throw new McpException(exception.Message); }
```

New write tools: `ReadOnly = false`, honest `Destructive`/`Idempotent`; `apply_changes` additionally `[McpMeta("anthropic/requiresUserInteraction", true)]`. Tool classes take only domain/service types; no store or `LedgerDbContext` parameters. Put the untrusted-data sentence first in each description and keep descriptions and instructions at or under 2,000 characters. Remove the "Categories do not exist yet" sentences (lines 40-43 area and `find_counterparties` text) and the matching text in `ServerInstructions.cs`.

### Operator REST endpoints

**Analog:** `Ledger.Service/Endpoints/StatusEndpoints.cs` lines 6-20 (static `MapXEndpoints(this IEndpointRouteBuilder)` extension, `/api/v1/...`, minimal API lambdas, client name from `context.User.Identity?.Name`, private sealed response records) and `BankEndpoints.cs` lines 24-31 (`MapGroup`, `MapGet`/`MapPost`, auth required by the fallback policy unless `.AllowAnonymous()`). Add `GET /api/v1/changes` and `POST /api/v1/changes/{id}/undo`, registered next to `MapStatusEndpoints` in `Program.cs`. The audit `client_id` is the API key name here and the OAuth client id for MCP (read from the principal; assert the presenter claim in an integration test).

### Spending dashboard and new panel types

**Analogs:** `Ledger.Dashboards/Definitions/SyncDashboard.cs` lines 1-60 (static class, SQL as `private const string` strings with `${account:sqlstring}` and `$__timeFilter(...)`, `Reporting = new("grafana-postgresql-datasource", "ledger-reporting")`, `Build(language, translator)` using local `T(key)`, uid per language, `Links` to the other language, `QueryVariable`, `SchemaVersion: 41`) and `Ledger.Dashboards/Model/Panels.cs` lines 8-44.

```csharp
[JsonDerivedType(typeof(TablePanel))]
public abstract record Panel(
    [property: JsonPropertyOrder(-5)] int Id, [property: JsonPropertyOrder(-4)] string Type, ...);

public sealed record TablePanel(int Id, string Title, string? Description, GridPos GridPos,
    DatasourceRef Datasource, IReadOnlyList<SqlTarget> Targets, FieldConfig FieldConfig,
    IReadOnlyList<Transformation> Transformations) : Panel(Id, "table", Title, Description, GridPos);
```

Add `StatPanel`, `TimeSeriesPanel`, `BarChartPanel` as sibling records with their own `[JsonDerivedType]` entries; widen `FieldDefaults` (now only `Decimals`) with unit, color, links, mappings, thresholds. Make `DashboardGenerator.Generate()` iterate both dashboards. Panel SQL must not differ by language (parity test compares `targets` and `fieldConfig.defaults`): use a hidden constant variable `lang` and `CASE WHEN '${lang}' = 'nl' THEN name_nl ELSE name_en END`; update the parity test to skip `constant` variables. Titles and headers go in `translations.json`.

### Alert rules

**Analog:** `deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml` lines 12-52 (rule with `datasourceUid: prometheus`, instant `expr`, threshold node `C`, `noDataState: OK`, `execErrState: Alerting`, fixed `annotations.summary`, `labels: {}`, nothing interpolated). Add `increase(ledger_mcp_writes_refused_total[15m]) > 0` and `increase(ledger_suspicious_bank_text_flagged_total[1h]) > 0`.

### Tests

**Integration analog:** `Ledger.IntegrationTests/Mcp/ToolSurfaceTests.cs` lines 14-40: `[Collection("Database")]`, `DatabaseFixture`, `LedgerQuerySeed.CreateIsolatedDatabaseAsync`, `McpTestHost.StartAsync(connectionString, configureServices: ... FakeTimeProvider)`, `connection.Client.CallToolAsync`. Extend `LedgerQuerySeed` so the seed runs the built-in transfer pass (otherwise totals tests silently stop excluding transfers). Update the tool-count and "grouped by counterparty" wording assertions.

**Privilege matrix:** `Ledger.IntegrationTests/Database/DatabaseRoleTests.cs` lines 213-219 and 266-271 list every relation as `"public.provider_calls=SELECT,INSERT"` and `"reporting.accounts="`; add every new table and view deliberately.

**Unit analog:** `Ledger.UnitTests/Dashboards/DashboardGeneratorTests.cs` line 15 (`ExpectedFiles` hard-coded) and line 126 (property parity loop); `Ledger.UnitTests/Auth/TotpCodesTests.cs` for pure-logic style. xUnit v3, FluentAssertions, NSubstitute, `FakeTimeProvider`. Test names must not carry planning references. Keep pure logic (matching, specificity, undo planning, cleaning, detection, comparison) in unit tests.

## Shared Patterns

### One-time, hashed, expiring tokens
**Source:** `Ledger.Repository/Stores/BankAuthorizationStore.cs` lines 28-51 and `Entities/BankAuthorizationEntity.cs`
**Apply to:** `pending_changes` (confirmation token and change-set redemption), generated with `RandomNumberGenerator.GetBytes(32)`, only SHA-256 stored.

### Opaque metrics labels
**Source:** `Ledger.Service/Mcp/McpMetrics.cs`
**Apply to:** every new counter and gauge; labels are fixed tool names or reason words, never IBAN, amount, counterparty, text or client id.

### Background refresher
**Source:** `Ledger.Service/Ingestion/SyncMetricsRefresher.cs`
**Apply to:** review queue and proposals gauges.

### Errors out of tools
**Source:** `LedgerTools.cs` lines 71-80: domain exception to `McpException(message)`, message free of bank text and secrets.
**Apply to:** all new tools and write-guard refusals.

### Raw SQL
**Source:** `LedgerQueryStore.cs` line 302 `dbContext.Database.SqlQuery<GroupedRow>($"""...""")` with interpolated parameters.
**Apply to:** all new store SQL.

### Principal and client inside a tool
**Source:** no repo analog; research spike: `context.JsonRpcRequest.Context?.User` and `...ClientCapabilities?.Elicitation`. Scope check today at `Ledger.Service/Mcp/McpEndpoint.cs:74` (`context.User.HasScope(ClientRegistrations.ReadScope)`), scope constant at `Ledger.Service/OAuth/ClientRegistrations.cs:16`, scope list at `Pages/Connect/Authorize.cshtml.cs:133`. Add a write scope the same way.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `Ledger.Service/Categorisation/ConfirmationService.cs` and the `apply_changes` tool | service | request-response | No MRTR or elicitation code exists. Use the spike-verified example in the research (`InputRequiredException`, `InputRequest.ForElicitation`, `ITimeLimitedDataProtector` for `requestState`); never `server.ElicitAsync` (throws in stateless mode). Data Protection access pattern: `Ledger.Service/Security/DataProtectionCanary*`, `DataProtectionSecretProtector.cs` |
| Audited write pipeline (`AuditContext`, `IUndoHandler`, `UndoPlanner` application, REPEATABLE READ plus `FOR UPDATE`) | service/store | CRUD | First write path in the repo; follow the research Pattern 1 and 6. Append-only precedent is only `provider_calls` |
| `WriteGuard` (sliding window per principal) | utility | request-response | No rate limiter exists; use `System.Threading.RateLimiting.SlidingWindowRateLimiter` from the shared framework |
| Architecture test: no store or `LedgerDbContext` types in `Ledger.Service.Mcp` constructors or methods | test | n/a | No architecture tests exist; write with plain reflection in `Ledger.UnitTests` |

## Metadata

**Analog search scope:** all tracked `.cs` files in `Ledger.Domain`, `Ledger.Repository`, `Ledger.Service`, `Ledger.Dashboards`, `Ledger.UnitTests`, `Ledger.IntegrationTests`; `deploy/provisioning/grafana`.
**Files scanned:** about 180 listed, 12 read in full or in part.
**Pattern extraction date:** 2026-10-13
