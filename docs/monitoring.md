# Monitoring and alerts

This describes what is monitored, where the numbers come from, what each
alert means and what to do about it. Every hostname and address below is a
placeholder — replace them with the server's own values.

## What is monitored, and where each number comes from

- **The application** exposes its own operational metrics — build info and a
  health-check status per check — on a loopback-only endpoint that
  Prometheus scrapes.
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

Alert emails only ever say which rule fired, for which service, and its
current state. They never contain a query result, a connection string or
any financial detail.

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
