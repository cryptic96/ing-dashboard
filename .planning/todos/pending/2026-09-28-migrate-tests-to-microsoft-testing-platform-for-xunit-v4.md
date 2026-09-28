---
created: 2026-09-28T17:22:07.560Z
title: Migrate tests to Microsoft.Testing.Platform for xunit v4
area: testing
severity: minor
files:
  - global.json
  - Ledger.UnitTests/Ledger.UnitTests.csproj
  - Ledger.IntegrationTests/Ledger.IntegrationTests.csproj
  - Ledger.UnitTests/packages.lock.json
  - Ledger.IntegrationTests/packages.lock.json
  - .github/workflows/ci.yml
  - .github/workflows/release.yml
---

## Problem

Dependabot's update of xunit.v3 from 3.2.2 to 4.0.1 failed CI in both test projects:

> Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later. If you use dotnet test, you should opt-in to the new dotnet test experience. (https://aka.ms/dotnet-test-mtp-error)

xunit.v3 4.x runs on Microsoft.Testing.Platform 2, which on the .NET 10 SDK requires `dotnet test` to use the new test runner. Both xunit PRs (xunit.v3 4.x and xunit.runner.visualstudio 4.x) were closed with `@dependabot ignore this major version`, so without this migration the project silently stays on xunit 3.x and stops receiving xunit updates once 3.x is unmaintained.

Everything works today on xunit 3.x (all tests and CI green). Do this after go-live, not while the first release is being proven: it changes CI and the release workflow.

## Solution

Via `/gsd-quick` on a branch from the milestone branch:

1. Opt in to the new runner: `global.json` → `"test": { "runner": "Microsoft.Testing.Platform" }`.
2. Upgrade xunit.v3 to the latest 4.x in both test projects; drop `xunit.runner.visualstudio` (and anything else VSTest-only) if no longer needed; regenerate both `packages.lock.json` files (CI restores in locked mode).
3. Update every `dotnet test` invocation to the new syntax: `.github/workflows/ci.yml`, `.github/workflows/release.yml`, and any script. Replace VSTest `--filter "Category=..."` with xunit's trait filter options (categories in use: Health, DataProtectionRestart, DataProtection, Migrations, DatabaseRoles, ApiKeyCli, ApiAuth, LogRedaction, Configuration).
4. Keep `LEDGER_EFBUNDLE` and `ConnectionStrings__TestAdmin` handling working. Integration tests use the user's own local PostgreSQL container via dotnet user-secrets (never print the value, never Testcontainers).
5. Verify: package the release bundle, run the full suite locally with `LEDGER_EFBUNDLE` set, run `build/lint.sh`, and get CI green on the PR.
6. After merging, re-enable updates with `@dependabot unignore xunit.v3` and `@dependabot unignore xunit.runner.visualstudio` (or equivalent).
