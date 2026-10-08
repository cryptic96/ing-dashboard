---
created: 2026-10-08T00:00:00.000Z
title: Encrypt the reverse-proxy-to-Grafana hop the same way as the application hop
area: security
severity: minor
files:
  - deploy/traefik/ledger.yml.example
  - deploy/provisioning/grafana/grafana.ini
  - deploy/provision.d/40-services.sh
  - deploy/lib/backend-tls.sh
  - deploy/bin/ledger-selfcheck
---

## Problem

The hop from the reverse proxy to the application on port 5080 is now HTTPS with a host-generated certificate that Traefik pins. The hop to Grafana on port 3000 is still plain HTTP, so Grafana sign-ins (admin and viewer passwords) and dashboard data cross the server network segment unencrypted. A compromised neighbour on that segment could read or alter it. The firewall only limits who may connect, not who can intercept. Grafana is far less sensitive than the application, which is why this was deferred.

## What to do

Serve Grafana over HTTPS on 3000 (`protocol = https`, `cert_file`, `cert_key` in grafana.ini) from a second host-generated self-signed certificate created by the same idempotent helper (a different fixed internal name, readable by the grafana user), pin it in a second Traefik serversTransport with the same rules (https URL, serverName, inline rootCAs, never skipping verification), and extend `ledger-selfcheck` and its tests the same way (pinned TLS probes of 3000, handshake fingerprint match, no plain-HTTP answer). Update the Grafana datasource and provisioning checks that call 127.0.0.1:3000 over HTTP (the selfcheck's anonymous-access and admin probes, the installer's Grafana health wait). Document install and rotation in the host guide next to the application certificate.

## Origin

Found by the pre-release code review of the proxy-to-application hop; split off when that hop was encrypted.
