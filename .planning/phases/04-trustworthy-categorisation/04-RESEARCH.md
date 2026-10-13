# Phase 4: Trustworthy Categorisation - Research

**Researched:** 2026-10-13
**Domain:** Rule-based and Claude-assisted categorisation over a PostgreSQL ledger, audited and undoable write tools on a stateless MCP server, prompt-injection defence, Grafana drill-down generated from C#
**Confidence:** HIGH on the confirmation mechanics (verified by a running spike against the pinned SDK) and on the in-repo constraints (read this session); MEDIUM on Grafana panel JSON and client behaviour in claude.ai/Desktop; the open items are listed in Assumptions and Open Questions.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

#### Category tree
- **D-01:** **The tree can be any depth.** A transaction can sit on **any node**, not only on the lowest level. Every total for a category includes everything beneath it. When Claude later adds a subcategory under a category that already has transactions, nothing has to move. — **Reversibility:** costly — the tree shape and roll-up semantics run through rules, totals, reporting views, the drill-down dashboard and later budgets; changing depth or assignment rules after real data lands needs a data migration.
- **D-02:** **A generic starter tree ships in a migration:**
  - the four Nibud bands: vaste lasten, reserveringsuitgaven, huishoudelijke uitgaven, vrije tijd
  - an **Income** band and a **Transfers** band
  - about 30 generic categories under them, such as housing, energy, insurance, groceries, eating out and subscriptions

  The names are household-neutral and name no merchant or counterparty, so they are safe for the public repo. **No reference figures.** Claude then refines the tree from real transactions (CAT-08), and each refinement is an audited write.
- **D-03:** **Every category has an English and a Dutch name.** The EN and NL dashboards each show their own language, and Claude sees both. When Claude adds or renames a category, it supplies both names.
- **D-04:** **One category per transaction. No splits.** A mixed supermarket receipt counts as Groceries. — **Reversibility:** costly — introducing splits later turns the one-assignment-per-transaction model into amount-carrying parts, touching totals, rules, audit items, undo and every view.
- **D-05:** **Removing or merging a category names a target that receives its transactions, child categories and rules.** The target defaults to the parent. The change first shows the counts, and is then one audited, undoable operation that goes through the confirmation step (D-21).
- **D-06:** **Every category has a kind: spending, income or transfer.** Exclusion from spending and income follows the kind, not a separate flag (see D-16). The Income band's categories are income, the Transfers band's are transfer, and the rest are spending. — **Reversibility:** costly — totals, comparisons, reporting views and the dashboard top line all key on category kind.

#### Review queue and proposals
- **D-07:** **A transaction goes to the review queue when no rule categorises it confidently.** That means one of three things:
  - no rule matched
  - the counterparty is flagged as an intermediary and no rule with a second signal matched (D-15)
  - equally specific rules disagree (D-12)

  Everything else is categorised automatically. Unusual amounts on a matching rule do **not** send a transaction to the queue.
- **D-08:** **Claude's proposals are stored and held.**
  - Each proposal records the category, Claude's reason and its confidence.
  - The transaction stays uncategorised in every total until the user confirms.
  - Proposals survive across sessions, and a batch is confirmed in one previewed step through D-21's confirmation.
  - Rejected proposals leave the transaction in the queue.
  - Storing a proposal is a write, so it is audited and rate-limited, but it does not need confirmation.
  - The scheduled monthly run in Phase 6 can therefore only propose, never apply.
