# Roadmap: Household Ledger

## Overview

The build starts with a secure platform, so no real financial data ever lands on a server that hasn't been hardened. Next comes the hardest integration, built as a thin vertical slice: real ING transactions syncing daily, then Claude reading them through a public, OAuth-protected MCP endpoint. After that the data is made trustworthy: categorisation, with an audit log and undo in place before Claude's first write tools. Planning features (budgets, goals, recurring costs, forecast, affordability) come next, and the advisor's shared memory and scheduled monthly reviews come last. Every phase delivers something the household can see in Grafana or ask Claude about. Grafana grows with each slice instead of getting a separate "dashboards" phase. Each security control ships in the phase that first needs it. The last phase adds only an end-to-end verification pass over the finished app.

## Phases

**Phase Numbering:**

- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Secure Platform & Release Pipeline** - Hardened tag-to-deploy pipeline and a locked-down LXC (app, Prometheus, Grafana) ready to hold financial data (completed 2026-09-29)
- [x] **Phase 2: Automatic ING Sync** - Link ING once; real transactions arrive daily, deduplicated, with consent expiry never silent (completed 2026-10-07)
- [ ] **Phase 3: Claude Reads the Ledger** - Claude (desktop, Code, claude.ai web and mobile) answers grounded spending questions over a secured MCP endpoint
- [ ] **Phase 4: Trustworthy Categorisation** - Rules, review queue and Claude-assisted corrections with audit and undo; category drill-down and history comparison
- [ ] **Phase 5: Budgets, Goals & Forecast** - Budgets, savings goals, recurring costs and income, true monthly cost, forecast and a single affordability answer
- [ ] **Phase 6: Advisor Memory & Scheduled Reviews** - Shared household context for every Claude session, monthly reviews stored and shown, notification-only email

## Phase Details

### Phase 1: Secure Platform & Release Pipeline

**Goal:** A tagged release deploys itself to the homelab LXC through a pipeline that cannot leak secrets. The running platform (app, PostgreSQL, Prometheus and Grafana) is locked down and ready to hold financial data before any real bank data arrives.
**Mode:** mvp
**Depends on**: Nothing (first phase)
**Requirements**: SEC-02, SEC-03, SEC-05, SEC-06, SEC-07, SEC-08, SEC-09, SEC-10, OPS-03, OPS-04, OPS-05, OPS-07, API-02, DASH-06, DASH-08, DASH-09
**Success Criteria** (what must be TRUE):

  1. Pushing a semver tag on `main` builds the release on a GitHub-hosted runner and pauses for the operator's approval on the deploy environment. After approval the release is published, and the LXC pulls it and runs the new version as a systemd service alongside PostgreSQL, Prometheus and Grafana. An artifact whose build-provenance attestation fails verification is refused before it is unpacked.
  2. No self-hosted runner exists and no GitHub-executed code runs on the server; the LXC only pulls and installs approved, attested releases from `main`. Outside-contributor workflow runs wait for approval, and release-tag creation is restricted. CI fails on any third-party action not pinned to a commit SHA and on any untrusted value interpolated into a shell command. A full-history secret scan of the repository passes.
  3. The app reaches its own database only through dedicated runtime and migrator roles over the local Unix socket (the database has no network listener), never as the superuser, and a SELECT-only Grafana reader role exists. A deploy that includes a new Entity Framework Core migration applies it automatically with the migrator role, with no manual database step. Secrets live only in the server-side env file. A value encrypted before a restart and redeploy still decrypts afterwards. Logs, exception messages and metric labels contain no secrets.
  4. An encrypted backup of the finance database has actually been restored by following the documented procedure. Every one-time setup step for the LXC is documented step by step.
  5. Each partner signs in to Grafana with their own viewer login from the home network or VPN. Anonymous access, public dashboards and snapshot sharing are disabled, and datasources are provisioned from the repository. Grafana, Prometheus and the REST API are unreachable from the internet, and REST calls without credentials are rejected even on the home network.

**Plans**: 12/12 plans executed

Plans:
**Wave 1**

