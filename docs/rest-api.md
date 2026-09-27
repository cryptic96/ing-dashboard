# REST API authentication

Every REST endpoint requires a key, even when the request comes from inside the home network. There is no endpoint that answers without one.

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
