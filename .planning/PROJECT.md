# Household Ledger

## What This Is

A self-hosted personal-finance backend for a two-person household that banks with ING (Netherlands). It automatically syncs transactions from the household's ING joint account and savings accounts, categorises them against a Nibud-based category tree (rules plus Claude-assisted suggestions), and exposes the result three ways: **Grafana dashboards** (English and Dutch) so both partners can see where the money goes, an **MCP server** so Claude can serve as the household's financial advisor, and a **REST API** for anything MCP is not suited to. Written in .NET 10 and deployed to a Proxmox homelab LXC from a public GitHub repository.

## Core Value

Claude can serve as a trustworthy financial advisor for the household — answering any question about our money accurately and giving grounded, useful advice — because it has complete, correctly categorised transaction data, budgets, goals, and a shared advisor memory to reason over.

## Requirements

### Validated

- [x] Public GitHub repo; release on semver tag on `main` → build on GitHub-hosted runner → attested release artifact → published after approval → the app LXC pulls, verifies and installs it (no self-hosted runner) — *Validated in Phase 1: Secure Platform & Release Pipeline*
- [x] Deployment pattern hardened versus the existing homelab reference (see Context → Security review) — *Validated in Phase 1: Secure Platform & Release Pipeline*
- [x] App, PostgreSQL, Grafana and Prometheus run in one LXC; provisioning is automated where possible and any one-time setup is documented step by step — *Validated in Phase 1: Secure Platform & Release Pipeline*
- [x] Dashboards, datasources and alert rules are provisioned as code from the repository — nothing clicked together by hand — *Validated in Phase 1: Secure Platform & Release Pipeline*
- [x] Both partners can open the dashboards without technical steps, at home and away (via the home VPN) — *Validated in Phase 1: Secure Platform & Release Pipeline*
- [x] Transactions from the ING joint accounts sync automatically every morning through a read-only licensed PSD2 aggregator (Enable Banking, restricted personal-use mode) — *Validated in Phase 2: Automatic ING Sync*
- [x] Sync is idempotent: re-running never duplicates transactions; pending and booked transactions are reconciled, and the daily balance check reconciles each account to the cent — *Validated in Phase 2: Automatic ING Sync*
- [x] Bank consent renewal is a guided flow, with advance warning 14 and 7 days before expiry (first live renewal around March 2027 is a tracked follow-up) — *Validated in Phase 2: Automatic ING Sync*
- [x] Initial history is whatever the bank link returns (two years right after approval); no manual backfill needed — *Validated in Phase 2: Automatic ING Sync*
- [x] Ingestion sits behind a provider interface; a synthetic provider feeds the same pipeline unchanged — *Validated in Phase 2: Automatic ING Sync*
- [x] Grafana reads financial data through a SELECT-only database role on a reporting schema of views — *Validated in Phase 2: Automatic ING Sync*
- [x] App exposes `/metrics` for Prometheus: sync health, time of last successful sync, consent state and days until expiry, error counts — *Validated in Phase 2: Automatic ING Sync*
- [x] Alerts for failing syncs, rate limits, consent rejection, stale syncs, balance drift and consent nearing expiry, sent to the operator without financial detail — *Validated in Phase 2: Automatic ING Sync*

### Active

**Ingestion**
- [ ] Savings balance and interest, which the bank link does not expose: decide on manual balance entry or a CSV import when budgets and goals need them

**Categorisation**
- [ ] Category tree based on Nibud household budget categories, refined by Claude from the household's real data
- [ ] Rule-based auto-categorisation (counterparty name, IBAN, description patterns) — rules live in the database, never in code
- [ ] Claude reviews uncategorised / low-confidence transactions and proposes categories for the user to confirm
- [ ] User can correct a category by telling Claude (e.g. "that Tikkie in January was concert tickets"); Claude can turn a correction into a rule
- [ ] Dutch payment quirks handled: Tikkie / betaalverzoeken, iDEAL payments via payment service providers that hide the real merchant, SEPA description formats, internal transfers between own accounts (not spending), income detection

**Planning**
- [ ] Monthly budgets per category, with actual vs budget visible
- [ ] Named savings goals (e.g. buffer, holiday) with target, progress, and projected completion date
- [ ] Recurring costs view: detected subscriptions and fixed costs, with price increases flagged
- [ ] End-of-month forecast from recurring costs and current spending pace

