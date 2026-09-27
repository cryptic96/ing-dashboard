# Requirements: Household Ledger

**Defined:** 2026-09-26
**Core Value:** Claude can serve as a trustworthy financial advisor for the household — answering any question about our money accurately and giving grounded, useful advice — because it has complete, correctly categorised transaction data, budgets, goals, and a shared advisor memory to reason over.

## v1 Requirements

Requirements for initial release. Each maps to roadmap phases.

### Ingestion

- [ ] **INGEST-01**: Household links the ING joint account and ING savings accounts once through the bank aggregator's consent flow, after which the app syncs them daily with no manual steps
- [ ] **INGEST-02**: Re-running a sync never creates duplicate transactions, and a pending transaction that later books ends up as one transaction, not two
- [ ] **INGEST-03**: Each transaction keeps booking date, value date, signed amount as an exact decimal, currency, counterparty name and IBAN, description/remittance text and pending/booked status; the raw provider payload is retained for replay and diagnosis
- [ ] **INGEST-04**: Bank consent status (linked, expiring, expired) is tracked explicitly; the household is warned at least 14 days before expiry and can renew through a guided flow without losing history
- [ ] **INGEST-05**: The first sync requests the longest history the bank link offers, and that history is the starting point (no manual backfill)
- [ ] **INGEST-06**: Ingestion goes through a provider interface, so another aggregator or a file import can be added without changing categorisation, planning, MCP or dashboards
- [ ] **INGEST-07**: Sync respects the aggregator's rate limits, and every failed sync is visible rather than silent

### Categorisation

- [ ] **CAT-01**: Category tree follows Nibud's structure (vaste lasten, reserveringsuitgaven, huishoudelijke uitgaven, vrije tijd) and is editable; it ships without any Nibud reference figures
- [ ] **CAT-02**: Transfers between the household's own accounts are detected and excluded from spending and income
- [ ] **CAT-03**: Rules on counterparty, IBAN and description patterns categorise new transactions automatically; rules are stored in the database, never in code
- [ ] **CAT-04**: Every category assignment records its source (rule, Claude, user) and a confidence level
- [ ] **CAT-05**: Transactions that rules cannot categorise confidently — including Tikkie/betaalverzoek payments and iDEAL payments routed through payment service providers — go to a review queue instead of being force-matched
- [ ] **CAT-06**: Claude works through the review queue via MCP, proposing categories that the user confirms
- [ ] **CAT-07**: User corrects a category by telling Claude, and Claude can turn that correction into a rule; applying a rule to past transactions first shows which transactions would change (no silent history rewrite)
- [ ] **CAT-08**: Claude can propose refinements to the category tree based on the household's actual transactions

### Planning & insight

- [ ] **PLAN-01**: Household sets a monthly budget per category and sees actual vs budget
- [ ] **PLAN-02**: Household defines named savings goals with a target amount, sees current progress and a projected completion date
- [ ] **PLAN-03**: Recurring costs (subscriptions, fixed costs) are detected automatically, with next expected date and amount
- [ ] **PLAN-04**: Price increases on recurring charges are flagged when a charge is higher than its previous occurrence
- [ ] **PLAN-05**: Recurring income is detected from history — salary, vakantiegeld, 13e maand / eindejaarsuitkering, toeslagen — without assuming fixed calendar months
- [ ] **PLAN-06**: Annual and irregular costs (e.g. eigen risico, gemeentelijke belastingen, annual insurance, energy annual settlement) are shown as a "true monthly cost"
- [ ] **PLAN-07**: An end-of-month forecast combines recurring income, recurring costs and current spending pace
- [ ] **PLAN-08**: Household is warned when expected income (including vakantiegeld or a bonus) may cross a zorgtoeslag or huurtoeslag income threshold; thresholds are public yearly configuration and the household's income parameters live in the database only
- [ ] **PLAN-09**: Spending is compared against the household's own history (month over month, year over year) — no external reference figures

### Advisor (MCP)

- [ ] **ADV-01**: The MCP server exposes a small set of intent-shaped tools (roughly 8–15), not one tool per table or endpoint
- [ ] **ADV-02**: Every "how much" answer comes from server-side aggregation, and each tool result states its provenance (date range, filters, number of transactions included)
- [ ] **ADV-03**: Transaction search is paginated with a server-side cap and explicitly reports when results are truncated
- [ ] **ADV-04**: A single affordability tool answers "can we afford X by date Y" by combining forecast, goals, recurring and annual costs, and expected income
- [ ] **ADV-05**: Write tools let Claude recategorise transactions, manage rules, manage budgets and goals, annotate transactions, update advisor memory and store reviews
- [ ] **ADV-06**: Every change made through MCP or REST is audit-logged (what changed, before and after, when, which client) and can be reverted
- [ ] **ADV-07**: Text originating from bank data (descriptions, counterparty names, payment-request messages) is returned to Claude clearly marked as untrusted data; write tools act only on explicit parameters, bulk writes require an explicit confirmation step, and writes are rate-limited
- [ ] **ADV-08**: Advisor memory holds a household profile (goals, fixed commitments, preferences) as current state plus history, with superseded entries kept alongside their source, and any Claude session can load it at the start
- [ ] **ADV-09**: An advice and decision log records what was flagged and what the household decided, so dismissed topics are not raised again
- [ ] **ADV-10**: Claude Desktop and Claude Code connect on the home network or VPN; claude.ai web and mobile connect through the public `/mcp` endpoint over HTTPS with OAuth 2.1
- [ ] **ADV-11**: A Claude-side scheduled task (Claude Code, Claude Desktop or a cloud routine) runs a monthly review through the MCP server — guided by a server-provided review prompt so every review covers the same ground — and stores the result in the app
- [ ] **ADV-12**: Stored reviews are readable through MCP and shown in Grafana
- [ ] **ADV-13**: When a review is stored, both partners receive a notification email that contains no financial details — only that a review is ready and a link to the dashboard

