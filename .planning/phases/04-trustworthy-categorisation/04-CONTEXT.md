# Phase 4: Trustworthy Categorisation - Context

**Gathered:** 2026-10-13
**Status:** Ready for planning

<domain>
## Phase Boundary

Every transaction gets a category from a Nibud-structured tree that the household can edit:
- **Rules** stored in the database categorise automatically where they are confident.
- **Everything else** goes to a review queue. Claude works through it, its proposals are stored, and they count only after the user confirms.
- **Corrections** go through Claude ("that Tikkie in January was concert tickets"). A correction can become a rule, but the rule touches history only after a preview the user confirms.
- **Transfers** between the household's own accounts stop counting as spending or income.

This phase also delivers:
- **The first write tools**, with the audited write pattern every later write tool reuses: a domain service writes the audit entry, and the tool never writes directly. Every change made through MCP or REST is audited and can be undone.
- **Defences against injected bank text:**
  - bank text reaches Claude marked as untrusted
  - bulk and structural writes need a confirmation that comes from the user, not the model
  - writes are rate-limited
  - instruction-like bank text is flagged
- **Grafana:** both partners get a drill-down by category and period, with trends and comparisons against the household's own history.
- **For Claude:** the same comparisons.
- **Metrics:** the review-queue size on `/metrics`.

Not in this phase:
- budgets, goals, recurring costs, forecasts, true monthly cost, affordability, and the overview dashboard's budget top row (Phase 5)
- advisor memory, the advice log, scheduled reviews, and the remaining write tools for budgets, goals, memory and reviews (Phase 6)
- the review/fix web page, and a full set of REST write endpoints (v2)
- any reference figures from Nibud or elsewhere

</domain>

<decisions>
## Implementation Decisions

### Category tree
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

### Review queue and proposals
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

### Rules
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

### Own accounts and transfers
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

### Write confirmation and injection defence (ADV-07)
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

### Audit and undo (ADV-06)
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

### Grafana
- **D-30:** **One new drill-down dashboard, "Spending", in EN and NL**, generated by the existing C# dashboard generator (Phase 2 D-15).
  - It is built around a **category selector**. It shows the selected category's children for the period, its monthly trend, and the transactions beneath it.
  - Clicking a child selects it, so any depth works (D-01). A breadcrumb or "up" link goes back.
  - It starts at the top, with all bands.
  - The existing sync dashboard stays as it is.
  - Financial data is read through `reporting` views as `grafana_reader`.
- **D-31:** **Uncategorised is a bar of its own in every breakdown**, so totals always add up. A status row shows the queue size, "N proposals waiting for confirmation" and the count of suspicious bank texts (D-24).
- **D-32:** **A top row shows income, spending, money moved to savings and what is left, per month.** The spending drill-down sits below it.
- **D-33:** **A "recent changes" panel shows recent audited operations:** when, which client, what kind of change, how many transactions, and whether it was undone. Both partners can see what Claude changed. It reads a reporting view over the audit log that exposes no IBANs and no bank text.

### Comparisons with the household's own history (PLAN-09)
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

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Scope and requirements
- `.planning/ROADMAP.md` §Phase 4: the goal, the five success criteria, and the note that this phase sets up the audited write-tool pattern every later write tool reuses
- `.planning/REQUIREMENTS.md`:
  - this phase: CAT-01 to CAT-08, ADV-06, ADV-07, DASH-02, PLAN-09, OPS-08
  - boundaries:
    - ADV-05: the full set of write tools is Phase 6; this phase builds categorisation writes and notes only
    - ADV-01: tool count
    - DASH-01 and DASH-03: Phase 5
    - PLAN-06 and PLAN-07: Phase 5
- `.planning/PROJECT.md`:
  - §Constraints
  - §Key Decisions: Nibud-based tree refined by Claude; spending compared with the household's own history and not Nibud figures; Claude may change categories and rules, all audit-logged and reversible; review page deferred to v2
  - §Out of Scope: no app-side LLM calls, so unclear transactions wait in the queue for a Claude session
- `.claude/CLAUDE.md` §Hard rules:
  - no planning references outside `.planning/`
  - `///` comments only
  - public repo, no personal data
  - rules and counterparty names only in the database
  - security first
  - no LLM API calls from the app

### Prior phase decisions that constrain this phase
- `.planning/phases/03-claude-reads-the-ledger/03-CONTEXT.md`:
  - D-08: merchant filter, superseded by category filters here
  - D-09: booked-only totals
  - D-10: the synced-account transfer rule this phase builds on (D-16)
  - D-11: Amsterdam calendar
  - D-12: out/in/net
  - D-14: masked IBANs and opaque counterparty references
  - D-15: untrusted marking deferred to this phase in full
  - the Claude's Discretion tool set
- `.planning/phases/02-automatic-ing-sync/02-CONTEXT.md`:
  - D-09: account display names and opaque keys
  - D-14: immutable transaction identity; assignments key on it
  - D-15: the C# dashboard generator with the EN/NL translation file
  - D-16: reporting views read as `grafana_reader`
  - D-17: operator-only alerts