**Advisor (MCP)**
- [ ] MCP server (official C# MCP SDK) with read tools: search/query transactions, aggregates by category / period / merchant, budgets, goals, recurring costs, forecast
- [ ] MCP write tools: recategorise transactions, create/edit rules, create/adjust budgets and goals, annotate transactions, update advisor memory, store reviews
- [ ] Every Claude-initiated change is audit-logged (what, when, before/after, which client) and can be reverted
- [ ] Advisor memory stored in the app: household profile (goals, fixed commitments, preferences) plus a log of past advice and decisions, so every Claude session — desktop, phone, scheduled — starts from the same context
- [ ] Reachable from Claude Desktop / Claude Code (home network or VPN) and from claude.ai web/mobile (public `/mcp` endpoint over HTTPS with OAuth 2.1)
- [ ] Scheduled proactive reviews (e.g. monthly): explain the past period, flag leaks, check budgets/goals — stored in the app, visible in Grafana, and readable as a Claude conversation
- [ ] Review notification email contains no financial details — only "your review is ready" plus a link to the dashboard

**Dashboards (Grafana)**
- [ ] Dashboards for: where the money goes, category drill-down, trends over time, budget vs actual, savings goals, recurring costs
- [ ] Dashboards available in both English and Dutch (the sync dashboard is generated in both languages from one source since Phase 2; the financial dashboards follow)

**Operations & observability**

**REST API & web page**
- [ ] REST endpoints for operations that MCP is not suited to, and to back the web page
- [ ] (Deferred to v2) Small web page for reviewing and fixing transactions, translatable English/Dutch

**Deployment & security**

### Out of Scope

- Moving money / payment initiation — bank access is read-only by design; neither the app nor Claude can ever move funds
- Investment execution or investment-product advice — the advisor is about spending, budgeting and saving
- Either partner's personal (non-joint) accounts — v1 covers the joint account and savings accounts only
- Banks other than ING in v1 — ingestion is abstracted so this can be revisited
- Manual CSV/CAMT backfill in v1 — user chose fully automatic ingestion; history starts from what the bank link returns
- Prometheus as the store for financial data — scraping cannot backfill history, records values at scrape time instead of booking date, and stored samples are immutable, which breaks recategorising past transactions
- Public internet access to dashboards, REST API or web page — only `/mcp` is public
- Financial figures in email — email leaves the home network and persists at the mail provider
- The app calling any LLM API itself (background categorisation, app-run reviews) — all Claude usage stays on the household's subscription; unclear transactions wait in the review queue for a Claude session or the scheduled task
- A full custom frontend replacing Grafana — Grafana is the dashboard; the web page is only for review/editing
- Multiple households / multi-tenancy — single-household app

## Context

**Household and motivation**
- Two-person household. Both incomes arrive in the ING joint account, which carries all shared spending, so the joint account is effectively the complete household picture; ING savings accounts sit alongside it.
- The itch is a mix of all three classic problems: money seems to disappear each month, the household wants to save towards goals, and there are suspected cost leaks (subscriptions, groceries, eating out).
- Both partners will look at the dashboards; the user is the primary operator and the one who talks to Claude most.
- "Claude as financial advisor" means: explain the past ("where did our money go in August?"), plan ahead ("can we afford a €2k holiday in March?"), find leaks (subscriptions, price increases), and produce proactive scheduled reviews.

**Banking domain**
- ING's official open-banking (PSD2) APIs are generally only available to licensed third-party providers, not to individual customers. The realistic automatic route for personal use is a licensed aggregator (candidates to research: Enable Banking, GoCardless Bank Account Data, others — check new-signup availability, ING NL coverage including savings accounts, consent duration, personal-use terms, cost).
- Nibud publishes reference budgets for Dutch households; basing the category tree on Nibud lets Claude compare the household's spending against reference figures.
- Dutch payment specifics complicate categorisation: Tikkie and betaalverzoeken, iDEAL payments routed through payment service providers, and transfers between own accounts.

**User and environment**
- The user's main language is .NET; they use Prometheus and Grafana professionally.
- Claude clients in use: Claude Desktop / Claude Code, claude.ai web and mobile, and scheduled/automated runs.
- Homelab: Proxmox VE host running LXC containers, including an existing Traefik reverse proxy (Let's Encrypt, public ingress on 80/443), an existing MS SQL Server instance (shared with another app), and an existing Postfix container for outbound mail. The home network has a VPN (UniFi WireGuard/Teleport) for access when away.
- Reference deployment (another of the user's public repos): semver tag → GitHub-hosted build → GitHub Release zip → self-hosted runner on the app LXC runs a deploy script → systemd unit; secrets in a server-side env file.

**Security review of the reference deployment** — issues this project must not inherit:
1. Self-hosted runner on a public repo, running as the same user that owns the secrets file → any job (e.g. from a fork PR) could read every secret. Fix: no self-hosted runner at all — the server pulls and verifies approved, attested releases, so no GitHub-executed code runs on it; require approval for all outside-contributor workflow runs, gate publishing a release behind a GitHub Environment with required reviewer, restrict who can create release tags.
2. App connects to the shared SQL Server as `sa` → a flaw in one app exposes every database on the instance. Fix: this app gets its own PostgreSQL inside its own LXC, reachable only over the local Unix socket, with separate roles (runtime vs migration) plus a SELECT-only role for Grafana.
3. Deploy script runs a downloaded artifact without integrity verification. Fix: build-provenance attestation in CI, verify before unpacking.
4. Third-party GitHub Actions pinned by mutable tag. Fix: pin to commit SHAs, Dependabot for updates.
5. Workflow interpolates tag/input values straight into shell. Fix: pass via env vars and validate against a strict semver pattern.
6. Secrets at rest: env file root-owned with group read for the app only; bank consent tokens encrypted in the database (ASP.NET Data Protection); encrypted DB backups (encrypted to a public key; the private key stays off the server); the database has no network listener at all instead of a TLS connection with `TrustServerCertificate=true`.
7. Only `/mcp` is internet-facing, behind OAuth 2.1 (and an IP allowlist if Anthropic publishes egress ranges); everything else is LAN + VPN only.

**Accepted data flows** — inherent to the design, not flaws: transaction data reaches Anthropic whenever Claude reads it through MCP, and the chosen aggregator sees all synced transactions.

## Constraints

- **Tech stack**: .NET 10 / C#; one ASP.NET Core host serving REST API, MCP endpoint and background sync — user's main language, one deployable unit
- **Solution**: a single `.slnx` solution file
- **Data access**: Entity Framework Core, code-first, with migrations; migrations are applied automatically during deployment using the migrator role (the runtime role has no schema rights) — keeps deployments hands-off
- **LLM usage**: none from the app — no Anthropic (or other LLM) API key or billing; all Claude usage runs on the household's Claude subscription via Claude Desktop, Claude Code, claude.ai and Claude-side scheduled tasks connecting to the MCP server
- **Local development**: tests that need a real database use the user's own local PostgreSQL Docker container; connection strings live in `dotnet user-secrets`, never in committed config; CI uses a PostgreSQL service container
- **Database**: PostgreSQL inside the app LXC, reachable only over its Unix socket (no network listener), with least-privilege roles and peer authentication; the app never uses the superuser
- **Code style**: no `//` comments — only `///` XML doc summaries
- **No planning references outside `.planning/`**: never put requirement keys, decision IDs, phase/plan numbers or planning document names in documentation, READMEs, code, comments, XML docs, test names, dashboards, MCP tool descriptions or config — they go stale the moment a phase closes
- **Public repository**: no personal details anywhere in code, commits, comments, docs, fixtures or dashboards — IBANs, names, domains, API keys and similar live only in the server-side env file; merchant/category rules live in the database; all test data is synthetic
- **CI/CD**: GitHub Actions free tier (requires public repo) on GitHub-hosted runners only; no self-hosted runner — the server pulls approved, attested releases
- **Hosting**: Proxmox LXC; app, PostgreSQL, Grafana and Prometheus in the same LXC; automated provisioning preferred, documented one-time setup acceptable
- **Network exposure**: only `/mcp` public (HTTPS via existing Traefik, OAuth 2.1); dashboards, REST and web page reachable on the home network and VPN only
- **Bank access**: read-only, fully automatic sync
- **Language**: application code and UI in English; dashboards (and web page) translatable English/Dutch
- **Security posture**: this handles household finances — treat security as a first-class requirement, and flag weaknesses proactively

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Name the app "Household Ledger" | Bank-agnostic, English throughout | — Pending |
| .NET 10, single ASP.NET Core host for REST + MCP + BackgroundService sync | User's main language; one process to deploy and secure | — Pending |
| Store data in PostgreSQL inside the app LXC, Unix socket only, with separate runtime / migrator / Grafana reader roles (replaces the earlier plan to use the shared MS SQL Server) | Removes cross-container TLS and firewall work and the shared-instance risk (the other app on that server connects as `sa`); ~150 MB RAM instead of SQL Server's 2 GB minimum; peer auth means no database passwords; SQLite rejected (no logins, decimals stored as text) | — Pending |
| Grafana is the primary UI | User preference; partner-friendly; known stack | — Pending |
| Prometheus only for operational metrics, not financial data | No backfill, scrape-time timestamps, immutable samples conflict with recategorisation | ✓ Good (Phase 2): metrics are a projection of the database with opaque labels only |
| Grafana reads financial data via a SELECT-only role on a `reporting` schema of views; a JSON datasource against the REST API only for computed panels | Research compared views, REST/Infinity and Prometheus; views are Grafana's own recommended least-privilege pattern and keep the app the owner of its tables | ✓ Good (Phase 2): live dashboards read through grafana_reader; writes are refused by the database |
| Bank link via Enable Banking's free personal-use tier; verify ING savings-account coverage early, Salt Edge as fallback | Official ING API not available to individuals; GoCardless Bank Account Data closed to new signups in 2025 | ✓ Good (Phase 2): restricted mode works for ING NL; two joint accounts, no savings account (outside PSD2, joint-accounts-only fallback); 180-day consent; two years of history only right after approval; no rate limit seen up to 44 calls a day |
| OAuth authorization server: separate Authentik vs a lightweight embedded server | claude.ai client-registration requirements (DCR vs pre-registered client) decide it; single-LXC resource budget matters | — Pending (MCP/auth phase research) |
| Spending compared with the household's own history, not Nibud reference figures, in v1 | Nibud figures are a paid product and cannot be committed to a public repo | — Pending |
| Review/fix web page deferred to v2 | Corrections go through Claude in v1 | — Pending |
| Only `/mcp` exposed publicly, behind OAuth 2.1 | claude.ai web/mobile and cloud-scheduled runs need a public endpoint; everything else stays private | — Pending |
| Dashboards on home network + VPN only | Financial data sensitivity | — Pending |
| Nibud-based category tree, refined by Claude | Enables comparison with Dutch reference budgets | — Pending |
| Advisor memory stored in the app | All Claude sessions share one context | — Pending |
| Claude may change categories, rules, budgets, goals, notes, memory — all audit-logged and reversible | Core value needs write access; audit + undo keeps it safe | — Pending |
| Review emails are notification-only | Email leaves the network and persists at the provider | — Pending |
| Scheduled reviews run from a Claude-side schedule (Claude Code / Desktop / cloud routine) through the MCP server; the app stores them and sends the notification | Uses the existing Claude subscription, no separately billed API key; reviews stay readable as a conversation | — Pending |
| Harden the reference deployment pattern (pull-based deploys instead of a self-hosted runner, scoped database roles, artifact verification, SHA-pinned actions) | Finance data demands more than a hobby app | — Pending |
| Pull-based deploys: approval publishes the release, a timer on the LXC pulls it and a root-owned installer verifies and installs it; no self-hosted runner | GitHub advises against self-hosted runners on public repos; a central SSH deploy box would become a hub reaching every app; no GitHub-executed code ever runs on the finance server | — Pending |
| Backups stay local inside the LXC, encrypted to a public key whose private key lives in the operator's password manager | User decision; losing the SSD, theft or fire loses data and backups (accepted risk, offsite copies deferred) | — Pending |
| Generate the Grafana dashboards from one C# definition with an EN/NL translation file | One source for both languages, drift checked in CI, no second toolchain | ✓ Good (Phase 2) |
| Daily balance check on ING's undated expected balance, dated at fetch time, with exposed pending payments neutral and drift flagged only when it persists | ING sends no booked or dated balance; a payment pending over a weekend must not raise a false alarm | ✓ Good (Phase 2) |
| Background call budget of 12 per account per day; syncs the operator starts carry PSU headers; the first sync after linking is never cut off by the budget | Measured in the spike: a daily sync costs 2 calls and no limit appeared up to 44; full history is only offered right after approval | ✓ Good (Phase 2) |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd-complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-10-07 after Phase 2 (Automatic ING Sync) completed: v0.2.3 live; both joint ING accounts sync every morning, reconcile to the cent and show in the EN/NL sync dashboards*