### Dashboards (Grafana)

- [ ] **DASH-01**: An overview dashboard shows a top row with this month's spending vs budget, savings goal progress and the next large known cost, followed by where the money goes
- [ ] **DASH-02**: Category drill-down and trends over time are available per category and per period
- [ ] **DASH-03**: Dashboards cover budget vs actual, savings goals, recurring costs with price increases, and true monthly cost of annual expenses
- [ ] **DASH-04**: The latest scheduled review is shown as a dashboard panel
- [ ] **DASH-05**: Every dashboard exists in English and Dutch, generated from a single source so the two languages cannot drift apart
- [ ] **DASH-06**: Dashboards, datasources and alert rules are provisioned as code from the repository
- [ ] **DASH-07**: Grafana reads financial data through a read-only, least-privilege path (a SELECT-only database role on a reporting schema of views) and cannot write
- [ ] **DASH-08**: Each partner has their own Grafana viewer login; anonymous access, public dashboards and snapshot sharing are disabled
- [ ] **DASH-09**: Grafana is reachable from the home network and the VPN only

### Operations

- [ ] **OPS-01**: The app exposes `/metrics` for Prometheus: time of last successful sync, sync errors, days until bank consent expires, review-queue size
- [ ] **OPS-02**: Alerts fire when syncs fail and when bank consent is 14 and 7 days from expiry
- [x] **OPS-03**: App, PostgreSQL, Grafana and Prometheus run in one LXC as systemd services; setup is automated where possible and any one-time steps are documented step by step
- [ ] **OPS-04**: Finance database backups are encrypted, and a restore procedure is documented and tested
- [ ] **OPS-05**: Data Protection keys are persisted, so encrypted bank credentials survive restarts and redeploys (verified by an actual restart)
- [ ] **OPS-06**: All period bucketing (days, months, years) uses the Europe/Amsterdam time zone
- [x] **OPS-07**: Database schema changes ship as Entity Framework Core migrations and are applied automatically during deployment with the migrator role

### Security & deployment

- [ ] **SEC-01**: Bank access is read-only; no code path exists that can initiate a payment or move money
- [ ] **SEC-02**: The app uses separate database roles — runtime (data access to its own tables only, no schema changes), migrator (schema changes) and Grafana reader (SELECT on reporting views only); the database superuser is never used by the app
- [x] **SEC-03**: Secrets live only in a server-side env file readable by the app alone; bank consent tokens and the aggregator key are encrypted at rest
- [ ] **SEC-04**: Only `/mcp` and the OAuth endpoints it needs are internet-facing via Traefik, restricted to Anthropic's published IP ranges; access tokens are audience-validated on every request and never passed through to other services
- [ ] **SEC-05**: The database is reachable only from inside the app's LXC over its local Unix socket (no network listener), with OS-user-to-role peer authentication
- [x] **SEC-06**: Secrets and financial details never appear in logs, exception messages or metric labels
- [x] **SEC-07**: A semver tag triggers a build on a GitHub-hosted runner that produces a release artifact with build-provenance attestation; the server verifies the attestation before deploying
- [x] **SEC-08**: Deploys are pull-based: no self-hosted runner exists, and no GitHub-executed code runs on the server; a release is published only after approval on a GitHub Environment with a required reviewer, and the server installs only published releases from `main`; workflow runs from outside contributors require approval; release-tag creation is restricted
- [x] **SEC-09**: All third-party GitHub Actions are pinned to commit SHAs and kept current by Dependabot; no workflow interpolates untrusted values directly into shell commands
- [x] **SEC-10**: The repository contains no personal data: secret scanning and push protection stay enabled, CI scans the full history for secrets, and all test data is synthetic

### REST API

- [ ] **API-01**: REST endpoints cover what MCP is not suited to (bank consent linking and renewal callback, health, computed data for Grafana, administration) and go through the same application layer and audit log as MCP
- [x] **API-02**: The REST API requires authentication even on the home network

## v2 Requirements

Deferred to a future release. Tracked but not in the current roadmap.

### Review & editing UI

- **WEB-01**: Small web page for reviewing and fixing transactions, translatable English/Dutch
- **WEB-02**: Simple edits from within Grafana (form panel calling the REST API)

### Reference comparison

