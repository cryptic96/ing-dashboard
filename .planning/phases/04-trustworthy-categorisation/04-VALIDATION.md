---
phase: 4
slug: trustworthy-categorisation
# status lifecycle: draft (seeded by plan-phase) → validated (set by validate-phase §6)
# audit-milestone §5.5 distinguishes NOT-VALIDATED (draft) from PARTIAL (validated + nyquist_compliant: false) (#2117)
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-13
---

# Phase 4 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 on Microsoft.Testing.Platform, FluentAssertions, NSubstitute, `FakeTimeProvider` |
| **Config file** | `global.json` (`"runner": "Microsoft.Testing.Platform"`), `Ledger.IntegrationTests/xunit.runner.json` |
| **Quick run command** | `dotnet test --project Ledger.UnitTests -- --filter-trait "Category=<Trait>"` |
| **Integration run** | `dotnet test --project Ledger.IntegrationTests -- --filter-trait "Category=<Trait>"` (real local PostgreSQL) |
| **Full suite command** | `dotnet test --solution Ledger.slnx` |
| **Static checks** | `build/lint.sh` |
| **Estimated runtime** | ~1 s per unit trait, ~45 s per integration trait |

Trait categories: `Rules`, `Categorisation`, `Audit`, `Confirmation`, `Injection`, `Comparison`, `CategoryMetrics`, plus existing `McpTools`, `Dashboards`, `DatabaseRoles`.

---

## Sampling Rate

- **After every task commit:** the unit trait for the code touched, plus `build/lint.sh` when strings, comments or docs change
- **After every plan wave:** the integration traits touched plus `DatabaseRoles`, `Dashboards` and `McpTools`
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** 60 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| _filled by planner / executor_ | | | CAT-01..08, ADV-06, ADV-07, DASH-02, PLAN-09, OPS-08 | | | | | ❌ W0 | ⬜ pending |

Requirement-to-test map: see `04-RESEARCH.md` § Validation Architecture → Phase Requirements to Test Map.

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] New test files per trait and a synthetic fixture set (Tikkie-shaped, PSP-shaped, injection-shaped, savings-shaped descriptions, all invented)
- [ ] `McpTestHost.OpenAsync` accepts `McpClientOptions` (elicitation handler, protocol version)
- [ ] `LedgerQuerySeed` runs the built-in transfer pass after seeding
- [ ] `DatabaseRoleTests` privilege matrix extended for every new relation and view
- [ ] `DashboardGeneratorTests` and `DashboardQueryTests` extended (file lists, `lang` constant variable, `category` variable)
- [ ] Tests asserting old wording / flat fields updated (`ToolSurfaceTests`, `TotalsQueryTests`, `ToolCatalogTests`, `FreeTextMaskingTests`, `SearchPaginationTests`)
- [ ] Architecture test: MCP types depend on no store or `LedgerDbContext`
- [ ] No framework install needed

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| A real model reading a hostile description does not call a write tool | ADV-07 | Model behaviour cannot be proven by automated tests | One supervised session with a synthetic instruction-bearing row |
| Claude Code shows the elicitation dialog and the per-call approval prompt (no "don't ask again" on `apply_changes`) | ADV-07, CAT-07 | Client UI behaviour | Run a rule preview + apply from Claude Code |
| Grafana click-through drill-down (any depth, breadcrumb, time range kept, EN and NL) | DASH-02 | Interactive UI | Click through the deployed dashboards |
| Savings-account transfer rule against real text | CAT-02 | Real data must not enter the repo | Operator-assisted check on the host; nothing copied into the repository |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
