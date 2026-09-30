# REST API authentication

Every REST endpoint requires a key, even when the request comes from inside the home network. The one exception is the bank link callback described under "Bank link" below, which the bank's redirect reaches in your browser and which is protected by a one-time link instead.

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

The ledger reads your bank accounts through a bank data provider. You link them once, by approving a consent in your bank app, and renew that consent when it is about to expire. Every call below is made with your key, except the callback the bank redirects your browser to. Ready-to-run requests are in `docs/bank-link.http`.

Bank linking only works when a provider is configured with the `Ingestion:Provider` setting and `BankLink:RedirectUrl` holds the https address of the callback. Until then the endpoints answer `503` with a message that bank linking is not configured.

### Linking

1. **Start the link.** `POST /api/v1/bank/connections/link` returns an `authorizationUrl` and the time it stops being valid.
2. **Approve it.** Open the address in a browser on the home network or over the VPN and approve the consent in your bank app.
3. **Callback.** The bank sends your browser to `GET /api/v1/bank/callback`. The page only says how many accounts were found. Nothing else is shown, and every failure looks the same on purpose.
4. **Find the connection.** `GET /api/v1/bank/connections` lists every connection with its `connectionKey`, its `consentState` (`linked`, `expiring`, `expired`, `revoked` or `superseded`) and the whole days left.
5. **List the accounts.** `GET /api/v1/bank/connections/{connectionKey}/accounts` shows each account with a masked IBAN. Nothing is synced yet.
6. **Select the accounts.** `PUT /api/v1/bank/connections/{connectionKey}/accounts` takes the accounts to sync and the name to show for each, for example `{"accounts":[{"accountKey":"...","displayName":"Joint","sync":true}]}`. Do this straight away: the first sync starts as soon as you confirm the selection and reads as much history as the bank will give, which is typically only available for about an hour after approval. The response reports `firstSync` as `queued` or `not-needed`. Accounts you do not select are recorded but never fetched.

### Renewing and revoking

- `POST /api/v1/bank/connections/{connectionKey}/renew` starts a new consent through the same steps. After approval the accounts keep their keys, names, selection and all their history, the old connection becomes `superseded`, and a sync with the longest available history runs. Renew when the consent is `expiring`.
- `DELETE /api/v1/bank/connections/{connectionKey}` ends the consent at the provider and marks the connection `revoked`. If the provider cannot end it, the call answers `502` and nothing changes.

### How the callback is protected

The callback is the only endpoint that answers without a key, because the bank redirects your browser to it. It only accepts the one-time link created by your own start call: a random 256-bit value that is stored only as a hash, works once and expires after 15 minutes. A reused, expired, cancelled or unknown link, and a failed code exchange, all get the same short message and reveal no account detail, code or identifier. The responses are never cached and never send a referrer.

Linking works only while the browser is on the home network or connected over the VPN, because the callback is served only through the internal route. It is never reachable from the internet.