- **REF-01**: Compare household spending with Nibud reference budgets, using figures the household enters itself or fetched from Nibud's paid API at runtime (never committed)

### Ingestion

- **INGEST-08**: Import historical ING exports (CSV/CAMT.053) to backfill beyond what the bank link returns
- **INGEST-09**: Support additional banks or aggregators through the provider interface

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Payment initiation / moving money | Read-only by design; irreversible financial risk if an LLM ever acted on a wrong instruction |
| Investment execution or investment-product advice | Advisor is scoped to spending, budgeting and saving; personalised product advice is regulated territory (AFM/Wft) |
| Subscription cancellation / contract switching on the household's behalf | Advise, don't act — Claude can draft the message, a person sends it |
| Gamification (streaks, badges) | Documented to cause anxiety and poor financial decisions |
| Manual transaction entry flows | Joint account is the complete picture; ingestion is fully automatic |
| Either partner's personal (non-joint) accounts | v1 covers the joint account and savings accounts only |
| Financial figures in email | Email leaves the home network and persists at the mail provider |
| Public internet access to dashboards, REST API or web page | Only `/mcp` is public |
| Prometheus as the store for financial data | No backfill, scrape-time timestamps, immutable samples break recategorisation |
| Committing Nibud reference figures to the repository | Paid commercial data; cannot be redistributed in a public repo |
| App calling any LLM API itself (reviews, background categorisation) | All Claude usage runs on the household's subscription; no separately billed API key |
| Full custom frontend replacing Grafana | Grafana is the dashboard; any web page is only for review/editing |
| Multiple households / multi-tenancy | Single-household app |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| INGEST-01 | Phase 2 | Pending |
| INGEST-02 | Phase 2 | Pending |
| INGEST-03 | Phase 2 | Pending |
| INGEST-04 | Phase 2 | Pending |
| INGEST-05 | Phase 2 | Pending |
| INGEST-06 | Phase 2 | Pending |
| INGEST-07 | Phase 2 | Pending |
| CAT-01 | Phase 4 | Pending |
| CAT-02 | Phase 4 | Pending |
| CAT-03 | Phase 4 | Pending |
| CAT-04 | Phase 4 | Pending |
| CAT-05 | Phase 4 | Pending |
| CAT-06 | Phase 4 | Pending |
| CAT-07 | Phase 4 | Pending |
| CAT-08 | Phase 4 | Pending |
| PLAN-01 | Phase 5 | Pending |
| PLAN-02 | Phase 5 | Pending |
| PLAN-03 | Phase 5 | Pending |
| PLAN-04 | Phase 5 | Pending |
| PLAN-05 | Phase 5 | Pending |
| PLAN-06 | Phase 5 | Pending |
| PLAN-07 | Phase 5 | Pending |
| PLAN-08 | Phase 5 | Pending |
| PLAN-09 | Phase 4 | Pending |
| ADV-01 | Phase 3 | Pending |
| ADV-02 | Phase 3 | Pending |
| ADV-03 | Phase 3 | Pending |
| ADV-04 | Phase 5 | Pending |
| ADV-05 | Phase 6 | Pending |
| ADV-06 | Phase 4 | Pending |
| ADV-07 | Phase 4 | Pending |
| ADV-08 | Phase 6 | Pending |
| ADV-09 | Phase 6 | Pending |
| ADV-10 | Phase 3 | Pending |
| ADV-11 | Phase 6 | Pending |
| ADV-12 | Phase 6 | Pending |
| ADV-13 | Phase 6 | Pending |
| DASH-01 | Phase 5 | Pending |
| DASH-02 | Phase 4 | Pending |
| DASH-03 | Phase 5 | Pending |
| DASH-04 | Phase 6 | Pending |
| DASH-05 | Phase 2 | Pending |
| DASH-06 | Phase 1 | Pending |
| DASH-07 | Phase 2 | Pending |
| DASH-08 | Phase 1 | Pending |
| DASH-09 | Phase 1 | Pending |
| OPS-01 | Phase 2 | Pending |
| OPS-02 | Phase 2 | Pending |
| OPS-03 | Phase 1 | Complete |
| OPS-04 | Phase 1 | Pending |
| OPS-05 | Phase 1 | Pending |
| OPS-06 | Phase 3 | Pending |
| OPS-07 | Phase 1 | Complete |
| SEC-01 | Phase 2 | Pending |
| SEC-02 | Phase 1 | Pending |
| SEC-03 | Phase 1 | Complete |
| SEC-04 | Phase 3 | Pending |
| SEC-05 | Phase 1 | Pending |
| SEC-06 | Phase 1 | Complete |
| SEC-07 | Phase 1 | Complete |
| SEC-08 | Phase 1 | Complete |
| SEC-09 | Phase 1 | Complete |
| SEC-10 | Phase 1 | Complete |
| API-01 | Phase 6 | Pending |
| API-02 | Phase 1 | Complete |

**Coverage:**

- v1 requirements: 65 total
- Mapped to phases: 65
- Unmapped: 0

---
*Requirements defined: 2026-09-26*
*Last updated: 2026-09-26 after roadmap creation (traceability filled)*
