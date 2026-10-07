# REST API authentication

Every REST endpoint requires a key, even when the request comes from inside the home network. The one exception is the bank link callback described under "Bank link" below, which the bank's redirect reaches in your browser and which is protected by a one-time link instead.

API keys work only on the REST API. The MCP endpoint that Claude connects to accepts only OAuth tokens issued after a sign-in, and the REST API never accepts those tokens. The two ways in do not overlap: a key cannot be used on the MCP endpoint, and a Claude connection cannot call the REST API. How Claude is connected is described in `docs/mcp.md`.

## Creating a key

Keys are created, listed and revoked with the `ledger-apikey` command on the server, run as the operator:

```
sudo ledger-apikey create grafana
sudo ledger-apikey list
sudo ledger-apikey revoke grafana
```

Each key gets its own name (for example `grafana`, `operator` or the name of a client application), so a leaked or unneeded key can be revoked on its own without affecting any other client.

The token is printed exactly once, at creation time, and cannot be retrieved again afterward. Store it immediately in a password manager or in the consuming client's secure configuration.

## Sending the key

Send the token in the `X-Api-Key` header on every request:

```
curl -H "X-Api-Key: <the token from ledger-apikey create>" https://ledger-api.example.com/api/v1/status
```

A request without the header, or with an invalid, malformed or revoked key, is rejected with a 401 response.

## If a key leaks

Revoke the leaked key immediately with `ledger-apikey revoke <name>`, then create a fresh key with the same name and update the affected client with the new token. Revocation takes effect on the very next request.

## Bank link

The ledger reads your bank accounts through a bank data provider. You link them once, by approving a consent in your bank app, and renew that consent when it is about to expire. Every call below is made with your key, except the callback the bank redirects your browser to. Ready-to-run requests are in `docs/bank-link.http`, and the one-time setup on the host (registering the application, generating the key, linking, renewing and revoking) is walked through in `docs/bank-link.md`.

Bank linking only works when a provider is configured with the `Ingestion:Provider` setting and `BankLink:RedirectUrl` holds the https address of the callback. Until then the endpoints answer `503` with a message that bank linking is not configured.

### Linking

1. **Start the link.** `POST /api/v1/bank/connections/link` returns an `authorizationUrl` and the time it stops being valid.
2. **Approve it.** Open the address in a browser on the home network or over the VPN and approve the consent in your bank app.
3. **Callback.** The bank sends your browser to `GET /api/v1/bank/callback`. The page says how many accounts were found and tells you to select them now, because the bank returns the full history only for about an hour after approval. Nothing else is shown, and every failure looks the same on purpose.
4. **Find the connection.** `GET /api/v1/bank/connections` lists every connection with its `connectionKey`, its `consentState` (`linked`, `expiring`, `expired`, `revoked` or `superseded`) and the whole days left. A connection that is still waiting for its account selection also shows `selectionPending: true` and a `hint` that repeats the advice to select now; both clear once accounts are selected.
5. **List the accounts.** `GET /api/v1/bank/connections/{connectionKey}/accounts` shows each account with a masked IBAN. Nothing is synced yet.
6. **Select the accounts.** `PUT /api/v1/bank/connections/{connectionKey}/accounts` takes the accounts to sync and the name to show for each, for example `{"accounts":[{"accountKey":"...","displayName":"Joint","sync":true}]}`. Do this straight away: the first sync starts as soon as you confirm the selection and reads as much history as the bank will give, which is only available for about an hour after approval and cannot be recovered later. If the hour has passed, select the accounts and then renew the connection to get another full-history window. The first sync is never held back by the allowance for unattended calls. The response reports `firstSync` as `queued` or `not-needed`. Accounts you do not select are recorded but never fetched, which is why nothing is read before you choose. Linking again with accounts that were selected before keeps that selection and queues the first sync without this step.

### Renewing and revoking

- `POST /api/v1/bank/connections/{connectionKey}/renew` starts a new consent through the same steps. After approval the accounts keep their keys, names, selection and all their history, the old connection becomes `superseded`, and a sync with the longest available history runs. Renew when the consent is `expiring`. If that sync cannot be queued, the callback page says so and a sync request starts it. Accounts the renewal exposes for the first time are unselected until you select them.
- `DELETE /api/v1/bank/connections/{connectionKey}` ends the consent at the provider and marks the connection `revoked`. If the provider cannot end it, the call answers `502` and nothing changes.

### Syncing

Once accounts are selected, the ledger syncs them by itself. You never have to trigger anything.

- **Every morning.** Each active connection with at least one selected account syncs once a day at 06:30 Amsterdam time. A process that starts after that time and has not synced yet that day catches up at once. The time and zone are set with `Ingestion:ScheduleLocalTime` (HH:mm) and `Ingestion:TimeZone`.
- **One retry.** When the morning sync fails for a temporary reason, it is retried once, no earlier than four hours after it finished and only on the same day (`Ingestion:RetryDelayHours`). A rate limit from the bank, a used-up call budget, a consent the bank no longer accepts or rejected application credentials are never retried that day. A consent the bank no longer accepts also marks the connection as expired straight away, so you see it in the connection list.
- **A call budget per account.** Banks only allow a few unattended calls per account per day. The ledger writes every call to a call ledger before it is sent and allows at most `Ingestion:BackgroundCallsPerDay` (default 12) unattended calls per account in the trailing 24 hours (`Ingestion:QuotaWindow` can switch this to the local calendar day). A sync that would go over the budget stops before calling and is recorded as quota exhausted.
- **Nothing is silent.** Every run, successful or not, is recorded with how it ended and the provider's short error code. A run that a restart interrupted is marked abandoned when the service starts, whether or not the scheduler is on, and counts as a temporary failure for that day's retry.
- **Only the schedule and your own requests fetch from the bank.** Reading connections or accounts, the health check and the metrics never start a sync, so opening a dashboard can never use up a call.

#### Syncing right now

`POST /api/v1/bank/sync` queues a sync of the connection and answers `202` with `{"status":"queued"}`. Use it after renewing a consent or when you want fresh data before the next morning. With several active connections, add `?connectionKey=...`; without it the only active connection is used.

The request carries your client address and User-Agent on to the bank, so the bank treats it as attended access and it spends none of the unattended allowance. If that is switched off with `Ingestion:PsuHeadersOnOperatorSyncs`, the call counts as an unattended one and is refused with `429` when it would use an account's last remaining call of the day. Repeated requests are collapsed: while a sync of the connection is waiting to run, another request answers `409`. Other answers: `409` when there is no active connection, no account is selected, a sync is already running or queued or the consent has ended, `404` for an unknown connection key and `503` when bank linking is not configured.

### How the callback is protected

The callback is the only endpoint that answers without a key, because the bank redirects your browser to it. It only accepts the one-time link created by your own start call: a random 256-bit value that is stored only as a hash, works once and expires after 15 minutes. A reused, expired, cancelled or unknown link, and a failed code exchange, all get the same short message and reveal no account detail, code or identifier. The responses are never cached and never send a referrer.

Linking works only while the browser is on the home network or connected over the VPN, because the callback is served only through the internal route. It is never reachable from the internet.
