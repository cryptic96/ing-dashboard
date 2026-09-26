# Feature Research

**Domain:** Self-hosted personal-finance backend with LLM (Claude/MCP) financial advisor, Grafana dashboards, Dutch two-person household banking with ING
**Researched:** 2026-09-26
**Confidence:** MEDIUM (cross-checked web sources; no single authoritative spec exists for "LLM finance advisor via MCP" as a category — synthesized from adjacent categories: self-hosted budgeting apps, commercial finance apps, existing finance MCP servers, and Nibud's own published material)

## Survey of Comparable Products

### Self-hosted / open-source budgeting apps

**Firefly III** (PHP, self-hosted, closest spiritual sibling to this project)
- Does well: double-entry bookkeeping so transfers are structurally distinct from spend/income; a real rule engine (condition on amount/description/account, action sets category/tag/budget) that runs automatically on import; recurring-transaction objects that predict future balance and flag "last Friday of the month"-style patterns; budgets + categories + tags as three independent, combinable dimensions; a mature webhook/API surface.
- Does badly: UI is dense and technical (not partner-friendly out of the box); no native forecasting beyond recurring-bill projection — no "can I afford X" reasoning; no LLM/advisor layer, though several unofficial MCP servers now sit in front of it (see below).
- Lesson: a rule engine that lives in data, not code, plus a first-class "this is a transfer" concept, is table stakes for correct categorisation — this project's PROJECT.md already commits to both.

**Actual Budget** (self-hosted, local-first, envelope/zero-based budgeting)
- Does well: bank sync via GoCardless (EU) is built in, not bolted on; schedules (recurring transactions) reconcile automatically against imported transactions; a real undo/redo system for every change, which is exactly the audit+revert requirement this project has; file-based import for CAMT.053/OFX/QIF/CSV as a fallback path.
- Does badly: envelope budgeting assumes you allocate every euro up front — awkward for a household that wants "explain the past" more than "plan every euro," and painful when two partners disagree on categories.
- Lesson: undo is not a nice-to-have bolted onto a changelog — it is a first-class operation with its own UI/API verb (`undo`, `redo`), which this project should mirror for Claude-initiated writes rather than expecting the user to manually reverse a categorisation.

### Commercial (non-self-hosted, mostly US) apps

**YNAB** — zero-based budgeting, strong reports (spending trends, income vs. expense, net worth by custom date range), but net worth/account balances are user-entered, not synced automatically for many account types; goals are visual (progress bars) but not date-projected the way this project wants ("projected completion date"). Lesson: goal tracking needs a projection, not just a progress bar — table stakes is the number, differentiator is "and here's when you'll hit it at current pace."

**Lunch Money** — closest to a "developer-friendly" commercial product: first-class public API, custom budget periods that match pay schedules, a rules engine, and paid access to human financial coaches as an add-on. Multi-currency is a non-goal for this project (single currency, EUR) but the API-first design and rules engine validate the architecture choice here.

**Monarch Money** — the most relevant commercial analog to "Claude as advisor": net worth roll-up across all accounts, automatic recurring/subscription detection surfaced in a dedicated calendar/list view, forward-looking cash-flow projections, an in-app AI assistant, and — notably — built-in **partner/couple collaboration and shared dashboards at no extra cost**, explicitly marketed as its differentiator over the discontinued Mint. Lesson: for a two-person household, "one shared view, two people, no separate logins/tiers" is table stakes, not a differentiator — Monarch made it a headline feature precisely because single-user finance apps get this wrong.

**Copilot Money** — best commercial example of "proactive AI insight" done well: AI categorization that improves from corrections (not just static rules), and proactive surfacing of unusual charges, budget overruns, **subscription price increases**, and miscategorized transactions — i.e. the app pushes findings at the user instead of waiting to be asked. This directly validates the "find leaks" and "proactive scheduled review" advisor jobs in PROJECT.md. Confidence: MEDIUM (marketing copy plus multiple independent reviews agree on this feature set).

### Dutch-market apps and bank-native features

**Dyme** (NL fintech) — reads the account via bank connection, finds "sleeping" subscriptions (gym memberships, magazines) the user forgot about, cross-references utility/telecom contracts against cheaper alternatives, and offers (paid) subscription-cancellation-as-a-service including sending registered cancellation letters. Lesson: "recurring cost detected + flagged" is the table-stakes half; "acted on it for you" (switching/cancelling) is a differentiator Dyme monetizes — and one this project should explicitly **not** build (see Anti-Features: no payment/contract actions on the user's behalf, only surfacing + advice).

**ING "Kijk Vooruit"** (built into the household's own bank's app) — forecasts the next 35 days split into "certain" (scheduled direct debits, known 3–5 days ahead) and "predicted" (modelled from the last 4–5 occurrences of a recurring debit). This is a live description of the exact forecasting mechanism a recurring-cost detector should replicate: date predicted from recency of last N occurrences, amount from the average/most-recent of last N. Confidence: MEDIUM (consistent across Consumentenbond, iCulture, ING's own help page).

**bunq Insights** — auto-categorized spending shown as a proportion/circle chart, ML-based balance forecasting from spending history, one-tap "auto-budget the categories you spend most in," round-up-to-savings, and named savings "pots" (sub-accounts) for goals. Lesson: round-up/pot-based goal savings is a genuinely differentiating mechanic worth considering as a *dashboard nudge* (e.g., "you could top up the holiday goal by €X this month at current pace") even though this project's bank access is read-only and cannot move money itself.

### What's table stakes across all of the above

Every serious product in this space has, at minimum: automatic categorization (rules + ML/AI-assisted), a way to mark transfers as not-spending, monthly budgets with actual-vs-budget, recurring/subscription detection, and *some* forward-looking forecast. None of the incumbents fully solve the Dutch-specific problems in `<must_cover>` item 4 (PSP-obscured merchants, annual-cost smoothing tied to vakantiegeld/toeslagen) — that gap is this project's opening.

## Existing Finance MCP Servers — What They Expose, What Goes Wrong

Confidence: MEDIUM — multiple independent GitHub repos surveyed (not official/maintained-by-vendor in most cases), cross-checked against general MCP design literature.

**Landscape found:**
- YNAB: at least 8 independent community MCP servers (EdgeCaseLabs, calebl, EthanKang1, ianthebeard, mattweg, issmirnov, matthauck, obviyus). Most wrap the official YNAB REST API close to 1:1 (one tool per endpoint: list budgets, list categories, list transactions, update transaction, etc.).
- Actual Budget: multiple servers (justadityaraj, s-stefanov, jimmyzmli, agigante80/andycarlberg with **71 tools**) covering transactions, budgets, rules, and bank sync.
- Firefly III: multiple servers (horsfallnathan, braindaamage exposing **114 tools** across 13 resource categories, daften exposing **140 tools**, etnperlong) — essentially the full Firefly III REST API surfaced as MCP tools.
- Plaid: an **official** Plaid MCP server exists, but it is a *developer/dashboard* tool (Link conversion diagnostics, usage metrics, sandbox tooling) — not a personal-finance-advisor tool. Community servers (t-rhex, iteaguy) exist for personal read-only analysis of Plaid-linked accounts, keeping access tokens local and never calling out except to Plaid's own API.

**The dominant failure pattern:** nearly every community server takes the "expose the whole REST API as MCP tools" shortcut, producing 70–140 tools per server. This is the single biggest anti-pattern to avoid:
- General MCP design research is explicit that servers should target **~5–8 tools**, with a hard ceiling around **10–15** before tool-selection accuracy degrades — every tool definition (name, description, schema) is loaded into context before a single call happens, and 50+ tool definitions can add tens of thousands of tokens per request before any real work starts.
- The fix used by teams that get this right is **not removing functionality** but **consolidating fine-grained CRUD-per-endpoint tools into a handful of intent-shaped tools** with richer parameters (e.g., one `query_transactions` tool with date range, category, merchant, text-search, and pagination parameters, instead of separate list/search/filter/paginate tools).

**Lessons to carry into this project's MCP tool design (maps directly to PROJECT.md's "MCP read/write tools" requirement):**
1. **Server-side aggregation, not raw rows.** Never return a page of raw transactions and expect Claude to sum/average them — LLMs measurably lose arithmetic and multi-step reasoning accuracy as the amount of numeric data in context grows, and financial hallucination research shows models will fabricate plausible-but-wrong totals or invent transactions rather than admit uncertainty. Every "how much did we spend on X" question should be answered by a tool that does the SQL aggregation server-side and returns a number plus the query that produced it — Claude should never be the one adding up a list.
2. **Coarse, intent-shaped tools.** Aim for the ~8–15 tool range PROJECT.md's own MCP surface implies (search/query transactions, aggregates by category/period/merchant, budgets, goals, recurring costs, forecast, plus a handful of write tools) — resist the temptation, seen in every surveyed community server, to expose one tool per database table or REST endpoint.
3. **Deterministic pagination with capped, opaque limits.** Any list-shaped tool (transaction search) must cap results server-side regardless of what the model asks for, return a `truncated`/`total_count` flag and a cursor rather than silently dropping data, and default to a small page size.
4. **Provenance in every answer.** Aggregate/query tools should return not just a number but the parameters that produced it (date range, category filter, transaction count included) so the answer is independently checkable and so Claude can cite it back to the user ("€412 across 38 transactions, Jan 1–31") instead of a bare figure.
5. **Explicit date-range and "as of" semantics.** Because Dutch households have pending vs. booked transactions and month-boundary payments (salary sometimes lands on the last banking day of the prior month), every aggregate tool needs unambiguous, documented boundary rules — the source of most subtle "why don't your numbers match the bank app" bugs in this class of tool.
6. **Write tools separated from read tools, all mediated through confirm/undo.** Actual Budget's built-in undo/redo stack is the right mental model: every Claude-initiated write (recategorise, edit rule, adjust budget/goal, annotate, update memory) should be a distinct, audit-logged operation with a corresponding revert path, matching PROJECT.md's "every Claude-initiated change is audit-logged … and can be reverted."
7. **Read-only by construction for money movement.** None of the finance MCP servers surveyed expose payment-initiation tools (even Plaid's official server is diagnostics-only) — this validates the project's hard "no payment initiation" boundary as an ecosystem norm, not just a personal choice.

## Nibud — Category Structure and Licensing

Confidence: MEDIUM (Nibud's own site and webshop pages, cross-checked across 3 searches; no direct access to the paid Budgethandboek content itself)

**Category structure.** Nibud's own household-budget methodology (used in its free "Persoonlijk Budgetadvies" tool, its "Uitgaven" content pages, and the professional Budgethandboek) groups spending into four generic bands:
1. **Vaste lasten** (fixed costs) — rent/mortgage, energy, water, telecom/internet/TV, insurance premiums: contractually fixed, recurring, predictable.
2. **Reserveringsuitgaven** (reserve expenses) — costs that *will* happen but not on a fixed date: health-insurance deductible (eigen risico), clothing, household-item replacement, home/garden maintenance, holiday. Nibud's own guidance is that households should set money aside for these in advance — this is structurally the same concept as a "sinking fund" (see below) and validates building sinking-fund-style forecasting into the product.
3. **Huishoudelijke uitgaven** (household expenses) — groceries, cleaning products, personal care: frequent, variable amount, no fixed schedule.
4. **Uitgaven vrije tijd / sociale participatie** (leisure / social participation) — the "minimum package" band covering non-essential-but-normal spending.

**Licensing — the constraint that matters for a public repo.** Nibud sells the actual reference figures commercially:
- The **Budgethandboek** (the compiled reference-budget book + online tool) costs **€142/year** (single purchase) or a **€122/year** subscription, sold via Nibud's own webshop.
- Nibud additionally operates a **developer API portal** (`developer.nibud.nl`) offering an "Uitgaven API" (expenditure data for a typical household), a "Pensioen Uitgaven API" (pensioner-specific), and an "Afloscapaciteit API" (debt-repayment-capacity calculator built on Nibud expenditure data) — with a 30-day free trial, implying a paid subscription beyond that. Exact redistribution terms were not published on the page reached; they sit behind API sign-up.
- No evidence was found that Nibud licenses the *specific euro reference figures* for free redistribution or embedding in third-party open-source software. Given they are sold as the core commercial product (book + subscription + metered API), the reasonable assumption (LOW confidence on the specific legal conclusion, MEDIUM confidence on the commercial-product framing) is that the **figures themselves are proprietary compiled data**, not freely reusable, while the **category *names/structure*** (vaste lasten, reserveringsuitgaven, huishoudelijke uitgaven, sociale participatie) are generic Dutch budgeting terminology also used by Wijzer in Geldzaken, Consumentenbond, budget coaches, and municipal schuldhulp organisations — i.e. functional descriptions, not creative expression, and very unlikely to be independently copyrightable.

**Recommended approach:**
- **Do** commit the Nibud-*inspired* category tree (names, structure, parent/child grouping mirroring vaste lasten / reserveringsuitgaven / huishoudelijke uitgaven / vrije tijd) to the public repo as ordinary application configuration — this is standard domain modelling, not Nibud's protected commercial output.
- **Do not** hard-code Nibud's specific euro reference figures (e.g., "a couple without children should budget €X/month on groceries") anywhere in the public repository, in seed data, in fixtures, or in dashboard defaults.
- **Do** treat reference figures as either (a) a small piece of household-supplied configuration the user enters once from their own (separately, legitimately licensed) Budgethandboek access, stored only in the private database — never in source control — or (b) a live call to the paid Nibud API at runtime using a server-side credential from the env file, with the response cached in the database rather than committed anywhere. Either path keeps the *comparison capability* ("Claude, are we spending more than a typical household on X?") without redistributing Nibud's commercial data product in a public GitHub repo.
- This should be flagged to the user as a decision point before implementation (which of the two paths, and whether the €122–142/year cost is worth it for v1, or whether "compare to reference budget" is deferred to v1.x once the household has enough of its own history to compare month-over-month instead).

## Dutch-Specific Money Patterns and Their Implications

Confidence: MEDIUM–HIGH depending on item (statutory rules like vakantiegeld are HIGH/well-documented law; PSP bank-statement behaviour is MEDIUM, sourced from payment-provider support docs and comparison sites rather than official specs)

| Pattern | What happens | Implication for this project |
|---|---|---|
| **Tikkie / betaalverzoek** | A Tikkie payment request is settled via the recipient's own bank using iDEAL; if the household's bank supports Instant Payments it lands within ~5 seconds, otherwise next business day. The exact wording on the bank statement was not confirmed from documentation (bank-specific), so this must be verified empirically against real (synthetic-shaped) ING statement data during implementation. | Categorisation rules cannot assume a fixed "Tikkie" string pattern with certainty — build the rule engine to let the user correct once and generalise ("that Tikkie was concert tickets" → rule), exactly as PROJECT.md already specifies, rather than trying to hard-code Tikkie parsing. |
| **iDEAL via PSPs (Mollie, Adyen, Buckaroo, etc.)** | Confirmed pattern: payouts route through the payment service provider's own merchant account, so the bank statement shows the **PSP's** name (e.g., "Mollie", processor-style descriptor strings for Adyen) rather than the actual webshop/merchant. This is called out by Mollie's own support docs as a recognized point of customer confusion. | This is the single biggest categorisation accuracy risk in the whole system — a large share of online purchases will arrive with a generic PSP name instead of the merchant. Rules keyed only on counterparty name will systematically miscategorise these. Needs: (a) Claude-assisted review specifically flags PSP-name transactions as low-confidence regardless of amount, (b) the correction UX ("tell Claude what this was") is the *primary* categorisation path for this transaction class, not the fallback, (c) description-field parsing (SEPA remittance info sometimes embeds the real merchant/order reference even when the counterparty name is the PSP) should be attempted before falling back to manual review. |
| **Vakantiegeld (holiday allowance)** | Statutory minimum 8% of gross annual salary, legally required to be paid out at least once a year, in practice almost always in May. Not usually accrued on top of a 13th month/end-of-year bonus (CAO-dependent). | A May income spike is normal, not a leak or a windfall to blindly allocate — the forecast/leak-detection logic must recognise "this is vakantiegeld, expected every May" (ideally via a recurring-income detector mirroring the recurring-cost detector) rather than flagging it as an anomaly, and the advisor should proactively ask the household how they want to earmark it (buffer, goal, discretionary) rather than silently folding it into "spare cash." |
| **13e maand / eindejaarsuitkering** | A separate, CAO/contract-dependent year-end payment, distinct from vakantiegeld, that increases taxable income and can affect toeslagen. | Same recurring-income-detection need as vakantiegeld, but on a different (December-ish, contract-specific) schedule — cannot assume a fixed calendar month across all households; must be learned from the household's own transaction history (recurring, once-yearly, larger-than-usual credit) rather than hard-coded to May/December. |
| **Toeslagen (zorgtoeslag, huurtoeslag) & belastingteruggave** | Monthly or annual government payments, means-tested against total taxable income (including vakantiegeld and 13th-month payments, which can push a household over an income threshold and trigger partial repayment the following year). | These are recurring income too, but with *repayment risk* baked in — a genuinely differentiating advisor capability is flagging "your income this year (incl. vakantiegeld/bonus) may cross the zorgtoeslag/huurtoeslag threshold, budget for a possible reclaim next year" — this is a real "explain the past / plan ahead" job neither Firefly III, YNAB, nor Monarch has any concept of, because it is Dutch-tax-system-specific. |
| **Energy: voorschot (monthly instalment) + jaarnota (annual settlement)** | Monthly instalments are provisional; the annual settlement either bills a shortfall or refunds a surplus based on actual usage, and providers auto-adjust the next year's instalment amount accordingly. | The annual settlement is a genuine one-off transaction that should not be miscategorised as "energy overspend this month" — it needs its own recognisable pattern (large one-off same-counterparty transaction once a year) and should feed into the sinking-fund/reserve logic (Nibud's own "reserveringsuitgaven" concept) rather than distorting a single month's category-vs-budget view. |
| **Eigen risico (health-insurance deductible), gemeentelisted taxes (WOZ/gemeentelijke belastingen), car insurance billed annually, subscriptions billed yearly** | All are real but infrequent (annual or ad hoc) costs — exactly Nibud's "reserveringsuitgaven" band. | These are the concrete list of costs that a **sinking-fund / annual-cost-smoothing feature** needs to track: identify historically-annual, same-counterparty-or-category costs, divide by 12, and show "true monthly cost" alongside the raw monthly ledger — directly addresses the "money seems to disappear" and "plan ahead" jobs named in PROJECT.md's Context section. |
| **Transfers to/from own savings accounts** | Must never be counted as spending or income — every serious competitor (Firefly III via double-entry, Actual Budget, MoneyWiz, and others) treats an internal transfer as a structurally distinct transaction type, typically auto-detected by matching amount+date+counterparty-is-own-account across linked accounts. | Table stakes, already named directly in PROJECT.md's categorisation requirements. Detection approach: match by IBAN-is-one-of-the-household's-own-accounts (known set, since v1 only covers the joint account + savings accounts) rather than heuristic amount/date matching — simpler and more reliable than the general "any two banks" case competitor apps have to solve. |

## Advisor Capabilities — Mapped to the Four Named Jobs

PROJECT.md names four jobs for "Claude as financial advisor": explain the past, plan ahead, find leaks, proactive scheduled reviews. Each has different data/tool needs:

**1. Explain the past** ("where did our money go in August?")
- Needs: category/period/merchant aggregation tools (server-side sums, never raw-row math — see MCP lessons above), month-over-month and year-over-year comparison, and — pending the Nibud licensing decision above — comparison against a household-configured or API-fetched reference figure.
- Differentiator: explaining *why*, not just *what* — e.g., correlating an unusual month against a known one-off (annual settlement, vakantiegeld, price increase) rather than just reporting a number. This requires the recurring-cost/income detectors above to be queryable by the advisor, not just displayed on a dashboard.

**2. Plan ahead** ("can we afford a €2k holiday in March?")
- Needs: end-of-month/period forecast (recurring costs + current pace, already in PROJECT.md), goal progress + projected completion date, and critically the sinking-fund view of annual costs due between now and March so the answer accounts for, e.g., an insurance renewal landing in February.
- This is the single hardest advisor job technically, because it requires composing three data sources (forecast, goals, annual-cost calendar) into one coherent answer — a strong argument for a dedicated `forecast_affordability`-style aggregate tool rather than expecting Claude to compose three separate tool calls correctly and consistently every time (recall: LLMs are worse at multi-step arithmetic composition, so push the composition into the server).

**3. Find leaks** (subscriptions, price increases)
- Needs: recurring-cost detection (already scoped) plus explicit **price-increase detection** — comparing the amount of the Nth occurrence of a recurring charge against the (N-1)th, flagging deltas above a threshold. Copilot Money's "subscription price increase" surfacing is the direct commercial precedent (MEDIUM confidence, consistent across three independent reviews) — validate this as a distinct detector, not a side effect of recurring-cost detection.
- Differentiator vs. Dyme: **surface and advise, do not act.** Dyme's "cancel this for you" service is explicitly the line this project should not cross (see Anti-Features) — the advisor's job ends at "here's a subscription you haven't used, here's what it costs annualised, want me to draft a note for you to send," not initiating any cancellation or contract change.

**4. Proactive scheduled reviews** (already scoped in PROJECT.md: monthly, stored in app, visible in Grafana, readable as a Claude conversation)
- Needs: everything from jobs 1–3 pre-aggregated into a review record, plus a notification path that deliberately excludes financial content (already decided — email is link-only).
- Advisor memory is what makes review #2 build on review #1 instead of starting cold: it should hold (a) a **household profile** — named goals, known fixed commitments, stated preferences ("we don't want reminders about eating out, we know"), (b) a **log of past advice and decisions** — what was flagged, what the household decided to do about it, and whether it recurred — so the advisor doesn't re-flag something already discussed and dismissed, and (c) **provenance** — which review/session produced each memory entry, so a stale or superseded piece of advice can be identified and pruned. General LLM-agent-memory research explicitly recommends temporal versioning, source attribution, and periodic consolidation to avoid a memory store silently going stale — this maps directly onto PROJECT.md's "store reviews" and "update advisor memory" write tools.

**Audit log + undo** — already scoped as a requirement; the competitive precedent (Actual Budget's undo/redo stack) confirms this should be a first-class reversible operation per write, not a generic changelog the user has to manually interpret and reverse by hand.

## Dashboard Features (Grafana, Partner-Facing, Bilingual)

Confidence: MEDIUM (general Grafana UX best-practice sources, not finance-specific)

- **Table stakes glanceability**: a single top row of the 3–5 numbers that matter most (this month's spend vs. budget, savings-goal progress, days-until-next-large-known-cost) — best-practice guidance consistently says put the numbers that matter where the eye lands first, one row, before any drill-down panel.
- **Consistent units/colour/labels, no jargon**: for a non-technical partner, this means EUR formatting throughout, a fixed colour meaning (e.g., always red = over budget, never reused for anything else), and category labels in the household's own words, not raw bank-description strings.
- **Bilingual EN/NL**: dashboards provisioned as code (already decided) should treat panel titles/labels as data driven by a locale, not duplicated dashboards forked and hand-maintained — duplicated dashboards drift out of sync the first time one is edited and the other isn't.
- **Differentiator over the competitor apps surveyed**: none of Firefly III, Actual Budget, YNAB, Monarch, or the Dutch bank apps expose a "read as a Claude conversation" review artifact next to the chart — pairing a structured scheduled-review record (job 4 above) with its own Grafana panel (e.g., a text panel rendering the latest review, linked from the top-level dashboard) is a genuine differentiator unique to this project's Claude-as-advisor core value, not available in any surveyed competitor.

## Feature Landscape

### Table Stakes (Users Expect These)

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Rule-based auto-categorization (counterparty/IBAN/description) | Every surveyed product (Firefly III, Actual, YNAB, Lunch Money, Monarch, Copilot, Dyme, bunq) has this; without it every transaction needs manual review | MEDIUM | Rules must live in data (DB), per PROJECT.md constraint; must handle PSP-obscured merchants gracefully (flag, don't force-match) |
| Internal-transfer detection (own accounts) | Every competitor treats this as non-negotiable; without it, moving money to savings looks like spending | LOW–MEDIUM | Simplified here: known, fixed set of own IBANs (joint + savings), not the general N-bank matching problem competitors solve |
| Monthly budgets with actual-vs-budget | Present in every product surveyed, from Firefly III to YNAB to bunq's "auto-budget" suggestion | MEDIUM | |
| Recurring-cost / subscription detection | Firefly III (recurring transactions), Actual (schedules), Monarch, Copilot, Dyme, ING Kijk Vooruit all have this; users increasingly expect it as standard, not premium | MEDIUM–HIGH | ING's own model (date from last-4, amount from last-5 occurrences) is a concrete, provable algorithm to benchmark against |
| Named savings goals with progress | YNAB, Monarch, bunq (pots) all have this | LOW–MEDIUM | PROJECT.md also wants a projected completion date — most competitors show progress but not projection; treat projection as slightly above table stakes |
| End-of-month / short-horizon forecast | ING Kijk Vooruit (35-day forecast), bunq (ML balance prediction), ba Monarch (cash-flow projection) all ship this natively in a *bank* app, raising the bar for a purpose-built product | MEDIUM–HIGH | |
| Category drill-down / trends-over-time dashboard | Every product's core screen | LOW–MEDIUM | Grafana-native, well-trodden |
| Undo/revert for automated changes | Actual Budget's undo/redo stack is the direct precedent | MEDIUM | Maps to PROJECT.md's audit-log + revert requirement |

### Differentiators (Competitive Advantage)

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Claude as a conversational advisor with write access (not just chat-about-data) | No surveyed competitor lets the assistant *change* categories/rules/budgets/goals itself with audit+undo — Monarch and Copilot have "AI assistants" but they answer questions, they don't edit the ledger | HIGH | This is the project's stated core value; everything else in this document supports it |
| Advisor memory across sessions/clients (household profile + advice log) | Neither Firefly III/Actual (no AI layer) nor Monarch/Copilot (single-vendor AI, not user-owned memory store) persist advisor context the household fully owns and can inspect | HIGH | Needs temporal versioning/provenance per the LLM-memory research above to avoid staleness |
| Dutch-specific pattern handling (PSP-obscured merchants, vakantiegeld/toeslagen-aware income detection, Nibud-structured reference comparison) | No competitor surveyed (US-centric YNAB/Monarch/Copilot, generic Firefly III/Actual, or the Dutch apps which don't do LLM advice) solves this combination | HIGH | The genuine "opening" in the competitive landscape identified by this research |
| Annual-cost smoothing / sinking-fund view ("true monthly cost") | Directly addresses Nibud's own "reserveringsuitgaven" concept and the "money seems to disappear" complaint named in PROJECT.md | MEDIUM–HIGH | Requires reliable recurring-annual-cost detection (same detector family as subscription detection, longer period) |
| Price-increase detection on recurring charges | Copilot Money's differentiator; direct precedent to replicate | MEDIUM | Compare Nth vs (N-1)th occurrence amount, threshold-based flag |
| Proactive scheduled review as a stored, dashboard-visible, conversational artifact | Unique combination — no competitor pairs a Grafana panel with a readable Claude-authored review | MEDIUM–HIGH | Builds on audit log + memory + aggregation tools already required |
| Toeslagen/tax-threshold-crossing warning (vakantiegeld/bonus pushing household over a toeslag income threshold) | Genuinely novel — not present in any surveyed product, Dutch or otherwise | HIGH | Needs household gross-income awareness and threshold data (a small, low-risk piece of static Dutch tax config, unlike Nibud's proprietary figures) |
| Two-person shared view with no separate tiers/logins | Monarch treats this as its headline differentiator over Mint | LOW (given single-household, no multi-tenancy scope) | Already trivially true here since there is only one household and one shared dashboard set |

### Anti-Features (Commonly Requested, Often Problematic)

| Feature | Why Requested | Why Problematic | Alternative |
|---------|---------------|------------------|-------------|
| Payment initiation / "let Claude pay this bill" | Feels like the natural next step once Claude can read everything | PSD2 read-only aggregators don't offer this safely for personal use; irreversible financial risk if the LLM ever acts on a hallucinated instruction; already explicitly out of scope in PROJECT.md | Read-only always; Claude can draft a payment reminder/message for a human to act on, never execute one |
| Investment advice / portfolio recommendations | Natural extension of "financial advisor" framing | In the Netherlands, automated systems that surface a specific, personalised product recommendation directly to a consumer cross into Wft-regulated "advies" territory requiring an AFM licence and designated responsible persons — a hobby project cannot and should not attempt this; PROJECT.md already scopes investment advice out | Keep the advisor scoped to spending/budgeting/saving, exactly as PROJECT.md states; if investment questions come up, the advisor should say so is out of scope rather than answer |
| Subscription cancellation / contract-switching as a service (à la Dyme) | Looks like the logical payoff of "found a leak" | Requires acting as an agent in third-party contracts/negotiations — a different trust and liability surface than "read my data and advise me"; also often monetized by competitors as a paid, human-assisted service for good reason (it's operationally hard, not just technically) | Surface the leak and its annualised cost; let Claude draft the cancellation email/message text for the human to send |
| Gamification (streaks, badges, leaderboards) | Seen across consumer fintech as an engagement lever; feels like it would nudge better habits | Research is explicit that streak/loss-aversion mechanics can create anxiety and cause people to make bad financial decisions just to protect a streak (e.g., topping up a savings pot they can't spare); also actively drew UK FCA scrutiny for gambling-like patterns in trading apps | Rely on the advisor's judgement and scheduled reviews for behavioural nudges instead of manufactured engagement mechanics; a two-person household using this occasionally is a feature, not a retention problem to solve |
| Manual-entry-heavy transaction flows (cash tracking, receipt photos, envelope allocation of every euro) | Some competitors (YNAB's zero-based method, cash-envelope apps) build their whole UX around this | The joint account is described as "effectively the complete household picture" — manual entry adds ongoing user burden for a case PROJECT.md says doesn't need it; conflicts with the "fully automatic ingestion" decision already made | Automatic ingestion only, as already decided; the (nice-to-have) web page is for *correcting* auto-imported data, not entering it from scratch |
| Financial figures in email notifications | Feels convenient — see the number without opening the dashboard | Email leaves the home network and persists indefinitely at the mail provider, an unnecessary and already explicitly rejected data-exposure surface in PROJECT.md | Notification-only email ("your review is ready") with a link into the VPN-only dashboard, exactly as already decided |
| Full custom frontend replacing Grafana | Tempting once a "small web page" exists for corrections, to unify everything in one UI | Duplicates Grafana's provisioning-as-code investment and doubles the UI surface to secure/maintain, for a two-person household that doesn't need a polished consumer product | Grafana stays the dashboard; the web page stays scoped to review/correction only, per PROJECT.md |
| Multi-bank / multi-tenant support in v1 | "Might as well build it generically" instinct | Adds abstraction and testing surface for a problem this specific household doesn't have (single bank, single household); PSD2 aggregator behaviour differs enough per bank that "generic" is more work than it looks | Interface-isolate the ingestion layer (already decided) so it's *revisitable*, but don't build for banks/households that don't exist yet |

## Feature Dependencies

```
Rule-based auto-categorization
    └──requires──> Internal-transfer detection (own IBANs known first)
                       └──enhances──> Recurring-cost/income detection (transfers excluded from spend/income series)

Recurring-cost detection
    └──requires──> Stable category tree (Nibud-structured)
    └──enables──> Price-increase detection (Nth vs N-1th amount)
    └──enables──> End-of-month forecast
    └──enables──> Annual-cost smoothing / sinking-fund view (longer-period recurrence)

Recurring-income detection (salary, vakantiegeld, 13e maand, toeslagen)
    └──requires──> Internal-transfer detection (so income series isn't polluted by savings transfers)
    └──enables──> Toeslagen/tax-threshold warning
    └──enables──> "Plan ahead" affordability answers (income timing vs. cost timing)

Named savings goals with projection
    └──requires──> Recurring-income detection (to project contribution pace)
    └──enhances──> "Plan ahead" affordability answers

MCP read tools (aggregate/query, server-side math)
    └──requires──> Rule-based categorization + transfer detection + recurring detection (all read tools query pre-cleaned data)
    └──enables──> Advisor jobs 1–4 (explain, plan, find leaks, review)

MCP write tools + audit log + undo
    └──requires──> Audit log storage design (before/after, who, when)
    └──enables──> User-correction-becomes-a-rule flow
    └──enables──> Advisor memory writes (update household profile, log past advice)

Advisor memory (household profile + advice log)
    └──requires──> MCP write tools + audit log
    └──enables──> Proactive scheduled reviews that build on prior reviews (job 4)

Nibud-inspired category tree (names/structure)
    └──conflicts──> Committing Nibud's actual reference euro figures to the public repo (licensing)
    └──enhances──> "Explain the past" (reference-budget comparison), IF reference figures sourced via user config or paid API, never hard-coded

Dashboards (Grafana, provisioned as code)
    └──requires──> All read-side aggregation (categorization, recurring detection, goals, forecast)
    └──enhances──> Proactive scheduled review (paired review-artifact panel is a differentiator)

Anti-feature: Payment initiation ──conflicts──> Read-only bank access (hard architectural boundary, not a feature gap)
Anti-feature: Investment advice ──conflicts──> AFM/Wft-regulated automated-advice obligations (legal boundary)
```

### Dependency Notes

- **Recurring-cost/income detection requires internal-transfer detection first:** without excluding transfers, a monthly transfer to the savings account would masquerade as a huge, recurring "expense," corrupting every downstream forecast and leak-detection feature.
- **MCP read tools require the categorization/detection pipeline to already be clean:** per the MCP-design lessons above, the aggregate tools should query already-categorised, already-transfer-excluded data — pushing "what counts as spending" logic into the ingestion/categorisation layer, not into every individual MCP tool's query.
- **Nibud reference-figure comparison conflicts with the public-repo constraint:** this is the one place in the feature landscape where a genuinely valuable table-stakes-adjacent capability (comparing to a reference budget, which every Nibud-based Dutch budget-coach tool does) cannot be implemented the "obvious" way (hard-coded seed data) without a licensing problem — flag this explicitly for the requirements/roadmap phase as a decision, not an oversight.
- **Advisor memory enhances but does not block proactive reviews:** the first scheduled review can run without prior memory (cold start); memory's value compounds from the second review onward, so it can reasonably land in a later phase than the review mechanism itself, provided the review-record schema is memory-ready from day one (don't retrofit provenance/versioning later).

## MVP Definition

### Launch With (v1)

Minimum viable product — enough to validate "Claude as trustworthy advisor," not a feature-complete budgeting app.

- [ ] Automatic ingestion + idempotent sync — nothing else works without clean, current data
- [ ] Internal-transfer detection (known own-IBAN set) — table stakes, cheap given the known-account simplification
- [ ] Rule-based categorization with Claude-assisted review of low-confidence/PSP-obscured transactions, and "tell Claude what this was → becomes a rule" correction loop — this is the categorisation accuracy backbone everything else depends on
- [ ] Nibud-structured category tree (names/structure only — no hard-coded reference figures) — needed as the shared vocabulary for every other feature
- [ ] Monthly budgets, actual-vs-budget
- [ ] Recurring-cost detection with a simple end-of-month forecast (ING Kijk Vooruit's last-4/last-5-occurrence model is a good concrete starting algorithm)
- [ ] MCP read tools: search/aggregate transactions (server-side math, paginated, provenance-carrying), budgets, goals, recurring costs, forecast
- [ ] MCP write tools + audit log + undo for recategorisation and rule creation (the minimum write surface needed for the correction loop above)
- [ ] Grafana dashboards: where the money goes, category drill-down, trends, budget vs. actual (EN/NL)
- [ ] One proactive scheduled review (monthly), stored, dashboard-visible, notification-only email

### Add After Validation (v1.x)

- [ ] Named savings goals with projected completion date — add once the income/recurring-cost detection is proven accurate enough to project against
- [ ] Annual-cost smoothing / sinking-fund view — add once at least one full year of history exists to detect annual patterns reliably
- [ ] Price-increase detection on recurring charges — add once recurring-cost detection has enough occurrences per merchant to compare deltas meaningfully
- [ ] Advisor memory (household profile + advice log across sessions) — add once the first 1–2 scheduled reviews have run and there is real content to remember and build on
- [ ] Nibud reference-figure comparison — add once the licensing path (user-entered config vs. paid API) is decided and budgeted

### Future Consideration (v2+)

- [ ] Vakantiegeld/toeslagen-aware income detection and toeslagen-threshold warnings — genuinely novel and valuable, but needs a full year of real income data and Dutch tax-threshold config to be trustworthy; premature before the core advisor loop is proven
- [ ] Multi-bank ingestion (beyond ING) — explicitly deferred; the ingestion interface should stay open to it without building it now
- [ ] Web page for review/correction beyond what's needed for the categorisation loop — nice-to-have per PROJECT.md, safe to defer entirely to v1.x or v2

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| Automatic ingestion + idempotent sync | HIGH | HIGH | P1 |
| Internal-transfer detection | HIGH | LOW | P1 |
| Rule-based categorization + Claude-assisted correction loop | HIGH | HIGH | P1 |
| Nibud-structured category tree (names only) | HIGH | LOW | P1 |
| Monthly budgets, actual-vs-budget | HIGH | MEDIUM | P1 |
| Recurring-cost detection + forecast | HIGH | MEDIUM–HIGH | P1 |
| MCP read tools (server-side aggregation) | HIGH | MEDIUM–HIGH | P1 |
| MCP write tools + audit log + undo | HIGH | MEDIUM | P1 |
| Grafana dashboards (EN/NL, provisioned) | HIGH | MEDIUM | P1 |
| One proactive scheduled review | HIGH | MEDIUM | P1 |
| Savings goals with projected completion | MEDIUM | LOW–MEDIUM | P2 |
| Annual-cost smoothing / sinking fund | HIGH | MEDIUM–HIGH | P2 |
| Price-increase detection | MEDIUM | LOW | P2 |
| Advisor memory (cross-session) | HIGH | MEDIUM | P2 |
| Nibud reference-figure comparison | MEDIUM | MEDIUM (licensing decision + integration) | P2 |
| Toeslagen-threshold warnings | MEDIUM | HIGH | P3 |
| Multi-bank ingestion | LOW (not needed by this household) | HIGH | P3 |
| Web page beyond correction flow | LOW | MEDIUM | P3 |

**Priority key:**
- P1: Must have for launch
- P2: Should have, add when possible
- P3: Nice to have, future consideration

## Competitor Feature Analysis

| Feature | Firefly III | Monarch Money | Copilot Money | Our Approach |
|---------|--------------|--------------|--------------|--------------|
| Categorization | Rule engine (data-driven), manual fallback | AI-assisted, learns from corrections | AI-assisted, learns from corrections | Rule engine (DB-stored) + Claude-assisted review of low-confidence/PSP-obscured transactions, corrections become rules |
| Transfers | Double-entry bookkeeping (structural) | Auto-detected | Auto-detected | Known-own-IBAN matching (simpler than general case) |
| Recurring/subscriptions | Recurring-transaction objects, bill forecasting | Auto-detected, calendar view | Auto-detected, dedicated view, price-increase flagged | Detector modelled on ING's last-4/last-5-occurrence approach; price-increase flagging as a distinct capability |
| Goals | Piggy banks (basic) | Goals with targets | Not a focus | Named goals with target + projected completion date |
| Advisor/AI | None native (unofficial MCP servers exist, 100+ tools each) | In-app AI assistant (Q&A, not ledger-editing) | AI categorization + proactive insights (no write-back conversational agent) | Claude with read+write MCP tools, audit log, undo, persistent cross-session memory — the actual differentiator |
| Couple/household use | Single-tenant, technical UI | Native, free, headline feature | Single-user focus (Apple-ecosystem) | Native — only one household exists, shared dashboards by default |
| Dutch-specific handling | None | None | None | PSP-obscured-merchant handling, vakantiegeld/toeslagen-aware income detection, Nibud-structured categories |

## Sources

- Firefly III documentation and community reviews: [Introduction and features — Firefly III docs](https://docs.firefly-iii.org/explanation/firefly-iii/about/introduction/), [Firefly III Review 2026](https://www.expensesorted.com/blog/147_firefly_iii)
- Actual Budget: [actualbudget.org](https://actualbudget.org/), [Actual Budget Review 2026](https://www.expensesorted.com/blog/144_actual_budget)
- YNAB: [YNAB Review 2026](https://marriagekidsandmoney.com/ynab-review/), [Monarch vs YNAB — The Motley Fool](https://www.fool.com/money/personal-finance/monarch-money-vs-ynab)
- Lunch Money: [lunchmoney.app/features](https://lunchmoney.app/features), [Multi Currency — Lunch Money](https://lunchmoney.app/features/multicurrency/)
- Monarch Money: [monarch.com/features/tracking](https://www.monarch.com/features/tracking), [Monarch Money Review — FinanceBuzz](https://financebuzz.com/monarch-money-review)
- Copilot Money: [Copilot Money Review — WalletGrower](https://walletgrower.com/blog/copilot-money-review-2026), [Copilot Money Review — Forbes Advisor](https://www.forbes.com/advisor/banking/copilot-budget-app-review/)
- Dyme (NL): [Nederlandse fintech Dyme geeft inzicht in verborgen lasten](https://www.banken.nl/nieuws/21564/nederlandse-fintech-dyme-geeft-inzicht-in-verborgen-lasten), [Dyme opzegservice](https://dyme.app/meer-over-geld/overig/dyme-opzegservice)
- ING Kijk Vooruit: [ING — Kijk Vooruit](https://www.ing.nl/particulier/digitaal-bankieren/jouw-app/kijk-vooruit/kijk-vooruit), [Consumentenbond — ING Kijk(t) Vooruit](https://www.consumentenbond.nl/betaalrekening/ing-kijkt-vooruit)
- bunq: [bunq Insights](https://www.bunq.com/nl-nl/personal-account/features/insights), [bunq Insights — iCulture](https://www.iculture.nl/nieuws/bunq-insights-uitgaven-categorieen/)
- YNAB MCP servers: [EdgeCaseLabs/ynab-mcp](https://github.com/EdgeCaseLabs/ynab-mcp), [calebl/ynab-mcp-server](https://github.com/calebl/ynab-mcp-server), [issmirnov/ynab-mcp-server](https://github.com/issmirnov/ynab-mcp-server)
- Actual Budget MCP servers: [agigante80/actual-mcp-server](https://github.com/agigante80/actual-mcp-server) (71 tools), [jimmyzmli/actual-mcp](https://github.com/jimmyzmli/actual-mcp)
- Firefly III MCP servers: [braindaamage/firefly-iii-mcp](https://github.com/braindaamage/firefly-iii-mcp) (114 tools), [daften/fireflyiii-mcp](https://github.com/daften/fireflyiii-mcp) (140 tools)
- Plaid MCP: [Plaid — MCP Server docs](https://plaid.com/docs/resources/mcp/), [Plaid MCP AI assistant blog](https://plaid.com/blog/plaid-mcp-ai-assistant-claude/), [t-rhex/plaid-mcp](https://github.com/t-rhex/plaid-mcp)
- MCP tool-design guidance: [Designing MCP tools that don't blow up your agent's context window — RunPod](https://www.runpod.io/blog/designing-mcp-tools), [Block's Playbook for Designing MCP Servers](https://engineering.block.xyz/blog/blocks-playbook-for-designing-mcp-servers), [MCP and the "too many tools" problem](https://demiliani.com/2025/09/04/model-context-protocol-and-the-too-many-tools-problem/), [Extending ResourceLink: Patterns for Large Dataset Processing in MCP (arXiv 2510.05968)](https://arxiv.org/pdf/2510.05968)
- LLM financial-data hallucination: [FinOS — Hallucination and Inaccurate Outputs](https://air-governance-framework.finos.org/risks/ri-4_hallucination-and-inaccurate-outputs.html), [Fin-RATE benchmark (arXiv 2602.07294)](https://arxiv.org/pdf/2602.07294)
- LLM agent memory design: [Enabling Personalized Long-term Interactions in LLM-based Agents (arXiv 2510.07925)](https://arxiv.org/pdf/2510.07925), [Redis — AI agent memory: types, architecture & implementation](https://redis.io/blog/ai-agent-memory-stateful-systems/)
- Nibud: [Nibud — Voor professionals: de Nibud-methode van budgetteren](https://www.nibud.nl/onderwerpen/rondkomen/plannen-en-begroten/nibud-methode-van-budgetteren/), [Nibud — Referentiebegrotingen](https://www.nibud.nl/samenwerken/cijfers-en-rekentools/referentiebegrotingen/), [Nibud Webwinkel — Budgethandboek 2026](https://winkel.nibud.nl/budgethandboek), [Nibud Developer Portal](https://developer.nibud.nl/products2), [Nibud — Huishoudelijke uitgaven](https://www.nibud.nl/onderwerpen/uitgaven/huishoudelijke-uitgaven/)
- Tikkie / PSP behaviour: [Tikkie FAQ](https://www.tikkie.me/faq/particulier/veelgestelde-vragen-betaalverzoeken), [Mollie — What will my customer see on their bank statement?](https://help.mollie.com/hc/en-us/articles/360005449393-What-will-my-customer-see-on-their-bank-statement), [Adyen bank statement code — Who Charged Me?](https://merchants.letsweel.com/merchants/adyen)
- Vakantiegeld/toeslagen: [Vakantiegeld over bonus of 13e maand — Nmbrs](https://www.nmbrs.com/nl/blog/vakantiegeld-over-bonus-13e-maand), [Vakantiegeld 2026 — RekenmachinePro](https://rekenmachinepro.nl/blog/vakantiegeld-2026-wanneer-netto/)
- Eigen risico / energy settlement: [Zorgwijzer — Eigen risico](https://www.zorgwijzer.nl/faq/eigen-risico), [Vastelastenbond — Voorschotnota & verbruik jaarrekening](https://www.vastelastenbond.nl/energie/begrippen/voorschot-nota-bedrag-verbruik-jaar-eind-afrekening/)
- Sinking funds: [CalendarBudget — Sinking Funds](https://calendarbudget.com/sinking-funds-the-secret-to-stress-free-budgeting-for-irregular-expenses/)
- AFM/Wft automated advice: [Financiële zorgplicht bij geautomatiseerd advies — Barents Krans](https://www.barentskrans.nl/publicatie-blogs/financiele-zorgplicht-bij-geautomatiseerd-advies-wat-vereist-de-wft/)
- Gamification risk: [Gamification for Personal-Finance Apps — Trophy](https://trophy.so/blog/gamification-for-personal-finance-apps), [Gamification in fintech: Financial literacy or just engagement? — 11:FS](https://www.11fs.com/article/gamification-in-fintech-financial-literacy-or-just-engagement)
- Grafana dashboard design: [Grafana Labs — Getting started: best practices](https://grafana.com/blog/getting-started-with-grafana-best-practices-to-design-your-first-dashboard/), [MetricFire — 7 Best Practices for Grafana Dashboard Design](https://www.metricfire.com/blog/7-best-practices-for-grafana-dashboard-design/)

---
*Feature research for: Self-hosted household personal-finance backend with Claude/MCP financial advisor (Dutch household, ING)*
*Researched: 2026-09-26*
