---
phase: 02-automatic-ing-sync
plan: 01
subsystem: testing
tags: [xunit-v3, microsoft-testing-platform, dst, timezone, ci]

requires: []
provides:
  - Both test projects run on xunit.v3 4.0.1 under Microsoft.Testing.Platform (opt-in via global.json)
  - Ledger.Domain.Ingestion.SyncSchedule (InstantFor, LocalDate, IsNonexistentLocalTime) for Europe/Amsterdam wall-clock scheduling
  - Microsoft.Extensions.TimeProvider.Testing available in both test projects
  - CI and release workflows use dotnet test --solution Ledger.slnx --no-restore
affects: [02-automatic-ing-sync]

tech-stack:
  added: [xunit.v3 4.0.1, Microsoft.Extensions.TimeProvider.Testing 10.10.0]
  removed: [xunit.runner.visualstudio, Microsoft.NET.Test.Sdk]
  patterns:
    - "Trait filtering: dotnet test --project <csproj> --filter-trait \"Category=<name>\""
    - "Filtered solution-wide runs need --ignore-exit-code 8"
    - "Test projects set OutputType Exe explicitly (was supplied by Microsoft.NET.Test.Sdk)"

key-files:
  created:
    - Ledger.Domain/Ingestion/SyncSchedule.cs
    - Ledger.UnitTests/Ingestion/SyncScheduleTests.cs
  modified:
    - global.json
    - Ledger.UnitTests/Ledger.UnitTests.csproj
    - Ledger.UnitTests/packages.lock.json
    - Ledger.IntegrationTests/Ledger.IntegrationTests.csproj
    - Ledger.IntegrationTests/packages.lock.json
    - .github/workflows/ci.yml
    - .github/workflows/release.yml

key-decisions:
  - "OutputType Exe added to both test projects: xunit.v3 4.x rejects non-executable test projects and Microsoft.NET.Test.Sdk (which used to set it) was removed"
  - "No trait filter in either workflow: the gate keeps running every category"

patterns-established:
  - "Schedule math uses Unspecified-kind DateTime with TimeZoneInfo.ConvertTimeToUtc, never UTC date arithmetic"

requirements-completed: [INGEST-01]

duration: 25min
completed: 2026-09-30
status: complete
actuals:
  tokens: 9000
  tasks: 2
  commits: 2
---

# Phase 2 Plan 01: Test platform migration and Amsterdam schedule math Summary

**Both test projects moved to xunit.v3 4.0.1 on Microsoft.Testing.Platform, CI and release gate switched to the new `dotnet test --solution` syntax, proven with DST-safe Europe/Amsterdam `SyncSchedule` conversions.**

## Performance

- **Duration:** ~25 min
- **Tasks:** 2
- **Files modified:** 9

## Accomplishments

- Opted in to Microsoft.Testing.Platform in `global.json`; xunit.v3 4.0.1 in both test projects; VSTest-only packages removed; lock files regenerated and `dotnet restore --locked-mode` passes.
- `SyncSchedule` (test-first): 06:30 on 2026-10-25 is 05:30Z, 06:30 on 2027-03-28 is 04:30Z, 02:30 on 2027-03-28 is recognised as non-existent, local dates computed in Europe/Amsterdam across midnight and with `FakeTimeProvider`. 8 tests under `Category=Scheduler`.
- CI and release workflows changed by exactly one line each; every category still runs and the bundle test still fails (not skips) when `CI=true`.
- Full suite proven locally the way CI runs it: release bundle packaged with `build/package-release.sh`, `LEDGER_EFBUNDLE` set, 81 tests, 0 skipped (bundle test ran). `build/lint.sh repo-rules` and `build/lint.sh workflows` pass.

## Task Commits

1. **Task 1 (tracer): platform switch + SyncSchedule** - `989aa9d` (feat)
2. **Task 2: CI and release gate syntax** - `cfe9fb7` (ci)

## Filter conventions confirmed for later plans

- Single project: `dotnet test --project Ledger.UnitTests/Ledger.UnitTests.csproj --filter-trait "Category=<name>"` (Scheduler: 8 of 46 ran).
- Filtered solution-wide runs **need `--ignore-exit-code 8`**: without it, `--filter-trait "Category=DatabaseRoles"` exits 8 because the unit-test project has zero matching tests ("Zero tests ran"); with it the run exits 0 (4 tests passed).
- Unfiltered full suite: `dotnet test --solution Ledger.slnx --no-restore`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Test projects must be executable under xunit.v3 4.x**
- **Found during:** Task 1 (first build after the package change)
- **Issue:** xunit.v3.core.mtp-v2 errors with "xUnit.net v3 test projects must be executable"; `OutputType Exe` was previously supplied by the removed Microsoft.NET.Test.Sdk.
- **Fix:** Added `<OutputType>Exe</OutputType>` to both test csproj files.
- **Files modified:** Ledger.UnitTests/Ledger.UnitTests.csproj, Ledger.IntegrationTests/Ledger.IntegrationTests.csproj
- **Commit:** 989aa9d

### Tracer feedback gate

Auto mode was not active, so the interactive tracer gate would normally stop for a human check of the tracer slice. The tracer's automated verify (filtered Scheduler run, unit suite, integration suite, solution-wide run, locked restore, repo-rules lint) all passed end-to-end before expansion, and the project config sets `human_verify_mode: end-of-phase`, so the human check is deferred to the phase-level verification rather than blocking a parallel worktree agent.

## Follow-ups for when the milestone branch merges to main

- Re-enable Dependabot updates for xunit.v3 by commenting `@dependabot unignore xunit.v3` on a Dependabot pull request.
- `xunit.runner.visualstudio` no longer needs updates because it was removed (no unignore needed).
- The todo `.planning/todos/pending/2026-09-28-migrate-tests-to-microsoft-testing-platform-for-xunit-v4.md` is now fulfilled by this plan; the orchestrator can move it to completed.

## Known Stubs

None.

## Threat Flags

None. No new network endpoints, auth paths or trust-boundary schema changes.

## Issues Encountered

None beyond the deviation above.

## Self-Check: PASSED

- Files present: global.json, SyncSchedule.cs, SyncScheduleTests.cs, both csproj and lock files, both workflows.
- Commits present: 989aa9d, cfe9fb7.
