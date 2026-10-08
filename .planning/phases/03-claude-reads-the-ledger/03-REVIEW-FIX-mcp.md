---
phase: 03-claude-reads-the-ledger
part: B (MCP tools, query layer, docs)
fixed_at: 2026-10-08
review_path: .planning/phases/03-claude-reads-the-ledger/03-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
extra_items_fixed: 1
---

# Phase 3 (part B): Code Review Fix Report

**Base:** 1dbb062d9b52809e500ed5f77b063b439591ab5c (worktree branch worktree-agent-aa0f31e6c68d197ba)
**Verification ran in:** the isolated worktree, against the local PostgreSQL container (isolated per-test databases).

**Summary:**
- Findings in scope: 5 (B-WR-01, B-WR-02, B-IN-02, B-IN-03, B-IN-06), plus the `confirm-totp` documentation update
- Fixed: 5, plus the documentation update
- Skipped: 0
- Not attempted by instruction: B-WR-03 (parallel auth fixer), B-IN-01, B-IN-04, B-IN-05, B-IN-07

**Verification:**
- `dotnet test --solution Ledger.slnx`: 1039 total, 1037 passed, 0 failed, 2 skipped (the skips are pre-existing and need external capture files).
- `bash build/lint.sh`: repo-rules, workflows, shell, script-tests, observability pass. The `secrets` check fails, not from any change here: gitleaks flags the Dutch example account number quoted in the B-WR-01 scenario of `.planning/phases/03-claude-reads-the-ledger/03-REVIEW.md` (line 296), in the working tree and in the history that includes 1dbb062. That file is outside this fixer's scope; it needs the example redacted (and, for the history scan, an allowlist entry or a rewrite) by whoever owns the review file. This report deliberately does not repeat the number.

## Fixed Issues

### B-WR-01: Full IBANs can reach Claude through the description text

**Commit:** 707f96b
**Files modified:** `Ledger.Domain/Queries/IbanText.cs`, `Ledger.Service/Queries/LedgerQueryService.cs`, `docs/mcp.md`, `Ledger.UnitTests/Mcp/LedgerTextTests.cs`, `Ledger.IntegrationTests/Mcp/LedgerQuerySeed.cs`, `Ledger.IntegrationTests/Mcp/FreeTextMaskingTests.cs`
**Applied fix:** Added `IbanText.MaskInText`, which finds account-number-shaped tokens in free text (two letters, two digits, then eleven to thirty letters or digits, with or without spaces, any case, no word boundary required so a glued prefix does not hide one) and replaces each with the same mask as the structured counterparty account. The service applies it where results are built, so every tool is covered: search row `description` and `counterparty_name`, the money_totals counterparty breakdown and counterparty groups, and the find_counterparties name and spellings. Counterparty references are still computed from the raw name, so a reference taken from a masked name keeps working as a filter. The regex has a 100 ms timeout; on timeout the text is replaced by the hidden marker rather than passed through. Short look-alikes (under 15 normalised characters) are left alone. `docs/mcp.md` now says what is masked.
**Trade-off noted:** when an account number written in four-character groups is directly followed by words, the matcher may take up to a few following characters with it. That over-masks (never under-masks).
**Tests added:** unit theories for unspaced, spaced lower-case, mixed-case, two-in-one-text and glued-prefix numbers, for text that must stay unchanged, and for null. An integration test over the real MCP client calls all four tools against a seeded ledger with synthetic numbers (unspaced, spaced lower case, mixed case, and one inside a counterparty name) and asserts none appears in any result and the surrounding text is kept. A second integration test checks a reference from a masked counterparty still filters totals.
**Status:** fixed.

### B-WR-02: "Going public" does not require removing temporary operator access

