---
phase: 02-automatic-ing-sync
plan: 06
subsystem: dashboards
tags: [grafana, dashboards-as-code, i18n, provisioning, postgres]
requires:
  - phase: 02-automatic-ing-sync (plan 04)
    provides: reporting.accounts and reporting.transactions views, grafana_reader role, synthetic ingestion test support
provides:
  - Ledger.Dashboards console tool (generate, check) with a typed Grafana model and one EN/NL translation file
  - Provisioned English and Dutch sync dashboards with a recent-transactions table and account selector
  - Dashboard provider path that matches where the installer copies the provisioning tree
  - Drift, translation, parity and read-only tests; lint assertions that the pinned Grafana loads both dashboards
affects: [later dashboard work, deploy installer, observability lint]
tech-stack:
  added: []
  patterns:
    - "Dashboards are C# definitions serialised deterministically (fixed property order, LF, no timestamps) and committed as JSON"
    - "One translation file; Translator throws on missing or empty keys and records used keys so tests can detect unused keys"
key-files:
  created:
    - Ledger.Dashboards/Ledger.Dashboards.csproj
    - Ledger.Dashboards/packages.lock.json
    - Ledger.Dashboards/Program.cs
    - Ledger.Dashboards/DashboardGenerator.cs
    - Ledger.Dashboards/Translator.cs
    - Ledger.Dashboards/translations.json
    - Ledger.Dashboards/Model/Dashboard.cs
    - Ledger.Dashboards/Model/Panels.cs
    - Ledger.Dashboards/Model/FieldConfig.cs
    - Ledger.Dashboards/Definitions/SyncDashboard.cs
    - deploy/provisioning/grafana/provisioning/dashboards/json/ledger-sync-en.json
    - deploy/provisioning/grafana/provisioning/dashboards/json/ledger-sync-nl.json
    - Ledger.UnitTests/Dashboards/DashboardGeneratorTests.cs
    - Ledger.IntegrationTests/Dashboards/DashboardQueryTests.cs
  modified:
    - Ledger.slnx
    - deploy/provisioning/grafana/provisioning/dashboards/ledger.yaml
    - build/lint/checks/60-observability.sh
    - Ledger.UnitTests/Ledger.UnitTests.csproj
    - Ledger.UnitTests/packages.lock.json
key-decisions:
  - "Panels serialise through a polymorphic base record with no discriminator; base properties carry JsonPropertyOrder so id, type and title lead each panel"
  - "Value-mapping and rename dictionaries use ordinal-sorted dictionaries so output bytes never depend on insertion order"
  - "The lint Grafana container now mounts the provisioning tree at /etc/grafana/provisioning, the installer's real location, so the provider path is exercised as shipped"
patterns-established:
  - "Adding a dashboard: write a definition in Ledger.Dashboards/Definitions, add its keys to translations.json in both languages, run generate, commit the JSON"
requirements-completed: [DASH-05, DASH-07]
duration: ~35min
completed: 2026-09-30
status: complete
actuals:
  tokens: 26000
  tasks: 2
  commits: 2
---

# Phase 2 Plan 06: Dashboard generator and first English/Dutch dashboard Summary

**C# dashboard generator producing byte-stable English and Dutch Grafana JSON from one definition and one translation file, provisioned into the Household Ledger folder and read through grafana_reader.**

## Performance

- **Tasks:** 2 (tracer plus tests), both committed atomically
- **Files created/modified:** 19

## Accomplishments

- `Ledger.Dashboards` console tool: `dotnet run --project Ledger.Dashboards -- generate | check`. `check` exits 1 and names stale or missing files.
- Sync dashboard (`ledger-sync-en`, `ledger-sync-nl`): account multi-select with All (default), 30-day default range in Europe/Amsterdam, recent-transactions table (at most 500 rows, newest first), translated column headers, pending rows coloured, unclear matches marked, two-decimal amounts, link to the other language, not editable.
- `ledger.yaml` provider path now `/etc/grafana/provisioning/dashboards/json`, which the installer populates by copying the whole provisioning tree (the old `/var/lib/grafana/...` path was never populated).
- `build/lint.sh observability` now also asserts both dashboards are listed in the Household Ledger folder and reported `provisioned: true` by the pinned Grafana 13.2.2. A negative run (bogus uid) failed as expected.
- Tests: 9 unit tests (drift byte-for-byte, determinism and LF, translation key parity and non-empty, no unused or unknown key, EN/NL structural parity, fixed uids and non-editable, reporting-only datasource and relations) and 1 integration test running every committed query as `grafana_reader` against a migrated, synthetically seeded database.
- Acceptance check done: deleting `match.unclear` from the nl object made the unit Dashboards run fail with the key named; reverted before committing.

## Task Commits

1. **Task 1 (tracer): generator, dashboards, provider fix, lint assertions** - `8e3b528`
2. **Task 2: drift, translation, parity and read-only tests** - `8ec02a7`

The tracer feedback gate ran (auto mode): `dotnet run ... check` and `build/lint.sh observability` both passed before expansion.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Lint Grafana could not see the dashboards at the corrected provider path**
- **Found during:** Task 1
- **Issue:** The lint container pointed `GF_PATHS_PROVISIONING` at `/repo/...`, so the provider path `/etc/grafana/provisioning/dashboards/json` did not exist there and no dashboard would load.
- **Fix:** Mount the repository provisioning tree read-only at `/etc/grafana/provisioning` and point `GF_PATHS_PROVISIONING` there, mirroring the installer. Comment updated.
- **Files modified:** build/lint/checks/60-observability.sh
- **Commit:** 8e3b528

**2. [Rule 3 - Blocking] Integration test cannot reference the generator's output-directory constant**
- **Found during:** Task 2
- **Issue:** The integration project has no reference to Ledger.Dashboards and its csproj is not in this plan's file list.
- **Fix:** The integration test holds the repository-relative dashboard directory as its own constant. It deliberately reads the committed JSON, not the generator.
- **Commit:** 8ec02a7

**3. [Rule 1 - Bug] Panel property order put id, type and title last**
- **Found during:** Task 1 first generation
- **Fix:** `JsonPropertyOrder` on the base panel properties.
- **Commit:** 8e3b528

## Known Stubs

None. Amounts and counts in tests are synthetic.

## Threat Flags

None. No new endpoints or trust boundaries; dashboards read only `reporting.*` through the read-only datasource (covered by T-02-06-01, -02 and -04 mitigations: reporting-only test, `editable` false plus drift test, `LIMIT 500` with the time filter).

## Issues Encountered

- The datasource is provisioned with `type: postgres`; dashboards reference `grafana-postgresql-datasource`, which is what Grafana 13.2.2 stores (as noted in the plan). The pinned Grafana loaded the dashboards without complaint, but a live panel render against a database was not exercised here (no Grafana-to-database path in the lint harness). Query correctness is covered by the integration test.

## Self-Check: PASSED

- All created files exist on disk and are committed; commits `8e3b528` and `8ec02a7` exist.
- `build/lint.sh observability`, `repo-rules` and `shell` pass; unit suite 76/76; integration Dashboards 1/1; `dotnet restore Ledger.slnx --locked-mode` exits 0.
