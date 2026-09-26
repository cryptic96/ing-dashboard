# Project Research Summary

**Project:** Household Ledger (Self-hosted household personal-finance backend)
**Domain:** Personal finance with LLM advisor, PSD2 open-banking ingestion, Grafana dashboards
**Researched:** 2026-09-26
**Confidence:** MEDIUM overall (HIGH on core architecture/stack facts; MEDIUM on fast-moving specifics like MCP OAuth and PSD2 provider details; LOW on a few unvalidated integration points)

## Executive Summary

Household Ledger is a self-hosted personal-finance backend designed to make Claude a trustworthy financial advisor for a two-person Dutch household banking with ING. The product automatically syncs bank transactions, categorises them against a Nibud-based category tree (with Claude-assisted review for ambiguous cases), and exposes the data three ways: Grafana dashboards (EN/NL) for visibility, an MCP server so Claude can read/write/advise, and a REST API for operations MCP isn't suited to.

The recommended approach is a single .NET 10 ASP.NET Core host serving all three transports, with ingestion via Enable Banking (the only individual-eligible free aggregator found that covers ING NL in 2026), Authentik for OAuth 2.1 authorization (to support claude.ai dynamic client registration), Grafana dashboards sourced from SQL Server reporting views, and Prometheus strictly for operational metrics (not financial data, which breaks on recategorisation). The architecture prioritises correctness over scale — multiple places where "doing it right" requires careful handling: idempotent pending→booked transaction reconciliation, immutable audit logs for every advisor write, least-privilege SQL logins, and explicit separation of MCP write tools from data content to prevent prompt injection.

The single highest-priority pitfall is **silent consent expiry** (PSD2 consents expire every 90–180 days) being treated as just another sync error rather than a distinct operational state — this must be tracked, monitored, and proactively alerted from day one, not added as an afterthought. Secondary critical pitfalls include prompt-injection attack surface (free-text transaction descriptions reaching Claude alongside write tools), OAuth misconfiguration (token audience validation, no passthrough to downstream APIs), and accidental data leakage in a public repository. All are preventable with upfront design discipline.

## Key Findings

### Recommended Stack

**Bank data ingestion:** Enable Banking (Restricted Production tier) is the only mainstream aggregator found that has a genuine no-cost, production-mode path for an individual household in 2026. GoCardless Bank Account Data (historically the default) closed new signups in July 2025. ING's own developer portal is restricted to licensed third-party providers (not individuals). Enable Banking covers ING NL across joint and savings accounts, uses JWT-authenticated private-key credentials (secure but requires key storage like connection strings), and implements the PSD2 consent-duration pattern (90–180 days, variable by bank). **Early spike needed:** verify Enable Banking's ING NL coverage explicitly includes spaarrekening (savings accounts) against a real consent, since no source confirms this; Salt Edge's Developer tier is fallback if needed.

**Grafana data access:** Recommended primary: **MSSQL datasource with reporting views and SELECT-only login.** Zero extra infrastructure (uses the existing SQL Server), full SQL expressiveness for drill-downs, views versioned alongside schema migrations. Secondary, additive: **Infinity datasource (JSON API) for panels needing business logic** (forecast projections, computed metrics) that shouldn't live in SQL. Prometheus explicitly out-of-scope for financial data — scrape-time timestamps (not booking dates), immutable samples breaking recategorisation, cardinality explosion on per-transaction labels all make it wrong for transactional data; use it only for operational metrics (sync health, consent expiry countdown, error counters).

**MCP and OAuth:** Use the official C# MCP SDK (`ModelContextProtocol.AspNetCore` v2.x) as a **resource server only** (validates tokens, never issues them). For authorization, **Authentik ≥2026.8** is recommended over embedded OpenIddict (which lacks Dynamic Client Registration, needed for claude.ai) and lighter than Keycloak. Authentik is fully open source with no licensing ambiguity, has proven DCR support, and a well-documented single-compose deployment. Run it as a separate service (logically distinct from the MCP resource-server role, though co-located in the same LXC). **Decision point flagged:** verify whether claude.ai custom connectors accept Client ID Metadata Documents (CIMD) as an alternative to DCR — if so, a simpler embedded OpenIddict approach becomes viable once it ships DCR, revisit then.

**Core .NET libraries:** EF Core 10, Microsoft.Data.SqlClient 6.1.x, ASP.NET Core Data Protection (for encrypting bank consent tokens at rest), `Microsoft.Extensions.Http.Resilience` (for HTTP retry/circuit-breaker), MailKit for notification email. Test stack: xUnit v3, FluentAssertions, NSubstitute, plus add `Testcontainers.MsSql` (new) to validate least-privilege logins and view-based reporting contracts.

### Expected Features