**Commit:** 9d41cad
**Files modified:** `docs/mcp.md`
**Applied fix:** Added a paragraph at the start of "Going public": delete every temporary operator login and SSH key on the ledger host, the reverse proxy host and the workstation (check `authorized_keys` and the SSH configuration on both hosts), then rerun `ledger-selfcheck`, and state that the selfcheck only catches sudo-capable logins on the ledger host, so a login without sudo, any SSH key and anything on the reverse proxy host must be removed and checked by hand. Plain language, no personal details.
**Tests added:** none (documentation only; the repository-rules lint passes).
**Status:** fixed.

### B-IN-02: The totals basis text claims "by booking date" when a fallback date was used

**Commit:** 1514081
**Files modified:** `Ledger.Service/Queries/LedgerQueryService.cs`, `Ledger.IntegrationTests/Mcp/TotalsQueryTests.cs`
**Applied fix:** The basis now reads "booked transactions by booking date (value date, transaction date or first-seen day where the bank gave none) in <zone>; pending reported separately; transfers between the household's own synced accounts excluded".
**Tests added:** the August totals test asserts the exact fallback wording.
**Status:** fixed.

### B-IN-03: The counterparty breakdown ranks by money out only

**Commit:** b86899c
**Files modified:** `Ledger.Domain/Queries/TotalsAggregator.cs`, `Ledger.UnitTests/Mcp/TotalsAggregatorTests.cs`
**Applied fix:** Counterparties are ordered by money out plus money in (total movement) descending, then money out descending, then name. The remainder row is still the sum of everything past the cap, so the parts add up exactly to the totals. The doc comment on the breakdown now states the ranking. Existing tie-break tests stay green.
**Tests added:** a unit test with 30 outflow counterparties plus a large income source and a mixed refund counterparty: the income source ranks first, the remainder merges the right counterparties, and money out, money in and counts all sum exactly to the totals.
**Status:** fixed.

### B-IN-06: Edge inputs near the maximum date and a non-IANA zone id fail with a generic error

**Commit:** 523adae
**Files modified:** `Ledger.Domain/Queries/PeriodResolver.cs`, `Ledger.Service/Queries/QueryTimeZone.cs` (new), `Ledger.Service/Queries/LedgerQueryService.cs`, `Ledger.Service/Mcp/LedgerTools.cs`, `Ledger.UnitTests/Mcp/PeriodResolverTests.cs`, `Ledger.UnitTests/Mcp/QueryTimeZoneTests.cs` (new), `Ledger.IntegrationTests/Mcp/QueryEdgeInputTests.cs` (new)
**Applied fix:** `PeriodResolver.Resolve` refuses an explicit end date after 9000-12-31 with the fixed message "Dates after 9000-12-31 are not accepted.", which keeps day, week and month slicing away from the calendar's end. A new `QueryTimeZone.Resolve` turns the configured zone into an IANA zone (a Windows name is converted, so the name passed to the database is always IANA) and refuses a blank or unknown zone with a fixed message naming the IANA form. The service uses it in all four queries and reports the resolved zone id in results. The overview tool now translates query refusals into a tool error like the other three. The zone was resolved in the query layer rather than at host startup because the startup validator lives in the sign-in and host files owned by the parallel fixer.
**Tests added:** unit tests for the last allowed date and the day after it, for a period ending on the maximum date, and for IANA, Windows, unknown, blank and null zone names. Integration tests over the real client for money_totals (grouped by day, week, month and none), search_transactions and find_counterparties with a period ending 9999-12-31, and for all four tools with a nonexistent and a blank configured zone.
**Status:** fixed.

### Documentation: `ledger-login confirm-totp` usage

**Commit:** aee0759
**Files modified:** `docs/mcp.md`, `docs/monitoring.md`
**Applied fix:** Every occurrence now reads `sudo ledger-login confirm-totp NAME`, with the current code entered at the prompt or piped on standard input, and notes that the code is never an argument. `docs/lxc-setup.md` does not mention the command.
**Tests added:** none (documentation only).
**Status:** fixed.

## Skipped Issues

None.

---

_Fixed: 2026-10-08_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