- **D-09:** **Confidence has three named levels: high, medium and low.** Each has a fixed meaning:
  - a rule matching an exact IBAN or exact name is high
  - a name-contains or description-word rule is medium
  - Claude states its own level on each proposal
  - an assignment the user made or confirmed is recorded as user-sourced and certain

  Low never applies automatically. Every assignment records its source (rule with the rule's id, Claude, or user), its confidence and when it was set (CAT-04). — **Reversibility:** costly — the provenance columns are read by the queue, the audit log, MCP results and the dashboard.
- **D-10:** **The two years of uncategorised history are worked through rules first, then the queue.**
  - Claude groups the history by counterparty and proposes rules for the most frequent ones. Each rule shows how many past transactions it would categorise and goes through the preview and confirmation of D-14.
  - What is left lands in the queue.
  - The queue metric (OPS-08) starts large and shrinks.
  - Comparisons with last year work once the backlog is done; until then D-34 applies.

#### Rules
- **D-11:** **What a rule can match:**
  - counterparty IBAN (exact)
  - counterparty name (exact, or contains)
  - words in the description

  Each can be narrowed further by direction, amount range and account. **No regular expressions:** a careless pattern can match far too much, and the patterns run against text outsiders control. Rules live in the database only, never in code or seed data (CAT-03).
- **D-12:** **When rules conflict, the most specific one wins.** The order is: exact IBAN, then exact name, then name-contains, then description words. Extra narrowing (amount, account, direction) makes a rule more specific. If equally specific rules pick different categories, the transaction goes to the queue (D-07). There is no manual priority order to maintain.
- **D-13:** **Assignments the user set or confirmed by hand are never overwritten by a rule.** That holds at sync time and when a rule is applied to history.
- **D-14:** **History changes only through preview, then confirm (CAT-07).** When a rule is added, changed or removed:
  - transactions that already have a category keep it until the user confirms a preview such as "these 37 would change from X to Y"
  - newly synced transactions follow the current rules
  - applying a rule to the past is one confirmed bulk operation (D-21), audited as one operation (D-25)

  Claude proposes the **narrowest** rule that covers the correction, and suggests broadening only with the user's agreement.
- **D-15:** **Intermediaries get a flag and need a second signal.**
  - A counterparty can be **flagged as an intermediary** in the database: PSPs such as Mollie, Adyen or Buckaroo, and Tikkie or betaalverzoek settlement.
  - Claude proposes which counterparties are intermediaries from the household's data, and the user confirms.
  - A rule on an intermediary is accepted only if it also matches description words or a fixed amount, e.g. "Mollie + 'Spotify'". **A name-only rule on an intermediary is refused.**
  - The intermediary list itself is database data, never code or seed data. Flagging or unflagging an intermediary changes which rules are allowed, so it goes through the D-21 confirmation as a structural write.

#### Own accounts and transfers
- **D-16:** **A transfer is a category of kind transfer** (D-06), not a separate flag (CAT-02).
  - Phase 3's exact rule becomes a built-in assignment into the Transfers band: the normalised counterparty IBAN equals a synced account's IBAN.
  - Other flows between the household's own accounts reach the Transfers band through normal rules matching IBAN or description text, with the usual preview, confirmation and audit. The main example is the ING savings account, which is not synced.
  - This phase builds on Phase 3's D-10 rather than redefining it. Existing MCP totals switch from the IBAN-equality exclusion to category-kind exclusion without changing what they report for synced-account transfers.
- **D-17:** **Claude proposes and the user confirms which unsynced accounts are the household's own.**
  - Examples are the savings account and any later ones.
  - Claude spots candidates in the history and marks them through the opaque counterparty reference; full IBANs stay on the server (Phase 3 D-14).
  - Marking an account as own hides money flows from spending and income. So it is a structural write behind the D-21 confirmation, and it is audited and undoable.
- **D-18:** **Money between the joint account and a partner's personal account is counted, in its own categories.** Personal accounts are not synced.
  - Money out to a personal account is spending, under its own category (e.g. "Personal money").
  - Money in from a personal account is income, under its own category.
  - Neither is an internal transfer.
- **D-19:** **Refunds and paybacks net within their category.** A Tikkie payback for a shared dinner, categorised as Eating out, lowers Eating out: €120 paid minus €80 back is €40 real cost. A returned order works the same way. Only categories of kind income count as income. Phase 3's out/in/net reporting (D-12 there) stays; per category, net is the figure that matters.

#### Write confirmation and injection defence (ADV-07)
- **D-20:** **The confirmation must come from the user, never from the model.**
  - **Primary:** **MCP elicitation.** The server asks the user directly in the client's own dialog, e.g. "Apply 37 changes: X → Y?", and the answer comes from the human.
  - **Fallback**, where a client does not support elicitation: the preview returns a **short-lived, single-use token bound to exactly that change set**, and applying requires it. The client's per-call tool approval is then the human gate.
  - Write tools carry honest MCP annotations (`ReadOnly = false`, and `Destructive` where it applies) so clients prompt.
  - Research must establish:
    - which clients support elicitation: Claude Code now, claude.ai web/mobile and Desktop later
    - how the server learns that a client supports it under the stateless 2026-07-28 spec, where there is no `initialize` handshake
    - that text from bank data can never switch the server from elicitation to the token fallback

  Docs must tell the operator **not** to "always allow" write tools in Claude Code, because that turns the fallback gate off.
- **D-21:** **These writes need the confirmation:**
  - confirming stored proposals
  - any write touching more than one transaction
  - applying a rule to the past
  - tree changes (merge or remove)
  - marking own accounts (D-17)
  - flagging intermediaries (D-15)
  - undoing a bulk operation

  These writes go without it:
  - a **single-transaction** recategorisation or note that the user asked for in the chat
  - storing a proposal

  Both are still audited, rate-limited and undoable. Write tools act only on **explicit parameters**, such as transaction ids or opaque references. There is no "everything matching X" selector except a rule, and rules go through D-14's preview.
- **D-22:** **Write limits are moderate: roughly 30 write calls per 10 minutes, and at most about 500 transactions changed in one confirmed step.** That is enough to work the backlog in several rounds. Hitting a limit is refused with a clear message, counted in a metric, and sent to the operator as an alert without financial detail. The exact numbers are the planner's to tune.
- **D-23:** **Bank text is marked as untrusted in full, with no light version** (this fulfils Phase 3's D-15). Covered fields: counterparty names, descriptions and remittance or payment-request text.
  - It is returned in clearly labelled fields inside a structured untrusted wrapper.
  - Invisible and bidirectional control characters are stripped and lengths are capped.
  - The server instructions and the tool descriptions say this text is data and never an instruction.
  - A synthetic test transaction whose description contains an instruction must trigger **no** write.
- **D-24:** **The server flags instruction-like bank text and alerts on it.**
  - Incoming bank text is checked for instruction-like patterns.
  - Matching transactions are marked suspicious in tool results and on the dashboard.
  - They are counted in a metric, and the operator gets an alert without financial detail.
  - This is visibility, not the defence; the confirmation, limits and audit log are the defence.

#### Audit and undo (ADV-06)
- **D-25:** **The audit log is built from operations that contain items.**
  - One audit entry per operation records what kind of change it was, when, **which client** made it, and which rule or proposal batch it came from.
  - The client is the OAuth client id for MCP and the API key's name for REST.
  - The entry holds a before/after per affected transaction or object.
  - **Undo reverts a whole operation.** A single transaction can still be fixed afterwards with a normal write. There is no per-item undo.

  — **Reversibility:** costly — every later write tool (budgets, goals, memory, reviews) records through this model.
- **D-26:** **Undo skips anything that changed since, and says so.**
  - It reverts what is still as the operation left it, skips anything changed by a later operation, and reports both, e.g. "34 reverted, 3 skipped: changed later".
  - An undo is itself an audited operation and can be undone.
  - Undoing a bulk operation goes through D-21's confirmation.
- **D-27:** **REST gets a list of recent changes and an undo, for the operator only.**
  - REST and MCP share one audited application layer.
  - REST adds two endpoints: "list recent changes" and "undo operation". They are an escape hatch for when Claude misbehaves or is unavailable, and use the existing API-key scheme.
  - Full REST write endpoints wait for the v2 web page.
  - Claude can read the audit log and undo through MCP too.
- **D-28:** **A transaction can carry a short household note**, such as "concert tickets".
  - It is written through the same audited tools.
  - It is shown in Grafana's transaction tables and returned to Claude **labelled as the household's own note**, kept apart from bank text, and still length-capped and cleaned.
  - Notes are the only part of annotations (ADV-05) built in this phase.
- **D-29:** **The audit log holds data changes only.**
  - It covers every change made through MCP or REST, plus confirmed rule applications to the past.
  - Automatic categorisation of newly synced transactions is not an audit entry. Each transaction's assignment provenance (D-09) still answers "why is this X?".
  - Operational actions keep their current logging: bank linking and renewal, sync now, API keys, and OAuth grant revocations.

#### Grafana
- **D-30:** **One new drill-down dashboard, "Spending", in EN and NL**, generated by the existing C# dashboard generator (Phase 2 D-15).
  - It is built around a **category selector**. It shows the selected category's children for the period, its monthly trend, and the transactions beneath it.
  - Clicking a child selects it, so any depth works (D-01). A breadcrumb or "up" link goes back.
  - It starts at the top, with all bands.
  - The existing sync dashboard stays as it is.
  - Financial data is read through `reporting` views as `grafana_reader`.
- **D-31:** **Uncategorised is a bar of its own in every breakdown**, so totals always add up. A status row shows the queue size, "N proposals waiting for confirmation" and the count of suspicious bank texts (D-24).
- **D-32:** **A top row shows income, spending, money moved to savings and what is left, per month.** The spending drill-down sits below it.
- **D-33:** **A "recent changes" panel shows recent audited operations:** when, which client, what kind of change, how many transactions, and whether it was undone. Both partners can see what Claude changed. It reads a reporting view over the audit log that exposes no IBANs and no bank text.

#### Comparisons with the household's own history (PLAN-09)
- **D-34:** **There are three baselines:** the previous month, the same month last year, and the average of the last 12 full months.
  - Every comparison states how much of each period was still uncategorised.
  - When a baseline is mostly uncategorised, both Claude and the dashboard say the comparison is not reliable yet, rather than showing a misleading jump.
- **D-35:** **The current month is compared like for like.** Month-to-date is set against the same number of days in the comparison month, e.g. "by the 13th: €640 groceries; last month by the 13th: €590". The full previous month is shown alongside. Nothing is projected forward, because forecasting is Phase 5.
- **D-36:** **Grafana shows a comparison table, plus last year as a line in the trend chart.**
  - For the selected category: its children with this period, the previous month, the same month last year and the 12-month average, plus the change in € and %.
  - The monthly trend chart shows last year as a faint second line.
  - Claude gets the same comparisons from one server-side comparison tool, computed on the server with full provenance. Claude never computes differences itself (Pitfall 17).


### Claude's Discretion
- **The MCP tool set and names.**
  - Phase 3 has 4 read tools, and ADV-01 caps the milestone at roughly 8–15. Phases 5 and 6 still need room for budgets, goals, recurring costs, forecast, affordability, memory and reviews.
  - Prefer a few intent-shaped tools with an action parameter over one tool per operation. Expected capabilities:
    - review queue and proposals
    - categorise / confirm
    - rules (create, preview, apply, remove)
    - the category tree
    - own accounts and intermediaries
    - notes
    - audit read and undo
    - period comparison
  - Existing `money_totals` and `search_transactions` gain a category filter and grouping by category.
  - The "Categories do not exist yet" wording in their descriptions and in `ServerInstructions` must go.
- **When the rule engine runs.** After each sync's ingestion, on newly ingested rows. Pending rows are categorised too, so the current month looks right. The assignment stays on the immutable transaction id when a pending row books (Phase 2 D-14).
- **The starter tree's exact categories and their EN/NL names.**
- **Confirmation details:**
  - the token's lifetime
  - how a change set is fingerprinted
  - how elicitation and the fallback are wired in the MCP C# SDK
- **Detection patterns for instruction-like text**, and its alert threshold.
- **Metric names and alert rules:**
  - the queue size (OPS-08)
  - proposals waiting
  - write calls
  - writes refused by a limit
  - suspicious-text count

  Labels are opaque only, with no financial data (SEC-06).
- **Audit retention** (default: keep indefinitely), and how before/after values are stored.
- **Note length cap**, and how notes are cleaned.
- **New panel types in the dashboard generator.** It only has table panels today; bar chart, time series and stat are likely needed. Also how the drill-down's breadcrumb and links are built.
- **Comparison edge cases**, e.g. day 31 against a shorter month, or a month with no history.
- **How Claude's proposals and reasons are shown back** to Claude and in the queue.


### Deferred Ideas (OUT OF SCOPE)

- **Splitting a transaction across several categories:** rejected for now (D-04). Revisit only if mixed receipts turn out to distort the picture.
- **A full set of REST write endpoints:** these come with the v2 review/fix web page. This phase adds only the operator audit list and undo.
- **The partner confirming proposals:** this needs the v2 web page, or partner access to an AI client (deferred in Phase 3).
- **Projecting the current month forward by spending pace:** Phase 5's end-of-month forecast (PLAN-07).
- **Smoothing annual costs in comparisons:** a yearly insurance bill makes month-over-month jumpy. That is Phase 5's true monthly cost (PLAN-06); the 12-month average baseline (D-34) softens it meanwhile.
- **Annotations beyond the short transaction note:** with the rest of ADV-05 in Phase 6.

</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CAT-01 | Editable Nibud-structured category tree, no reference figures | Adjacency-list tree with kind and EN/NL names seeded by migration; recursive-CTE closure view for roll-up; no money column on categories (Data Model, Starter Tree) |
| CAT-02 | Own-account transfers excluded from spending and income | Stored assignment into a transfer-kind category; built-in synced-IBAN pass; kind-based exclusion replaces the IBAN CTE in `LedgerQueryStore` (Pattern 5, Pitfalls 1 and 2) |
| CAT-03 | Rules in the database categorise new transactions | Pure `RuleMatcher` in `Ledger.Domain`, no regex, specificity tuple, engine hook after ingestion (Pattern 3, Pattern 5) |
| CAT-04 | Source and confidence on every assignment | `transaction_categorisations` columns with CHECK constraints; surfaced in MCP rows and `reporting` views (Data Model) |
| CAT-05 | Low-confidence, Tikkie and PSP transactions go to a review queue | Queue = unassigned; intermediary flag plus second-signal rule; conflict goes to queue (Pattern 3) |
| CAT-06 | Claude works the queue via MCP and the user confirms | `category_proposals` table, held until a confirmed batch (Pattern 4, MCP Tool Surface) |
| CAT-07 | Correction becomes a rule; preview before any history change | Preview creates a server-stored change set; apply consumes it (Pattern 2, Pattern 3) |
| CAT-08 | Claude proposes tree refinements | Tree writes through the same audited pipeline; merge/remove are confirmation-gated (Pattern 1, Data Model) |
| ADV-06 | Audit log with before/after, client, undo | Append-only `audit_operations` and `audit_items`, `IUndoHandler` per object type, `UndoPlanner` (Pattern 1, Pattern 6) |
| ADV-07 | Untrusted marking, explicit parameters, confirmation, rate limit | Verified MRTR elicitation path plus single-use token fallback, `UntrustedText`, `InstructionTextDetector`, `WriteGuard` (Confirmation Mechanics, Patterns 2, 7, 8) |
| DASH-02 | Category drill-down and trends in Grafana | New "Spending" dashboard, new panel types, `reporting` views, data links with `var-category` (Grafana section) |
| PLAN-09 | Comparison with the household's own history | Three baselines, like-for-like month-to-date, reliability by uncategorised share; one server tool plus Grafana table (Pattern 9) |
| OPS-08 | Review-queue size on `/metrics` | Gauge refreshed with the existing refresher pattern (Pattern 10) |
</phase_requirements>

## Project Constraints (from CLAUDE.md)

Source: `/mnt/Data/repos/ing-dashboard/.claude/CLAUDE.md` (read this session). These have the same authority as locked decisions.

- **No planning references outside `.planning/`.** No requirement keys, decision ids, phase or plan numbers, or planning document names in code, `///` comments, string literals, log/exception text, test names, dashboards, alert text, MCP tool names/descriptions, docs, scripts or config. The repository lint enforces it: `build/lint/checks/10-repo-rules.sh` greps `\b(SEC|OPS|API|DASH|INGEST|CAT|PLAN|ADV|WEB|REF)-[0-9]{2}\b`, `\bD-[0-9]{2}\b`, `\b[Pp]hase[-_ ]?[0-9]+\b` and planning file names. Test display names such as "Phase 4 ..." or "D-12 ..." will fail lint. [VERIFIED: build/lint/checks/10-repo-rules.sh:12-18]
- **Comments: `///` only.** The lint flags any `//` that is not `///` and not after a colon, except under `/Migrations/`. [VERIFIED: build/lint/checks/10-repo-rules.sh:22-26]
- **Public repository, no personal data.** No merchant, counterparty, IBAN, hostname or real transaction text in code, seeds, fixtures, dashboards or docs. Rules and counterparty-specific categories live only in the database. The starter tree names no merchant. All fixtures synthetic, including PSP-, Tikkie- and injection-shaped descriptions.
- **Branching:** work on a branch, never `main`. Current branch is `milestone/v1-household-ledger`.
- **Local database:** real-database tests use the user's own PostgreSQL container via `dotnet user-secrets` (`ConnectionStrings:TestAdmin`). No Testcontainers. Do not tear the container down.
- **No LLM API calls from the app.** Unclear transactions wait in the queue for a Claude session; nothing in this phase may call a model.
- **Security first:** bank access stays read-only; secrets never in logs, exceptions, metric labels or MCP output; only `/mcp` is public; runtime role has no DDL; PostgreSQL stays on its Unix socket.
- **Stack override:** PostgreSQL with Npgsql, not SQL Server.

## Summary

The phase adds four things on top of the Phase 3 read-only ledger: a stored category model (tree, rules, assignments, proposals, notes), the first write tools with a reusable audited-write pipeline, an injection defence around bank text, and a drill-down Grafana dashboard. Almost everything can be built with the packages already locked in the repository. No new NuGet package is needed. The only genuinely unknown technology question, how a human confirmation reaches a stateless 2026-07-28 MCP server, was settled by running a spike against the pinned `ModelContextProtocol.AspNetCore` 2.2.0 this session (see Confirmation Mechanics).

The confirmation result changes the D-20 plan in one important way. `McpServer.ElicitAsync` throws "Elicitation is not supported in stateless mode" on every protocol version, so the obvious call is unusable. The working mechanism is multi round-trip requests (MRTR): the tool reads the client's per-request capability declaration, throws `InputRequiredException` carrying an elicitation request and a protected `requestState`, and the client retries the same tool call with the user's answer. A client that does not declare elicitation (the SDK client without a handler, or any client on the 2025-11-25 revision) is visible to the server as "capability absent", which selects the token fallback. The capability comes from client-set transport metadata, never from tool arguments or bank text. Claude Code additionally lets a server mark a tool with `_meta["anthropic/requiresUserInteraction"] = true`, which forces a permission prompt on every call, even in bypass modes, and offers no "don't ask again". That turns the fallback's human gate from a documentation request into a client-enforced control for Claude Code.

The remaining risk is concentrated in a few places the planner must treat as first-class work: the existing database tests enumerate privileges and forbid SQL functions in `public` and `reporting` (so no `ltree`, triggers or SQL functions), the existing dashboard parity test compares panel targets across languages (so language-specific SQL needs a constant variable), the existing MCP tests assert "grouped by counterparty, not category" wording, and the reporting view's date fallback order differs from the query store's. Each is called out with its remedy.

**Primary recommendation:** Build one audited write pipeline (domain service, one DB transaction for change plus audit operation, append-only audit tables, per-object undo handlers) and route every write through it; gate multi-row and structural writes with a server-stored change set that is confirmed either by MRTR elicitation (when the client declares it) or by a single-use token on a tool flagged `requiresUserInteraction` (when it does not); mark all bank text as untrusted in a structured wrapper; keep rules/categories/assignments in new append-or-update tables with privileges decided explicitly in the runtime-role matrix test.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Rule matching, specificity, undo conflict logic, comparison math, text cleaning, injection patterns | Domain (`Ledger.Domain`, pure C#) | none | Must be unit-testable without EF or HTTP; Phase 3 layering |
| Category, rule, assignment, proposal, audit, confirmation persistence | Repository (`Ledger.Repository` stores, EF plus raw SQL) | PostgreSQL constraints | Atomic change-plus-audit in one transaction; privileges enforced by roles |
| Audited write orchestration, write guard, confirmation service, REST audit endpoints | Service (`Ledger.Service`) | none | Domain services here write the audit entry; tools stay thin adapters |
| MCP tools, elicitation, annotations, untrusted wrapping | Service/Mcp (`Ledger.Service/Mcp`) | none | Adapters only; no store or DbContext dependency (enforce by test) |
| Rule engine run after ingestion, suspicious-text flagging | Service/Ingestion (`SyncOrchestrator` hook) | Repository | Runs on rows touched by the sync; failure must not fail the sync |
| Drill-down dashboards, comparison tables | Grafana (generated JSON) | `reporting` views | Reads only `reporting.*` as `grafana_reader` |
| Queue, proposals, suspicious, write-limit metrics | Service/Metrics (prometheus-net) | Prometheus and Grafana alert rules | Opaque labels only |
| Backlog grouping and proposing | Claude (outside the app) | MCP read tools | The app never calls a model |

## Standard Stack

No new packages. Everything below is already locked in the repository; the Standard Stack for this phase is "use what is there".

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `ModelContextProtocol.AspNetCore` | 2.2.0 | MCP server, tools, MRTR `InputRequiredException`, `[McpMeta]`, `AddAuthorizationFilters()` | Already pinned. Quote: `<PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.2.0" />` [VERIFIED: Ledger.Service/Ledger.Service.csproj:10] |
| `Npgsql.EntityFrameworkCore.PostgreSQL` via `Microsoft.EntityFrameworkCore` | 10.0.12 (tool pin `dotnet-ef` 10.0.12) | Entities, migrations, raw SQL (`Database.SqlQuery<T>($"...")`) | Existing stores use interpolated raw SQL, which EF parameterises [VERIFIED: .config/dotnet-tools.json `"dotnet-ef": { "version": "10.0.12"`; Ledger.Repository/Stores/LedgerQueryStore.cs:302 `return await dbContext.Database.SqlQuery<GroupedRow>($"""`] |
| `prometheus-net.AspNetCore` | 8.2.1 | Static counters and gauges on `/metrics` | Existing pattern in `McpMetrics`, `SyncMetrics`. Quote: `<PackageReference Include="prometheus-net.AspNetCore" Version="8.2.1" />` [VERIFIED: Ledger.Service/Ledger.Service.csproj:12] |
| `OpenIddict.AspNetCore` | 7.7.1 | OAuth server; `ClaimsPrincipal.GetPresenters()` is the extension that reads the client id the server attaches by default (`AttachDefaultPresenters` handler exists in OpenIddict.Server 7.7.1) | Quote: `<PackageReference Include="OpenIddict.AspNetCore" Version="7.7.1" />` [VERIFIED: Ledger.Service/Ledger.Service.csproj:11; XML docs of OpenIddict.Abstractions and OpenIddict.Server list `GetPresenters` and `AttachDefaultPresenters`]. That the validated reference-token principal actually exposes it is [ASSUMED] until an integration test asserts it (Open Question 3) |
| ASP.NET Core Data Protection | shipped with ASP.NET Core 10 | AEAD protection of the MRTR `requestState`; `ITimeLimitedDataProtector` for expiry | Key ring already persisted (see `ISecretProtector`); do not hand-roll HMAC |
| `System.Threading.RateLimiting` | in `Microsoft.AspNetCore.App` 10.0.12 | `SlidingWindowRateLimiter` / `PartitionedRateLimiter` for write calls | Present in the shared framework, no package needed [VERIFIED: ls of /usr/lib/dotnet/shared/Microsoft.AspNetCore.App/10.0.12 lists `System.Threading.RateLimiting.dll`] |
| Grafana | 13.2.3 (pinned) | Dashboards, alert rules | `GRAFANA_VERSION_PIN=13.2.3` [VERIFIED: deploy/versions.env:20] |

### Supporting (test)

| Library | Version | Purpose |
|---------|---------|---------|
| `xunit.v3` | 4.0.1 | Test framework, Microsoft.Testing.Platform runner (`global.json` sets `"runner": "Microsoft.Testing.Platform"`) |
| `FluentAssertions` | 8.11.0 | Assertions |
| `NSubstitute` | 6.2.0 | Mocks in unit tests |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.10.0 | `FakeTimeProvider` for TTL, rate-window and month-to-date tests |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.12 | In-process host for MCP and REST tests |

[VERIFIED: Ledger.UnitTests/Ledger.UnitTests.csproj and Ledger.IntegrationTests/Ledger.IntegrationTests.csproj package references]

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| MRTR elicitation in a stateless server | `HttpServerSessionMode.StatefulForInitializeClients` so legacy-handshake clients get real sessions and `ElicitAsync` | Needs session affinity, GET/DELETE endpoints and `Mcp-Session-Id` handling on the public endpoint, and only helps clients on the old revision. Not recommended: keep Stateless and give legacy clients the token fallback [CITED: ModelContextProtocol.AspNetCore.xml `HttpServerSessionMode`] |
| Recursive CTE over an adjacency list | `ltree` extension or a materialised path | `ltree` installs functions into `public`; the existing role test fails when `pg_proc` has any row in `public` or `reporting`. Quote: `WHERE n.nspname IN ('public', 'reporting')` ... `.Should().BeEmpty("a function that every role can execute by default must be reviewed before it is added")` [VERIFIED: Ledger.IntegrationTests/Database/DatabaseRoleTests.cs:266-271]. A materialised-path column costs rewrite work on every re-parent |
| Stored synced-account transfer assignments | Derive the transfer category at query time with the existing IBAN CTE | Derived keeps old behaviour with no backfill but duplicates the IBAN logic in every view and query and cannot carry provenance. Stored is what the decision text says; accept the backfill and a pass after account selection changes |

**Installation:** none.

**Version verification:** versions read from the project files and lock-restored packages this session. The dev machine runs .NET SDK 10.0.112 and runtimes 10.0.12 [VERIFIED: `dotnet --version`, `dotnet --list-runtimes`].

## Package Legitimacy Audit

No external package is added by this phase, so no `package-legitimacy check` run is required. `System.Threading.RateLimiting` is part of the ASP.NET Core shared framework already in use.

| Package | Registry | Age | Downloads | Source Repo | Verdict | Disposition |
|---------|----------|-----|-----------|-------------|---------|-------------|
| (none added) | n/a | n/a | n/a | n/a | n/a | n/a |

**Packages removed due to SLOP verdict:** none
**Packages flagged as suspicious SUS:** none

If planning later decides to add a package (for example an unaccent/diacritic helper), it must be run through the Package Legitimacy Gate first. Prefer `string.Normalize(NormalizationForm.FormKD)` plus category filtering, which is in the BCL.

## Confirmation Mechanics (verified by spike)

This section answers the three D-20 research questions. Evidence is a throwaway ASP.NET Core app against `ModelContextProtocol.AspNetCore` 2.2.0 (the pinned version) with `WithHttpTransport(t => t.Stateless = true)` and the SDK's own `McpClient` as the caller. The spike lives outside the repository (scratchpad) and is not committed; the code that matters is in Code Examples. [VERIFIED: spike run 2026-10-13]

| Caller | Negotiated revision | Per-request `_meta.clientCapabilities` | `IsMrtrSupported` | Tool sees elicitation capability via `RequestContext.JsonRpcRequest.Context.ClientCapabilities` | Result |
|--------|--------------------|-----------------------------------------|-------------------|--------------------------------------------------------------------------------------------|--------|
| SDK client with an elicitation handler | 2026-07-28 | `{"elicitation":{"form":{}}}` | True | present | Tool threw `InputRequiredException`; the client showed its handler the message; the retry carried `InputResponses["confirm"] = {"action":"accept","content":{"confirm":true}}` and `RequestState = "state-abc"` echoed |
| SDK client without a handler | 2026-07-28 | `{}` | True | null | Tool took the fallback branch |
| SDK client forced to 2025-11-25 with a handler | 2025-11-25 | none (no `_meta`) | False | null | Tool took the fallback branch |

Further observations from the same run:

- `McpServer.ClientCapabilities` is null in all three cases in stateless mode, and so is `RequestContext.Server.ClientCapabilities`. The capability must be read from `context.JsonRpcRequest.Context?.ClientCapabilities`. The SDK documentation says the property is null "in stateless transport mode, where server-to-client requests are unsupported" [CITED: ModelContextProtocol.Core.xml `McpServer.ClientCapabilities`].
- `server.ElicitAsync(...)` inside a stateless tool throws `System.InvalidOperationException: Elicitation is not supported in stateless mode.` for every caller. It is unusable here; do not write tools that call it.
- `context.JsonRpcRequest.Context?.User` returns the `ClaimsPrincipal` set by ASP.NET Core authentication, so the OAuth subject and client id are reachable inside a tool.

**Answers to the three questions in the decision text**

1. **Which clients support elicitation.** Claude Code: yes. Its documentation says elicitation dialogs "appear automatically", that form mode shows a dialog and URL mode asks to open a link, and that on connections using revision 2026-07-28 it declares `elicitation: {form: {}, url: {}}`. The v2 runtime that speaks 2026-07-28 is the default from Claude Code v2.1.274, and it "asks HTTP, stdio, and claude.ai connector servers whether they support the newer revision" [CITED: code.claude.com/docs/en/mcp]. Two third-party bug reports describe Claude Code advertising only form mode on some builds; form mode is all this phase needs. claude.ai web/mobile and Claude Desktop: I found no documentation either way. A search for connector documentation returned nothing on elicitation [ASSUMED: treat as unsupported until the hosted clients are connected; the token fallback covers them]. A user-configured Claude Code `Elicitation` hook can auto-answer dialogs [CITED: code.claude.com/docs/en/mcp], so a hook that answers "accept" defeats the dialog by the user's own configuration; say so in the operator docs.
2. **How the server learns support without an `initialize` handshake.** The client declares capabilities on every request in `_meta["io.modelcontextprotocol/clientCapabilities"]`, and the spec forbids inferring them from earlier requests [CITED: modelcontextprotocol.io/specification/2026-07-28/client/elicitation]. In this SDK that value is `RequestContext.JsonRpcRequest.Context.ClientCapabilities` (spike above). Read it per call; never cache it.
3. **Bank text cannot switch elicitation to the fallback.** The capability value comes from client-set transport metadata. No tool argument and no bank text feeds it. The one real risk is the other direction: if the server accepted a fallback token from a client that does declare elicitation, an injected model could mint a token with a preview and apply it without any dialog. Rule: **when the request declares elicitation, a confirmation token is refused and the dialog is required.** Add a test where a tool call carries a valid token from an elicitation-capable client and is refused.

**Normative constraints from the 2026-07-28 revision that shape the design** [CITED: modelcontextprotocol.io/specification/2026-07-28/basic/patterns/mrtr]:

- Servers MUST treat `requestState` as attacker-controlled input and MUST protect its integrity (HMAC or AEAD) if it influences authorisation or business logic.
- Servers SHOULD put the authenticated principal, a short expiry and a digest of the originating request inside the protected state. These bound replay but do not give single use; a once-only redemption "MUST" be enforced server-side. This is why the change set is stored in the database and consumed atomically.
- Servers MUST NOT send an input request the client did not declare support for.
- `InputRequiredResult` is allowed only on `tools/call`, `prompts/get` and `resources/read`.
- Elicitation form mode MUST NOT carry secrets. The dialog text contains counts and category names only.

**Claude Code client-enforced approval.** `_meta["anthropic/requiresUserInteraction"] = true` on a tool in `tools/list` makes Claude Code show that tool's permission prompt on every call, "even in `acceptEdits`, `auto`, and `bypassPermissions` permission modes", with no "don't ask again" and with allow rules unable to skip it. Requires Claude Code v2.1.214 or later [CITED: code.claude.com/docs/en/mcp]. The C# SDK sets tool `_meta` with `[McpMeta("anthropic/requiresUserInteraction", true)]` (constructor overload `(string name, bool value)` exists) [VERIFIED: ModelContextProtocol.Core.xml `McpMetaAttribute.#ctor(String,Boolean)`]. Other clients ignore the key.

## Architecture Patterns

### System Architecture Diagram

```
 bank sync (daily)                     Claude (Code now; hosted clients later)
       |                                          |  OAuth 2.1 bearer, /mcp, stateless
       v                                          v
 SyncOrchestrator --ApplyAsync--> transactions   MCP tool adapters (thin; no store or DbContext dependency)
       |                              ^             |  read tools         |  write tools
       | after each account           |             v                     v
       v                              |      LedgerQueryService     Audited write services
 CategorisationEngine                 |      (totals, search,       (CategorisationService, RuleService,
  - built-in synced-IBAN/own-account  |       compare, queue)         CategoryTreeService, ConfirmationService)
  - RuleMatcher (pure, no regex)      |             |                     |  1 DB transaction:
  - intermediary + second signal      |             |                     |  change + audit operation + items
  - InstructionTextDetector           |             |                     |  WriteGuard (rate + batch cap)
       |  INSERT ... ON CONFLICT      |             |                     |  UntrustedText on every bank string out
       |  DO NOTHING (user wins)      |             v                     v
       +----------------------------->+---- PostgreSQL (public schema, runtime role)
                                      |       categories, categorisation_rules, transaction_categorisations,
 REST (API key, operator only)        |       category_proposals, transaction_notes, transaction_text_flags,
  GET changes / POST undo ------------+       intermediary_counterparties, own_accounts,
                                      |       audit_operations (append-only), audit_items (append-only),
 Grafana (grafana_reader) <-- SELECT -+       pending_changes (confirmation state)
  Spending dashboard EN/NL      reporting.* views (no runtime privileges, migrator-owned)

 Confirmation of a bulk or structural write
   preview (change_ledger) -> pending_changes row {principal, client, fingerprint, items, expiry}
   apply (apply_changes) -> read per-request client capabilities
        elicitation declared -> InputRequiredException (message from counts + category names only, protected requestState)
                                 -> retry with accept -> consume row atomically -> apply + audit
        elicitation absent   -> require single-use token (SHA-256 stored) -> consume atomically -> apply + audit
        elicitation declared AND token presented -> refuse
```

### Recommended Project Structure

```
Ledger.Domain/Categorisation/       pure logic and store interfaces
  CategoryTree.cs                   roll-up, ancestors, cycle and kind checks
  RuleMatcher.cs, RuleSpecificity.cs, RuleCriteria.cs
  UntrustedText.cs                  clean, mask, cap
  InstructionTextDetector.cs
  ChangeSetFingerprint.cs
  UndoPlanner.cs                    revert vs skipped, no I/O
  ComparisonCalculator.cs           three baselines, like-for-like window, reliability
  I*Store.cs                        interfaces only
Ledger.Repository/Entities/ and Stores/   new entities and stores; one migration for tables, grants, views, seed tree
Ledger.Service/Categorisation/      audited write services, WriteGuard, ConfirmationService, CategorisationEngine, metrics refresher
Ledger.Service/Mcp/                 new tool classes beside LedgerTools; ServerInstructions; UntrustedFields wrapper records
Ledger.Service/Endpoints/AuditEndpoints.cs   GET /api/v1/changes, POST /api/v1/changes/{id}/undo
Ledger.Dashboards/Definitions/SpendingDashboard.cs, Model/Panels.cs (+ Stat, TimeSeries, BarChart), translations.json
deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml   new rules
docs/mcp.md, docs/rest-api.md, docs/monitoring.md
```

The project's own phrase "domain service" maps to `Ledger.Service` here: the existing orchestration classes (`LedgerQueryService`, `SyncOrchestrator`) live there and use domain stores through interfaces. Keep that, and keep `Ledger.Domain` free of EF and HTTP.

### Data Model

All tables follow the existing conventions: snake_case, `uuid` keys (no sequences, so no sequence privileges), enums stored as lower-snake text with a CHECK built from `EnumText.CheckList<T>()` and a `SnakeCaseEnumConverter<T>`, `DeleteBehavior.Restrict`. [VERIFIED: Ledger.Repository/Conventions/EnumText.cs; Ledger.Repository/LedgerDbContext.cs `ConfigureProviderCalls`]

| Table | Key columns | Runtime role privilege (must be added to the matrix test) |
|-------|-------------|----------------------------------------------------------|
| `categories` | `id`, `key` (unique slug, immutable), `parent_id` (self FK, null for bands), `kind` (`spending`/`income`/`transfer`), `name_en`, `name_nl`, `sort_order`, `archived_at`, `merged_into_id`, `created_at` | SELECT, INSERT, UPDATE |
| `categorisation_rules` | `id`, `category_id`, `iban_norm`, `name_exact`, `name_contains`, `description_words` (text[], all must appear), `direction`, `min_amount`, `max_amount`, `account_id`, `created_at`, `created_by_operation_id`, `removed_at`; CHECK at least one identity criterion; CHECK `min_amount <= max_amount`; partial unique index on a criteria fingerprint where `removed_at IS NULL` | SELECT, INSERT, UPDATE |
| `transaction_categorisations` | `transaction_id` (PK, FK, one row per transaction), `category_id`, `source` (`rule`/`claude`/`user`), `confidence` (`high`/`medium`/`low`/`certain`), `rule_id` (null when built-in), `proposal_id`, `set_at`, `operation_id` (null for automatic assignment); CHECK `(source = 'user') = (confidence = 'certain')`; CHECK `NOT (source = 'rule' AND confidence = 'low')` | SELECT, INSERT, UPDATE, DELETE |
| `category_proposals` | `id`, `transaction_id`, `category_id`, `reason` (capped, cleaned), `confidence` (`high`/`medium`/`low`), `status` (`pending`/`confirmed`/`rejected`/`superseded`), `batch_id`, `created_at`, `resolved_at`, `proposed_by_client`; partial unique index on `transaction_id` where `status = 'pending'` | SELECT, INSERT, UPDATE |
| `intermediary_counterparties` | `id`, `counterparty_ref`, `name_norm`, `iban_norm` (optional), `created_at`, `removed_at` | SELECT, INSERT, UPDATE |
| `own_accounts` | `id`, `iban_norm` (unique where active), `label`, `category_id` (a transfer-kind category), `created_at`, `removed_at` | SELECT, INSERT, UPDATE |
| `transaction_notes` | `transaction_id` (PK), `note` (<= 140 chars), `updated_at`, `operation_id` | SELECT, INSERT, UPDATE, DELETE |
| `transaction_text_flags` | `transaction_id` (PK), `pattern` (fixed code), `flagged_at` | SELECT, INSERT |
| `audit_operations` | `id`, `occurred_at`, `kind`, `client_kind` (`mcp`/`rest`/`system`), `client_id`, `item_count`, `rule_id`, `proposal_batch_id`, `reverts_operation_id` (unique) | SELECT, INSERT (append-only, as `provider_calls` is) |
| `audit_items` | `operation_id`, `seq`, `object_type`, `object_id`, `before` (jsonb), `after` (jsonb); PK `(operation_id, seq)` | SELECT, INSERT (append-only) |
| `pending_changes` | `id`, `token_sha256` (unique), `kind`, `principal_sub`, `client_id`, `fingerprint`, `items` (jsonb), `created_at`, `expires_at`, `consumed_at`, `consumed_by_operation_id` | SELECT, INSERT, UPDATE |

Why a separate assignments table and not columns on `transactions`: the runtime role has only column-level UPDATE on `transactions` and a test enumerates exactly which columns. Quote: `GRANT UPDATE (status, booking_date, value_date, transaction_date, amount, counterparty_name, counterparty_iban, description, match_flag, updated_at, booked_at, dropped_at) ON TABLE transactions TO ledger_runtime;` [VERIFIED: Ledger.Repository/Migrations/20260930181840_AddLedgerIngestion.cs:12]. A separate table leaves that test and the immutable-ledger guarantee untouched, and the assignment keys on the immutable transaction id the way the decisions require.

Why "undone" is derived, not stored: `audit_operations.reverts_operation_id` points from the undo operation to the one it undid, with a unique index. An operation counts as undone while an operation that reverts it exists and is not itself reverted. Nothing in the audit tables is ever updated, so the runtime role gets INSERT and SELECT only and an injected or buggy write path cannot rewrite history.

Privilege matrix test, existing and to be edited, with the pattern to follow. Quote: `"public.provider_calls=SELECT,INSERT",` and `"reporting.account_status=",` `"reporting.accounts=",` `"reporting.transactions="` [VERIFIED: Ledger.IntegrationTests/Database/DatabaseRoleTests.cs:213-219]. The `reporting.*=` entries mean the runtime role has no privilege on any reporting view; this is a deliberate invariant, so the app's own comparison tool must read base tables (it cannot read the Grafana views). Every new `reporting.*` view must be added to that list with an empty privilege set.

Starter tree migration data (Claude's discretion; names are household-neutral and name no merchant). Roughly 32 nodes, each with English and Dutch names, keyed by slug, inserted with `INSERT ... SELECT` resolving parents by key:

| Band (kind) | Categories (EN / NL) |
|-------------|----------------------|
| Fixed costs / Vaste lasten (spending) | Housing / Wonen; Energy / Energie; Water / Water; Local taxes / Gemeentelijke heffingen; Insurance / Verzekeringen; Health insurance / Zorgverzekering; Phone, internet and TV / Telefoon, internet en tv; Loans and credit / Leningen en krediet; Childcare / Kinderopvang |
| Reserve expenses / Reserveringsuitgaven (spending) | Clothing and shoes / Kleding en schoenen; Home and furnishing / Inventaris en inrichting; Home maintenance / Onderhoud woning en tuin; Health costs / Zorgkosten en eigen risico; Education / Onderwijs; Gifts and celebrations / Cadeaus en feestdagen; Holidays / Vakantie |
| Household expenses / Huishoudelijke uitgaven (spending) | Groceries / Boodschappen; Personal care / Persoonlijke verzorging; Household supplies / Huishoudelijke artikelen; Pets / Huisdieren; Transport / Vervoer; Cash / Contant geld |
| Leisure / Vrije tijd (spending) | Eating out / Uit eten en horeca; Subscriptions / Abonnementen; Sports and hobbies / Sport en hobby's; Entertainment / Uitjes en cultuur; Personal money / Persoonlijk besteedbaar |
| Income / Inkomen (income) | Salary / Salaris; Benefits and allowances / Toeslagen en uitkeringen; Other income / Overige inkomsten; Personal money received / Persoonlijk ontvangen |
| Transfers / Overboekingen (transfer) | Own accounts / Eigen rekeningen; Savings / Sparen |

Names follow the generic structure described in `.planning/research/FEATURES.md` (Nibud section); no reference figures appear anywhere and no money column exists on `categories`. [CITED: .planning/research/FEATURES.md lines 66-84]

### Pattern 1: The audited write pipeline (the pattern every later write tool reuses)

**What:** One `IAuditedChange` shape. A service builds a typed plan (what to change, plus the `before` snapshot read under `SELECT ... FOR UPDATE`), then a repository method applies the plan, inserts the `audit_operations` row and one `audit_items` row per object, and commits, all in one database transaction. The MCP tool or REST endpoint never sees a store.

**When to use:** every write: recategorise, propose, note, rule add/remove, category add/rename/merge, own account, intermediary flag, confirmed history application, undo.

**Rules that make it hold:**
- Every mutating store method takes an `AuditContext(clientKind, clientId, ruleId?, proposalBatchId?, revertsOperationId?)` argument, so a write without audit does not compile.
- `object_type` + an `IUndoHandler` per type (assignment, category, rule, note, intermediary, own account). Phase 5 and 6 add handlers for budgets, goals, memory and reviews; they do not touch the pipeline.
- Automatic categorisation of newly synced rows writes no audit entry (it is not a data change through MCP or REST). Provenance columns answer "why is this X".
- An architecture test asserts that no type in `Ledger.Service.Mcp` has a constructor or method parameter of a `Ledger.Domain.*.I*Store` type or of `LedgerDbContext`, so the tool-never-writes rule is checked, not remembered.
- `before`/`after` hold ids, category keys, numbers and household notes, never bank text. The recent-changes view and tool then cannot leak bank text.

### Pattern 2: Two-path confirmation over a server-stored change set

**What:** A preview action computes the exact change set server-side (transaction ids, from and to values), stores it in `pending_changes` with a fingerprint, the principal's `sub`, the OAuth client id and a short expiry, and returns counts plus either an instruction to expect a dialog or a single-use token. Apply works only from the stored row; the model supplies an id (and a token on the fallback path), never the items.

**Why stored rows:** the SDK offers nothing for single use, the spec requires once-only redemption to be enforced server-side, and the app already has this pattern: `BankAuthorizationEntity` stores only the SHA-256 of a one-time state with `ExpiresAt` and `ConsumedAt` [VERIFIED: Ledger.Repository/Entities/BankAuthorizationEntity.cs, properties `StateSha256`, `ExpiresAt`, `ConsumedAt`].

**Atomic consume (the only way a change set is redeemed):** one statement, no read-then-write:

```sql
UPDATE pending_changes
SET consumed_at = {now}
WHERE id = {id}
  AND principal_sub = {sub}
  AND client_id = {clientId}
  AND consumed_at IS NULL
  AND expires_at > {now}
RETURNING items, fingerprint, kind;
```

Zero rows means refuse and count `ledger_confirmations_total{outcome="invalid"}`. On the fallback path the lookup key is `token_sha256` compared against `SHA-256(presented token)`.

**Elicitation path:** the first `apply_changes` call returns `InputRequiredException` with one boolean confirm field and a message built only from the stored counts and cleaned category names (never bank text), and a `requestState` protected with `ITimeLimitedDataProtector` containing `{changeSetId, sub, clientId, fingerprint}`. The retry is accepted only if the state verifies, the principal and client match, `InputResponses["confirm"].action == "accept"`, and the row consumes atomically. Decline or cancel returns "not applied" and leaves the row to expire.

**Token lifetime and fingerprint** are the planner's to tune (CONTEXT lists them as discretion). Recommended: 10 minutes; fingerprint = SHA-256 over a canonical, ordered serialisation of `(kind, principal, sorted [(transaction_id, from_category_id, to_category_id)])`; store it and re-verify on apply as a cheap tamper check on the stored items.

**Optional UX:** the fallback permission prompt shows only tool arguments. Prefix the token with a readable label, for example `37-changes.` then the random part, inside the protected value, so the person approving sees what is being approved.

### Pattern 3: Rule matching with specificity (pure domain)

**What:** `RuleMatcher.Decide(transaction, rules, intermediaryFlags)` returns `Assign(category, rule, confidence)` or `Queue(reason)` where reason is `no_rule`, `intermediary` or `conflict`.

- Normalise with the existing `TextNormalizer.ForMatching` (NFC, trimmed, whitespace-collapsed, upper-invariant) at rule write time and at match time. [VERIFIED: Ledger.Domain/Ingestion/TextNormalizer.cs]
- Criteria: exact IBAN equality on `IbanText.Normalize`; name exact equality; name contains (ordinal substring on normalised text); description words = every listed word appears as a whole token (split on non letter/digit). No regex anywhere; the rule model has no pattern field.
- Specificity is a tuple `(strongestIdentityTier, criteriaCount)` compared lexicographically, tier order IBAN 4, exact name 3, name-contains 2, description words 1. Direction, amount range and account each count as one extra criterion. Highest tuple wins; if two rules with the same top tuple point at different categories the result is `Queue(conflict)`; same tuple and same category picks the oldest rule id for provenance.
- Confidence: rule with an IBAN or exact-name criterion is `high`, otherwise `medium`. `low` is never produced by a rule. The combined case (exact name plus description words) is not covered by the decision text; treat it as `high` (Assumption A2).
- Intermediary guard, both at rule creation and at match time: a rule whose only identity criterion matches an intermediary-flagged counterparty is refused at creation; at match time a flagged counterparty with no matching rule that carries a description-words criterion or a fixed amount (`min_amount = max_amount`) yields `Queue(intermediary)`. This keeps old rules honest when a counterparty is flagged later.
- User-set assignments are never produced by this function and are never overwritten: the store inserts with `ON CONFLICT (transaction_id) DO NOTHING`.
- No manual priority column exists.

### Pattern 4: Assignment, queue and proposals

- **Queue = non-dropped transactions on synced accounts with no row in `transaction_categorisations`.** Proposals do not change queue membership; a pending proposal is shown beside the queue entry and the transaction stays uncategorised in every total until a confirmed batch.
- **Source values.** The decision text says an assignment the user made or confirmed is `user` and `certain`. A confirmed Claude proposal therefore becomes `source = 'user'`, with `proposal_id` kept so "Claude proposed, the user confirmed" stays visible. The value `claude` stays in the CHECK list because the requirement names it; see Open Question 2.
- **Engine run.** After each account's `ApplyAsync`, in `SyncOrchestrator`, wrapped so a failure is logged by exception type only, counted, and does not turn the sync into a failed sync. Candidates: unassigned, non-dropped rows with `updated_at >= runStartedAt` (insert and update both set `UpdatedAt = observedAt` [VERIFIED: Ledger.Repository/Stores/LedgerStore.cs:207, 300, 336, 362, 406]). Pending rows are included, the assignment stays on the immutable id when the row books.
- **Built-in transfer step.** Not a user rule: any row whose normalised counterparty IBAN equals a synced account's IBAN, or an active `own_accounts.iban_norm`, gets the Transfers-band category with `source = 'rule'`, `rule_id = NULL`, `confidence = 'high'`. Run it over all unassigned rows (not only touched rows) so it also fires after the account selection changes, plus once inside the migration as a backfill for existing rows. It never touches a row that already has an assignment.
- **Unassigned history.** Stays unassigned until a confirmed rule application or confirmed proposal batch. A new rule does not categorise rows already evaluated; only confirmed bulk application does (preview first).

### Pattern 5: Switching totals from IBAN equality to category kind

`ReadGroupedRowsAsync`, `ReadSearchRowsAsync` and `ReadCounterpartyRowsAsync` each build an `own_accounts` CTE and an `is_transfer` expression. Quote: `(t.status = 'booked' AND t.counterparty_iban IS NOT NULL AND upper(regexp_replace(t.counterparty_iban, '[[:space:]]+', '', 'g')) IN (SELECT iban FROM own_accounts WHERE iban <> '')) AS is_transfer` [VERIFIED: Ledger.Repository/Stores/LedgerQueryStore.cs:319-322 and 413-416; the counterparty lookup at 489-491 has the same IBAN test without the `status = 'booked'` condition]. Replace the expression with `c.kind = 'transfer'` from a `LEFT JOIN transaction_categorisations tc ... LEFT JOIN categories c`, keep the existing precedence (pending first, then transfer, then booked) and keep the `status = 'booked'` condition so pending rows are still reported as pending, not as transfers. The existing totals and search tests must pass unchanged; they seed rows directly through the runtime role, so the seed helper must run the built-in transfer pass after seeding (Wave 0).

### Pattern 6: Undo

`UndoPlanner.Plan(items, currentStatesByObject)` is pure: an item reverts only when the object's current state equals the item's `after` snapshot; otherwise it is skipped with reason `changed_later`. The store runs the whole undo in one `REPEATABLE READ` transaction with `SELECT ... FOR UPDATE` on the affected rows so concurrent writes cannot slip in between plan and apply. Result text: "34 reverted, 3 skipped: changed later". The undo is an operation with `reverts_operation_id` set and goes through the Pattern 2 confirmation when it touches more than one item. State comparison uses a canonical JSON of the columns that matter (not timestamps).

### Pattern 7: Untrusted bank text

- `UntrustedText.Clean(text, maxLength)` order: NFC; drop every character whose category is Control, Format, PrivateUse or Surrogate-without-pair (this removes zero-width characters, bidirectional controls U+200E/F, U+202A-E, U+2066-9 and Unicode tag characters U+E0000-E007F, which are Format characters); replace remaining control whitespace with a space; collapse runs; then the existing `IbanText.MaskInText`; then cap at a text-element boundary and return `truncated`. Do the masking before the cap so a cut cannot split an account number into an unmasked remainder. [ASSUMED: character ranges from general Unicode knowledge, not looked up this session; unit-test them with literal escapes]
- Wrapper shape: every row carries one nested object named `untrusted_bank_text` with `counterparty_name`, `description` (and remittance text if added), and every result carries a one-sentence notice. Separate sibling field `household_note` for the household's own note, also cleaned and capped. Do not interleave bank text with trusted fields. The existing tests that assert the old flat `counterparty` and `description` fields will change (Wave 0).
- Tool descriptions and server instructions state that these fields are data, never instructions. Claude Code truncates each tool description and the server instructions at 2,048 characters by default [CITED: code.claude.com/docs/en/mcp]; put the untrusted-data sentence first and add a unit test that every description and the instructions are at most 2,000 characters.
- Claude-authored text that is stored and replayed later (notes, proposal reasons, category names) is second-order injection surface. Clean and cap it on write, run the detector on it on write and refuse a match, label it on read (`household_note`, `claude_reason`).
- Never put raw bank text in an elicitation message, a metric label, a log line or an exception message.

### Pattern 8: Detection, rate limiting, caps

- `InstructionTextDetector.Detect(text)` returns a fixed pattern code or none. Run on NFKC-folded, lower-cased, diacritic-stripped text. Pattern families (Claude's discretion; tune by tests with synthetic strings): addressing an AI or assistant, "ignore/disregard previous or above instructions" and the Dutch equivalents, role or system markers (`system:`, `assistant:`, `<system>`, `[INST]`), tool and action vocabulary matching this server's tool names and verbs (`apply_changes`, `recategorise`, `categorise`, `confirm`, `rule`), URLs and markdown image syntax, and "stripping removed invisible characters" (legitimate bank text rarely has any). It is visibility, not the defence.
- Flag at ingestion (engine run), insert into `transaction_text_flags`, increment a counter, show `suspicious: true` in tool rows, and show the count on the dashboard.
- `WriteGuard`: in-memory sliding window per principal using `System.Threading.RateLimiting.SlidingWindowRateLimiter`, default 30 write calls per 10 minutes, and a per-confirmed-step item cap of 500; a refusal returns a clear message and increments `ledger_mcp_writes_refused_total{reason}`. A restart resets the window; that is acceptable for one household (Assumption A6). If a rule application would exceed the cap, the preview says how many rounds are needed and each round is its own audited operation tied to the same rule (Open Question 4).

### Pattern 9: Comparisons (three baselines, like for like)

`ComparisonCalculator` (domain, pure) takes daily category totals and an as-of date from `TimeProvider` plus the Amsterdam zone. For a selected category node and its children:

- Baselines: previous month, same month last year, average of the last 12 full months (completed calendar months before the current one).
- Current month is compared like for like: month-to-date through today against the same number of days of the comparison month, with the full previous month shown alongside. If the comparison month is shorter (day 31 against February) clamp to its last day and say so in the result. A month with no rows yields a null baseline with a reason, not zero.
- Each period states its uncategorised share (share of absolute booked amount, non-transfer). Above a threshold the result says "not reliable yet" instead of showing the jump. Default threshold 20 percent, stated in the result, tunable (Assumption A5).
- Net within a category per decision: refunds and paybacks lower the category; only income-kind categories count as income.
- Grafana computes the same figures in SQL over `reporting` views. The runtime role cannot read `reporting.*`, so there are two implementations. Close the drift risk with a parity integration test that seeds one dataset, runs the Grafana panel SQL (macros expanded the way `DashboardQueryTests.Expand` does) and the MCP tool, and asserts equal figures for the same category, month and as-of date.

### Pattern 10: Metrics and alerts

Follow `McpMetrics` and `SyncMetrics`: static prometheus-net gauges and counters, created at zero at startup, opaque or fixed-word labels only. [VERIFIED: Ledger.Service/Mcp/McpMetrics.cs, Ledger.Service/Metrics/SyncMetrics.cs]

| Metric | Type | Labels | Meaning |
|--------|------|--------|---------|
| `ledger_review_queue_size` | gauge | none | unassigned non-dropped rows on synced accounts |
| `ledger_review_proposals_waiting` | gauge | none | proposals with status `pending` |
| `ledger_suspicious_bank_text_flagged_total` | counter | none | transactions newly flagged |
| `ledger_mcp_write_calls_total` | counter | `tool` | write calls by tool name |
| `ledger_mcp_writes_refused_total` | counter | `reason` (`rate_limit`, `batch_size`) | refused by the guard |
| `ledger_confirmations_total` | counter | `outcome` (`accepted`, `declined`, `cancelled`, `invalid`, `expired`) | confirmation results |
| `ledger_categorisation_errors_total` | counter | none | engine runs that threw |

Refresh the two gauges from the database at startup and every minute with a hosted service shaped like `SyncMetricsRefresher`, so a restart never hides the queue and the number visibly shrinks as work proceeds. [VERIFIED: Ledger.Service/Ingestion/SyncMetricsRefresher.cs `RefreshInterval = TimeSpan.FromSeconds(60)`] New Grafana alert rules in `household-rules.yaml` follow the existing shape: Prometheus datasource uid `prometheus`, threshold expression node, fixed `summary`, `labels: {}`, no query result interpolated into any text; `increase(ledger_mcp_writes_refused_total[15m]) > 0` and `increase(ledger_suspicious_bank_text_flagged_total[1h]) > 0`. [VERIFIED: deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml]

### Anti-Patterns to Avoid

- **Calling `server.ElicitAsync`** in a stateless server: it throws. Use `InputRequiredException`.
- **A `confirmed: true` or `force: true` tool parameter.** The model controls arguments; confirmation must come from a client dialog or a client-enforced approval.
- **Accepting a token from an elicitation-capable client.** Defeats the dialog.
- **A "match everything like X" selector on a write tool.** Write tools take transaction ids, ref values and change-set ids only.
- **Deleting or updating audit rows.** Undo appends a new operation.
- **Language-specific SQL in a panel target.** The parity test compares `targets` across languages; branch on a constant variable instead (Grafana section).
- **Reusing `reporting.transactions.effective_date` for period maths.** It orders its fallback `booking_date, transaction_date, value_date`; the query store orders `booking_date, value_date, transaction_date, first-seen day`. [VERIFIED: Ledger.Repository/Migrations/20260930181840_AddLedgerIngestion.cs:33 `COALESCE(t.booking_date, t.transaction_date, t.value_date)` versus Ledger.Repository/Stores/LedgerQueryStore.cs:316 `COALESCE(t.booking_date, t.value_date, t.transaction_date, (t.first_seen_at AT TIME ZONE {zoneId}::text)::date)`]

## MCP Tool Surface (recommendation within Claude's discretion)

The milestone cap is roughly 8 to 15 tools; Phase 3 already has four, and later phases still need budgets, goals, recurring costs, forecast and affordability, advisor memory and reviews. Spend six, leaving about five. The organising rule is honest annotations: **read tools and write tools never share a tool**, because the annotations apply to the whole tool and clients prompt on `ReadOnly = false`. The confirmation-gated actions share one tool so the client-enforced approval flag applies only to them.

| Tool (new) | Annotations | Actions | Notes |
|------------|-------------|---------|-------|
| `review_queue` | read-only, idempotent, closed world | queue page, proposals waiting, backlog grouped by `counterparty_ref` with counts and totals | Rows wrapped untrusted, each with `transaction_id`, `suspicious`; page cap 25 to 50 (large-result pitfall) |
| `category_overview` | read-only | tree with kind, EN/NL names, counts and totals; rules (criteria, category, match counts, no full IBAN); intermediaries; own accounts (label only) | Lets Claude pick category keys and propose refinements |
| `compare_spending` | read-only | one category node or its children against the three baselines | Server-computed with provenance; Claude never subtracts |
| `recent_changes` | read-only | list operations; one operation's items | `before`/`after` carry ids and keys, never bank text |
| `change_ledger` | `ReadOnly = false`, `Destructive = true`, `Idempotent = false`, `OpenWorld = false` | `recategorise` (one transaction id), `propose`, `set_note`, `add_category`, `rename_category`, `add_rule`, `remove_rule`, and the `preview_*` actions that create a stored change set (`preview_rule_application`, `preview_confirm_proposals`, `preview_merge_category`, `preview_own_account`, `preview_intermediary`, `preview_undo`) | Single-transaction recategorise, notes, proposals and rule or category edits need no confirmation; they are audited, rate limited and undoable. `Destructive = true` because it can overwrite an assignment; the hint covers the worst case of the whole tool |
| `apply_changes` | `ReadOnly = false`, `Destructive = true`, `Idempotent = false`, `OpenWorld = false`, plus `[McpMeta("anthropic/requiresUserInteraction", true)]` | apply a stored change set by `changeSetId` (and `confirmationToken` on the fallback path) | The only tool that redeems confirmations; arguments are ids only |

Existing `money_totals` and `search_transactions` gain `category` (key, includes everything beneath it), `uncategorised`, `groupBy = category`, and source and confidence in rows; their descriptions and `ServerInstructions` lose the "Categories do not exist yet" wording. [VERIFIED: Ledger.Service/Mcp/LedgerTools.cs `+ "Categories do not exist yet: pass the counterparty or description words that identify what is asked, ...`; Ledger.Service/Mcp/ServerInstructions.cs `"Until categories exist, totals are grouped by counterparty name, not by category: say so in the answer, ..."`] The cursor hash must include the new filters. [VERIFIED: Ledger.Service/Queries/LedgerQueryService.cs `var filterHash = SearchCursor.FilterHash(filter, statusText);`]

Search rows currently expose no transaction id; write tools need one. A random `Guid` id is opaque and safe to return. [VERIFIED: Ledger.Service/Queries/SearchResult.cs `public record SearchRow(` lists `date`, `booking_date`, `status`, `amount`, `direction`, `currency`, `counterparty_name`, `counterparty_ref`, `counterparty_account`, `description`, `account_key`, `account_name`, `internal_transfer` and no id; Ledger.Repository/Entities/LedgerTransactionEntity.cs `The row's primary key, immutable and never a provider value.`]

**Authorisation.** The MCP policy today requires only one scope. Quote: `.RequireAssertion(context => context.User.HasScope(ClientRegistrations.ReadScope)));` and `public const string ReadScope = "ledger.read";` and `var scopes = new List<string> { ClientRegistrations.ReadScope };` [VERIFIED: Ledger.Service/Mcp/McpEndpoint.cs:74; Ledger.Service/OAuth/ClientRegistrations.cs:16; Ledger.Service/Pages/Connect/Authorize.cshtml.cs:133]. The SDK offers `AddAuthorizationFilters()` plus `[Authorize(Policy = ...)]` on tool types, which also hides unauthorised tools from `tools/list` [CITED: ModelContextProtocol.AspNetCore.xml `AddAuthorizationFilters`]. Recommendation: add a `ledger.write` scope, register it in `RegisterScopes` and in the client permission list, grant it on the consent page, and require it on the two write tools. A leaked read-only token then cannot write, and the existing Claude Code connection keeps working read-only until it is re-approved. This is a user-visible consent change; see Open Question 1.

## Grafana: the Spending dashboard

**What exists.** `Ledger.Dashboards` generates `ledger-sync-en.json` and `ledger-sync-nl.json` from `SyncDashboard`, a translation file with one object per language, and a `generate`/`check` CLI. `Panel` has one derived type, `TablePanel`; `FieldDefaults` has only `Decimals`. [VERIFIED: Ledger.Dashboards/Model/Panels.cs `[JsonDerivedType(typeof(TablePanel))]`; Ledger.Dashboards/Model/FieldConfig.cs `public sealed record FieldDefaults(int? Decimals);`] Datasource: `new DatasourceRef("grafana-postgresql-datasource", "ledger-reporting")`; schema version 41; Grafana 13.2.3. [VERIFIED: Ledger.Dashboards/Definitions/SyncDashboard.cs; deploy/versions.env:20]

**Model additions needed** (no existing type covers them): `StatPanel`, `TimeSeriesPanel`, `BarChartPanel` (each with its own `options`), and richer `FieldDefaults` (`unit`, `color`, `custom`, `links`, `mappings`, `thresholds`). Panels are serialised by runtime type with no discriminator, so adding derived types needs only the `[JsonDerivedType]` entries. The exact option names for these panel types are [ASSUMED] from training knowledge. Authoritative path: build each panel once in a local Grafana 13.2.3 UI against a seeded database, copy the exported JSON into the C# records, and keep the generated output byte-stable with the existing drift check. Add unit assertions on the keys that matter instead of snapshotting everything.

**Language handling.** The parity test compares `targets` and `fieldConfig.defaults` across languages and compares variables property by property including `query`. [VERIFIED: Ledger.UnitTests/Dashboards/DashboardGeneratorTests.cs:126 `foreach (var property in new[] { "id", "type", "gridPos", "datasource", "targets", "fieldConfig.defaults" })`]. So category names cannot be selected with language-specific SQL. Use one hidden constant variable `lang` (`type: "constant"`, `hide: 2`, value `en` or `nl`) and write every query as `CASE WHEN '${lang}' = 'nl' THEN name_nl ELSE name_en END`. Update the parity test to skip variables of type `constant`. Panel titles, column headers and mappings stay in `translations.json` and in the per-language `organize` transformation, as today.

**Variables.** `category` (query variable, single value, no "All", default `all`): `SELECT <name by lang> AS __text, key AS __value FROM reporting.category_nodes ORDER BY sort_path`. The synthetic root `all` is a UNION ALL row in the view so "starts at the top, with all bands" needs no special case.

**Drill-down and breadcrumb.** A table of the selected node's children carries a data link on the name cell; the key column is hidden by a field override (`custom.hidden`) so it stays available to the link. Link URL (the generator knows the language, so the uid is literal): `/d/ledger-spending-en?var-category=${__data.fields["category_key"]}&${__url_time_range}`. A second table lists the ancestors of the selected node (view `reporting.category_closure`) with the same link form, which is the breadcrumb and the "up" path. Data links on table cells and the `__data.fields["name"]`, `${__url_time_range}` and `var-` syntax are documented [CITED: grafana.com/docs/grafana/latest/panels-visualizations/configure-data-links/]. Hiding the key column and still using it in a link is reported to work through the override but I found no current documentation for it; verify on 13.2.3 and fall back to a narrow visible key column if it does not (Assumption A7). Bar chart and time series also support data links, but a table link is the dependable click target.

**Panels (all read `reporting.*` only; `$__timeFilter(...)` for the time range; PostgreSQL time series need a column named `time` sorted ascending and `format: "time_series"`)** [CITED: grafana.com/docs/grafana/latest/datasources/postgres/query-editor/]:

| Row | Panel | Type | Source |
|-----|-------|------|--------|
| 1 | Income, spending, moved to savings and left, per month | time series (bars) | monthly sums from the category daily view by kind; "left" is income minus spending minus savings; month key built with `date_trunc('month', day)::timestamp AT TIME ZONE 'Europe/Amsterdam'` on date columns, not a Grafana time macro, so months follow the Amsterdam calendar |
| 1 | Queue size, proposals waiting, suspicious texts | stat x3 | `reporting.review_status` |
| 2 | Breadcrumb | table | closure view |
| 2 | Children of the selected category, this period, with Uncategorised as its own bar | bar chart plus the link table | rolled up per child; an `uncategorised` row appears at the top level so totals add up |
| 3 | Monthly trend of the selected category, last year as a faint second line | time series | two queries; the second shifts `time` by one year and is styled by an override (low opacity, dashed) |
| 3 | Comparison table: children with this period, previous month, same month last year, 12-month average, change in amount and percent, uncategorised share | table | comparison view over daily or monthly totals |
| 4 | Transactions beneath the selected category | table (limit 500) | extended `reporting.transactions` with category, source, confidence, note, suspicious |
| 5 | Recent changes | table | `reporting.audit_recent`: when, client, kind, item count, undone; no IBAN, no bank text |

**Views to add (migrator-owned, in `reporting`):** `category_nodes`, `category_closure` (recursive CTE with a depth guard), `category_daily_totals` (booked and pending flagged), `review_status`, `audit_recent`, a comparison view, and appended columns on `transactions`. Net per category is `money_out - money_in` for spending kinds and `money_in - money_out` for income, so refunds lower their category. The date rule must be the query store's fallback order, not the existing view's. Counting basis: booked only, with pending shown beside, so Grafana and the MCP tool never disagree (Open Question 5). New views inherit `SELECT` for `grafana_reader` through the default privileges set in the first migration. [VERIFIED: Ledger.Repository/Migrations/20260927183611_InitialCreate.cs:47 `"ALTER DEFAULT PRIVILEGES FOR ROLE ledger_migrator IN SCHEMA reporting GRANT SELECT ON TABLES TO grafana_reader;"`]

**Tests that change with the new dashboard.** `DashboardGeneratorTests.ExpectedFiles` and the fixed-uid test list are hard-coded to the two sync files; `DashboardGenerator.Generate()` iterates only `SyncDashboard`; `DashboardQueryTests.DashboardFiles` and its `Expand` helper (which replaces `$__timeFilter(...)` and `${account:sqlstring}`) must learn the `category` and `lang` variables. [VERIFIED: Ledger.UnitTests/Dashboards/DashboardGeneratorTests.cs:15 `private static readonly string[] ExpectedFiles = ["ledger-sync-en.json", "ledger-sync-nl.json"];`; Ledger.IntegrationTests/Dashboards/DashboardQueryTests.cs:19 `private static readonly string[] DashboardFiles = ["ledger-sync-en.json", "ledger-sync-nl.json"];`]

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Integrity and expiry of MRTR `requestState` | Custom HMAC format | `IDataProtector` / `ITimeLimitedDataProtector` from the existing Data Protection key ring | Spec requires AEAD or HMAC; key ring is already persisted and tested |
| Single-use confirmation | In-memory set or read-then-write | One `UPDATE ... WHERE consumed_at IS NULL ... RETURNING` | Survives restarts and races; matches the bank-authorisation pattern |
| Rate limiting | Hand-written counters | `System.Threading.RateLimiting` sliding window per principal | Correct window maths, in the shared framework |
| Token generation | Guid or `Random` | `RandomNumberGenerator.GetBytes(32)`, store `SHA256` only | Same as API keys and bank-authorisation state |
| Elicitation request shapes | JSON by hand | `InputRequest.ForElicitation`, `ElicitRequestParams`, `InputRequiredException` | SDK emits the 2026-07-28 shape |
| Unicode folding and category tests | Own tables | `string.Normalize`, `char.GetUnicodeCategory`, `System.Text.Rune` | BCL |
| Tree roll-up in SQL | Application-side recursion over rows | `WITH RECURSIVE` with a depth guard | Cycles and depth are checked in one place; works for any depth |
| Rule text matching | Regex engine or user-supplied patterns | Token and substring comparison on normalised text | Decision forbids regex; outsiders control the text |
| Audit diffing | Field-by-field diff engine | Store `before` and `after` snapshots per object | Undo needs the snapshots anyway |
| Dashboards | Hand-edited JSON | The existing C# generator and its drift check | JSON is generated; hand edits fail CI |

**Key insight:** the dangerous parts of this phase (confirmation, injection, audit) are exactly where hand-rolled shortcuts look cheap and fail quietly. Every one of them has a library or SQL primitive here.

## Common Pitfalls

### Pitfall 1: Phase 3 tests silently pass while real data double counts transfers
**What goes wrong:** After switching to category-kind exclusion, rows that never received the built-in transfer assignment count as spending and income.
**Why:** The old rule was computed at query time; the new one is a stored row. Existing tests insert rows directly through the runtime role with no ingestion pass. [VERIFIED: Ledger.IntegrationTests/Mcp/LedgerQuerySeed.cs `context.Transactions.AddRange(`]
**How to avoid:** the migration backfills the built-in step for existing rows; the pass also runs after each sync and after account selection changes; the seed helper runs the pass; add a test that migrates a database that already holds a joint-to-savings transfer and asserts totals are unchanged.
**Warning signs:** `internal_transfers_excluded` drops to zero in a regression run.

### Pitfall 2: Account added later does not reclassify history
Stored assignments do not follow a newly synced account. Run the built-in pass over all unassigned rows when selection changes, not only touched rows.

### Pitfall 3: Fallback token accepted from a dialog-capable client
See Confirmation Mechanics: refuse tokens when elicitation is declared; test it.

### Pitfall 4: Calling `ElicitAsync` or reading `McpServer.ClientCapabilities`
Both are dead ends in stateless mode (spike). Read `context.JsonRpcRequest.Context?.ClientCapabilities`; throw `InputRequiredException` only when that value has `Elicitation`; never when `IsMrtrSupported` is false (the SDK turns it into an internal error).

### Pitfall 5: Database tests that enumerate everything
`Runtime_role_holds_exactly_the_intended_privileges_on_every_relation_and_sequence` lists every relation, so each new table and view must be added deliberately. `The_database_holds_only_the_expected_schemas_and_no_application_functions` fails on any function in `public` or `reporting`: no `ltree`, no trigger functions, no helper SQL functions. Use views and CTEs. If a table gets column-level UPDATE, the transactions-columns test lists it too; prefer table-level grants except for the append-only tables.

### Pitfall 6: Two period-date rules
The reporting view and the query store order their date fallbacks differently (Anti-Patterns). A comparison that disagrees between Grafana and Claude is the visible symptom. One rule, copied from the query store, in every new view; parity test.

### Pitfall 7: Tool descriptions truncated at 2,048 characters
Phase 3 descriptions are already long. Intent-shaped tools with an action parameter grow quickly. A truncated description loses the "this is untrusted data" sentence. Keep that sentence first and test the length.

### Pitfall 8: Retrying a stored-text injection through notes, reasons and category names
Anything Claude writes can be replayed to a later session. Clean, cap, detect on write; label on read.

### Pitfall 9: Rule creation is not confirmation-gated
The decisions gate applying a rule to history, not creating one. An injected model could create a rule that mislabels future transactions. Mitigations in scope: audit, undo, rate limit, the recent-changes panel, intermediary guard. Residual risk is for the user to accept or tighten (Open Question 6).

### Pitfall 10: Search cursors and filters
Adding `category` and `uncategorised` filters without adding them to `SearchCursor.FilterHash` lets a cursor from one search be replayed on another.

### Pitfall 11: Raw SQL built by concatenation
Existing code uses `SqlQuery<T>($"...")`, where interpolated values become parameters. Keep that. Array parameters are cast (`{x}::text[]`). Never use `FromSqlRaw` with interpolation or string concatenation, especially with rule or note text.

### Pitfall 12: Grafana link and variable mismatches
A data link that names a variable the dashboard does not define silently does nothing. Add a unit test that every `var-<name>` in generated links names a defined variable, and a manual click-through at the end of the phase (a real browser is the only complete check).

### Pitfall 13: Day-of-month windows
Month-to-date on the 31st against a shorter month: clamp and say so. Use `DateOnly` and the Amsterdam zone through `PeriodResolver`; never `DateTime.Now`. [VERIFIED: Ledger.Domain/Queries/PeriodResolver.cs exists; Ledger.Service/Queries/QueryTimeZone.cs exists]

### Pitfall 14: Lint rules bite test names and strings
Test display names, MCP descriptions and dashboard text must not contain requirement keys, decision ids, "Phase N" or planning file names, and C# must not contain `//` comments outside generated migrations. Run `build/lint.sh` before committing.

### Pitfall 15: Integration test cost
Each integration test starts a host: 21 tests took about 44 seconds in this session. Group by trait, keep pure logic (matching, specificity, undo planning, cleaning, detection, comparison) in unit tests that run in about a second.

## Code Examples

Spike-verified (2026-10-13, ModelContextProtocol.AspNetCore 2.2.0, stateless). Comments omitted because the project forbids `//` comments.

### Per-request capability and MRTR in a tool

```csharp
[McpServerTool(Name = "apply_changes", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
[McpMeta("anthropic/requiresUserInteraction", true)]
public async Task<ApplyResult> ApplyChanges(
    RequestContext<CallToolRequestParams> context,
    Guid changeSetId,
    string? confirmationToken = null,
    CancellationToken cancellationToken = default)
{
    var principal = context.JsonRpcRequest.Context?.User;
    var clientCanElicit = context.JsonRpcRequest.Context?.ClientCapabilities?.Elicitation is not null;

    if (!clientCanElicit)
    {
        return await confirmation.ApplyWithTokenAsync(principal, changeSetId, confirmationToken, cancellationToken);
    }

    if (confirmationToken is not null)
    {
        throw new McpException("This client confirms in a dialog; a token is not accepted.");
    }

    if (context.Params?.InputResponses is { } responses
        && responses.TryGetValue("confirm", out var answer)
        && confirmation.IsAccepted(answer)
        && confirmation.VerifyState(context.Params.RequestState, principal, changeSetId))
    {
        return await confirmation.ApplyAfterDialogAsync(principal, changeSetId, cancellationToken);
    }

    var summary = await confirmation.DescribeAsync(principal, changeSetId, cancellationToken);

    throw new InputRequiredException(
        new Dictionary<string, InputRequest>
        {
            ["confirm"] = InputRequest.ForElicitation(new ElicitRequestParams
            {
                Message = summary.DialogText,
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = { ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = "Apply these changes" } }
                }
            })
        },
        confirmation.ProtectState(principal, changeSetId));
}
```

`ProtectState` wraps `{changeSetId, sub, clientId, fingerprint}` with `ITimeLimitedDataProtector`; `DescribeAsync` builds `DialogText` from stored counts and cleaned category names only.

### Specificity comparison

```csharp
public readonly record struct RuleSpecificity(int StrongestTier, int CriteriaCount) : IComparable<RuleSpecificity>
{
    public int CompareTo(RuleSpecificity other)
    {
        var byTier = StrongestTier.CompareTo(other.StrongestTier);

        return byTier != 0 ? byTier : CriteriaCount.CompareTo(other.CriteriaCount);
    }
}
```

### Recursive closure for roll-up (also the reporting view body)

```sql
WITH RECURSIVE closure AS (
    SELECT c.id AS ancestor_id, c.id AS descendant_id, 0 AS depth
    FROM categories c
    UNION ALL
    SELECT cl.ancestor_id, child.id, cl.depth + 1
    FROM closure cl
    JOIN categories child ON child.parent_id = cl.descendant_id
    WHERE cl.depth < 12
)
SELECT ancestor_id, descendant_id, depth FROM closure;
```

### Append-only audit tables in the migration

```sql
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE audit_operations, audit_items FROM ledger_runtime;
```

Same shape as the existing `REVOKE UPDATE, DELETE, TRUNCATE ON TABLE provider_calls FROM ledger_runtime;` [VERIFIED: Ledger.Repository/Migrations/20260930185230_AddProviderCalls.cs:54 `"REVOKE UPDATE, DELETE, TRUNCATE ON TABLE provider_calls FROM ledger_runtime;"`].

### Data link on a hidden key column (generated per language)

```json
{
  "matcher": { "id": "byName", "options": "category_key" },
  "properties": [{ "id": "custom.hidden", "value": true }]
}
```

with `fieldConfig.defaults.links` holding `{"title":"...","url":"/d/ledger-spending-en?var-category=${__data.fields[\"category_key\"]}&${__url_time_range}"}`. [ASSUMED: field names; verify in a real Grafana 13.2.3]

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Server calls `elicitation/create` over a held SSE stream or session | Multi round-trip requests: the server returns an input-required result and the client retries with the answer | MCP revision 2026-07-28 (SEP-2322, SEP-2567, SEP-2575) | A stateless server can confirm with the user; `ElicitAsync` is unusable in stateless mode |
| Capabilities declared once at `initialize` | Capabilities declared per request in `_meta` | 2026-07-28 | Read them per call from the request context; never cache |
| "Don't always allow this tool" as advice | Server-set `_meta["anthropic/requiresUserInteraction"]` makes Claude Code prompt every call, no "don't ask again" | Claude Code 2.1.214 | The token fallback has a client-enforced human gate in Claude Code |
| Claude Code on the legacy handshake | Claude Code v2 runtime, 2026-07-28 default from 2.1.274, form and URL elicitation declared | 2.1.274 and later | Dialog-based confirmation works for the operator's Claude Code connection |

**Deprecated or dead ends here:** `McpServer.ElicitAsync` and `McpServer.ClientCapabilities` in stateless mode; session-based elicitation for this deployment; Grafonnet (already replaced by the C# generator).

## Assumptions Log

All claims tagged `[ASSUMED]` in this research. The planner and discuss-phase should confirm A1, A2, A10, A11 and A12 with the user before they become locked.

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | claude.ai web/mobile and Claude Desktop do not (yet) declare elicitation, so they use the token fallback | Confirmation Mechanics | Low: if they do declare it they simply get the dialog path, which is also supported |
| A2 | A rule combining an exact-name or IBAN criterion with description words is `high` confidence | Pattern 3 | Low to medium: affects how the queue and dashboards show confidence, not safety |
| A3 | The Unicode ranges to strip (bidirectional controls, zero-width characters, tag characters U+E0000-E007F) are all Control or Format categories | Pattern 7 | Medium: a missed range lets invisible text through; unit-test with literal escapes |
| A4 | Option names for Grafana `stat`, `barchart` and `timeseries` panels and the `custom.hidden` override | Grafana | Medium: wrong keys render defaults or fail; mitigated by exporting real JSON from a local Grafana 13.2.3 |
| A5 | Reliability threshold of 20 percent uncategorised, measured on absolute booked non-transfer amount | Pattern 9 | Low: a tunable constant stated in the result |
| A6 | An in-memory rate window that resets on restart is acceptable for one household | Pattern 8 | Low: an attacker cannot force restarts; a persisted counter is the upgrade |
| A7 | A table data link can read a field hidden with `custom.hidden` on Grafana 13.2.3 | Grafana | Medium: fallback is a narrow visible key column |
| A8 | The validated reference-token principal exposes the OAuth client id through `GetPresenters()` | Standard Stack, Open Question 3 | Medium: audit "which client" would be empty; fallback is resolving the authorization id to its application as `GrantRevocationService` already does |
| A9 | The operator's Claude Code is at least 2.1.274 for dialogs and 2.1.214 for the approval flag | Confirmation Mechanics | Medium: otherwise the legacy path applies (token fallback, no client-enforced prompt); `claude --version` was not available to check |
| A10 | Description words in a rule are conjunctive (every word must appear) | Pattern 3 | Medium: Phase 3 filters treat word lists as alternatives; conjunctive is narrower and matches the "narrowest rule" instruction |
| A11 | Rules are immutable; "change" is remove plus add in one operation | Data Model | Low: simpler audit and undo; mutable rules are possible later |
| A12 | Grafana month figures count booked transactions only, with pending beside | Grafana | Medium: the user may expect the current month to include pending (Open Question 5) |
| A13 | The real shape of savings-account transfers is unknown to this research | Open Question 7 | Medium: the design handles both an IBAN and description text, so the risk is rule content, not structure |

## Open Questions (RESOLVED)

1. **Add a `ledger.write` OAuth scope?** RESOLVED (adopted recommendation, recorded as a flagged assumption in plan 04-09).
   - What we know: the policy requires only `ledger.read`; the SDK can hide tools per policy; the consent page grants a fixed scope list.
   - What is unclear: whether the household wants a second consent for write access.
   - Recommendation: add it. A read-only token cannot write, and the existing Claude Code grant stays read-only until re-approved. Cost: one consent-page and seeder change plus a re-approval.
   - Resolution: plan 04-09 adds `ledger.write`, the `mcp-write` policy and the consent sentence; the operator re-approves Claude Code at go-live (plan 04-43, with the remove-and-re-add fallback).
2. **What source does a confirmed Claude proposal carry?** RESOLVED (adopted recommendation, recorded as a flagged assumption in plans 04-01 and 04-32).
   - The decision text says an assignment the user confirmed is user-sourced and certain; the requirement lists Claude as a source.
   - Recommendation: `user` with `proposal_id` retained, and `claude` kept in the CHECK for future use. Confirm that is the intended reading.
   - Resolution: this follows D-09's own wording ("an assignment the user made or confirmed is recorded as user-sourced and certain"); `via_proposal` keeps "Claude proposed, the user confirmed" visible, and `claude` stays an allowed source value.
3. **Does the validated access-token principal expose the client id?** RESOLVED (adopted plan, recorded as a flagged assumption in plan 04-07).
   - Plan: an integration test connects with each registered client id and asserts the audit row carries it. Fallback: authorization id to application lookup.
   - Resolution: plan 04-07 reads the client id from `GetPresenters()` with the authorization-to-application lookup as fallback, and its Audit tests assert both registered client ids.
4. **A rule application larger than the per-step cap.** RESOLVED (adopted recommendation, recorded as a flagged assumption in plan 04-40).
   - The decisions say one confirmed bulk operation and at most about 500 transactions per confirmed step.
   - Recommendation: the preview states the number of rounds; each round is a separate audited operation sharing the rule id. Confirm this reading, or raise the cap for rule applications.
   - Resolution: plan 04-40 previews round 1 of N, each confirmed round is its own audited operation with the same rule id, and the cap stays 500 (D-22).
5. **Do Grafana month figures include pending transactions?** RESOLVED (adopted recommendation, recorded as a flagged assumption in plans 04-08, 04-21, 04-33 and 04-38).
   - Recommendation: booked only, pending shown as its own figure, so the dashboard and Claude's totals agree.
   - Resolution: comparisons and dashboard figures count booked transactions; pending ones are shown beside the current window and listed with their status in the transactions table.
6. **Should creating a rule that targets an income or transfer category need confirmation?** RESOLVED (decided within the plans' existing assumption, recorded as a flagged assumption in plan 04-40).
   - The decisions gate applying a rule to history, not creating one. Creating such a rule changes how future transactions are counted. Residual risk of an injected rule; mitigations are audit, undo, rate limit and the recent-changes panel. The user can accept this or tighten it.
   - Resolution: a rule that targets a transfer-kind category is a confirmed structural write (it hides money from spending and income, like marking an own account); every other new rule, income-kind included, affects only future syncs and stays unconfirmed but audited, rate-limited, undoable and visible in recent_changes and on the dashboard. The residual risk is accepted in the threat models of plans 04-34 and 04-40.
7. **Real shape of the savings-account transfers.** RESOLVED (captured by the operator checkpoint in plan 04-02, Task 1).
   - The context says to confirm against the live ledger. This research could not: the operator revoked Claude's access to the ledger host and no MCP session was available. The execution plan needs an operator-assisted step (search the live ledger through MCP, note only the shape, never copy real text into fixtures) before the savings rule is designed.
   - Resolution: plan 04-02 Task 1 is a blocking operator checkpoint in which the operator answers eight yes/no questions in their own session; only the answers are recorded. Stop condition: if neither own-account marking (plan 04-27) nor a description-words rule into Savings (plan 04-40) fits the recorded shape, execution stops and reports to the operator instead of inventing a new matcher.
8. **What does "N suspicious texts" count on the dashboard?** RESOLVED (adopted recommendation, recorded as a flagged assumption in plan 04-33).
   - A count that never falls becomes noise. Recommendation: rows flagged in the last 90 days, with an optional `acknowledged_at` later.
   - Resolution: `reporting.review_status.suspicious_90d` counts rows flagged in the last 90 days; no acknowledgement column is built in this phase.
9. **REST undo confirmation.** RESOLVED (adopted recommendation, recorded as a flagged assumption in plan 04-39).
   - The operator calls it with an API key, so the human gate is the operator's explicit call. Recommendation: `POST /api/v1/changes/{id}/undo` accepts `dryRun` to show the revert and skip counts first; no token. It still counts against the write guard and is audited with the API key name as client.
   - Resolution: plan 04-39 implements exactly this.
10. **Tool-count budget.** RESOLVED (adopted recommendation, recorded as a flagged assumption in plan 04-11).
    - Six new tools leaves about five for later work. If that proves tight, `recent_changes` can fold into `category_overview`.
    - Resolution: the phase adds six tools (change_ledger, apply_changes, recent_changes, category_overview, review_queue, compare_spending); recent_changes stays its own tool.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK and runtime | Build and tests | yes | SDK 10.0.112, runtimes 10.0.12 | none needed |
| Local PostgreSQL (user's container) | Real-database tests | yes | container `postgres-dev` running image `postgres:18` | CI uses a PostgreSQL service container |
| `ConnectionStrings:TestAdmin` user-secret | Real-database tests | yes (the integration test run in this session created and dropped throwaway databases) | n/a | environment variable `ConnectionStrings__TestAdmin` |
| Docker | Optional local Grafana spike, lint compose | yes | present, containers listed | none needed |
| Grafana 13.2.3 locally | Verifying generated panel JSON and data links by eye | no (not installed) | pinned in `deploy/versions.env` | run the pinned image in Docker for the spike, or verify on the household's Grafana after deploy |
| `dotnet-ef` | Generating the migration | yes via tool manifest | 10.0.12 | `dotnet tool restore` |
| Claude Code (operator's) | Dialog and approval-flag behaviour | not checkable here | unknown | the token fallback works without it; verify the version during acceptance |
| MCP access to the live ledger | Savings-transfer shape | no | n/a | operator-assisted step, see Open Question 7 |

**Missing dependencies with no fallback:** none block planning.
**Missing dependencies with fallback:** local Grafana (use Docker or the deployed instance), operator Claude Code version.

## Validation Architecture

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 4.0.1 on Microsoft.Testing.Platform, FluentAssertions 8.11.0, NSubstitute 6.2.0, `FakeTimeProvider` |
| Config file | `global.json` (`"runner": "Microsoft.Testing.Platform"`), `Ledger.IntegrationTests/xunit.runner.json` (`parallelizeTestCollections: false`) |
| Quick run command (unit, about 1 s) | `dotnet test --project Ledger.UnitTests -- --filter-trait "Category=<Trait>"` (ran with `Dashboards`: 10 passed) |
| Integration run (real PostgreSQL) | `dotnet test --project Ledger.IntegrationTests -- --filter-trait "Category=<Trait>"` (ran with `McpTools`: 21 passed in about 44 s) |
| Full suite | `dotnet test --solution Ledger.slnx` (CI adds `--no-restore` and the TestAdmin variable) |
| Static checks | `build/lint.sh` (planning-reference and `//`-comment rules, workflow and shell checks) |

[VERIFIED: runs in this session; .github/workflows/ci.yml `run: dotnet test --solution Ledger.slnx --no-restore`]

Proposed trait categories: `Rules`, `Categorisation`, `Audit`, `Confirmation`, `Injection`, `Comparison`, `CategoryMetrics`, plus the existing `McpTools`, `Dashboards`, `DatabaseRoles`.

### Phase Requirements to Test Map

| Req ID | Behaviour | Test Type | Automated Command | File Exists? |
|--------|-----------|-----------|-------------------|--------------|
| CAT-01 | Migration seeds the four bands, Income and Transfers, about 30 categories with non-empty EN and NL names and correct kinds; no money column on `categories`; add, rename and merge work; a transaction can sit on a non-leaf node; totals for a node include descendants | integration + unit (`CategoryTree` roll-up, cycle and kind checks) | `dotnet test --project Ledger.IntegrationTests -- --filter-trait "Category=Categorisation"` | Wave 0 |
| CAT-02 | Synced-account transfers assigned to the Transfers band; totals identical to the old behaviour (existing totals and search tests unchanged); unsynced own account marked, preview then confirm; personal-account flows counted in their own categories | integration | `... "Category=Categorisation"` and `"Category=McpTools"` | existing tests need seed update |
| CAT-03 | Rules match on IBAN, exact name, contains, description words, direction, amount, account; most specific wins; equal specificity with different categories goes to queue; no regex field exists; rules table is empty after migration (no seeded rules or merchants); engine runs after a synthetic sync | unit (`RuleMatcher`) + integration (sync pipeline) | `dotnet test --project Ledger.UnitTests -- --filter-trait "Category=Rules"` | Wave 0 |
| CAT-04 | Every assignment stores source, confidence, rule id, time; CHECK constraints reject user without certain and low from a rule; MCP rows and `reporting.transactions` show them | integration | `... "Category=Categorisation"` | Wave 0 |
| CAT-05 | Synthetic Tikkie- and PSP-shaped rows reach the queue; name-only rule on a flagged intermediary is refused; unusual amount on a matching rule does not queue; user-set assignment is never overwritten at sync or on application | unit + integration | `... "Category=Rules"` | Wave 0 |
| CAT-06 | Proposals are stored with reason and confidence, survive a host restart, leave the row uncategorised in totals, confirm as a previewed batch, reject leaves it queued | integration | `... "Category=Categorisation"` | Wave 0 |
| CAT-07 | "That Tikkie was concert tickets": single recategorise plus note, rule preview lists exactly the transactions that would change, database unchanged until confirmed (snapshot compare), confirmed application writes one audited operation | integration | `... "Category=Confirmation"` | Wave 0 |
| CAT-08 | Adding a child under a category with transactions moves nothing; merge or remove shows counts, needs confirmation, moves transactions, children and rules to the target, is one undoable operation | integration | `... "Category=Categorisation"` | Wave 0 |
| ADV-06 | One audit operation per change with before and after, time and client id (OAuth client id over MCP, API key name over REST); undo reverts what is unchanged, skips the rest and reports counts; undo is itself undoable; audit tables are append-only for the runtime role; REST list and undo work with a key and return 401 without | unit (`UndoPlanner`) + integration | `... "Category=Audit"` | Wave 0 |
| ADV-07 | Bank text only appears inside `untrusted_bank_text` and is cleaned and capped (unit); an injected description triggers no write (integration: reading it, listing it and acting on it leaves `audit_operations` unchanged); write tools expose only id-type parameters (schema test); elicitation accept, decline, cancel; fallback with a missing, wrong, expired, reused, other-client or changed token; a token from a dialog-capable client is refused; legacy-revision client takes the fallback; rate limit and batch cap refuse with a message and count; annotations and the approval flag present; description length at most 2,000 characters | unit + integration with `McpClientOptions.Handlers.ElicitationHandler` and `ProtocolVersion` | `... "Category=Injection"`, `"Category=Confirmation"` | Wave 0 (host helper needs client options) |
| DASH-02 | Generated EN and NL files match committed files; parity (targets identical, `lang` constant skipped); every query reads only `reporting.*`; every link names a defined variable; all queries run as `grafana_reader` on a seeded database; children sum to the parent; Uncategorised closes the total | unit + integration | `dotnet test --project Ledger.UnitTests -- --filter-trait "Category=Dashboards"` then the integration equivalent | existing tests need extending |
| PLAN-09 | Three baselines, like-for-like window, day-31 clamp, missing history, reliability flag (unit with `FakeTimeProvider`); tool result carries provenance; Grafana comparison SQL equals the tool for one seeded dataset | unit + integration | `... "Category=Comparison"` | Wave 0 |
| OPS-08 | `ledger_review_queue_size` equals the database count at startup and after refresh, falls after categorisation, present at zero before any data; other new series present at zero | integration (as `SyncMetricsTests` does) | `... "Category=CategoryMetrics"` | Wave 0 |

### Sampling Rate

- **Per task commit:** the unit trait for the code touched (about 1 s), plus `build/lint.sh` when strings, comments or docs change.
- **Per wave merge:** the integration traits touched plus `DatabaseRoles`, `Dashboards` and `McpTools`.
- **Phase gate:** full suite green before the verify step, plus the manual checks below.

### Wave 0 Gaps

- [ ] New test files per trait above and a synthetic fixture set: Tikkie-shaped, PSP-shaped, injection-shaped and savings-shaped descriptions, all invented.
- [ ] `McpTestHost.OpenAsync` accepts `McpClientOptions` (elicitation handler, protocol version), so dialog and legacy paths are testable. The spike shows the SDK client supports both.
- [ ] `LedgerQuerySeed` runs the built-in transfer pass after seeding so the existing totals and search tests keep passing.
- [ ] `DatabaseRoleTests` privilege matrix extended for every new relation and view.
- [ ] `DashboardGeneratorTests` and `DashboardQueryTests` extended (file lists, `lang` constant variable, `category` variable in `Expand`).
- [ ] Update tests that assert the old wording and flat fields: `ToolSurfaceTests` ("not by category", "counterparty names, not categories"), `TotalsQueryTests` (`GroupedBy`, summary text), `ToolCatalogTests`, `FreeTextMaskingTests` and `SearchPaginationTests` for the new row shape. [VERIFIED: grep results in Ledger.IntegrationTests/Mcp/ToolSurfaceTests.cs:103,126; TotalsQueryTests.cs:100,294; Ledger.UnitTests/Mcp/ToolCatalogTests.cs:128]
- [ ] An architecture test that MCP types depend on no store or `LedgerDbContext`.
- [ ] No framework install needed.

### Manual-only checks (cannot be proven by automated tests)

- A real model reading a hostile description does not call a write tool. Automated tests prove that nothing in the server turns reading into writing and that writes need a confirmation the model cannot supply; they cannot prove model behaviour. Do one supervised session with a synthetic instruction-bearing row.
- Claude Code shows the elicitation dialog and the per-call approval prompt (and that "don't ask again" is absent for `apply_changes`).
- Grafana click-through: clicking a child selects it at any depth, the breadcrumb returns, time range is kept, Dutch and English both work, the hidden key column link resolves.
- The savings-account transfer rule against real text (operator-assisted, nothing copied into the repository).

## Security Domain

`workflow.security_enforcement` is enabled and `security_asvs_level` is 2 with `security_block_on: high` [VERIFIED: .planning/config.json].

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | unchanged | existing OAuth 2.1 with OpenIddict and API keys; no change except the optional write scope |
| V3 Session Management | no | stateless MCP, no session ids |
| V4 Access Control | yes | `ledger.write` scope and `[Authorize]` filters on write tools (recommended); confirmations bound to principal `sub` and client id; operator REST endpoints use the API-key scheme only |
| V5 Input Validation | yes | bounded rule criteria (no regex, length and count caps), cleaned and capped notes, reasons and category names, parameterised SQL through `SqlQuery<T>($"...")`, explicit ids only on write tools |
| V6 Cryptography | yes | Data Protection AEAD for `requestState`, SHA-256 of tokens at rest, `RandomNumberGenerator` for tokens; nothing hand-rolled |
| V7 Error Handling and Logging | yes | no bank text, amounts, IBANs or tokens in logs, exceptions, metric labels or MCP errors (exception type only, as `SyncMetricsRefresher` logs it) |
| V8 Data Protection | yes | public repository: seed tree names no merchant; rules and flags live in the database; fixtures synthetic; reporting views expose no IBAN or bank text beyond what `reporting.transactions` already shows to Grafana |
| V11 Business Logic | yes | confirmation for bulk and structural writes, per-principal rate limit, per-step cap, append-only audit, undo |
| V13 API and Web Service | yes | MCP tool annotations are honest; REST audit endpoints under the fallback authenticated policy |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Prompt injection through a transaction description or counterparty name (anyone can send 0.01 with text) | Tampering, Elevation | structured untrusted wrapper, stripped control and bidirectional characters, caps, instruction-like flag and alert, explicit-parameter write tools, user-originated confirmation, rate limit, audit and undo |
| Model supplies its own confirmation (`confirmed: true`, replayed token) | Spoofing, Repudiation | no confirmation parameters; single-use server-stored change sets bound to principal and client; token refused from dialog-capable clients; client-enforced approval flag |
| Capability confusion to downgrade the dialog to a token | Tampering | capability read per request from client transport metadata only; refusal rule in both directions |
| Confirmation replay or tampered `requestState` | Tampering, Repudiation | AEAD-protected state with expiry and principal; atomic once-only consume in the database |
| Second-order injection through notes, reasons, category names | Tampering | clean, cap, detect on write; label on read |
| Audit log tampering | Tampering, Repudiation | append-only tables for the runtime role, undo as a new operation |
| Write flood or runaway loop | Denial of Service | sliding-window guard, per-step cap, refusal metric and operator alert without financial detail |
| Information disclosure through dialog text, metrics or alerts | Information Disclosure | dialog text from counts and category names only; opaque or fixed-word labels; fixed alert summaries |
| Rule that mislabels future data (injected rule creation) | Tampering | audit, undo, recent-changes panel, intermediary guard; residual risk noted for the user |
| Public-repo leakage through seeds, fixtures or docs | Information Disclosure | household-neutral names, synthetic fixtures, secret and personal-data scanning already in CI |

## Sources

### Primary (HIGH confidence)
- This repository, read this session: `.claude/CLAUDE.md`; `Ledger.Service/Mcp/{LedgerTools,McpEndpoint,McpMetrics,ServerInstructions}.cs`; `Ledger.Service/Queries/{LedgerQueryService,SearchResult}.cs`; `Ledger.Repository/Stores/{LedgerQueryStore,LedgerStore}.cs`; `Ledger.Repository/Migrations/20260927183611_InitialCreate.cs`, `20260930181840_AddLedgerIngestion.cs`, `20260930185230_AddProviderCalls.cs`, `20260930193516_AddAccountStatusView.cs`; `Ledger.Repository/LedgerDbContext.cs`; `Ledger.Repository/Conventions/EnumText.cs`; `Ledger.Dashboards/**`; `Ledger.Service/OAuth/**`; `Ledger.Service/Pages/Connect/Authorize.cshtml.cs`; `Ledger.Service/Ingestion/{SyncOrchestrator,SyncMetricsRefresher}.cs`; `Ledger.IntegrationTests/{Database/DatabaseRoleTests,Infrastructure/DatabaseFixture,Infrastructure/McpTestHost,Mcp/*,Dashboards/DashboardQueryTests}.cs`; `Ledger.UnitTests/Dashboards/DashboardGeneratorTests.cs`; `build/lint/checks/10-repo-rules.sh`; `deploy/**` provisioning and SQL; `docs/mcp.md`, `docs/monitoring.md`.
- Spike against `ModelContextProtocol.AspNetCore` 2.2.0 and its XML documentation in the local NuGet cache (`ModelContextProtocol.Core.xml`, `ModelContextProtocol.AspNetCore.xml`), run 2026-10-13.
- MCP specification 2026-07-28: elicitation (modelcontextprotocol.io/specification/2026-07-28/client/elicitation) and multi round-trip requests (modelcontextprotocol.io/specification/2026-07-28/basic/patterns/mrtr).
- Claude Code MCP documentation: code.claude.com/docs/en/mcp (runtime versions and protocol negotiation, elicitation dialogs and the `Elicitation` hook, `anthropic/requiresUserInteraction`, 2,048-character description limit).

### Secondary (MEDIUM confidence)
- Grafana data-link documentation: grafana.com/docs/grafana/latest/panels-visualizations/configure-data-links/ (variable syntax, supported visualisations).
- Grafana PostgreSQL query editor documentation: grafana.com/docs/grafana/latest/datasources/postgres/query-editor/ (macros, time series format).
- `.planning/research/FEATURES.md` Nibud section (category structure and licensing); `.planning/research/PITFALLS.md` Pitfalls 4 to 7, 17 and 20.

### Tertiary (LOW confidence, marked for validation)
- Community threads on hiding a table column while still using it in a data link (Grafana community forum); third-party summaries of Claude Code's 2026-07-28 behaviour and URL-mode elicitation bug reports. Nothing found on elicitation support in claude.ai or Claude Desktop.
- Grafana panel option names for stat, bar chart and time series from training knowledge.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH, nothing new and every version read from project files.
- Confirmation mechanics: HIGH for the server side (spike), MEDIUM for client behaviour (Claude Code documented, hosted clients unknown).
- Architecture and data model: HIGH on constraints from existing tests and conventions, MEDIUM on exact table shapes (planner may adjust within the stated invariants).
- Grafana panels and links: MEDIUM, with a stated verification path.
- Pitfalls: HIGH, most were found by reading the existing tests and code.

**Research date:** 2026-10-13
**Valid until:** about 30 days for the repository findings; 7 days for Claude Code client behaviour (the 2026-07-28 revision and Claude Code are moving quickly).

