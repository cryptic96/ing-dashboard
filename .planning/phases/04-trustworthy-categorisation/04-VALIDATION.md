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
| 04-01-T1 | 04-01 | 1 | CAT-01, CAT-04 | T-04-01-05 | A categorised transaction shows its category, kind, source and confidence in search_transactions and in reporting.transactions; unassigned transactions show none. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-01-T2 | 04-01 | 1 | CAT-01, CAT-04 | T-04-01-05 | The starter tree is proven complete, bilingual, kind-consistent and free of figures and rules, and provenance combinations the decisions forbid cannot be stored. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-02-T1 | 04-02 | 1 | CAT-02, CAT-05 | T-04-02-01, T-04-02-02 | Operator records the shape of real savings transfers as yes/no answers | manual (operator) | n/a (checkpoint:human-action) | n/a | ⬜ pending |
| 04-02-T2 | 04-02 | 1 | CAT-02, CAT-05 | T-04-02-01, T-04-02-02 | Every categorisation test of this phase can load one synthetic scenario that has the real savings shape and every risky bank-text shape, and the repository holds no real data. | unit + integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-03-T1 | 04-03 | 2 | ADV-06, CAT-04 | T-04-03-01, T-04-03-02, T-04-03-06 | An assignment written through the store commits together with one audit operation and its item, and a no-op write records nothing. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Audit"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-03-T2 | 04-03 | 2 | ADV-06, CAT-04 | T-04-03-01, T-04-03-02, T-04-03-06 | The audit log is append-only for the application, carries no bank data, never records a half-applied write and lists in a stable order. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Audit"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-04-T1 | 04-04 | 2 | CAT-01, CAT-08 | T-04-04-02 | Tree navigation and the removal check are pure, tested and cycle-safe. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Categorisation"` | ❌ W0 | ⬜ pending |
| 04-04-T2 | 04-04 | 2 | CAT-01, CAT-08 | T-04-04-02 | New categories get stable, readable, unique keys that never collide with the reserved ones. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Categorisation"` | ❌ W0 | ⬜ pending |
| 04-05-T1 | 04-05 | 2 | CAT-03, CAT-05 | T-04-05-01, T-04-05-03 | A pure, regex-free matcher decides assign or queue for any transaction against any rule set, with conflicts and intermediaries handled as decided. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Rules"` | ❌ W0 | ⬜ pending |
| 04-06-T1 | 04-06 | 2 | ADV-07 | T-04-06-01, T-04-06-02 | Any bank or household text can be cleaned, masked and capped safely before it reaches Claude or storage. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Injection"` | ❌ W0 | ⬜ pending |
| 04-06-T2 | 04-06 | 2 | ADV-07 | T-04-06-01, T-04-06-02 | Instruction-like bank or household text is recognised by fixed, linear checks, and ordinary payments are not. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Injection"` | ❌ W0 | ⬜ pending |
| 04-07-T1 | 04-07 | 3 | CAT-04, ADV-06 | T-04-07-01, T-04-07-03, T-04-07-05 | One change_ledger call recategorises one transaction as a single audited operation naming the client, and the category with its provenance is visible in search_transactions and in reporting.transactions. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-07-T2 | 04-07 | 3 | CAT-04, ADV-06 | T-04-07-01, T-04-07-03, T-04-07-05 | The catalogue knows read and write tools, descriptions stay within the client's limit, and the layering rule is enforced by a test. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=McpTools"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-08-T1 | 04-08 | 3 | PLAN-09, CAT-01 | T-04-08-01, T-04-08-02 | The comparison windows follow D-34 and D-35 for every calendar edge and explain every clamp. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Comparison"` | ❌ W0 | ⬜ pending |
| 04-08-T2 | 04-08 | 3 | PLAN-09, CAT-01 | T-04-08-01, T-04-08-02 | Every comparison figure, change and reliability statement comes from one pure, tested calculator. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Comparison"` | ❌ W0 | ⬜ pending |
| 04-09-T1 | 04-09 | 4 | ADV-07 | T-04-09-01, T-04-09-02 | Only write-scoped connections see and call change_ledger. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-09-T2 | 04-09 | 4 | ADV-07 | T-04-09-01, T-04-09-02 | Writing requires the ledger.write scope that the user approves on the consent page; read-only grants keep working and never see a write tool. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-10-T1 | 04-10 | 4 | CAT-02 | T-04-10-01, T-04-10-04 | Each sync stores synced-account transfers in the Transfers band and leaves everything else, including user decisions and partner flows, untouched. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-10-T2 | 04-10 | 4 | CAT-02 | T-04-10-01, T-04-10-04 | Categorisation can never break a sync, and its failures are visible to the operator without leaking data. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-11-T1 | 04-11 | 5 | ADV-06 | T-04-11-01, T-04-11-02, T-04-11-04 | Claude can list every audited change with its client and look inside one operation, in a stable order and without bank text. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Audit"` | ❌ W0 | ⬜ pending |
| 04-11-T2 | 04-11 | 5 | ADV-06 | T-04-11-01, T-04-11-02, T-04-11-04 | The tool surface is asserted from one list, and recent_changes is part of it. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=McpTools"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-12-T1 | 04-12 | 5 | CAT-02 | T-04-12-01, T-04-12-02, T-04-12-03 | Totals exclude transfers by category kind and report exactly what they reported before. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=McpTools"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-12-T2 | 04-12 | 5 | CAT-02 | T-04-12-01, T-04-12-02, T-04-12-03 | Upgrading a real ledger changes no total: existing transfers are assigned in the same release that starts reading them by kind. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-13-T1 | 04-13 | 6 | ADV-06 | T-04-13-01, T-04-13-02, T-04-13-04 | Revert-or-skip is a pure, tested decision. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Audit"` | ❌ W0 | ⬜ pending |
| 04-13-T2 | 04-13 | 6 | ADV-06 | T-04-13-01, T-04-13-02, T-04-13-04 | Any one-item operation can be undone through MCP; undo skips what changed later, says so, and is itself undoable. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Audit"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-14-T1 | 04-14 | 6 | ADV-07 | T-04-14-01, T-04-14-02 | Two different change sets never share a fingerprint, and the same change set always has the same one. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Confirmation"` | ❌ W0 | ⬜ pending |
| 04-14-T2 | 04-14 | 6 | ADV-07 | T-04-14-01, T-04-14-02 | A stored change set can be redeemed once, only by its owner and only before it expires. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Confirmation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-15-T1 | 04-15 | 6 | ADV-07 | T-04-15-01, T-04-15-02, T-04-15-04 | Search results carry bank text only inside the cleaned, capped untrusted wrapper with the notice. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=McpTools"` | ❌ W0 | ⬜ pending |
| 04-15-T2 | 04-15 | 6 | ADV-07 | T-04-15-01, T-04-15-02, T-04-15-04 | No read tool returns bank text outside the untrusted wrapper, and Claude is told on connection and in each tool that the wrapper holds data, never instructions. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=McpTools"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-16-T1 | 04-16 | 7 | ADV-07 | T-04-16-01, T-04-16-02, T-04-16-06 | A multi-transaction recategorisation waits for the user's answer in the client dialog and applies once, as one audited operation. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Confirmation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-16-T2 | 04-16 | 7 | ADV-07 | T-04-16-01, T-04-16-02, T-04-16-06 | Only an accepted dialog applies a change set, once; everything else changes nothing. | unit + integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Confirmation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-17-T1 | 04-17 | 7 | CAT-03, CAT-04, CAT-05 | T-04-17-01, T-04-17-03 | Stored rules categorise new transactions after each sync, as data, never as code. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Rules"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-17-T2 | 04-17 | 7 | CAT-03, CAT-04, CAT-05 | T-04-17-01, T-04-17-03 | Each sync categorises new transactions by the most specific stored rule, with intermediaries and conflicts left for the queue and user decisions untouched. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Rules"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-18-T1 | 04-18 | 7 | CAT-01, CAT-04 | T-04-18-01 | Totals answer by category at any depth and by uncategorised, with transfers handled by kind. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=McpTools"` | ❌ W0 | ⬜ pending |
| 04-18-T2 | 04-18 | 7 | CAT-01, CAT-04 | T-04-18-01 | Search answers by category and uncategorised, and paging cannot leak rows across filters. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=McpTools"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-19-T1 | 04-19 | 8 | ADV-07, ADV-06 | T-04-19-01, T-04-19-02, T-04-19-03 | The token path is a real human gate for clients without a dialog and cannot be used to skip the dialog, replayed, forged or raced. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Confirmation"` | ❌ W0 | ⬜ pending |
| 04-19-T2 | 04-19 | 8 | ADV-07, ADV-06 | T-04-19-01, T-04-19-02, T-04-19-03 | A bulk change can be undone only after the user confirms, with honest skip reporting. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Audit"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-20-T1 | 04-20 | 8 | ADV-07 | T-04-20-01 | Hostile bank text is flagged once at sync, counted for the operator and visible to Grafana. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Injection"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-20-T2 | 04-20 | 8 | ADV-07 | T-04-20-01 | Reading instruction-shaped bank text through any read tool cannot change the ledger. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Injection"` | ❌ W0 | ⬜ pending |
| 04-21-T1 | 04-21 | 8 | PLAN-09 | T-04-21-01 | Claude answers "compared with before" questions from one server-computed comparison with three like-for-like baselines and an honest reliability statement. | unit + integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Comparison"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-21-T2 | 04-21 | 8 | PLAN-09 | T-04-21-01 | compare_spending is part of the asserted tool surface. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=McpTools"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-22-T1 | 04-22 | 9 | ADV-07 | T-04-22-01 | Write floods and oversized steps are refused and counted, and every confirmation outcome is visible on /metrics. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Confirmation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-22-T2 | 04-22 | 9 | ADV-07 | T-04-22-01 | The operator knows how to grant write access, how confirmations look, what the limits are, and which settings would switch the human check off. | static | `build/lint.sh repo-rules` | ✅ (extends existing) | ⬜ pending |
| 04-23-T1 | 04-23 | 10 | ADV-07, ADV-06 | T-04-23-01 | A household note on one transaction is audited, undoable and visible in Grafana's reporting view. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-23-T2 | 04-23 | 10 | ADV-07, ADV-06 | T-04-23-01 | A note can never carry an instruction or invisible characters into a later session. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` | ❌ W0 | ⬜ pending |
| 04-24-T1 | 04-24 | 10 | CAT-08, CAT-01 | T-04-24-02 | Claude can read the tree with both names and real counts at every depth. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` | ❌ W0 | ⬜ pending |
| 04-24-T2 | 04-24 | 10 | CAT-08, CAT-01 | T-04-24-02 | category_overview is part of the asserted tool surface. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=McpTools"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-25-T1 | 04-25 | 11 | ADV-07 | T-04-25-02 | Claude sees which rows carry suspicious bank text and reads the household's notes as the household's own. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Injection"` (+3 more) | ❌ W0 | ⬜ pending |
| 04-26-T1 | 04-26 | 11 | CAT-08, CAT-01 | T-04-26-01 | Claude can add a subcategory with both names at any depth, audited and undoable, without moving anything. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-26-T2 | 04-26 | 11 | CAT-08, CAT-01 | T-04-26-01 | Claude can rename categories in both languages; names cannot carry an instruction or exceed the cap. | unit + integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-27-T1 | 04-27 | 12 | CAT-02 | T-04-27-01, T-04-27-02, T-04-27-03 | A confirmed own account moves its transfers out of spending now and at every sync. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-27-T2 | 04-27 | 12 | CAT-02 | T-04-27-01, T-04-27-02, T-04-27-03 | Own accounts are household data that Claude proposes and the user confirms, and their numbers stay on the server. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-28-T1 | 04-28 | 12 | CAT-05 | T-04-28-01, T-04-28-02, T-04-28-04 | Claude sees every transaction that waits for a category and why it waits. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` | ❌ W0 | ⬜ pending |
| 04-28-T2 | 04-28 | 12 | CAT-05 | T-04-28-01, T-04-28-02, T-04-28-04 | The queue pages without gaps or repeats, and review_queue is part of the asserted tool surface. | unit + integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-29-T1 | 04-29 | 13 | CAT-01, CAT-04, PLAN-09 | T-04-29-02 | Claude groups totals by category at any level, with Uncategorised closing every total. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=McpTools"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-29-T2 | 04-29 | 13 | CAT-01, CAT-04, PLAN-09 | T-04-29-02 | Claude is told on connection and in each tool how to ask by category and where history questions go. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=McpTools"` (+3 more) | ❌ W0 | ⬜ pending |
| 04-30-T1 | 04-30 | 13 | CAT-05 | T-04-30-02, T-04-30-04 | Intermediaries are household data that Claude proposes and the user confirms; their payments are no longer force-matched on name alone. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-30-T2 | 04-30 | 13 | CAT-05 | T-04-30-02, T-04-30-04 | Intermediary flags are confirmed, audited and undoable, and Claude sees them as untrusted names. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-31-T1 | 04-31 | 14 | CAT-06 | T-04-31-01, T-04-31-02 | Claude stores proposals that change no total, as audited batches. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-31-T2 | 04-31 | 14 | CAT-06 | T-04-31-01, T-04-31-02 | Only well-formed, clean proposals are stored. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` | ❌ W0 | ⬜ pending |
| 04-32-T1 | 04-32 | 15 | CAT-06, CAT-05 | T-04-32-01, T-04-32-02, T-04-32-03 | Proposals become categories only through the user's confirmed batch; rejections and later changes are handled and audited. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-32-T2 | 04-32 | 15 | CAT-06, CAT-05 | T-04-32-01, T-04-32-02, T-04-32-03 | Claude can see the biggest uncategorised groups first and the proposals waiting, always in the same order. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` | ❌ W0 | ⬜ pending |
| 04-32-T3 | 04-32 | 15 | CAT-06, CAT-05 | T-04-32-01, T-04-32-02, T-04-32-03 | Reading the queue never writes, and the operator knows the rules-first workflow. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Injection"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-33-T1 | 04-33 | 15 | DASH-02, CAT-01 | T-04-33-01 | Grafana's reader can query category roll-ups that add up at any depth. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Dashboards"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-33-T2 | 04-33 | 15 | DASH-02, CAT-01 | T-04-33-01 | The status and recent-changes data the dashboard shows are available read-only and free of bank data. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Dashboards"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-34-T1 | 04-34 | 16 | CAT-07, CAT-03 | T-04-34-01, T-04-34-02 | A correction becomes a narrow rule whose effect on history is previewed exactly and applied only after the user confirms. | unit + integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Rules"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-34-T2 | 04-34 | 16 | CAT-07, CAT-03 | T-04-34-01, T-04-34-02 | History previews are exact, safe when stale, and never touch what the user decided. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Rules"` | ❌ W0 | ⬜ pending |
| 04-35-T1 | 04-35 | 16 | DASH-02 | T-04-35-01, T-04-35-02, T-04-35-03 | Both partners can open the Spending dashboard in their language, start at all bands and click down through any depth and back up. | unit + integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Dashboards"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-35-T2 | 04-35 | 16 | DASH-02 | T-04-35-01, T-04-35-02, T-04-35-03 | The English and Dutch dashboards are proven to differ only in text, and every link resolves. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Dashboards"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-36-T1 | 04-36 | 16 | OPS-08 | T-04-36-01 | The queue size is on /metrics from startup and visibly shrinks as categorisation proceeds. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=CategoryMetrics"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-36-T2 | 04-36 | 16 | OPS-08 | T-04-36-01 | Write-limit hits, hostile bank text and categorisation failures reach the operator by email without financial detail, and the docs say how to respond. | script | `build/lint.sh script-tests` (+2 more) | ✅ (extends existing) | ⬜ pending |
| 04-37-T1 | 04-37 | 17 | CAT-08 | T-04-37-01 | A category can be removed or merged into a chosen target with everything beneath it, as one confirmed, audited operation. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+1 more) | ❌ W0 | ⬜ pending |
| 04-37-T2 | 04-37 | 17 | CAT-08 | T-04-37-01 | Removal and merge are confirmed and undoable, the overview shows every rule safely, and the operator knows which structural changes need confirmation. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Categorisation"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-38-T1 | 04-38 | 17 | DASH-02 | T-04-38-01, T-04-38-04 | The drill-down shows where the money went per child and how the selected category moves month by month against last year. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Dashboards"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-38-T2 | 04-38 | 17 | DASH-02 | T-04-38-01, T-04-38-04 | Both partners see the transactions behind any category with how each got its category. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Dashboards"` (+3 more) | ❌ W0 | ⬜ pending |
| 04-39-T1 | 04-39 | 17 | ADV-06 | T-04-39-01 | When Claude misbehaves or is unavailable, the operator can see and undo any change with an API key, audited under the key's name. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Audit"` | ❌ W0 | ⬜ pending |
| 04-39-T2 | 04-39 | 17 | ADV-06 | T-04-39-01 | The REST escape hatch is key-protected, rate-limited, plainly refusing and documented. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Audit"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-40-T1 | 04-40 | 18 | CAT-07, CAT-03, ADV-07 | T-04-40-01, T-04-40-02, T-04-40-03 | Only narrow, explicit, injection-checked rules can be created; rules on intermediaries need a second signal; rules into transfers need the user's confirmation. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Rules"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-40-T2 | 04-40 | 18 | CAT-07, CAT-03, ADV-07 | T-04-40-01, T-04-40-02, T-04-40-03 | Every way a rule can change history is previewed, confirmed, audited per round and undoable. | unit + integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Rules"` (+3 more) | ❌ W0 | ⬜ pending |
| 04-41-T1 | 04-41 | 18 | PLAN-09, DASH-02 | T-04-41-01, T-04-41-03 | Both partners see the same comparison in Grafana that Claude quotes, including when it is not reliable yet. | integration | `dotnet test --project Ledger.IntegrationTests/Ledger.IntegrationTests.csproj --filter-trait "Category=Comparison"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-41-T2 | 04-41 | 18 | PLAN-09, DASH-02 | T-04-41-01, T-04-41-03 | Each partner reads the comparison and its reliability in their own language. | unit | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Dashboards"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-42-T1 | 04-42 | 19 | DASH-02 | T-04-42-01, T-04-42-03, T-04-42-04 | The Spending dashboard opens with the month's money picture and what needs attention. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Dashboards"` (+2 more) | ❌ W0 | ⬜ pending |
| 04-42-T2 | 04-42 | 19 | DASH-02 | T-04-42-01, T-04-42-03, T-04-42-04 | Both partners can see what Claude changed, and the guide explains every part of the dashboard. | unit + integration | `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=Dashboards"` (+3 more) | ❌ W0 | ⬜ pending |
| 04-43-T1 | 04-43 | 20 | CAT-02, CAT-07, ADV-07, DASH-02, OPS-08 | T-04-43-01, T-04-43-02 | The release candidate is proven end to end and waits for the operator's merge. | integration | `LEDGER_EFBUNDLE="$PWD/artifacts/release/stage/efbundle" dotnet test --solution Ledger.slnx --no-restore` (+2 more) | ✅ | ⬜ pending |
| 04-43-T2 | 04-43 | 20 | CAT-02, CAT-07, ADV-07, DASH-02, OPS-08 | T-04-43-01, T-04-43-02 | Operator releases v0.4.0, re-approves Claude Code with write access, and checks what only a person can check | manual (operator) | n/a (checkpoint:human-action) | n/a | ⬜ pending |

