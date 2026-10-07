---
quick_id: 261007-vrj
type: quick
autonomous: true
files_modified:
  - Ledger.Repository/Stores/LedgerUserStore.cs
  - Ledger.Repository/RepositoryServiceCollectionExtensions.cs
  - Ledger.Service/Cli/OperatorHost.cs
  - Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs
  - deploy/bin/ledger-login
  - deploy/bin/ledger-grants
  - deploy/tests/sandboxing-test.sh
  - Ledger.IntegrationTests/Security/AuthenticatorKeyProtectionTests.cs
  - Ledger.IntegrationTests/Mcp/OperatorCommandTests.cs
---

<objective>
Encrypt each login's TOTP authenticator secret at rest with the app's existing Data Protection key ring, so a copy of the
database or a database backup without the key ring (and its certificate) cannot be used to generate valid one-time codes.

Found during phase 03 execution: ASP.NET Core Identity stores the authenticator key as plain text in identity_user_tokens
(UserStore.SetAuthenticatorKeyAsync -> SetTokenAsync). Nothing is enrolled on the live host yet, so no data migration is needed.
User decision: fix before the phase 03 release.
</objective>

<context>
- Ledger.Repository/RepositoryServiceCollectionExtensions.cs: AddLedgerLoginStores() = AddEntityFrameworkStores<LedgerDbContext>() + AddDefaultTokenProviders().
- Ledger.Service/OAuth/OAuthServiceCollectionExtensions.cs: AddLedgerOAuthCore -> AddIdentityCore<LedgerUserEntity>(...).AddLedgerLoginStores(); shared by the web host and the operator command host.
- Ledger.Service/Security/DataProtectionSetup.cs: AddLedgerDataProtection(configuration, environment) — application name "HouseholdLedger", keys persisted to the database, protected with a certificate when DataProtection:CertificatePath is set (required in Production).
- Ledger.Service/Cli/OperatorHost.cs: the ledger-login / ledger-grants command host currently uses UseEphemeralDataProtectionProvider(). Once the authenticator key is protected, this MUST switch to the real key ring, otherwise secrets written by `ledger-login` (enrol, reset-totp) cannot be read by the web host and vice versa.
- deploy/bin/ledger-login and ledger-grants run the commands through sandboxed systemd-run as the ledger user with the env file; deploy/tests/sandboxing-test.sh asserts their hardening.
- Ledger.Service/Pages/Account/Totp.cshtml.cs verifies codes through the authenticator token provider, which reads the key via UserManager.GetAuthenticatorKeyAsync -> store.GetAuthenticatorKeyAsync.
</context>

<tasks>
<task type="auto" tdd="true">
  <name>Task 1: Protect the authenticator key in the login store, and give the operator commands the real key ring</name>
  <action>
    1. Add Ledger.Repository/Stores/LedgerUserStore.cs: a subclass of the Identity EF UserStore with the same generic arguments
       AddEntityFrameworkStores currently resolves for LedgerUserEntity/LedgerDbContext. Inject IDataProtectionProvider and create
       one protector with a fixed, descriptive purpose string. Override SetAuthenticatorKeyAsync to store Protect(key) and
       GetAuthenticatorKeyAsync to return Unprotect(stored). When the stored value cannot be unprotected (CryptographicException,
       e.g. a plaintext or foreign-key-ring value), return null so the login fails closed (it then behaves as having no second
       factor until the operator runs reset-totp); never log the stored or decrypted value.
    2. Register it in AddLedgerLoginStores so both the web host and the operator host use it (AddUserStore<LedgerUserStore>() or
       an equivalent replacement of IUserStore<LedgerUserEntity>), keeping AddDefaultTokenProviders. Update the XML doc.
    3. Recovery codes: confirm the app never generates or redeems them. If it does, protect them the same way; if not, leave a
       test asserting no recovery-code token row is ever written by enrolment.
    4. Ledger.Service/Cli/OperatorHost.cs: replace UseEphemeralDataProtectionProvider() with AddLedgerDataProtection(configuration,
       environment) from DataProtectionSetup, so the commands share the database key ring and certificate. Update the XML doc.
       Check deploy/bin/ledger-login and ledger-grants: the sandbox must allow reading the certificate file configured by
       DataProtection:CertificatePath (from the env file) and nothing more; adjust ReadOnlyPaths/BindReadOnlyPaths or similar only
       if needed, and extend deploy/tests/sandboxing-test.sh to assert it. Keep every existing hardening property.
  </action>
  <verify>
    Integration tests (Category=OAuth or a new Category=Security, matching neighbours), in AuthenticatorKeyProtectionTests:
    - After enrolment (CLI path and McpTestHost.CreateLoginAsync path), the raw identity_user_tokens value for the authenticator
      key is NOT the base32 secret and does not contain it; UserManager.GetAuthenticatorKeyAsync returns the original secret.
    - A full sign-in with password + current code still works end to end (existing SignInTests / OAuthFlowTests stay green).
    - A login whose stored authenticator value was written by a different key ring (or is plaintext) cannot complete the
      second factor and gets the generic failure message; no exception text or secret reaches logs.
    - OperatorCommandTests: a login enrolled by the command host can sign in through the web host (proves a shared key ring),
      and reset-totp through the command host produces a secret the web host verifies.
    Run: Category=OAuth, Category=OperatorCommands, the new tests, bash deploy/tests/sandboxing-test.sh,
    bash deploy/tests/oauth-commands-logic-test.sh, full `dotnet test --solution Ledger.slnx`, full build/lint.sh.
  </verify>
  <done>Authenticator secrets are stored only in protected form, both hosts share the key ring, all tests and lint pass.</done>
</task>
</tasks>

<rules>
- No `//` comments; `///` XML docs only. No planning references (quick ids, phase/plan numbers, requirement keys, planning doc
  names) outside .planning/ — code, test names, logs, docs, scripts. Commit messages may carry the quick id.
- Secrets (authenticator keys, codes, passwords) never appear in logs, exceptions or command output.
- Tests use the existing local PostgreSQL container via user-secrets; no Testcontainers; never tear the container down.
</rules>
