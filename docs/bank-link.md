# Linking the bank accounts

This is the operator's runbook for connecting the household's bank accounts to the ledger, keeping the connection alive and ending it. Every hostname, address and identifier below is a placeholder: replace them with your own values and never write real ones into this repository.

## What the bank link is

The ledger reads transactions and balances through an account-information aggregator that is licensed to talk to the bank. The link is **read only**: the application has no way to start a payment or move money, and the aggregator application it uses is registered for account information only. You approve the link once in the bank app. After that the ledger fetches new transactions every morning without you.

The link is a consent with an end date. When the date gets close you renew it with a few taps in the bank app, and the history and the account selection are kept.

## One-time setup

You need an account with the aggregator, a few minutes on the host and the bank app on your phone.

1. **Register the production application.** In the aggregator's control panel create an application in restricted production mode. This mode is free for personal use and only lets you link accounts you hold yourself. Do not enable payment initiation. Note the application id the panel shows: a GUID in the form `00000000-0000-0000-0000-000000000000`.
2. **Generate the key on the host.** The key that signs the application's requests is created on the host and never travels. As root on the host run:

   ```bash
   sudo ledger-bank-key generate
   sudo ledger-bank-key show-certificate
   ```

   `generate` creates a password-protected private key under `/etc/ledger` and writes its path and password into the application's environment file. It prints the certificate and the public key, never the password. `show-certificate` prints them again. Upload the public key or the certificate, whichever the control panel asks for, to the application. The private key is never uploaded.
3. **Register the callback address.** In the control panel add the redirect address of your ledger API, for example `https://ledger-api.example.com/api/v1/bank/callback`. It must be the address your browser reaches on the home network or over the VPN, and it must be `https`. Nothing from the internet needs to reach it.
4. **Link the accounts in the control panel.** In the control panel, link the joint account and, if the bank offers it, the savings account to the application. Only link accounts that belong to the household. Never link a personal account of one partner.
5. **Tell the ledger about the application.** On the host run the command below with your own application id and callback address, then restart the service:

   ```bash
   sudo ledger-bank-key configure \
     --application-id 00000000-0000-0000-0000-000000000000 \
     --redirect-url https://ledger-api.example.com/api/v1/bank/callback
   sudo systemctl restart ledger.service
   ```

   `configure` checks both values, selects the aggregator as the transaction source and stores the callback address. After the restart the service refuses to start in production if the application id is missing or not a GUID, if the key file is missing or if its password is empty. The error names only the setting, never its value.
6. **Keep the key safe.** Copy the key file `/etc/ledger/enablebanking-key.pem` and the environment file `/etc/ledger/ledger.env` into the password manager now. Database backups never contain either of them, on purpose, so losing the host without a copy means generating a new key and registering it again. Never put either file in a repository, a chat or an email.

The settings these steps write are listed, with placeholder values, in `deploy/ledger.env.example`. More background on the key files is in `docs/lxc-setup.md`.

## Linking and selecting

Do these two steps back to back. The bank hands out the full transaction history only for a short time right after you approve the consent, and a later sync can only reach back about 90 days. The first sync starts the moment you confirm the account selection.

The requests are ready to run in `docs/bank-link.http`; every endpoint is described in `docs/rest-api.md`.

1. Start the link. The answer holds an address to open.
2. Open that address in a browser on the home network or over the VPN and approve the consent in the bank app. Your browser is sent back to the callback, which only tells you how many accounts were found.
3. List the connections to find the new connection key, then list its accounts. The accounts are shown with a masked account number.
4. Select the accounts to sync and give each a display name. Accounts you do not select are recorded but never fetched.

The connection list shows the consent state (`linked`, `expiring`, `expired`, `revoked` or `superseded`) and the whole days left.

## What happens every morning

- Each connection with at least one selected account syncs once a day at 06:30 Amsterdam time. A service that starts later that day and has not synced yet catches up at once.
- If the sync fails for a temporary reason it is retried once, no earlier than four hours later and only on the same day. A rate limit, a used-up call budget, a consent the bank no longer accepts and rejected application credentials are never retried that day.
- The ledger counts every call it makes to the bank per account. A sync that would exceed `Ingestion:BackgroundCallsPerDay` (12 by default) in the trailing 24 hours stops before calling. A normal morning sync costs about two calls per account.
- Between mornings you can ask for fresh data with a sync now request. It passes your client address on to the bank, so it counts as attended access and spends none of the unattended allowance.

Nothing about a run is silent: every run is recorded with how it ended and the provider's short error code.

## Renewing

The consent lasts up to 180 days. Two alerts warn you, at 14 days and at 7 days before it ends; a third fires when it has ended. When the first one arrives:

1. Start a renewal for the connection and approve it in the bank app, the same way as when linking.
2. The accounts keep their keys, names, selection and all their history. The old connection becomes `superseded` and a sync with the longest available history runs straight away.

If the consent is allowed to end, no new transactions arrive until you renew. Because the renewal is approved right then, its sync asks for the longest history the bank offers and fills the gap.

## Revoking

To end the link, revoke the connection through the ledger. The consent is ended at the aggregator and the connection is marked `revoked`; renewing a revoked connection is blocked, so link again to start over. Also check the bank app's overview of granted consents to confirm that the aggregator's access is gone. Removing the application in the control panel and deleting the key file on the host ends everything for good.

## When something fails

- Failures show up as alerts; what each alert means and what to do is in `docs/monitoring.md`.
- The sync status of an account, including its last success and whether its balance agrees with the bank, is on the household dashboards.
- A sync that stops with a rejected application means the aggregator refused the key or the application id. Check the application in the control panel and the three bank settings in the environment file.
- A sync that stops with a rejected consent means the bank no longer accepts it: renew it.
- The logs never contain the key, its password, a session identifier or an authorisation code. If you find one, treat the secret as leaked, revoke the connection and generate a new key.

## What if the savings account is not offered

Some banks keep savings accounts outside the account-information interface, so they are not offered when you link. If the control panel and the account list show only the joint current account, the ledger ships with the joint account alone:

- Transfers to and from the savings account stay visible as transactions on the joint account.
- The savings balance and the interest paid on it are missing from the ledger.
- Direct withdrawals from the savings account are missing, because they never touch the joint account.

Coverage can be added later, for example through a second provider, without changing what is already stored. Until then, if more than one current account is offered, select each one you want in the ledger; a transfer between two selected accounts appears on both sides, once on each account.