Requirement-to-test map: see `04-RESEARCH.md` § Validation Architecture → Phase Requirements to Test Map.

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

Each item is delivered by the task that first needs it (the plans have no separate scaffolding plan):

- [ ] New test files per trait and a synthetic fixture set (payment-request-, PSP-, injection- and savings-shaped descriptions, all invented) — 04-02-T2 (scenario), per-trait files in the task that introduces each trait
- [ ] `CategorisationSeed` (isolated seeded database, direct assignment for read-side tests) — 04-01-T1
- [ ] `McpTestHost.ConnectAsync` write parameter and `OAuthTestDriver` scope parameter — 04-09-T1
- [ ] `ExpectedTools` shared read and write tool lists for the surface and scope tests — 04-11-T2
- [ ] `McpTestHost.OpenAsync` accepts `McpClientOptions` (elicitation handler, protocol version) — 04-16-T1
- [ ] `LedgerQuerySeed` runs the built-in transfer pass after seeding; `DatabaseFixture.MigrateAsync(databaseName, targetMigration)` — 04-12-T1, 04-12-T2
- [ ] `DatabaseRoleTests` privilege matrix extended for every new relation and view — 04-01-T1, 04-03-T1, 04-14-T2, 04-17-T1, 04-20-T1, 04-23-T1, 04-31-T1, 04-33-T1, 04-33-T2
- [ ] `DashboardGeneratorTests` and `DashboardQueryTests` extended (file lists, `lang` constant variable, `category` variable) — 04-35-T1, 04-35-T2
- [ ] Tests asserting old wording / flat fields updated (`ToolSurfaceTests`, `TotalsQueryTests`, `ToolCatalogTests`, `FreeTextMaskingTests`, `SearchPaginationTests`) — 04-15-T1, 04-15-T2, 04-29-T2
- [ ] Architecture test: MCP types depend on no store or `LedgerDbContext` — 04-07-T2
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
