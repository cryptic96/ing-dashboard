---
quick_id: 261008-evv
status: complete
completed: 2026-10-08
key-files:
  modified:
    - Ledger.IntegrationTests/Infrastructure/LedgerWebApplicationFactory.cs
actuals:
  tasks: 1
  commits: 1
---

# Quick Task 261008-evv: Remove the free-port race in the integration test hosts

Test hosts now bind their loopback sockets on port 0 and keep them bound until Kestrel takes them over, so no other host can claim a port in between.

## How the ports are obtained now

- `LedgerWebApplicationFactory` binds two `Socket`s to `127.0.0.1:0` in its constructor. The operating system assigns the ports, which are read back into `ApiPort` and `OpsPort`. Previously a throwaway `TcpListener` read a free port and closed it again, which left the window for another host to take it.
- The sockets are not closed. The factory sets `SocketTransportOptions.CreateBoundListenSocket`, so when Kestrel opens the `Api` and `Ops` endpoints it receives the already-bound sockets (matched by port) instead of binding anew.
- Unused sockets are released if startup fails and on dispose. Sockets Kestrel took over are owned and closed by Kestrel.
- `GetFreeLoopbackPort` is removed. No test or host helper pre-picks a port any more.

## Deviation from the plan wording

The plan suggested configuring the endpoints with port 0 and reading the bound addresses back from `IServerAddressesFeature` after start. That does not work without changing production code: `Program.cs` reads the ops port from `Kestrel:Endpoints:Ops:Url` before the host starts, and the health and metrics middleware match requests by that exact port. A configured port 0 would never match. Holding the sockets bound achieves the same guarantee, with the port still known before start, and leaves production code and the endpoint names untouched. The public shape (`ApiPort`, `OpsPort`, `CreateApiClient`, `CreatePinnedHttpsApiClient`, `CreateOpsClient`, `backendCertificate`) is unchanged, so `McpTestHost`, `EndpointBoundaryTests` and the rest keep working with no edits.

## Verification

- `dotnet test --solution Ledger.slnx` three times in a row: 1139 tests, 0 failed, 1137 passed, 2 skipped each time.
- `build/lint.sh` (all checks): PASS.

## Commits

- df423d6: test(quick-261008-evv): hold the test host ports bound until Kestrel takes them over
