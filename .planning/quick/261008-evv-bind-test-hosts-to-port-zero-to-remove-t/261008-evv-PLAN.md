---
quick_id: 261008-evv
type: quick
autonomous: true
files_modified:
  - Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs
  - Ledger.IntegrationTests/Infrastructure/McpTestHost.cs
  - Ledger.IntegrationTests/ (callers of ApiPort / OpsPort only, if their shape changes)
---

<objective>
Remove the free-port race in the integration test hosts. LedgerWebApplicationFactory.GetFreeLoopbackPort() opens a listener on
port 0, reads the port, closes it, and the real Kestrel host binds that port moments later; under parallel test execution another
host can take the port in between, failing a random test with "address already in use". It has failed the v0.3.0 release build
once (LogRedactionTests) and showed up during development earlier.
</objective>

<tasks>
<task type="auto">
  <name>Task 1: Test hosts bind to port 0 and read back the ports Kestrel actually got</name>
  <action>
    Configure the Api and Ops endpoints of the test host with port 0 (127.0.0.1:0) and, after the server has started, read the
    bound addresses from IServer's IServerAddressesFeature (or the equivalent for how the factory starts Kestrel) to set ApiPort
    and OpsPort. Keep the existing public shape (ApiPort/OpsPort, CreateClient behaviour, the backendCertificate HTTPS option)
    so callers keep working; if a caller needs the port before start, restructure it to read after start. Remove
    GetFreeLoopbackPort. Keep the Kestrel endpoint names the production configuration uses so the endpoint-mapping code paths
    under test stay the same.
  </action>
  <verify>
    Run the full `dotnet test --solution Ledger.slnx` three times in a row with no failures; run build/lint.sh (all checks).
    State in the SUMMARY how the ports are now obtained and confirm no test still pre-picks a port.
  </verify>
  <done>No test host pre-selects a port; full suite green three consecutive times.</done>
</task>
</tasks>

<rules>
- No `//` comments; `///` XML docs only. No planning references outside .planning/ (code, test names, logs).
- Tests use the existing local PostgreSQL container via user-secrets; no Testcontainers; never tear it down. Never kill processes by name.
</rules>