- [x] 01-01-PLAN.md — Walking skeleton tracer: solution, runtime-role canary row over PostgreSQL, loopback health/metrics, release package and database-boundary tests

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 01-02-PLAN.md — Supply-chain gate: lint harness (actionlint, zizmor, shellcheck, gitleaks), CI with PostgreSQL service container, Dependabot
- [x] 01-04-PLAN.md — LXC installer: unauthenticated poll, offline attestation verify before unpack, migrate, activate, health, rollback, reporting
- [x] 01-06-PLAN.md — REST API keys: hashed named keys, CLI, X-Api-Key auth on every endpoint, status endpoint
- [x] 01-09-PLAN.md — Provisioning I: verified install pins, packages, accounts, secrets, socket-only PostgreSQL with peer-mapped roles

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 01-03-PLAN.md — Release pipeline: strict tag gate, attested draft release, deploy-environment-gated publish, GitHub settings guide
- [x] 01-05-PLAN.md — Encrypted local backups with GFS retention and freshness metrics, restore drill and live restore
- [x] 01-07-PLAN.md — Certificate-protected key ring, fail-fast production config, secret redaction proven with sentinels
- [x] 01-08-PLAN.md — Grafana hardening, datasources, platform alerts and Prometheus config as code, validated in a real Grafana

**Wave 4** *(blocked on Wave 3 completion)*

- [x] 01-10-PLAN.md — Provisioning II: services, default-drop firewall, Grafana accounts, on-host selfcheck, Traefik template, setup guide

**Wave 5** *(blocked on Wave 4 completion)*

- [x] 01-11-PLAN.md — Go-live bring-up: GitHub protections (verified read-only) and the provisioned LXC

**Wave 6** *(blocked on Wave 5 completion)*

- [x] 01-12-PLAN.md — First real release and live acceptance: attested deploy, restart/redeploy, refusal, restore drill, reachability

**UI hint**: no

### Phase 2: Automatic ING Sync

**Goal:** After linking the ING accounts once, the household's real transactions arrive in the ledger every day. They are complete and never duplicated, bank-consent expiry never goes unnoticed, and both partners can see the data arriving.
**Mode:** mvp
**Depends on**: Phase 1
**Requirements**: INGEST-01, INGEST-02, INGEST-03, INGEST-04, INGEST-05, INGEST-06, INGEST-07, SEC-01, OPS-01, OPS-02, DASH-05, DASH-07
**Success Criteria** (what must be TRUE):

  1. After one guided consent flow, the joint account and every ING savings account appear in the ledger with the longest history the bank link offers. New transactions then arrive daily without anyone doing anything.
  2. Re-running a sync adds nothing, and a pending transaction that later books stays a single transaction. Each account's transactions reconcile with the bank to the cent: exact decimal amounts, both dates, counterparty, description and the raw provider payload are all retained.
  3. The operational metrics show consent state (linked, expiring, expired), days until expiry, the time of the last successful sync and sync errors. Alerts reach the household when a sync fails (including rate-limit rejections) and 14 and 7 days before consent expires. Renewing through the guided flow keeps all history.
  4. Both partners can open a Grafana dashboard showing sync status and recent transactions per account, in English or Dutch; both language versions are generated from one source. The dashboard reads through the SELECT-only reporting role, and the database rejects a write query attempted with that role.
  5. The bank connection is read-only, and no code path can initiate a payment. A synthetic test provider can feed the same pipeline with no changes outside ingestion.

**Plans**: 16/16 plans executed

Plans:
**Wave 1**

