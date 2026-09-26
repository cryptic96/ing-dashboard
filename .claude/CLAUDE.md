# CLAUDE.md

## Hard rules

These apply to every change in this repository, without exception.

### NEVER use planning references outside `.planning/`

Planning and tracking references belong in `.planning/` only. **Never** put them in documentation or anywhere else in the repository. That includes:

- requirement keys (`ABC-01`), decision IDs, review-finding IDs
- phase, plan, wave or milestone numbers (`Phase 2`, `02-01`, `v1`)
- planning document names (`PROJECT.md`, `REQUIREMENTS.md`, `ROADMAP.md`, `STATE.md`, `RESEARCH.md`, `CONTEXT.md`, `PLAN.md`, `SPEC.md`)

This covers: `README.md`, everything under `docs/`, setup guides, `///` XML doc comments, code, string literals, log and exception messages, test names, Grafana dashboard titles/descriptions/alert texts, MCP tool names and descriptions, OpenAPI descriptions, scripts, config and workflow files.

These references go stale the moment a phase closes. Write documentation that explains the *what* and *why* in plain language that stays true regardless of which phase produced it. Git commit messages are the only place outside `.planning/` where phase/plan identifiers may appear, for traceability.

### Comments: `///` only

No `//` comments. Only `///` XML doc summaries on types and members. If a line needs a `//` comment to be understood, rename or extract it instead.

### Public repository: no personal data, anywhere

This repository is public. Never commit personal details in code, comments, docs, fixtures, dashboards, seed data or commit messages: names, IBANs or account numbers, addresses, email addresses, real domains or hostnames, homelab IP addresses, API keys, household-specific merchant or counterparty names, or real transaction data.

- Personal configuration lives only in the server-side env file; `.env.example` contains placeholders only.
- Use `example.com` / `example.org` style placeholders in docs.
- Categories and categorisation rules that name specific counterparties live in the database, never in code or seed data.
- All test data and fixtures are synthetic.

### Security first

This application holds a household's complete financial history. Treat security as a first-class requirement and flag weaknesses proactively — in code, deployment, CI and infrastructure alike.

- Bank access is read-only. Nothing in this application may ever initiate a payment or move money.
- Only `/mcp` is internet-facing, behind OAuth 2.1. Dashboards, REST API and web pages are reachable on the home network and VPN only.
- Least-privilege SQL logins per purpose; never `sa`.
- Secrets never appear in logs, exceptions, metrics labels or MCP tool output.

<!-- GSD:project-start source:PROJECT.md -->

## Project

**Household Ledger**

A self-hosted personal-finance backend for a two-person household that banks with ING (Netherlands). It automatically syncs transactions from the household's ING joint account and savings accounts, categorises them against a Nibud-based category tree (rules plus Claude-assisted suggestions), and exposes the result three ways: **Grafana dashboards** (English and Dutch) so both partners can see where the money goes, an **MCP server** so Claude can serve as the household's financial advisor, and a **REST API** for anything MCP is not suited to. Written in .NET 10 and deployed to a Proxmox homelab LXC from a public GitHub repository.

**Core Value:** Claude can serve as a trustworthy financial advisor for the household — answering any question about our money accurately and giving grounded, useful advice — because it has complete, correctly categorised transaction data, budgets, goals, and a shared advisor memory to reason over.

### Constraints

- **Tech stack**: .NET 10 / C#; one ASP.NET Core host serving REST API, MCP endpoint and background sync — user's main language, one deployable unit
- **Database**: existing network MS SQL Server — new dedicated database with least-privilege logins; never `sa`
- **Code style**: no `//` comments — only `///` XML doc summaries
- **No planning references outside `.planning/`**: never put requirement keys, decision IDs, phase/plan numbers or planning document names in documentation, READMEs, code, comments, XML docs, test names, dashboards, MCP tool descriptions or config — they go stale the moment a phase closes
- **Public repository**: no personal details anywhere in code, commits, comments, docs, fixtures or dashboards — IBANs, names, domains, API keys and similar live only in the server-side env file; merchant/category rules live in the database; all test data is synthetic
- **CI/CD**: GitHub Actions free tier (requires public repo); the self-hosted runner only deploys
- **Hosting**: Proxmox LXC; app, Grafana and Prometheus in the same LXC; automated provisioning preferred, documented one-time setup acceptable
- **Network exposure**: only `/mcp` public (HTTPS via existing Traefik, OAuth 2.1); dashboards, REST and web page reachable on the home network and VPN only
- **Bank access**: read-only, fully automatic sync
- **Language**: application code and UI in English; dashboards (and web page) translatable English/Dutch
- **Security posture**: this handles household finances — treat security as a first-class requirement, and flag weaknesses proactively

<!-- GSD:project-end -->

<!-- GSD:stack-start source:STACK.md -->

## Technology Stack

Technology stack not yet documented. Will populate after codebase mapping or first phase.
<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->

## Conventions

Conventions not yet established. Will populate as patterns emerge during development.
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:ARCHITECTURE.md -->

## Architecture

Architecture not yet mapped. Follow existing patterns found in the codebase.
<!-- GSD:architecture-end -->

<!-- GSD:skills-start source:skills/ -->

## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, `.github/skills/`, or `.codex/skills/` with a `SKILL.md` index file.
<!-- GSD:skills-end -->

<!-- GSD:workflow-start source:GSD defaults -->

## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:

- `/gsd-quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd-debug` for investigation and bug fixing
- `/gsd-execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->

<!-- GSD:profile-start -->

## Developer Profile

> Profile not yet configured. Run `/gsd-profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->
