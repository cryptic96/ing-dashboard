---
created: 2026-10-07T11:16:22.000Z
title: Confirm the first real household alert email arrives without financial detail
area: monitoring
severity: minor
files:
  - deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml
  - deploy/provisioning/grafana/provisioning/alerting/contact-points.yaml
---

## Problem

The eight household alert rules are provisioned and show Normal, and the contact point's test email arrived on 2026-10-07. No real alert has fired yet, so the end-to-end path from a real condition to an email has not been observed. The first expected real alert is the 14-day consent warning around 2027-03-21 (consent valid until 2027-04-04), unless a sync fails earlier.

## What to check

When the first real alert fires: it arrives at every intended address, it carries no account numbers, names, amounts or descriptions, and the daily reminder repeats while it stays firing.

## Origin

Deferred follow-up from the phase 2 acceptance test (test 4).