- [x] 02-01-PLAN.md — Test platform moved to Microsoft.Testing.Platform (xunit.v3 4.x), proven with the DST-safe Amsterdam schedule math
- [x] 02-02-PLAN.md — Real-consent spike kickoff (operator): encrypted, structure-only spike tool; savings coverage, consent limits, history window; daily captures start; SSH key passphrase
- [x] 02-03-PLAN.md — Host hardening: selfcheck log secret scan, sandboxed installer unit and apikey wrapper, tzdata, ledger-bank-key for the password-protected aggregator key

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 02-04-PLAN.md — Ingestion core tracer: provider interface, ledger identity model and schema, idempotent apply with raw payloads, orchestrator, synthetic provider, reporting views read as grafana_reader

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 02-05-PLAN.md — Pending-to-booked reconciliation: certain-match merges, unclear-match flags, drops only after complete fetches, restores
- [x] 02-06-PLAN.md — Dashboard generator (C#, EN/NL from one source), recent-transactions dashboard, provider path fix, drift and read-only tests
- [x] 02-07-PLAN.md — Guided consent flow over REST: link, one-time-state callback, account selection, consent state, renewal keeping history, revoke

**Wave 4** *(blocked on Wave 3 completion)*

- [x] 02-08-PLAN.md — Daily Amsterdam scheduler with one safe retry, per-account call budget and call ledger, sync now with last-call refusal

**Wave 5** *(blocked on Wave 4 completion)*

- [x] 02-09-PLAN.md — Daily balance snapshots and to-the-cent balance reconciliation

**Wave 6** *(blocked on Wave 5 completion)*

- [x] 02-10-PLAN.md — Operational metrics seeded from the database and household alert rules (operator-only, daily reminder route)
- [x] 02-11-PLAN.md — Dashboard status row from reporting.account_status (sync, consent, balance, reconciliation, unclear matches)
- [x] 02-12-PLAN.md — Spike completion (operator): pair analysis, quota probe, spike consent revoked, adapter values chosen

**Wave 7** *(blocked on Wave 6 completion)*

- [x] 02-13-PLAN.md — Enable Banking adapter behind the provider interface: client token, AIS-only outbound allow-list, paging, errors, PSU headers, exact amounts
- [x] 02-16-PLAN.md — Balance reconciliation on ING's undated expected balance (fetch-time window) and drift flagged only when it persists two snapshots

**Wave 8** *(blocked on Wave 7 completion)*

- [x] 02-14-PLAN.md — Production readiness: replay of real captured pairs through the real code, spike data deleted, bank config validation, log redaction, runbook

**Wave 9** *(blocked on Wave 8 completion)*

- [x] 02-15-PLAN.md — Go-live: release, sandboxed installer proof, server's aggregator application and key, first real link, next-morning automatic sync

**UI hint**: yes
**Research flags**: Spike first: confirm with a real consent that Enable Banking's ING NL link includes the savings accounts (Salt Edge is the fallback). Also confirm the aggregator's rate limits and actual consent duration. Validate pending-to-booked reconciliation against real captured transaction pairs kept outside the repository; only synthetic fixtures are committed.

### Phase 3: Claude Reads the Ledger

**Goal:** Claude can answer accurate "how much" and "what did we spend" questions from the household's real transactions, on desktop, in Claude Code and on claude.ai web and mobile, through a securely exposed MCP endpoint.
**Mode:** mvp
**Depends on**: Phase 2
**Requirements**: ADV-01, ADV-02, ADV-03, ADV-10, SEC-04, OPS-06
**Success Criteria** (what must be TRUE):

  1. The user adds the ledger as a custom connector in claude.ai, signs in through OAuth 2.1, and gets answers from the household's real transactions on web and mobile. Claude Desktop and Claude Code reach the same tools from the home network or VPN.
  2. Claude is offered a small set of intent-shaped tools, not one per table. A question like "how much did we spend on groceries in August?" is answered from a server-side total that states its date range, filters and number of transactions included.
  3. Days, months and years follow Amsterdam time: a transaction late on the last evening of a month, and one on a daylight-saving changeover weekend, land in the correct period.
  4. Searching a full year of transactions returns paginated results under a server-side cap and says explicitly when a result was truncated.
  5. From outside the network, only `/mcp` and its OAuth endpoints respond, and only to Anthropic's published IP ranges. OAuth discovery works from outside the network. A token issued for a different audience is rejected, and the caller's token is never forwarded to any other service.

**Plans**: 1/8 plans executed

Plans:
**Wave 1**

- [x] 03-01-PLAN.md — Tracer: embedded OpenIddict with Identity logins, consent and pre-registered Claude clients; Claude Code's discovery chain, PKCE sign-in and /mcp with ledger_overview end to end; rotating refresh tokens that survive a restart

**Wave 2** *(blocked on Wave 1 completion)*

- [ ] 03-02-PLAN.md — Second factor (TOTP with replay guard) and hardened sign-in pages; the app enforces the public boundary itself: host and path allow-list, home/VPN-only sign-in, one audience, one scheme per surface, rate and size limits, no outbound calls
- [ ] 03-03-PLAN.md — money_totals: out, in and net from booked transactions in Amsterdam periods, pending and own-account transfers reported beside, per-counterparty breakdown, time and account grouping, full provenance

**Wave 3** *(blocked on Wave 2 completion)*

- [ ] 03-04-PLAN.md — search_transactions with a capped keyset cursor and truncation notice, find_counterparties, masked counterparty accounts, the final four-tool surface and instructions
- [ ] 03-05-PLAN.md — Operator controls: ledger-login enrolment, ledger-grants kill switch, MCP and token metrics, access alerts

**Wave 4** *(blocked on Wave 3 completion)*

- [ ] 03-06-PLAN.md — Exposure as code: Traefik MCP routers with Anthropic's range, provisioning keys, selfcheck sudo and MCP checks, outside-in exposure check, connection guide

**Wave 5** *(blocked on Wave 4 completion)*

- [ ] 03-07-PLAN.md — Home-network go-live (operator): release v0.3.0, host configured, routes on the home/VPN allowlist, login enrolled, Claude Code reads the real ledger

**Wave 6** *(blocked on Wave 5 completion)*

- [ ] 03-08-PLAN.md — Public go-live (operator): temporary SSH access removed, Anthropic range enabled, exposure proven from outside, claude.ai on web, mobile and Desktop, kill switch observed

**UI hint**: no
**Open decision**: Separate Authentik service vs a lightweight embedded OAuth server. Do not decide this before phase research. It depends on whether claude.ai custom connectors accept a pre-registered client (or client metadata documents) instead of dynamic client registration, and on what fits the single-LXC resource budget on a low-power host.
**Research flags**: Check the current claude.ai custom-connector OAuth requirements and whether Anthropic publishes egress IP ranges. The user's browser must reach the authorize/login step, and a phone on mobile data is outside Anthropic's ranges. So confirm that connecting while on the home network or VPN is enough, and that refresh-token lifetimes keep re-authorisation rare.

### Phase 4: Trustworthy Categorisation

**Goal:** Every transaction is correctly categorised: automatically by rules where they are confident, and by Claude and the user for the rest. Every change is audited and reversible, and the household can see where the money goes and how that compares with before.
**Mode:** mvp
**Depends on**: Phase 3
**Requirements**: CAT-01, CAT-02, CAT-03, CAT-04, CAT-05, CAT-06, CAT-07, CAT-08, ADV-06, ADV-07, DASH-02, PLAN-09, OPS-08
**Success Criteria** (what must be TRUE):

  1. Rules stored in the database categorise new transactions automatically into a Nibud-structured category tree that the household can edit; no reference figures are shipped. Every assignment shows its source (rule, Claude or user) and a confidence level. Transfers between the household's own accounts no longer count as spending or income.
  2. Tikkie/betaalverzoek payments, iDEAL payments routed through payment service providers, and other low-confidence transactions go to a review queue instead of getting a guessed category. Claude works through the queue and proposes categories for the user to confirm, and the queue size in the operational metrics goes down. Claude can also propose refinements to the category tree based on real transactions.
  3. The user tells Claude "that Tikkie in January was concert tickets" and Claude recategorises it. Turning that correction into a rule first shows exactly which past transactions would change, and nothing in history changes until the user confirms.
  4. Every change made through MCP or REST appears in an audit log (what changed, before and after, when, which client) and can be reverted. Bank-sourced text reaches Claude marked as untrusted, and a test transaction whose description contains an instruction triggers no write. Bulk writes require an explicit confirmation step, and writes are rate-limited.
  5. Both partners can drill into any category and period in Grafana and see trends over time. Both Grafana and Claude compare spending month over month and year over year against the household's own history.

**Plans**: TBD
**UI hint**: yes
**Notes**: This phase sets up the audited write-tool pattern (a domain service writes the audit entry; the tool never writes directly), and every later write tool reuses it. Transaction annotations naturally fit here.

### Phase 5: Budgets, Goals & Forecast

**Goal:** The household can plan ahead with budgets, savings goals, recurring costs and income, the true monthly cost of annual expenses and an end-of-month forecast. Claude can answer "can we afford X by Y?" from one grounded calculation.
**Mode:** mvp
**Depends on**: Phase 4
**Requirements**: PLAN-01, PLAN-02, PLAN-03, PLAN-04, PLAN-05, PLAN-06, PLAN-07, PLAN-08, ADV-04, DASH-01, DASH-03
**Success Criteria** (what must be TRUE):

  1. Through Claude, the household sets a monthly budget per category and defines named savings goals. They then see actual vs budget, each goal's progress and its projected completion date. Every change is audited and can be reverted.
  2. Subscriptions and fixed costs are detected automatically, with the next expected date and amount, and a charge higher than its previous occurrence is flagged as a price increase. Annual and irregular costs (eigen risico, gemeentelijke belastingen, annual insurance, the energy annual settlement) show as a true monthly cost.
  3. Recurring income (salary, vakantiegeld, 13e maand / eindejaarsuitkering, toeslagen) is detected from history without assuming fixed months. An end-of-month forecast combines recurring income, recurring costs and the current spending pace. The household is warned when expected income may cross a zorgtoeslag or huurtoeslag threshold; thresholds are public yearly configuration, and the household's income parameters live in the database only.
  4. Asked "can we afford a 2,000 euro holiday in March?", Claude gives one grounded answer from a single affordability tool. The answer shows how the forecast, goals, recurring and annual costs, and expected income were combined.
  5. The Grafana overview opens with this month's spending vs budget, savings goal progress and the next large known cost, followed by where the money goes. Further dashboards cover budget vs actual, savings goals, recurring costs with price increases, and the true monthly cost of annual expenses, all in English and Dutch.

**Plans**: TBD
**UI hint**: yes
**Research flags**: Dutch income patterns (vakantiegeld timing, toeslagen advances and final settlements) and where the yearly toeslag income thresholds are officially published. Recurring-detection heuristics that tolerate irregular intervals and amount drift.

### Phase 6: Advisor Memory & Scheduled Reviews

**Goal:** Every Claude session starts from the same household context. A monthly review arrives on schedule: it is stored in the app, shown in Grafana and announced by an email that reveals nothing financial.
**Mode:** mvp
**Depends on**: Phase 5
**Requirements**: ADV-05, ADV-08, ADV-09, ADV-11, ADV-12, ADV-13, DASH-04, API-01
**Success Criteria** (what must be TRUE):

  1. A new Claude session on any client (desktop, Claude Code, web, mobile or scheduled) loads the same household profile of goals, fixed commitments and preferences as current state. Superseded entries stay visible in the history, together with their source.
  2. Advice given and decisions taken are logged, and a topic the household dismissed is not raised again in a later session or review.
  3. A Claude-side scheduled task runs the monthly review from the server-provided review prompt, so every review covers the same ground, and stores the result. Stored reviews are readable through MCP, and the latest one appears as a Grafana panel.
  4. When a review is stored, both partners receive an email that says only that a review is ready and links to the dashboard. It contains no amounts, merchants, categories or balances.
  5. Across the complete write surface (recategorising, rules, budgets, goals, annotations, advisor memory and reviews), every change made through MCP or REST is audited and revertible. REST covers consent linking and renewal, health, computed data for Grafana and administration, all through the same application layer.

**Plans**: TBD
**UI hint**: yes
**Research flags**: Decide which Claude-side scheduler fits: a Claude Code scheduled task, Claude Desktop or a cloud routine. Check plan availability and whether cloud routines can reach a custom connector. Also check MCP prompt support across the Claude clients in use. Reviews are Claude-side by household decision; the app does not call the Anthropic API itself.
**Notes**: Consent-link and renewal REST endpoints are built in Phase 2, and computed-data endpoints for Grafana in Phase 5. This phase completes the administration endpoints and verifies the whole REST surface. The phase closes with an end-to-end security verification pass that re-runs earlier phases' checks against the finished app. The controls themselves shipped with earlier phases.

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → 6

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Secure Platform & Release Pipeline | 12/12 | Complete    | 2026-09-29 |
| 2. Automatic ING Sync | 16/16 | Complete    | 2026-10-07 |
| 3. Claude Reads the Ledger | 1/8 | In Progress|  |
| 4. Trustworthy Categorisation | 0/TBD | Not started | - |
| 5. Budgets, Goals & Forecast | 0/TBD | Not started | - |
| 6. Advisor Memory & Scheduled Reviews | 0/TBD | Not started | - |
