# Monitoring and alerts

This describes what is monitored, where the numbers come from, what each
alert means and what to do about it. Every hostname and address below is a
placeholder — replace them with the server's own values.

## What is monitored, and where each number comes from

- **The application** exposes its own operational metrics — build info and a
  health-check status per check — on a loopback-only endpoint that
  Prometheus scrapes.
- **The bank sync** is reported by the application too; the next section
  lists those numbers.
- **The installer** writes a small set of metrics after every poll and every
  install: whether the last poll succeeded, whether the last install
  succeeded, whether it was rolled back, and the timestamps of each. These
  land as a Prometheus textfile, so the node exporter picks them up on its
  next scrape without the installer needing to reach Prometheus directly.
- **The backup job** writes its own metrics the same way: whether the last
  backup run succeeded and when it last succeeded, per backup reason
  (scheduled or pre-migration).
- **The node exporter** reports whether each platform systemd service —
  the application, PostgreSQL, Grafana and Prometheus itself — is actually
  in the `active` state, plus general host metrics.

Prometheus, the node exporter and the application's metrics endpoint all
listen on loopback only, by design. None of them has a reverse-proxy route;
the only way to reach them from outside the server is the tunnel described
below.

## The bank sync metrics

The application exposes the state of the bank sync on the same loopback-only
endpoint. The numbers are read back from the database at startup and then
every minute, so restarting the application never resets the time of the
last successful sync and never hides a sync that is failing.

Every label is an opaque key: a random connection key or account key, or a
fixed word such as a failure reason. No metric carries an account number,
account name, bank name or counterparty, so the numbers are safe to look at
and to share.

| Metric | What the number means |
|---|---|
| `ledger_bank_consent_days_until_expiry` | Days until the bank consent of the connection ends, with fractions. Negative once it has ended. |
| `ledger_bank_consent_state` | For each connection, 1 on the current state (`linked`, `expiring` or `expired`) and 0 on the other two. A consent counts as expiring when fewer than 14 days are left. |
| `ledger_sync_last_success_timestamp_seconds` | When the last successful sync of a selected account happened, as Unix time. |
| `ledger_sync_failing` | For each connection and failure reason, 1 while the latest finished sync counts as failed for that reason. The reasons are `transient`, `rate_limited`, `consent_rejected` and `provider_auth`. |
| `ledger_sync_errors_total` | How many finished syncs have failed for each reason, over all history. Every reason is present from startup, at zero when it never happened. |
| `ledger_sync_calls_remaining` | How many background bank calls a selected account may still make in the current allowance window. |
| `ledger_balance_reconciliation_drift` | 1 when the latest reconciled balance of an account did not match its stored transactions, 0 otherwise. |
| `ledger_transactions_flagged` | How many pending transactions of an account have an ambiguous match and need a person to look. |

A temporary failure is marked as failing only after the day's single retry
also failed, or when no retry can follow (the failed run was itself a retry
or was started by hand, or the retry would fall on the next day). A rate
limit, a rejected consent and rejected credentials are marked at once.

## The alerts, and what to do for each

| Alert | What it means | What to do |
|---|---|---|
| Ledger app is down | Prometheus can no longer scrape the application. | Check whether the application process is running (`systemctl status`) and check its logs. |
| A platform service is down | One of the application, PostgreSQL, Grafana or Prometheus's own systemd units is not active. | Check `systemctl status` for the named unit and its logs; restart it if it has simply stopped. |
| Ledger health check is failing | The application is reachable, but at least one of its own health checks (for example, the database) is failing. | Check the application's own health endpoint and logs for which check is failing and why. |
| Ledger backup is stale | No backup run has succeeded in about 26 hours. | Check the backup timer and its last run's logs; run the backup job by hand if needed. |
| Ledger backup run failed | The most recent backup run itself failed. | Check the backup job's logs for the failure and re-run it once fixed. |
| Ledger deploy failed | The installer's most recent install attempt did not finish healthy. | Check the installer's logs and the notification email for the version and result; consider a manual rollback. |
| Ledger deploy was rolled back | The installer automatically reverted to the previous release after a failed health check. | Check the installer's logs for why the new release failed its health check before retrying. |
| Ledger deploy poll is stale | The poll timer has not completed a successful check for new releases in over an hour. | Check the poll timer's status and its logs; the timer may have stopped or be failing to reach the release feed. |

### Household alerts

The bank sync has its own group of alerts, in the Household folder in
Grafana. They tell you when the household's transactions stop arriving or
cannot be trusted.

| Alert | What it means | What to do |
|---|---|---|
| Bank sync is failing | A sync failed for a temporary reason and the day's retry did not fix it, or no retry could follow. | Check the application's logs and the status page of the bank aggregator, then use sync now once. |
| Bank sync was rate limited | The bank or aggregator refused the sync because a call allowance was used up. | Wait for the next day. The scheduler tries again on its own; do not keep using sync now. |
| Bank rejected the consent or credentials | The bank rejected the consent, or the aggregator rejected the application's credentials. | Renew the consent through the bank-link flow. If the consent is still valid, check the application's credentials with the aggregator. |
| Bank sync is stale | No sync has succeeded for more than 26 hours. | Look at the failing, rate limited and consent alerts first; if none fires, check the scheduler's logs and the sync status of the account. |
| Bank consent expires within 14 days | A bank consent ends in 14 days or less, but not yet within 7. | Renew the consent through the bank-link flow at a convenient moment. |
| Bank consent expires within 7 days | A bank consent ends in less than 7 days. | Renew the consent through the bank-link flow now. |
| Bank consent has expired | A bank consent has ended, so no new transactions arrive. | Renew the consent through the bank-link flow. Accounts keep their history and their selection. |
| Account balance does not reconcile | The latest balance the bank reported differs from what the stored transactions add up to. | Check the status row of the sync dashboard and the day's transactions for a missing or changed one. |

The consent alerts are bands, so the alert changes and a new email goes out
at 14 days, at 7 days and on expiry. Household alerts go to the operator
only, through the same contact point as every other alert. They share the
hourly grouping that caps alert mail, but one that is still firing is
repeated once a day rather than every 12 hours; to change how often, edit
the repeat interval of the Household route in the notification policy file.
No data series exists before the first bank link, so these alerts stay quiet
until then.

Alert emails only ever say which rule fired, for which service, and its
current state. They never contain a query result, a connection string or
any financial detail, and the household alerts in particular never contain
an amount, a balance, a counterparty, an account number or an account name.

## Looking at the raw metrics

There is no direct network route to Prometheus. The operator has two ways
to look at raw metrics:

- **Through Grafana**, logged in as an administrator, using Explore against
  the Prometheus datasource.
- **Through an SSH tunnel** to the server, forwarding a local port to
  Prometheus's loopback address, then opening Prometheus's own UI at that
  forwarded port from a browser.

## Changing the alert address or the mail relay

The alert address, the mail relay and every other server-specific Grafana
value live in the server's own environment file, never in this repository.
To change any of them, edit that file and restart the Grafana service so it
picks up the change.
