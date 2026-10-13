# Phase 4: Trustworthy Categorisation - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-13
**Phase:** 04-trustworthy-categorisation
**Areas discussed:** Category tree, Review queue & proposals, Rules & history, Own accounts & transfers, Write confirmation, Audit & undo, Grafana drill-down, History comparisons

---

## Category tree

**Tree depth**

| Option | Description | Selected |
|--------|-------------|----------|
| Two levels (Recommended) | Band → category; simple drill-down; no extra levels | |
| Three levels | Band → category → subcategory | |
| Any depth | Claude can nest as deep as needed | ✓ |

**Starter tree**

| Option | Description | Selected |
|--------|-------------|----------|
| Generic starter tree (Recommended) | Migration seeds the four Nibud bands, Income and Transfers, plus about 30 generic categories; Claude refines | ✓ |
| Bands only, Claude fills | Claude proposes categories in the first session | |
| Empty, Claude builds all | Nothing seeded | |

**Names**

| Option | Description | Selected |
|--------|-------------|----------|
| Both languages (Recommended) | English and Dutch name per category | ✓ |
| Dutch only | One Dutch name everywhere | |
| English only | One English name everywhere | |

**Splits**

| Option | Description | Selected |
|--------|-------------|----------|
| One category each (Recommended) | No splitting | ✓ |
| Allow splits | Parts with their own category and amount | |

**Assign where** (follow-up after "any depth")

| Option | Description | Selected |
|--------|-------------|----------|
| Any level, rolls up (Recommended) | Transactions can sit on parents; totals include everything beneath | ✓ |
| Lowest level only | Leaves only; adding a child forces moves | |

**Remove/merge**

| Option | Description | Selected |
|--------|-------------|----------|
| Move to a target (Recommended) | Named receiver (default parent), counts first, one audited, undoable change | ✓ |
| Refuse while in use | Only empty categories can be removed | |

**User's choice:** Any depth with roll-up; otherwise all recommended options.

---

## Review queue & proposals

| Question | Options presented | Selected |
|----------|-------------------|----------|
| What sends a transaction to the queue? | No confident rule (Recommended) / That, plus odd amounts | No confident rule |
| What happens to Claude's proposals before you confirm? | Stored, held (Recommended) / Only in the chat / Applied, unconfirmed | Stored, held |
| How is confidence recorded? | Three named levels (Recommended) / A score 0–100 | Three named levels |
| How are two years of uncategorised history worked through? | Rules first, then queue (Recommended) / New transactions only | Rules first, then queue |

**User's choice:** All recommended.

---

## Rules & history

| Question | Options presented | Selected |
|----------|-------------------|----------|
| What can a rule match on? | Fields + simple words (Recommended) / Also regular expressions / Exact IBAN or name only | Fields + simple words |
| Which rule wins when several match? | Most specific wins (Recommended) / Explicit priority | Most specific wins |
| What happens to already-categorised transactions when a rule changes? | Preview, then confirm (Recommended) / Rule-set ones follow | Preview, then confirm |
| How are PSPs and Tikkie handled? | Flag + needs a second signal (Recommended) / Flag, name rules go to queue | Flag + needs a second signal |

**User's choice:** All recommended.

---

## Own accounts & transfers

| Question | Options presented | Selected |
|----------|-------------------|----------|
| How is a transfer represented? | A kind of category (Recommended) / Separate transfer flag | A kind of category |
| How are unsynced own accounts registered? | Claude proposes, you confirm (Recommended) / Operator only, via REST | Claude proposes, you confirm |
| How is money to/from personal accounts counted? | Own category, counted (Recommended) / Internal transfer | Own category, counted |
| How do refunds and paybacks count? | Net within the category (Recommended) / All incoming is income | Net within the category |

**Notes:** Claude flagged that ING usually describes savings transfers by account number in the description, not as a normal IBAN, so the existing IBAN rule likely misses them. To be verified against real data.

---

## Write confirmation

| Question | Options presented | Selected |
|----------|-------------------|----------|
| How does a confirmation reach the user? | Elicitation, token fallback (Recommended) / Preview token only / Confirm outside Claude | Elicitation, token fallback |
| Which writes need it? | Bulk and structural (Recommended) / Every write | Bulk and structural |
| How strict are write limits? | Moderate (Recommended) / Strict | Moderate |
| Watch for instruction-like text? | Flag and alert (Recommended) / Marking only | Flag and alert |

**Notes:** Claude pointed out that "always allow" on a write tool in Claude Code turns the client-approval fallback off; the docs must say so.

---

## Audit & undo

| Question | Options presented | Selected |
|----------|-------------------|----------|
| How is a bulk change recorded and undone? | One operation, items inside (Recommended) / Also per item | One operation, items inside |
| Undo after later changes? | Skip and report (Recommended) / All or nothing / Overwrite anyway | Skip and report |
| Which REST endpoints? | Undo + audit read (Recommended) / Full REST mirror / No REST writes yet | Undo + audit read |
| Where does "concert tickets" go? | Note on the transaction (Recommended) / Not now | Note on the transaction |
| What goes into the audit log? | Data changes (Recommended) / Data + operational | Data changes |

**User's choice:** All recommended.

---

## Grafana drill-down

| Question | Options presented | Selected |
|----------|-------------------|----------|
| Dashboard layout | One drill-down dashboard (Recommended) / Overview + detail / Extend sync dashboard | One drill-down dashboard |
| Uncategorised and queue | Own bar + status row (Recommended) / Separate panel only | Own bar + status row |
| Income and savings flows | Top line + spending (Recommended) / Spending only | Top line + spending |
| Show changes to both partners | Recent changes panel (Recommended) / No, Claude/REST only | Recent changes panel |

**User's choice:** All recommended.

---

## History comparisons

| Question | Options presented | Selected |
|----------|-------------------|----------|
| Baselines | Three baselines (Recommended) / Month and year only | Three baselines |
| Unfinished month | Same days, like for like (Recommended) / Complete months only | Same days, like for like |
| Thin history during the backlog | Show and warn (Recommended) / Compare as is | Show and warn |
| Grafana display | Table + last-year line (Recommended) / Table only | Table + last-year line |

**User's choice:** All recommended.

---

## Claude's Discretion

- The MCP tool set, names and consolidation within the 8–15 tool budget
- When the rule engine runs, including on pending rows
- The starter tree's exact categories and EN/NL names
- Confirmation token lifetime, change-set fingerprinting and SDK wiring for elicitation
- Instruction-like text patterns and alert threshold
- Metric names and alert rules
- Audit retention and the before/after storage format
- Note length cap and cleaning
- New dashboard generator panel types and drill-down links
- Comparison edge cases
- How proposals and their reasons are presented

## Deferred Ideas

- Splitting transactions (rejected for now)
- Full REST write endpoints (v2 web page)
- The partner confirming proposals (v2 web page, or partner AI access)
- Projecting the current month forward (Phase 5 forecast)
- Annual-cost smoothing in comparisons (Phase 5 true monthly cost)
- Annotations beyond the short note (Phase 6)