**Must have for launch (v1):** Automatic ingestion, idempotent sync, internal-transfer detection, rule-based categorisation with Claude-assisted review, Nibud-structured category tree, monthly budgets, recurring-cost detection with forecast, MCP read/write tools with audit+undo, Grafana dashboards (EN/NL, provisioned), monthly scheduled review.

**Should add in v1.x:** Named savings goals with projection, annual-cost smoothing, price-increase detection, advisor memory across sessions, Nibud reference comparison (user decision on sourcing needed).

**Defer (v2+):** Vakantiegeld/toeslagen-aware income detection, multi-bank ingestion, full web UI.

**Anti-features (explicitly not building):** Payment initiation, investment advice, subscription cancellation service, gamification, manual entry, financial figures in email.

### Architecture Approach

Single ASP.NET Core host with three transports: REST controllers, MCP tool classes, and BackgroundService workers all sharing one dependency-injection container and domain services. This ensures categorisation logic runs identically whether triggered via REST, MCP, or background job. Project structure mirrors the household's existing reference app: Domain → Repository → Service. Ingestion abstraction (`IBankProvider` interface) lives in Domain so provider-swaps don't touch the rest of the app.

**Key patterns:** (1) Shared application layer under REST and MCP, audit logging at domain level. (2) Ingestion behind provider interface with consent state machine. (3) Rules → heuristics → review queue → Claude-assisted resolution. (4) Audit log + SQL Server temporal tables as defense-in-depth. (5) Grafana reporting contract: views-only schema, SELECT-only login, views in migration pipeline so contract can't drift.

### Critical Pitfalls to Address Upfront

1. **Silent consent-expiry sync death** — PSD2 consents expire 90–180 days; silent failures break sync for weeks. **Prevention:** Track consent state explicitly, alert 7–14 days before expiry, build re-consent flow in ingestion phase.

2. **Duplicate and ghost transactions from pending→booked transitions** — Aggregators sometimes re-issue IDs/amounts. **Prevention:** Reconcile by composite key (amount+counterparty+date), never auto-merge, store raw payload for replay, test against real ING pairs.

3. **Prompt injection via transaction descriptions in MCP write tools** — Attacker can embed instructions in a €0.01 transaction description and trigger a write tool. **Prevention:** Structural separation — writes only from user turns, never data content. Label untrusted fields explicitly. Require confirmation on bulk writes.

4. **OAuth and reverse-proxy misconfiguration** — Missing audience validation, token passthrough, wrong issuer URL in metadata. **Prevention:** Validate `aud` on every request, never forward caller tokens, explicit issuer URL, test OAuth discovery from outside network.

5. **Self-hosted runner exposed to fork PRs** — Any fork PR can execute attacker code with secrets access. **Prevention:** Deploy-only runner, gate deploy behind required reviewer, run runner as separate user, restrict tag-creation.

## Implications for Roadmap

Suggested 8-phase structure with explicit dependencies ensuring categorisation is trustworthy before advice features layer on top:

### Phase 1: Foundation & Deployment
Foundation (Domain/Repository/Service, hardened CI/CD per reference security review), SQL Server setup with three scoped logins, Prometheus `/metrics` endpoint. Avoids runner exposure, mutable actions, data leakage pitfalls.

### Phase 2: Ingestion & Consent Lifecycle
Real transactions flowing in (the hard part: idempotent upsert, pending→booked reconciliation), consent-state machine with proactive expiry tracking, raw-payload retention. **Research flag:** Phase 2 spike — verify Enable Banking ING NL savings coverage; if not, switch to Salt Edge.

### Phase 3: MCP Foundation & OAuth (Thin Read Slice)
Validates MCP+OAuth+Anthropic integration early. MCP read tools, Authentik, OAuth discovery. **Research flag:** Verify current claude.ai OAuth requirements (DCR vs CIMD), confirm Authentik 2026.8+ implementation, test OAuth discovery from outside network.

### Phase 4: Categorisation Pipeline & Audit Log
Rules engine, heuristic scoring, review queue, MCP write tools with confirmation gates, application-level audit log, Data Protection encryption. Audit/undo must exist before write tools multiply.

### Phase 5: Planning Features & MCP Write Tools
Budgets, goals, recurring-cost detection, forecast. MCP write tools for budget/goal adjustments. Server-side aggregation prevents Claude arithmetic hallucination.

### Phase 6: Grafana Dashboards & Prometheus Alerting
Reporting schema, SELECT-only views, Grafana dashboards (EN canonical, NL generated), Prometheus alerting. Deliberately after domain model stabilises.

### Phase 7: Advisor Memory & Scheduled Reviews
Household profile + advice log (mutable current state + append-only history), scheduled review generator calling Messages API, review artifacts in Grafana.

