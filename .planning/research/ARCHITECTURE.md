# Architecture Research

**Domain:** Self-hosted household personal-finance backend (.NET 10) — open-banking ingestion, categorisation, budgets/goals/recurring/forecast, MCP advisor server, REST API, Grafana/Prometheus
**Researched:** 2026-09-26
**Confidence:** MEDIUM overall (HIGH for .NET/EF/ASP.NET Core mechanics backed by Microsoft Learn and official SDK docs; LOW for provider-specific consent-lifecycle details and for "views-as-Grafana-contract" being an official recommendation rather than community convention — flagged inline)

## Standard Architecture

### System Overview

```
┌───────────────────────────────────────────────────────────────────────────┐
│                     ASP.NET Core Host (single process)                     │
│                                                                             │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────────────────────┐  │
│  │ REST API      │  │ MCP endpoint  │  │ BackgroundService(s)            │  │
│  │ (Controllers) │  │ (MapMcp)      │  │ - Sync worker (aggregator poll) │  │
│  │               │  │ [McpServerTool]│  │ - Consent-expiry checker       │  │
│  │               │  │ classes        │  │ - Recurring/forecast recompute │  │
│  │               │  │               │  │ - Scheduled review generator     │  │
│  └──────┬───────┘  └──────┬───────┘  └──────────────┬──────────────────┘  │
│         │                  │                          │                    │
│         └──────────┬───────┴──────────────────────────┘                    │
│                     ▼                                                      │
│         ┌───────────────────────────────────────────────┐                  │
│         │        Application / Domain layer               │                  │
│         │  Accounts · Transactions · Categorisation        │                  │
│         │  Budgets/Goals/Recurring/Forecast · Advisor       │                  │
│         │  memory · Audit/Undo · Ingestion abstraction      │                  │
│         └───────────────────┬───────────────────────────┘                  │
│                              ▼                                             │
│         ┌───────────────────────────────────────────────┐                  │
│         │        Repository layer (EF Core, DbContext)     │                  │
│         └───────────────────┬───────────────────────────┘                  │
└─────────────────────────────┼─────────────────────────────────────────────┘
                              ▼
          ┌───────────────────────────────────────────────┐
          │  SQL Server — dedicated database                 │
          │  dbo (application tables + audit + temporal)      │
          │  reporting (SELECT-only views, Grafana contract)   │
          └───────────────────────────────────────────────┘

External:
  Aggregator API (PSD2)  ← polled by Sync worker, via IBankProvider abstraction
  Anthropic (claude.ai / Claude Desktop / Code / scheduled)  ← calls /mcp over OAuth 2.1
  Prometheus  ← scrapes /metrics (operational only, never financial data)
  Grafana  ← queries `reporting` schema via SELECT-only login
```

### Component Responsibilities