- `.planning/phases/01-secure-platform-release-pipeline/01-CONTEXT.md`:
  - D-19: email contact point, no financial detail
  - D-22: named API keys for REST, used by the operator undo endpoint (D-27)

### Research (historical; decisions above take precedence)
- `.planning/research/PITFALLS.md`:
  - Pitfall 4: internal transfers double-counted
  - Pitfall 5: Tikkie and PSP-routed iDEAL
  - Pitfall 6: rules silently rewriting history and over-fitting
  - Pitfall 7: prompt injection through bank data (CRITICAL; flagged for security-focused research)
  - Pitfall 17: Claude doing arithmetic over raw rows
  - Pitfall 20: tool-result size
- `.planning/research/FEATURES.md`:
  - §Nibud: category structure and licensing; names and structure may be committed, figures may not
  - the Dutch payment specifics table: Tikkie, PSPs, own-account transfers, energy annual settlement
  - audit plus undo as a first-class operation (the Actual Budget precedent)
- `.planning/research/ARCHITECTURE.md`:
  - Pattern 1: one application layer under REST and MCP
  - Anti-Pattern 2: MCP tools must not bypass domain services
  - its SQL Server details are superseded by PostgreSQL

### Existing code, config and docs to extend
- `Ledger.Service/Mcp/LedgerTools.cs`: the read tools. They gain category filters and grouping, and untrusted wrapping of bank text. New write tool types sit alongside them as thin adapters over audited domain services.
- `Ledger.Service/Mcp/ServerInstructions.cs`: replace the "until categories exist" text. Add how untrusted fields, notes, proposals and confirmation work.
- `Ledger.Service/Mcp/McpEndpoint.cs`: tool registration, and where elicitation is wired in.
- `Ledger.Service/Mcp/McpMetrics.cs` and `Ledger.Service/Metrics/LedgerMetrics.cs`: the new metrics.
- `Ledger.Repository/Stores/LedgerQueryStore.cs`: today's `is_transfer` IBAN-equality CTE (`own_accounts`) moves to category-kind exclusion (D-16).
- `Ledger.Domain/Queries/TotalsAggregator.cs`, `PeriodResolver.cs`, `LedgerQueryFilter.cs`: totals and periods that comparisons and category totals build on.
- `Ledger.Domain/Ingestion/TextNormalizer.cs`: normalising names and descriptions for rule matching.
- `Ledger.Service/Ingestion/SyncOrchestrator.cs`: where the rule engine hooks in after ingestion.
- `Ledger.Repository/Migrations/20260930181840_AddLedgerIngestion.cs`: the `reporting.transactions` view, extended or joined with category, kind, provenance, note and the suspicious flag.
- `Ledger.Dashboards/` (`DashboardGenerator.cs`, `Model/Panels.cs`, which only has `TablePanel` today, `translations.json`, `Definitions/SyncDashboard.cs`): the new Spending dashboard and new panel types.
- `deploy/provisioning/grafana/provisioning/alerting/household-rules.yaml` and `contact-points.yaml`: the rate-limit and suspicious-text alerts.
- `Ledger.Service/OAuth/ClientRegistrations.cs`: the client ids recorded as "which client" in the audit log.
- `Ledger.Service/Auth/ApiKeyAuthenticationHandler.cs`, `Ledger.Service/Endpoints/`: the operator REST audit list and undo endpoints.
- `docs/mcp.md`, `docs/rest-api.md`, `docs/monitoring.md`: write tools, confirmation, "don't always-allow write tools", new endpoints, metrics and alerts.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `LedgerQueryService` with `LedgerQueryStore` and `TotalsAggregator`: server-side totals with full provenance (period, filters, counts, pending, excluded transfers). Category totals, roll-ups and comparisons extend this instead of starting fresh.
- `PeriodResolver` / `PeriodDate` / `QueryTimeZone`: Europe/Amsterdam period resolution. The like-for-like month-to-date window (D-35) builds on it.
- `CounterpartyRef` and `IbanText`: opaque counterparty references and IBAN masking. These let Claude mark own accounts and intermediaries without ever seeing a full IBAN (D-15, D-17).
- `TextNormalizer.ForMatching`: case and spacing normalisation for rule matching on names and descriptions.
- `McpMetrics.ToolCalled` and the prometheus-net static metrics: the pattern for the queue, write, limit and suspicious-text metrics.
- `ApiKeyAuthenticationHandler` with the fallback authenticated policy: the operator REST endpoints (D-27).
- `ISecretProtector` / Data Protection: available if confirmation tokens need protecting or signing.
- The dashboard generator: one C# definition, the EN/NL translation file, and a CI drift check.

### Established Patterns
- **Layering:**
  - `Ledger.Domain` holds the rules: matching, specificity, roll-up, comparison and undo conflict logic. No EF or HTTP.
  - `Ledger.Repository` holds the EF stores and SQL.
  - `Ledger.Service` holds the MCP tools and REST endpoints as thin adapters.
  - A write always goes through a domain service that writes the audit entry. The tool never writes directly.