### Phase 8: REST Completeness, Optional Web UI, Security Hardening Pass
REST endpoints, optional web page, final security pass (full-history secret scan, gitleaks CI integration).

### Phase Ordering Rationale

Ingestion before categorisation (nothing to categorise without transactions). MCP read before write tools (validate architecture for read first). Categorisation before planning (budgets on dirty data are misleading). Audit/undo before MCP write tools multiply (retrofitting is expensive). Grafana after schema stabilises (views are expensive to rewrite). Advisor memory after categorisation+planning exist (only useful with content to remember). REST completeness at the end (most traffic via MCP/Grafana by then).

### Research Flags

Phases likely needing deeper research:
- **Phase 2:** Enable Banking ING savings coverage, aggregator rate limits, pending→booked reconciliation against real pairs
- **Phase 3:** Current Anthropic OAuth requirements for claude.ai custom connectors, Authentik 2026.8+ MCP support verification
- **Phase 7:** Anthropic Messages API MCP connector scope and metering

Phases with standard patterns (skip research):
- **Phase 1:** Standard .NET/SQL Server/GitHub Actions patterns
- **Phase 4:** Audit/undo, EF Core temporal tables well-documented
- **Phase 5:** SQL aggregation patterns
- **Phase 6:** Grafana/Prometheus best practices well-established

## Conflicts & Open Decisions

### 1. OAuth Authorization Server Placement
**Position A:** Embed as logically separate module in same .NET host.
**Position B:** Run Authentik as separate service.
**Recommendation:** Start with Position B (separate service) — operational clarity worth one extra container. Embedded fallback once OpenIddict ships DCR.

### 2. Scheduled Review Triggering
**Position A:** Claude-side cloud Routines (plan-gated, research preview).
**Position B:** App calls Messages API on schedule (recommended primary).
**Recommendation:** Position B for v1 (full control, predictable cost). Position A available as user-triggered convenience.

### 3. Advisor Memory Timing
**Position A:** FEATURES suggests deferring to v1.x.
**Position B:** PROJECT.md (user's stated choice) includes in v1 scope.
**Recommendation:** Position B per user choice. FEATURES' caution noted: memory is only useful with content to remember (naturally lands after reviews exist); expect value to compound from review #2 onward.

### 4. Nibud Reference Figures Licensing
**Issue:** Figures appear to be proprietary product (€142/year Budgethandboek, metered API).
**Options:** (a) User-entered config from own Budgethandboek (database only, never committed), (b) paid API call at runtime, (c) defer to v1.x.
**Decision needed during planning:** Which path works for the household.

### 5. ING Savings-Account Coverage via Enable Banking
**Current status:** Docs list ING among covered ASPSPs but don't explicitly confirm spaarrekening inclusion.
**Decision needed:** Phase 2 spike with real test consent. Fallback to Salt Edge if not covered.

### 6. Real ING Data in Tests
**Resolution:** Real-data tests run locally (`.gitignore`'d captured pairs), synthetic fixtures committed. Reconciliation logic validated locally against real data without committing real data to public repo.

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | MEDIUM-HIGH | Core technologies well-established. Enable Banking savings coverage is LOW confidence (needs Phase 2 verification). MCP SDK and Authentik OAuth are MEDIUM (fast-moving, need implementation verification). |
| Features | MEDIUM | Feature landscape synthesized from comparable products; patterns sound. Dutch-specific patterns MEDIUM confidence (consumer sources, not authoritative specs). Nibud licensing LOW (needs sign-up verification). |
| Architecture | MEDIUM-HIGH | Patterns well-established, match reference app. Pending→booked reconciliation MEDIUM (no authoritative spec; needs real-data validation Phase 2). |
| Pitfalls | HIGH | Sourced from real incidents, vendor docs, best-practice consensus. Prompt injection and OAuth misconfiguration explicitly documented in MCP spec. PSD2 consent-expiry confirmed across multiple sources. |
| **Overall** | MEDIUM | HIGH on architecture and core stack facts. MEDIUM on provider integration specifics (Enable Banking, Anthropic OAuth, rate limits) that must be validated in Phase 2–3 spikes. |

### Gaps to Address

- Enable Banking ING NL savings coverage → Phase 2 spike (real consent test)
- Anthropic OAuth requirements for claude.ai → Phase 3 research gate
- Aggregator rate-limit specifics → Phase 2 load testing
- PSD2 consent renewal mechanics per provider → Phase 2 implementation
- Prompt-injection mitigation verification → Phase 4 security review + Pitfall 7 checklist
- Data Protection key persistence in production → Phase 1 restart-cycle test
- Timezone handling for DST/month-boundaries → Ingestion/Advisor phase test cases

---

*Research completed: 2026-09-26 by four parallel researcher agents*
*Synthesized: 2026-09-26*
*Ready for roadmap: yes*
