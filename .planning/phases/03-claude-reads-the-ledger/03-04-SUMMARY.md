---
phase: 03-claude-reads-the-ledger
plan: 04
subsystem: mcp
tags: [mcp, search, keyset-pagination, counterparties, tool-surface, postgres]

requires:
  - phase: 03-03
    provides: PeriodResolver, LedgerQueryFilter, LedgerQueryStore filters and own-account rule, LedgerQueryService validation, money_totals
  - phase: 03-02
    provides: OAuthTestDriver, McpTestHost sign-in
provides:
  - search_transactions MCP tool returning capped, keyset-paged detail rows that state when they were truncated
  - find_counterparties MCP tool merging spellings under one counterparty_ref with per-currency totals and masked accounts
  - SearchCursor, an opaque keyset cursor bound to a hash of the filters
  - The final four-tool surface (ledger_overview, money_totals, search_transactions, find_counterparties), each read-only, non-destructive, idempotent and closed-world
  - Final server instructions routing every total to money_totals
affects: [03-05, 03-06, 03-07, 03-08]

tech-stack:
  added: []
  patterns:
    - "Count and one keyset page in a single SQL statement (count subquery LEFT JOINed to the ordered, filtered LIMIT limit+1 page) inside the existing repeatable-read read-only snapshot"
    - "Counterparty account numbers are masked in the store projection so a full number never leaves Ledger.Repository"
    - "Spelling merge happens in the service with TextNormalizer.ForMatching; the store returns one row per name and currency"
    - "Tool surface locked by reflection (unit) and by tools/list over a real MCP client (integration)"

key-files:
  created:
    - Ledger.Domain/Queries/SearchCursor.cs
    - Ledger.Service/Queries/SearchResult.cs
    - Ledger.Service/Queries/CounterpartiesResult.cs
    - Ledger.UnitTests/Mcp/SearchCursorTests.cs
    - Ledger.UnitTests/Mcp/ToolCatalogTests.cs
    - Ledger.IntegrationTests/Mcp/SearchPaginationTests.cs
    - Ledger.IntegrationTests/Mcp/ToolSurfaceTests.cs
  modified:
    - Ledger.Domain/Queries/ILedgerQueryStore.cs
    - Ledger.Repository/Stores/LedgerQueryStore.cs
    - Ledger.Service/Queries/LedgerQueryService.cs
    - Ledger.Service/Mcp/LedgerTools.cs
    - Ledger.Service/Mcp/ServerInstructions.cs
    - Ledger.IntegrationTests/Mcp/LedgerQuerySeed.cs
    - Ledger.IntegrationTests/Mcp/OAuthFlowTests.cs

key-decisions:
  - "search_transactions results use snake_case fields (consistent with the earlier tools) and camelCase parameters; user-approved at the tracer gate"
  - "search_transactions requires a period or fromDate plus toDate, like money_totals; find_counterparties accepts none and then covers all history"
  - "find_counterparties orders by transaction count descending then name, shows the most frequent spelling as the name, up to five spellings and up to three masked accounts"
  - "Nothing that labels, sanitises or warns about bank text is added to tool results, descriptions or instructions (operator decision, enforced by a unit test)"

patterns-established:
  - "A cursor is not a secret: it carries the last row key and a 16-hex-character SHA-256 of the canonical filters, and only detects reuse against a different search"
  - "Page size is clamped to 1..100 and the result states the limit used and whether it was clamped"

requirements-completed: [ADV-03, ADV-01, ADV-02]

duration: 2 sessions
completed: 2026-10-07
status: complete
actuals:
  tokens: 24000
  tasks: 2
  commits: 3
---

# Phase 3 Plan 04: search_transactions and find_counterparties Summary

**Claude can page through a full year of transactions under a hard cap that always says when it truncated, look up how a merchant is spelled, and sees exactly four read-only tools whose instructions keep every total on the server.**

## Performance

- **Tasks:** 2 of 2 (tracer plus counterparty lookup and surface lock-down); the tracer gate was approved by the user, including the search parameters, row fields and paging as built
- **Task commits:** d2bcd08 (tracer), b3e95d1 (lookup and surface)
- **Test run:** full solution 957 tests, 955 passed, 0 failed, 2 skipped (both pre-existing)
- **Lint:** `build/lint.sh` all six checks pass (repo-rules, workflows, shell, secrets, script-tests, observability)

## Accomplishments

### Task 1 (tracer): capped, cursor-paged search_transactions

- Evidence: unit tests Category=Search 11/11; integration tests Category=Search 10/10. A 1,200-row year is walked in 12 pages of 100 with every row exactly once in (date, first seen, id) descending order, including 40 tied rows across 7-row page boundaries.
- Truncated, next_cursor and note semantics: default 50; 150 becomes 100 with limit_clamped; 0 becomes 1 with limit_clamped; exactly 100 matches is not truncated; 101 gives a one-row second page; amount bounds are inclusive; a cursor replayed with other filters or a malformed cursor is refused with the fixed messages; no sum, total or net property exists; internal_transfer is true only between two synced accounts; counterparty accounts are masked and no seeded IBAN or provider account name appears in the raw result, including over an MCP client; pending status is shown and filterable; dropped rows never appear.
- Result fields: period, filters, rows, returned, matching_total, truncated, next_cursor, limit, limit_clamped, note. Row fields: date, booking_date, status, amount, direction, currency, counterparty_name, counterparty_ref, counterparty_account (masked), description, account_key, account_name, internal_transfer.

### Task 2: find_counterparties and the locked-down surface