- **Money and aggregation:** money stays `decimal` / `numeric`. Aggregation happens in SQL or the domain, never in Claude.
- **Opaque labels:** no IBAN, amount, counterparty or bank text ever appears in metric labels, logs or exceptions (SEC-06).
- **Reads:** MCP reads go through the app's application layer as the runtime role. Grafana reads only `reporting` views as `grafana_reader`.
- **Packages and tooling:** `packages.lock.json` with locked-mode restore. Code-first EF migrations run by the migrator role. The runtime role has no DDL rights.
- **Tests:**
  - xUnit on Microsoft.Testing.Platform, FluentAssertions, NSubstitute, with trait categories as CI filters
  - real-database tests against the user's local PostgreSQL container via user-secrets, and a service container in CI
  - all fixtures synthetic, including the injection test transaction and any PSP- or Tikkie-shaped descriptions

### Integration Points
- **Sync pipeline** (`SyncOrchestrator`): the rule engine runs after ingestion. The queue-size metric is refreshed alongside `SyncMetricsRefresher`.
- **MCP server:** new write tool types are registered with `.WithTools<...>()`. Elicitation support depends on the MCP C# SDK 2.x and the client.
- **Reporting schema:** new views for categories (with the tree path for drill-down), category totals per month, comparisons, the queue status and recent audit operations. `grafana_reader` gets SELECT on views only.
- **Grafana alerting:** new household rules for write-limit hits and suspicious bank text, through the existing operator contact point.

</code_context>

<specifics>
## Specific Ideas

- **The canonical correction:** "that Tikkie in January was concert tickets". Claude finds it, recategorises it with a note, and offers a narrow rule. The rule's preview lists exactly which past transactions would change, and nothing changes until the user confirms.
- **Example figures from the discussion:**
  - payback netting: "€120 paid, €80 back, €40 real cost"
  - rule preview: "these 37 would change from X to Y"
  - undo report: "34 reverted, 3 skipped: changed later"
  - like-for-like month: "by the 13th: €640 groceries; last month by the 13th: €590"
- **The savings account needs checking against real data.** ING usually describes transfers to the Oranje Spaarrekening with the savings account number and name in the description, not as a normal counterparty IBAN, so Phase 3's IBAN rule likely never catches them. Research and execution should confirm the real shape via MCP search against the live ledger and design the savings-transfer rule around it (D-16). The real text must never be copied into fixtures or the repo.
- **The partner's view:** the partner does not use Claude, and only sees Grafana. The recent-changes panel (D-33) and the queue status row (D-31) are how they see what Claude did and what is still open.
- **Security framing the user accepted:** text in a bank transaction is the attack surface, because anyone can send the household €0.01 with a description. Marking and detection give visibility. The structural defences are D-20 (a confirmation that comes from the user, not the model), D-21 (explicit parameters only), D-22 (limits) and D-25/26 (audit and undo).

</specifics>

<deferred>
## Deferred Ideas

- **Splitting a transaction across several categories:** rejected for now (D-04). Revisit only if mixed receipts turn out to distort the picture.
- **A full set of REST write endpoints:** these come with the v2 review/fix web page. This phase adds only the operator audit list and undo.
- **The partner confirming proposals:** this needs the v2 web page, or partner access to an AI client (deferred in Phase 3).
- **Projecting the current month forward by spending pace:** Phase 5's end-of-month forecast (PLAN-07).
- **Smoothing annual costs in comparisons:** a yearly insurance bill makes month-over-month jumpy. That is Phase 5's true monthly cost (PLAN-06); the 12-month average baseline (D-34) softens it meanwhile.
- **Annotations beyond the short transaction note:** with the rest of ADV-05 in Phase 6.

### Reviewed Todos (not folded)
The keyword matcher returned six todos, and all six matched on generic words only:
- **Confirm the first consent renewal keeps all history** (`.planning/todos/pending/2026-10-07-confirm-first-consent-renewal-keeps-history.md`): ingestion; can only run around March 2027.
- **Confirm the first real household alert email** (`.planning/todos/pending/2026-10-07-confirm-first-real-alert-email.md`): monitoring verification, independent of categorisation.
- **Connect the hosted Claude clients** (`.planning/todos/pending/2026-10-08-connect-hosted-claude-clients.md`): blocked on a household subscription. D-20's research should still cover claude.ai's elicitation support so the hosted clients work when they arrive.
- **Encrypt the proxy-to-Grafana hop** (`.planning/todos/pending/2026-10-08-encrypt-the-proxy-to-grafana-hop.md`): transport hardening, independent of this phase.
- **A doubtful pending→booked match counts twice for one snapshot** (`.planning/todos/pending/2026-10-12-doubtful-booking-match-counts-twice-for-one-snapshot.md`): an ingestion and reconciliation design decision. Categorisation keys on the immutable transaction id either way.
- **Internal version endpoint** (`.planning/todos/pending/2026-10-12-internal-version-endpoint.md`): operations.

</deferred>

---

*Phase: 04-trustworthy-categorisation*
*Context gathered: 2026-10-13*