| Component | Responsibility | Typical Implementation |
|-----------|----------------|------------------------|
| REST controllers | Thin: bind request → call application service → map result. No business logic. | ASP.NET Core MVC controllers, matching the user's existing pattern of thin `Service`-project controllers over a `Domain` layer |
| MCP tool classes | Thin: same role as controllers but for the MCP transport — bind MCP tool arguments → call the *same* application services → shape MCP-appropriate output (aggregated, LLM-readable) | `[McpServerToolType]` classes with `[McpServerTool]` methods, constructor-injected domain services, registered via `WithToolsFromAssembly()` |
| Application/domain services | All business logic: categorisation rules engine, budget/goal calculations, recurring detection, forecast, audit/undo, advisor memory | Plain C# services in a `Domain`-equivalent project, no EF/HTTP dependencies |
| Ingestion abstraction | Defines `IBankProvider`/`ITransactionSource`; today backed by one aggregator adapter, later swappable for CSV/CAMT | Interface + adapter in Domain, concrete client in Repository/Infrastructure |
| Sync worker | Polls the aggregator on a schedule, maps raw payloads to the normalised model, upserts idempotently, tracks consent state | `BackgroundService` + `PeriodicTimer`, single instance (no distributed lock needed — see Anti-Patterns) |
| Repository layer | EF Core entities, `DbContext`, migrations, temporal-table configuration | Mirrors the user's existing `Repository` project pattern |
| Audit/undo | Records every MCP/REST-initiated write with actor, before/after, and a revert path | Application-level audit log table + SQL Server temporal tables for row history (see must-cover #4) |
| OAuth 2.1 resource server | Validates bearer tokens on `/mcp`, publishes protected-resource metadata | ASP.NET Core JWT bearer middleware + a metadata endpoint; see MCP surface section |
| Authorization server | Issues tokens for MCP clients | Kept as a logically separate component even if co-hosted (see must-cover #6) |
| Reporting schema | Stable, read-only contract for Grafana, decoupled from application table shape | SQL views in a `reporting` schema, SELECT-only login |
| Grafana + Prometheus | Dashboards and operational metrics/alerts | Same LXC, provisioned as code |

## Recommended Project Structure

Aligned with the user's existing homelab app's three-layer pattern (`Domain` → `Repository`, with a `Service` host on top and strict one-way dependencies), extended for this project's dual REST+MCP surface and background workers:

```
HouseholdLedger.Domain/            # business logic, no EF/HTTP dependencies
├── Accounts/                      # Account, AccountType
├── Transactions/                  # Transaction, Counterparty, InternalTransferMatcher
├── Categorisation/                # Category tree, Rule, RuleEngine, ReviewQueue
├── Planning/                      # Budget, SavingsGoal, RecurringSeries, Forecast
├── Advisor/                       # HouseholdProfile, AdviceLogEntry, ReviewRecord
├── Audit/                         # AuditEntry, IAuditableChange, undo/revert logic
├── Ingestion/                     # IBankProvider, ConsentState machine, provider-agnostic DTOs
├── Interfaces/                    # repository interfaces, IBoardClock-style IClock seam
└── Services/                      # orchestration services consumed by both REST and MCP

HouseholdLedger.Repository/         # EF Core entities, DbContext, migrations
├── Entities/
├── Migrations/                    # includes temporal-table config, reporting view migrations
├── Automapper/                    # Entity <-> Domain model profiles
├── Ingestion/                     # concrete aggregator HTTP client(s), CSV/CAMT parser (later)
└── HouseholdLedgerContext.cs

HouseholdLedger.Service/            # single ASP.NET Core host — REST + MCP + workers
├── Controllers/                   # REST endpoints (thin)
├── Mcp/
│   ├── Tools/                     # [McpServerToolType] classes: ReadTools, WriteTools
│   ├── Resources/                 # [McpServerResourceType] if any are exposed as resources
│   └── Auth/                      # resource-server metadata endpoint, token validation config
├── BackgroundServices/
│   ├── SyncWorker.cs
│   ├── ConsentExpiryChecker.cs
│   ├── ForecastRecalculator.cs
│   └── ScheduledReviewGenerator.cs
├── Automapper/                    # Domain <-> ViewModel / MCP DTO profiles
├── Metrics/                       # Prometheus exporter wiring
├── Program.cs
└── appsettings.json

HouseholdLedger.UnitTests/
HouseholdLedger.IntegrationTests/

grafana/                            # provisioning as code
├── provisioning/datasources/
├── provisioning/dashboards/
└── dashboards/{en,nl}/             # generated from one source, see must-cover #7

prometheus/
└── prometheus.yml
```

### Structure Rationale

- **One `Service` host, two transports:** REST controllers and MCP tool classes are peers that both depend only on `Domain` services — never on each other, and never containing business logic themselves. This directly satisfies must-cover #1: a categorisation correction made via REST and one made via MCP run through the identical `CategorisationService`, so audit logging, validation and rule-application are not duplicated or divergent.
- **`Ingestion/` inside `Domain`:** the provider interface (`IBankProvider`) and the consent state machine are domain concepts — swapping the aggregator or adding CSV/CAMT import must not touch REST, MCP, or the sync scheduling logic. Only the concrete HTTP client moves to `Repository`/infrastructure.
- **`Mcp/Tools` split into `ReadTools` / `WriteTools`:** keeps the read/write boundary (must-cover #6) visible at the file level, not just as a code convention — makes it trivial to apply a stricter authorization policy or rate limit to the write-tool class.
- **`reporting/` views live in `Repository/Migrations`:** Grafana's contract is versioned in the same EF Core migration pipeline as the application schema, so a migration that renames or restructures a table is forced to also update the view that depends on it in the same commit (must-cover #7).

## Architectural Patterns

### Pattern 1: Shared application layer under two transports (REST + MCP)

**What:** REST controllers and MCP tool classes are both thin adapters over the same `Domain` service interfaces, resolved from the same DI container per-request/per-call.
**When to use:** Any time two transports (HTTP API, MCP, CLI, etc.) need to expose the same business capability without duplicating logic or letting one bypass validation the other enforces.
**Trade-offs:** Pro — one source of truth for business rules and audit logging; a Claude-initiated write and a REST-initiated write are indistinguishable at the domain layer except for the recorded actor. Con — MCP tool responses often need heavier aggregation/summarisation than a REST JSON response (an LLM benefits from pre-aggregated, narrated data rather than raw rows), so some MCP tools legitimately carry a thin presentation step on top of the shared service — keep that step in the `Mcp/Tools` class, not leaking back into `Domain`.

**Example:**
```csharp
[McpServerToolType]
public sealed class TransactionReadTools(ITransactionQueryService transactions)
{
    [McpServerTool, Description("Search transactions by date range, category, or merchant.")]
    public Task<TransactionSearchResult> SearchTransactions(TransactionSearchArgs args, CancellationToken ct)
        => transactions.SearchAsync(args.ToQuery(), ct);
}

[ApiController, Route("api/transactions")]
public sealed class TransactionsController(ITransactionQueryService transactions) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Search([FromQuery] TransactionSearchRequest request, CancellationToken ct)
        => transactions.SearchAsync(request.ToQuery(), ct).ToActionResultAsync();
}
```
Both call `ITransactionQueryService` — the only place the query is actually built.

### Pattern 2: Ingestion behind a provider interface with a consent state machine

**What:** `IBankProvider` (or `ITransactionSource`) exposes `FetchTransactionsAsync(account, since)` and `GetConsentStatusAsync()`; a `ConsentState` enum (`Linked → Expiring → Expired → Renewed`) is owned by the domain, transitions are driven by the sync worker and by an explicit consent-renewal flow, and `Expiring`/`Expired` states raise an operational metric and (per requirements) an alert.
**When to use:** Any external data source with a renewable, time-bound authorization — swapping aggregators or adding a manual CSV importer should only mean writing a new adapter, never touching the state machine or the sync scheduling.
**Trade-offs:** Pro — clean swap path (explicitly required: "sits behind an interface so another provider can be added later without touching the rest of the app"). Con — the state machine must be generic enough to fit an aggregator whose consent semantics aren't fully known yet (verify per-provider details in STACK research — this is flagged LOW confidence here).

```csharp
public interface IBankProvider
{
    Task<ConsentStatus> GetConsentStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<RawTransaction>> FetchTransactionsAsync(AccountRef account, DateOnly since, CancellationToken ct);
}

public enum ConsentState { Linked, Expiring, Expired, Renewed }
```

**Idempotent upsert and the pending→booked problem:** aggregators typically expose a provider transaction ID that is *stable for booked transactions* but pending transactions are sometimes reissued with a new ID once booked. The safe upsert key is therefore a composite: prefer the provider's stable booked-transaction ID when present, but match pending→booked transitions by a secondary key (account + amount + counterparty + date-within-tolerance) and update-in-place rather than insert-a-duplicate. Store the raw provider payload (as JSON) alongside the normalised row so a mismatch can be diagnosed and replayed without re-fetching history — this also gives the categorisation pipeline a fallback source of remittance-info text the normalised model might have dropped.

### Pattern 3: Rules → heuristics → review queue → Claude-assisted resolution

**What:** Every new transaction is run through the deterministic rule engine first (counterparty/IBAN/description pattern match against DB-stored rules); anything unmatched (or matched below a confidence threshold) is heuristically scored, then anything still unresolved lands in a `NeedsReview` queue. Claude, via MCP write tools, proposes a category for queued items; a user confirmation (or a direct correction) can optionally be promoted into a new rule, which is then re-applied to historical unmatched/low-confidence transactions.
**When to use:** Any domain where deterministic rules should do the bulk of the work cheaply, and an LLM or human only needs to handle the residual ambiguous cases — keeps Claude's involvement (and cost) proportional to genuine ambiguity, not every transaction.
**Trade-offs:** Pro — auditable, cheap, deterministic categorisation for the 80% case; Claude effort concentrates on the interesting 20%. Con — "turn a correction into a rule" must be careful not to over-generalise from one Tikkie/iDEAL transaction (e.g. rule on exact counterparty string, not on amount) — this needs an explicit rule-scope decision surfaced to the user/Claude, not silently auto-generalised.

### Pattern 4: Audit log with before/after JSON + temporal tables as a supporting layer, not a replacement

**What:** Every MCP/REST write to a mutable, advisor-relevant entity (categorisation, rule, budget, goal, annotation, memory) is recorded in an application-level `AuditEntry` table: `EntityType`, `EntityId`, `Actor` (MCP client ID / REST user), `Action`, `BeforeJson`, `AfterJson`, `Timestamp`, `Reason` (free text, e.g. Claude's stated rationale). SQL Server system-versioned temporal tables are additionally enabled on the core mutable tables (`Transaction.Category`, `Rule`, `Budget`, `SavingsGoal`) to give free, gap-free row-level history with zero extra application code, as a defense-in-depth layer under the explicit audit log.
**When to use:** Whenever "who changed what, why, and can it be undone" is a first-class requirement (it is here — must-cover #4).
**Trade-offs:** Pro — temporal tables are close to free (EF Core migration support exists, SQL Server maintains history automatically) and give a safety net even for changes that somehow bypass the application audit log (e.g. a manual SQL fix). Con — **temporal tables alone cannot answer "who" or "why"** because SQL Server has no user/actor context — confirmed across multiple sources; they are not a substitute for the application audit log, only a complement. Confidence: MEDIUM (mechanism is well-documented via EF Core's official temporal-tables provider page; the "combine both" recommendation is a repeated community pattern, not an MS-prescribed one).

**Revert semantics:** undo reads `AuditEntry.BeforeJson`, deserialises into the entity's shape, and calls the *same* domain service method used for the original change (e.g. `RecategorizeTransaction`) rather than writing raw SQL — this guarantees the revert itself is captured by the audit log and by rule-reapplication side effects, avoiding an "undo that doesn't trigger the same invariants as the original action" class of bug.

```csharp
public sealed record AuditEntry(
    Guid Id, string EntityType, string EntityId, string Actor,
    string Action, string? BeforeJson, string? AfterJson,
    string? Reason, DateTimeOffset OccurredAtUtc);
```

## Data Flow

### Ingestion → categorisation → advisor flow

```
Aggregator API
    │  (poll, scheduled)
    ▼
SyncWorker (BackgroundService)
    │  map raw → RawTransaction, store raw JSON
    ▼
Idempotent upsert (composite key: provider ID + pending/booked reconciliation)
    │
    ▼
Categorisation pipeline: Rules → Heuristics → NeedsReview queue
    │                                              │
    │ (auto-categorised)                           │ (ambiguous)
    ▼                                              ▼
Transaction.Category = X, Source=Rule       MCP ReadTools: Claude surveys queue
    │                                              │
    │                                     MCP WriteTools: Claude proposes category
    │                                              │
    │                                     User confirms (via Claude conversation)
    │                                              │
    └──────────────────────┬───────────────────────┘
                            ▼
                   AuditEntry recorded (Actor = Claude client ID / user)
                            │
                            ▼
              Optionally: correction promoted to new Rule
                            │
                            ▼
              Rule re-applied to historical NeedsReview backlog
                            │
                            ▼
        Budgets/Goals/Recurring/Forecast recompute (triggered or scheduled)
                            │
                            ▼
              reporting.* views (Grafana) ← always reflect committed state
```

### MCP request flow (advisor conversation)

```
Claude (claude.ai / Desktop / Code / scheduled)
    │  HTTPS, bearer token
    ▼
Traefik (public path: /mcp only)
    │
    ▼
ASP.NET Core host: OAuth 2.1 resource-server middleware
    │  validates token, checks scope
    ▼
MCP endpoint (MapMcp) → ReadTools / WriteTools
    │
    ▼
Domain services (same ones REST uses)
    │
    ▼
Repository (EF Core) → SQL Server (dbo schema)
    │
    ▼ (writes only)
AuditEntry + temporal history recorded
```

### Key Data Flows

1. **Sync → categorise → review:** the aggregator is the only external write source into the ledger; everything downstream (categorisation, budgets, forecasts, dashboards) is derived, so a resync is always safe to re-run and never the sole source of a category assignment.
2. **Advisor writes are always audited before they take effect visibly:** every MCP write commits the domain change and the audit entry in the same transaction, so there's never a window where a change is visible but unaudited.
3. **Grafana never talks to the domain layer:** it reads only the `reporting` schema, so a change to internal table shape can't silently break a dashboard — the view is the versioned contract (must-cover #7).
4. **Prometheus never touches financial data:** `/metrics` carries operational counters only (sync health, time since last successful sync, days-to-consent-expiry, error counts) — confirmed correct by requirements already (Prometheus explicitly out of scope for financial data due to scrape-time timestamps and immutable samples breaking recategorisation).

## MCP Surface Design (must-cover #6)

**Read tools vs write tools:** split into two tool classes (or two `[McpServerToolType]` groups) so the write surface is a distinct, smaller, more heavily audited/authorized set:
- Read tools: search/query transactions, category/period/merchant aggregates, budget-vs-actual, goal progress, recurring-cost list, forecast. These should do **server-side aggregation** (sums, group-bys, trend deltas) rather than handing Claude raw transaction rows to aggregate itself — cheaper in tokens, more consistent, and keeps aggregation logic in one tested place (the same `Domain` service REST uses for dashboard-backing endpoints).
- Write tools: recategorise transaction(s), create/edit rule, create/adjust budget or goal, annotate transaction, update advisor memory, store a review record. Every write tool call resolves to a single domain service call that itself writes the audit entry — the tool method should never write directly to the DbContext.

**Resources vs tools:** MCP resources are best suited to relatively static, addressable context (e.g. "the current household profile", "the current category tree") that Claude might want to read without a structured query; tools are better for anything parameterised (a date-range search, a category change). Given this project's needs are almost entirely parameterised (search, filter, mutate), the practical recommendation is to lean on tools for nearly everything and reserve resources for the advisor-memory/household-profile document, which is naturally a single addressable blob Claude re-reads at the start of every session.

**OAuth 2.1 resource server placement (verified against the MCP spec's own authorization docs and multiple implementation write-ups — MEDIUM confidence):** the MCP server's job is to validate bearer tokens and publish protected-resource metadata (RFC 9728) — it is explicitly *not* supposed to also be the token issuer. The specification allows co-locating the authorization server with the resource server, but current best practice treats them as separate logical components even in a single-tenant, self-hosted deployment, because:
- It keeps the MCP endpoint stateless from an auth perspective — it only validates, never issues.
- It avoids re-implementing OAuth 2.1's more finicky requirements (PKCE, dynamic client registration for claude.ai's connector flow, token rotation) inside application code that also has to serve REST and business logic.
- For a single-household app, this most likely means either (a) a small, dedicated ASP.NET Core Identity + OpenIddict (or Duende IdentityServer Community Edition) authorization-server component within the same host but as a logically separate module/route group, issuing tokens scoped only to MCP, or (b) evaluate whether a managed/hosted OAuth-for-MCP service (e.g. the kind surfaced in the research — Scalekit-style hosted AS) is worth the external dependency for a single-user household app. Given the project's "one deployable unit" constraint and small user base, **the pragmatic default is (a): an embedded but logically separate auth module** — same process, same host, but its own route prefix, its own token-signing key, and no shared code path with the REST/MCP domain services. This should be revisited in phase-specific research once the exact claude.ai connector OAuth requirements (dynamic client registration, redirect URIs) are confirmed against current Anthropic docs, since those are moving quickly.

**Public vs private endpoints:** only `/mcp` and the OAuth metadata/authorize/token endpoints that support it are internet-facing (matches the already-decided constraint). Everything else — REST API, web page, Grafana, Prometheus — stays on the home network + VPN only. This means the ASP.NET Core host serves both public and private routes from the same process but Traefik's routing rule (see LXC Topology) is the actual enforcement boundary, not application-level logic alone — belt-and-suspenders, the app should still reject non-MCP routes if somehow reached from a public IP (e.g. via a defense-in-depth middleware check), since Traefik misconfiguration is a realistic failure mode.

## Grafana Data Contract (must-cover #7)

- A dedicated `reporting` schema holds views only — no tables. Each view is a stable, documented contract (e.g. `reporting.vw_monthly_category_spend`, `reporting.vw_budget_vs_actual`, `reporting.vw_savings_goal_progress`, `reporting.vw_recurring_costs`).
- A SELECT-only SQL login (`grafana_reader`) is granted `SELECT` on the `reporting` schema only — no access to `dbo` — directly satisfying Grafana's own documented recommendation to use a restricted, dedicated login (HIGH confidence, sourced from Grafana's official MSSQL data source docs) and this project's constraint of least-privilege SQL logins.
- **Views stay in sync with EF migrations by living in the same migration pipeline:** each EF Core migration that changes a table depended on by a view also contains a raw-SQL step (`migrationBuilder.Sql(...)`) that drops and recreates the affected view(s) in the same migration file. This is the practical way to guarantee the contract can't silently drift — a migration that breaks a view fails to apply, not fails silently at dashboard-render time. (This specific migration-plus-view co-location pattern was not found as an official Grafana or EF Core recommendation in research — it is a reasonable synthesis from the "views as contract" principle and EF Core's raw-SQL migration capability; flag as LOW confidence / a judgement call, revisit if phase research surfaces a better pattern.)
- **EN/NL dashboards from one source:** keep exactly one canonical Grafana dashboard JSON per dashboard (English, since the constraint states application code/UI is English), and generate the Dutch variant via a build step that walks the JSON and swaps panel titles/legend labels using a translation dictionary file (e.g. `grafana/i18n/nl.json`), rather than hand-maintaining two JSON files that will drift. This keeps "one source of truth" per must-cover #7 while satisfying the EN/NL dashboard requirement.
- Provisioning layout:
  ```
  grafana/
  ├── provisioning/datasources/mssql.yml      # points at grafana_reader login only
  ├── provisioning/dashboards/dashboards.yml   # folder provider config
  ├── dashboards/en/*.json                     # canonical source
  ├── dashboards/nl/*.json                     # generated, committed for provisioning simplicity
  └── i18n/nl.json                             # translation dictionary driving generation
  ```

## Security Architecture (must-cover #8)

### Trust boundary diagram

```
                                   Internet
                                      │
                                      │ 80/443 only
                                      ▼
                          ┌───────────────────────┐
                          │  Traefik (existing)     │  Let's Encrypt TLS
                          │  file-provider route:    │  optional IP allowlist middleware
                          │  Host(ledger.<domain>)    │  for /mcp/* if Anthropic publishes
                          │  PathPrefix(/mcp)         │  egress ranges
                          └───────────┬───────────┘
                                      │ :5000 internal only
                                      ▼
        ┌─────────────────────────────────────────────────────────────┐
        │                     App LXC                                    │
        │  ┌───────────────────────────────────────────────────────┐  │
        │  │ ASP.NET Core host (systemd, user: ledger)                │  │
        │  │  - /mcp route  ← reachable via Traefik (public)          │  │
        │  │  - REST/web/Grafana routes ← LAN/VPN only, not routed    │  │
        │  │    by Traefik's public entrypoint                        │  │
        │  │  - reads /etc/ledger/env (root-owned, group-read)        │  │
        │  └───────────────────────────────────────────────────────┘  │
        │  ┌───────────────┐  ┌───────────────┐                        │
        │  │ Grafana        │  │ Prometheus     │  LAN/VPN only          │
        │  └───────┬───────┘  └───────┬───────┘                        │
        │          │ SELECT-only       │ scrapes /metrics                │
        │          ▼                   ▼                                │
        └──────────┼───────────────────┼────────────────────────────────┘
                    │                   │
                    ▼                   │
        ┌─────────────────────────┐     │
        │ SQL Server CT (existing)  │◄────┘
        │  - db_ledger_runtime login │ (app: read/write dbo)
        │  - db_ledger_migrator login│ (CI/deploy: DDL only, used once per deploy)
        │  - grafana_reader login    │ (SELECT on reporting schema only)
        └─────────────────────────┘

        ┌─────────────────────────┐
        │ Deploy path (separate    │  GitHub-hosted runner builds + attests provenance
        │ trust domain)            │  self-hosted runner (deploy user, no secrets access)
        │                          │  verifies attestation → unpacks → restarts systemd
        └─────────────────────────┘
```

### Key controls, mapped to the reference app's known issues

| Reference issue | This project's fix |
|---|---|
| Runner ran as the secrets-owning user | Separate `deploy` system user runs the self-hosted runner; app secrets in `/etc/ledger/env` are `640 root:ledger` — the `deploy` user is not in the `ledger` group, so a compromised runner job cannot read the connection string, bank tokens, or OAuth signing key |
| App connected as `sa` | Three dedicated logins: `db_ledger_runtime` (app, `dbo` read/write, no DDL), `db_ledger_migrator` (used only by the deploy pipeline to run `dotnet ef database update`, then not used again until next deploy), `grafana_reader` (SELECT on `reporting` only) |
| Artifact deployed without verification | CI runs `actions/attest-build-provenance` on the release zip; `deploy.sh` runs `gh attestation verify <artifact> --repo <owner/ledger>` and aborts on failure, before unpacking |
| Actions pinned by mutable tag | All third-party actions pinned to commit SHA; Dependabot configured for SHA-pinned action updates |
| Shell interpolation of tag input | Tag validated against a strict semver regex in a script step before use; passed via `env:`, never interpolated directly into `run:` |
| Secrets at rest / `TrustServerCertificate=true` | Env file root-owned as above; bank consent tokens encrypted via ASP.NET Core Data Protection before storage (`IDataProtector` on the token column) with keys persisted to a dedicated `dbo.DataProtectionKeys` table and protected with an X.509 certificate (MEDIUM confidence — official MS docs confirm `PersistKeysToDbContext` + `ProtectKeysWithCertificate`, see Sources); SQL connections use proper server TLS validation instead of `TrustServerCertificate=true` |
| Only `/mcp` should be public | Traefik's public router matches only `PathPrefix(\`/mcp\`)` (plus the OAuth metadata/authorize/token paths under a shared prefix, e.g. `/mcp` and `/.well-known/oauth-*`); everything else has no public Traefik route at all — not just an auth check, an absent route |
| Approval gates / tag restrictions | GitHub Environment with required reviewer gates the `deploy` job; outside-contributor PR workflow runs require approval (repo setting) |

## Scalability Considerations

At household scale (2 users, one joint account + a handful of savings accounts, tens of thousands of transactions over years, single Claude conversation at a time), this is explicitly a "does it work correctly and safely" problem, not a "does it scale" problem. Still, for completeness:

| Scale | Architecture Adjustments |
|-------|--------------------------|
| Household (actual target) | Single ASP.NET Core instance, single `BackgroundService` sync worker, no distributed locking needed — there is exactly one process, so re-entrancy is the only concern (guard against a slow sync run overlapping the next timer tick), not concurrent instances |
| Hypothetical multi-household | Would require a distributed lock (Redis/DB advisory lock) around the sync worker if scaled to multiple instances, tenant partitioning in the schema, and a real (not embedded) authorization server — out of scope, noted only because "how would this need to change" is a useful sanity check on today's design |
| N/A beyond that | Not a realistic concern for a self-hosted single-household app; do not over-engineer for it |

### Scaling Priorities

1. **First real risk is data correctness, not throughput:** the pending→booked transaction ID reconciliation and idempotent upsert design (Pattern 2) is far more important to get right early than any performance concern.
2. **Second: MCP tool response size/token cost** as transaction history grows over years — server-side aggregation (never handing Claude raw multi-year transaction lists) is the mitigation, not infrastructure scaling.

## Anti-Patterns

### Anti-Pattern 1: Distributed locking for a single-instance BackgroundService

**What people do:** Reach for Redis-backed distributed locks or a job scheduler library (Quartz.NET with ADOJobStore) by default when adding a scheduled sync job.
**Why it's wrong:** This app is one deployable unit, one process, one instance by design (explicit constraint). A distributed lock solves a problem — multiple instances racing — that does not exist here, and adds an operational dependency (Redis, or a heavier job-store schema) for no benefit.
**Do this instead:** A single `BackgroundService` with `PeriodicTimer`, guarded by a simple in-process re-entrancy flag (skip/log if the previous run hasn't finished), plus idempotent upsert as the real safety net if a run is ever interrupted mid-way.

### Anti-Pattern 2: Letting MCP tools bypass domain services

**What people do:** Write MCP tool methods that talk to the `DbContext` directly "because it's quicker," while REST controllers go through a proper service layer.
**Why it's wrong:** Immediately breaks the audit-log guarantee (must-cover #4) and creates two divergent code paths for the same business rule (e.g. what counts as an internal transfer, or how a recategorisation cascades to forecasts) — the exact kind of drift the shared-application-layer pattern (Pattern 1) exists to prevent.
**Do this instead:** MCP tool classes are exactly as thin as REST controllers — bind, call a `Domain` service, shape the response. If an MCP tool needs different aggregation than any existing REST endpoint, add that aggregation to the domain service (so REST can use it too later), not inline in the tool method.

### Anti-Pattern 3: Treating temporal tables as "the" audit log

**What people do:** Enable SQL Server temporal tables and consider audit logging solved.
**Why it's wrong:** Temporal tables have no actor/reason context (confirmed across multiple sources — SQL Server has no user context), so "who changed this category and why" — which is exactly what a household will ask Claude — cannot be answered from temporal history alone.
**Do this instead:** Application-level `AuditEntry` (actor, before/after JSON, reason) is the primary audit mechanism that MCP/REST read tools query; temporal tables are an additional, free safety net underneath it (Pattern 4).

### Anti-Pattern 4: Grafana querying application tables directly

**What people do:** Point Grafana straight at `dbo` tables with a broad read login, because it's faster to set up than a views layer.
**Why it's wrong:** Any schema refactor (renaming a column, splitting a table) silently breaks dashboards with no compile-time signal, and a broad read login on `dbo` exposes far more than dashboards need (e.g. raw bank consent token columns, even if encrypted, needlessly reachable from the reporting login).
**Do this instead:** `reporting` schema of views, SELECT-only login scoped to that schema only (must-cover #7 and #8).

## Integration Points

### External Services

| Service | Integration Pattern | Notes |
|---------|---------------------|-------|
| PSD2 aggregator (Enable Banking / GoCardless Bank Account Data / other — see STACK research) | Polled HTTP client behind `IBankProvider`, scheduled `BackgroundService` | Consent duration varies by provider and has shifted over time (90→180 days per EBA rule change) — verify the specific provider's current renewal mechanics (polling vs webhook) in STACK research; treat as LOW confidence here |
| Anthropic (claude.ai, Claude Desktop, Claude Code, scheduled runs) | MCP over HTTPS, OAuth 2.1 bearer tokens | Public-facing surface; resource-server-only role for the MCP endpoint (see MCP Surface Design) |
| Prometheus (in-LXC) | Scrapes `/metrics` on a private route | Operational metrics only, never financial data (already decided) |
| Grafana (in-LXC) | SQL data source against `reporting` schema, SELECT-only login | See Grafana Data Contract |
| Existing SQL Server CT | New dedicated database, three scoped logins | No `sa` usage anywhere in this app |
| Existing Traefik | File-provider dynamic config route, public path restricted to `/mcp` + OAuth well-known paths | Matches the reference app's Traefik pattern, narrowed in scope |

### Internal Boundaries

| Boundary | Communication | Notes |
|----------|---------------|-------|
| REST controllers ↔ Domain services | Direct method calls, DI-resolved | No business logic in controllers |
| MCP tool classes ↔ Domain services | Direct method calls, DI-resolved, scoped per MCP call | Same services as REST — see Pattern 1 |
| SyncWorker ↔ Ingestion abstraction | `IBankProvider` interface | Swappable adapter, no leakage into Domain business logic beyond the interface |
| Domain services ↔ Repository (EF Core) | Repository interfaces defined in Domain, implemented in Repository project | Matches the reference app's one-way `Service → Domain → Repository` dependency direction |
| Audit/undo ↔ everything else | Cross-cutting: domain services call `IAuditRecorder` as part of every mutating operation, in the same transaction as the change | Not a separate service to call after the fact — must be atomic with the change it records |
| Grafana ↔ database | `reporting` schema views only, via SELECT-only login | No access to `dbo` |

## Suggested Build Order (must-cover #10)

The core value proposition is "Claude as a trustworthy advisor," which requires real, correctly-categorised data before advice is meaningful — so the build order prioritises a thin, real, end-to-end slice over building any single layer to completion first.

1. **Foundation:** solution skeleton (`Domain`/`Repository`/`Service` projects matching the reference layering), SQL Server database + three scoped logins, EF Core `DbContext` with the core entities (Account, Transaction, Category — minimal), migrations pipeline, systemd + Traefik wiring reused from the reference deployment pattern but narrowed per the security fixes above. *Delivers:* a deployable empty app, proves the deployment pipeline works with the hardening fixes applied from day one (cheaper to build in than to retrofit).
2. **Ingestion, thin:** `IBankProvider` interface + one concrete aggregator adapter, `SyncWorker` BackgroundService, idempotent upsert including pending→booked reconciliation, raw payload retention, consent state machine (even if the "expiring/renewal" UI comes later, the state field and detection logic should exist now). *Delivers:* real transactions flowing into the database automatically — the precondition for everything else being meaningful rather than synthetic.
3. **Thin MCP read slice (deliberately early, before categorisation is polished):** MCP server hosted alongside REST, OAuth 2.1 resource-server wiring (even against a minimal embedded auth module), a handful of MCP read tools (list/search transactions, basic aggregate by period). *Delivers:* Claude can already look at real (if uncategorised) household data — validates the hardest architectural integration (MCP + OAuth + Anthropic client connectivity) before layering more features on top of it, and gives the household something demonstrably useful within the first few phases.
4. **Categorisation pipeline:** category tree (Nibud-based), rule engine + DB-stored rules, review queue, MCP write tools for recategorise/create-rule, audit log (application-level) wired into every mutating path from this point forward, temporal tables enabled on mutable entities. *Delivers:* the data becomes trustworthy enough for real advice, and the audit/undo pattern is established before more write-capable features are added on top of it (so it's not bolted on retroactively).
5. **Planning features:** budgets, savings goals, recurring-cost detection, forecast — all read from the now-correctly-categorised transaction history. *Delivers:* the richer advisor capabilities (can-we-afford-X, leak detection) that depend on categorisation being trustworthy.
6. **Advisor memory + scheduled reviews:** household profile storage, advice/decision log, scheduled review generation job, notification email (link-only, no financial detail). *Delivers:* session continuity and proactive value — deliberately sequenced after categorisation and planning exist, since a review is only useful once there's something correctly categorised to review.
7. **Grafana + reporting schema:** `reporting` views, SELECT-only login, dashboard provisioning (EN canonical + NL generated), Prometheus operational metrics and alerts. *Delivers:* the partner-facing visibility layer — deliberately after the data model has stabilized somewhat (fewer view rewrites), though the `/metrics` operational endpoint (sync health, consent expiry) can and should land as early as phase 2 since it's cheap and immediately useful for the operator.
8. **REST completeness + optional web review page + deployment hardening pass:** any REST endpoints not already needed by earlier phases, the nice-to-have review/edit web page, and a final security pass (artifact attestation, Environment gates, SHA-pinned actions, IP allowlist evaluation) once the full shape of the app is known.

**Dependency rationale:** ingestion must precede categorisation (nothing to categorise otherwise); categorisation must precede planning features (budgets/forecasts on uncategorised or poorly-categorised data are misleading, which directly undermines "Claude as trustworthy advisor"); the audit/undo mechanism must exist *before* MCP write tools multiply, not after, because retrofitting audit logging onto several already-built write paths is exactly the kind of rework this phase ordering is meant to avoid; Grafana intentionally comes after the domain model has had a chance to stabilize, because reporting views are a contract that's expensive to keep rewriting against a moving schema — but the Prometheus operational-metrics endpoint is cheap and valuable early, so it's pulled forward independent of the Grafana dashboard work.

## Sources

- [MCP C# SDK — Getting Started](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/getting-started.html) — HIGH (official SDK docs)
- [modelcontextprotocol/csharp-sdk (GitHub)](https://github.com/modelcontextprotocol/csharp-sdk) — HIGH (official repo)
- [Build a Model Context Protocol (MCP) server in C# — .NET Blog](https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/) — HIGH (Microsoft)
- [Understanding Authorization in MCP — modelcontextprotocol.io](https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/authorization) — HIGH (spec-adjacent official docs)
- [Diving Into the MCP Authorization Specification — Descope](https://www.descope.com/blog/post/mcp-auth-spec) — MEDIUM (vendor blog, technically detailed)
- [EF Core temporal tables provider docs — learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/temporal-tables) — HIGH (official Microsoft docs)
- [Temporal Tables in EF Core for Data Auditing — milanjovanovic.tech](https://milanjovanovic.tech/blog/temporal-tables-ef-core) — MEDIUM (reputable .NET community source)
- [Data Protection key management and lifetime — learn.microsoft.com](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0) — HIGH (official)
- [Key storage providers in ASP.NET Core — learn.microsoft.com](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-9.0) — HIGH (official)
- [Key encryption at rest in Windows and Azure using ASP.NET Core — learn.microsoft.com](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-encryption-at-rest?view=aspnetcore-9.0) — HIGH (official)
- [actions/attest-build-provenance (GitHub)](https://github.com/actions/attest-build-provenance) — HIGH (official GitHub Action)
- [Using artifact attestations to establish provenance for builds — docs.github.com](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds) — HIGH (official)
- [Configure the Microsoft SQL Server data source — Grafana docs](https://grafana.com/docs/grafana/latest/datasources/mssql/configure/) — HIGH (official Grafana docs)
- [GoCardless Bank Account Data overview — developer.gocardless.com](https://developer.gocardless.com/bank-account-data/overview) — HIGH (official provider docs) — note: PSD2 consent-duration and expiry-notification mechanics should be re-verified per exact provider in STACK research; treated as LOW confidence here
- [PSD2 consent validity extended to 180 days — EnableNow](https://www.enablenow.nl/en/blog/psd2-consent-to-180-days) — MEDIUM (vendor blog, regulatory claim plausible but not cross-checked against the EBA opinion text directly)
- [Best Practices for Background Jobs — Azure Architecture Center, learn.microsoft.com](https://learn.microsoft.com/en-us/azure/architecture/best-practices/background-jobs) — HIGH (official)
- The user's existing homelab app's `.claude/architecture.md` and `docs/server-setup.md` (read-only references, not linked — internal, private repo) — used to align project layering and deployment topology; referred to throughout as "the user's existing homelab app"

---
*Architecture research for: self-hosted household personal-finance backend (.NET 10, MCP advisor, open-banking ingestion)*
*Researched: 2026-09-26*
