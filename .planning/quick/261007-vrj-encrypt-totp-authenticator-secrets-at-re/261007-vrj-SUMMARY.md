---
quick_id: 261007-vrj
type: quick
subsystem: auth
tags: [data-protection, identity, totp, operator-commands]
status: complete
key-files:
  created:
    - Ledger.Repository/Stores/LedgerUserStore.cs
    - Ledger.IntegrationTests/Security/AuthenticatorKeyProtectionTests.cs
    - Ledger.IntegrationTests/Infrastructure/AuthenticatorKeyStore.cs
  modified:
    - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
    - Ledger.Service/Cli/OperatorHost.cs
    - Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs
    - Ledger.Service/Pages/Account/Login.cshtml.cs
    - deploy/bin/ledger-login
    - deploy/bin/ledger-grants
    - deploy/tests/sandboxing-test.sh
    - Ledger.IntegrationTests/Mcp/OperatorCommandTests.cs
    - Ledger.IntegrationTests/Infrastructure/McpTestHost.cs
    - Ledger.IntegrationTests/Infrastructure/TestCertificates.cs
actuals:
  tokens: 14000
  tasks: 1
  commits: 1
---

# Quick 261007-vrj: Encrypt TOTP authenticator secrets at rest

Each login's authenticator secret is now stored encrypted with the app's Data Protection key ring (LedgerUserStore over the Identity user-only store), and the ledger-login / ledger-grants command host shares that database key ring and certificate instead of an ephemeral one.

## What changed

- `LedgerUserStore` overrides `SetAuthenticatorKeyAsync` / `GetAuthenticatorKeyAsync` with a dedicated protector purpose; registered through `AddLedgerLoginStores`, so the web host and the command host both use it.
- An undecryptable stored value (plaintext, foreign key ring, malformed) reads as a fresh random secret nobody holds, never logged. The login keeps asking for a second factor and no code can satisfy it.
- `OperatorHost` now calls `AddLedgerDataProtection`. The key ring lives in the database, so nothing is written to disk. The certificate is root:ledger 0640 and the command sandbox (`ProtectSystem=strict`, no hidden paths) can already read it, so no wrapper property changed; only header notes were added. `sandboxing-test.sh` now asserts the wrappers hide no path and keep the filesystem readable, and that the certificate is installed readable by the ledger group only.
- No recovery codes are generated or redeemed anywhere; a test asserts enrolment writes only the single authenticator-key token row.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] "Return null so the login fails closed" is fail-open in Identity's SignInManager**
- **Found during:** tests for a plaintext / foreign-key-ring stored value
- **Issue:** with a null authenticator key the authenticator provider cannot generate tokens, so `SignInManager.PasswordSignInAsync` finds no valid second-factor provider and completes a password-only sign-in (cookie issued, cookie handler redirects to the authorize request). The test reached the consent page with only the password.
- **Fix:** the store returns an unheld random secret for an undecryptable value (the provider list stays non-empty and every code fails). The login page additionally refuses a login whose authenticator secret is missing or unreadable before calling the sign-in manager, closing the same hole for a deleted token row.
- **Files modified:** Ledger.Repository/Stores/LedgerUserStore.cs, Ledger.Service/Pages/Account/Login.cshtml.cs (outside the plan's files_modified list)
- **Commit:** 3eb9648

**2. [Rule 3 - Blocking] Command host environment is Production, so the certificate setting is required**
- **Issue:** the in-process command host defaults to Production, where `AddLedgerDataProtection` requires `DataProtection:CertificatePath`; the web test host and the command host must also share one certificate to share one key ring.
- **Fix:** test infrastructure gained one shared self-signed certificate (`TestCertificates.SharedKeyRingSettings`), passed to both the web host (`McpTestHost` now forwards `DataProtection:` settings as startup environment) and the command runner.
- **Files modified:** McpTestHost.cs, TestCertificates.cs, OperatorCommandTests.cs (test infrastructure only)

## Verification

- `Category=Security` + `Category=OperatorCommands`: 22 passed.
- `dotnet test --solution Ledger.slnx`: 1011 total, 1009 passed, 2 skipped, 0 failed.
- `bash deploy/tests/sandboxing-test.sh`, `bash deploy/tests/oauth-commands-logic-test.sh`: all checks passed.
- `bash build/lint.sh`: all six checks pass (one shellcheck SC2016 in the new sandbox assertion was fixed first).

## Notes

- Existing plaintext authenticator values read as unheld secrets after this change; per the plan nothing is enrolled on the live host yet, so no data migration was written. Any login enrolled before deploy needs `ledger-login reset-totp`.
- A command run needs the certificate path and password from the env file, which `ledger-login` / `ledger-grants` already load; running the command host in a non-Production environment without a certificate also works.

## Known Stubs

None.

## Self-Check: PASSED

Created files exist, commit 3eb9648 exists on the branch.