- `find_counterparties` (parameters text, period, fromDate, toDate, accounts, limit): case-insensitive match over booked rows of synced accounts, transfers between the synced accounts excluded, spellings merged under one counterparty_ref, per-currency count, money out and in, first and last date, up to three masked accounts. Default 25, hard maximum 100, clamping reported, truncation notice, text of 2 to 100 characters after whitespace collapse.
- Server instructions are in their final generic form: read-only ledger, call ledger_overview first, totals only through money_totals, quote period, count and exclusions, totals are grouped by counterparty name not by category until categories exist, use find_counterparties for spellings, search_transactions is capped detail only, Europe/Amsterdam calendar. The money_totals description now also points at find_counterparties.
- Tests (Category=McpTools): unit 16/16, integration 12/12. They pin exactly four tools with snake_case names and the four annotations (reflection and tools/list over a real client), the money_totals schema property list, the instructions content, the lookup behaviour (spellings merged, masked accounts, per-currency totals, own-account transfers left out, cap and clamp, short text refused, period narrowing), a counterparty_ref from the lookup totalling exactly that counterparty in money_totals, the log-sentinel check (a synthetic filter value never appears in a captured log across four tools) and that no tool result contains a seeded full IBAN, a whitespace-spaced variant or the provider account name. A unit test applies the repository's planning-reference patterns, an IBAN-shaped pattern and the no-bank-text-labelling words to every description, title and the instructions.

## TDD Gate Compliance

Both tasks were marked `tdd="true"`; tests and implementation were committed together in one `feat` commit per task rather than as separate RED and GREEN commits. Tests were written alongside the implementation and all behaviours in the plan are covered, but there is no `test(...)` commit preceding the `feat(...)` commit for either task. The plan is `type: execute`, so the plan-level gate does not apply.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] SearchPage has a fourth member and SearchRowData carries cursor material**
- **Found during:** Task 1
- **Issue:** The service must refuse unknown account keys with the same message as money_totals, which needs to know how many requested keys are synced accounts; the cursor needs each row's id and first-seen instant, which are not output.
- **Fix:** `SearchPage(Rows, MatchingTotal, HasMore, AccountsInScope)`; `SearchRowData` carries the row id and first-seen instant for the cursor only.
- **Commit:** d2bcd08

**2. [Method] Tests and implementation in one commit per task**
- See TDD Gate Compliance.

**3. [Design] One SQL statement returns count and page (Task 1)**
- The count subquery is LEFT JOINed to the ordered, keyset-filtered LIMIT limit+1 page inside the existing repeatable-read read-only snapshot, so an empty page still reports the true matching_total. The plan described a separate count; the single statement is equivalent and cheaper.
- **Commit:** d2bcd08

**4. [Scope] search_transactions requires a period or fromDate plus toDate (Task 1)**
- Same rule as money_totals, so an unbounded search cannot be asked for. The plan text did not say either way.
- **Commit:** d2bcd08

**5. [Rule 1 - Design] FindCounterpartiesAsync has no limit parameter on the store (Task 2)**
- **Issue:** The plan's contract passed `limit` to the store, but spellings differing in case or spacing must be merged before the cap applies, and the merge uses `TextNormalizer.ForMatching` (Unicode NFC), which SQL cannot reproduce. A store-side limit would cut off names that still need merging.
- **Fix:** The store returns one row per counterparty name and currency (grouped in SQL, at most three masked accounts per row); the service merges, orders, clamps to 1..100 and reports truncation. Result size is bounded by the number of distinct matching names, which a two-character minimum text keeps small at household scale.
- **Files modified:** Ledger.Domain/Queries/ILedgerQueryStore.cs, Ledger.Repository/Stores/LedgerQueryStore.cs, Ledger.Service/Queries/LedgerQueryService.cs
- **Commit:** b3e95d1

**6. [Orchestrator-approved] OAuthFlowTests tool-list assertion tightened (Task 2)**
- `Ledger.IntegrationTests/Mcp/OAuthFlowTests.cs` is outside this plan's `files_modified`; the orchestrator approved changing its `Should().Contain("ledger_overview")` to an exact match on the four tools now that the surface is complete.
- **Commit:** b3e95d1

**7. [Test data] The "limit 2" lookup test matches 35 counterparties, not five**
- The plan's example assumed five matches; the shared synthetic scenario also seeds 30 suppliers, so the test searches "example" and asserts matching_total 35 (five named counterparties plus the 30 suppliers). Truncation, the limit used and the clamp to 100 are still verified. `LedgerQuerySeed.cs` needed no further change for this task.
- **Commit:** b3e95d1

## Auth Gates

None.

## Known Stubs

None.

## Threat Flags

None beyond the plan's threat model. Mitigations applied: counterparty accounts masked in the store projection and scanned for in every raw result (T-03-04-01), hard caps of 100 rows and 100 counterparties with reported clamping (T-03-04-02), filter-hash cursor (T-03-04-03), parameterised SQL with LIKE escaping and length and count limits (T-03-04-04), no sum field and instructions routing totals to money_totals (T-03-04-05), sentinel test over captured logs (T-03-04-06). Bank text is intentionally not labelled (T-03-04-07, accepted by the operator).

## Self-Check: PASSED

- Files created: SearchCursor.cs, SearchResult.cs, CounterpartiesResult.cs, SearchCursorTests.cs, ToolCatalogTests.cs, SearchPaginationTests.cs, ToolSurfaceTests.cs all present.
- Commits d2bcd08 and b3e95d1 exist in history.
- Acceptance greps: four tool names, four `ReadOnly = true`, four `OpenWorld = false`, none of untrusted, sanitis, sanitiz or injection in the instructions or tools.
- Full solution test (restored, no --no-restore): 957 total, 0 failed, 2 skipped. `build/lint.sh`: all six checks pass.
