# PDDM Demo Script — Golden Questions

Use after a successful **Ingest**. Optimal answers must **navigate** (hierarchy + Jira browse links), not paste the retrieved context.

Prompts are also available as click-to-fill chips on the Chat empty state (`GoldenDemoQuestions` in Shared).

Ingest automatically:

- Wipes and recreates `./data/spark-docs` (no orphan duplicates)
- Requests Jira `comment` (+ Epic Link) fields
- Fetches Sub-tasks
- Seeds **SPARK-57337** and **SPARK-44444**, plus an ANSI text search slice

After ingest, Stats should show **Tier3 (Comments) > 0** and usually **Decision comments > 0**.

**Intent routing:** Q1–Q3 hit the **heuristic fast path** (key / phrase lists). Paraphrases of Q2/Q3 that miss those phrases use a short **LLM JSON classify** before retrieval. Classified intent is passed into the navigator system/user prompts (`SCENARIO: …`) so generation matches the retrieval path.

---

## Q1 — Scenario A (assigned ticket)

**Ask:**

```
I got assigned SPARK-57337 — help me understand it
```

**Seeded key:** `SPARK-57337`

**Expect:**

- Intent: `AssignedIssue`
- Sections: Epic (business header) → your issue → siblings → open risks
- Each key as a markdown link, e.g. `[SPARK-57337](https://issues.apache.org/jira/browse/SPARK-57337)`
- Short synthesis of what the ticket is for; call out open siblings if any
- Must **not** dump full descriptions verbatim
- Must **not** invent hosts like `jira.example.com`

---

## Q2 — Scenario B (new requirement)

**Ask:**

```
I need to add ANSI mode validation so invalid string-to-number casts throw instead of returning null
```

**Expect:**

- Intent: `NewRequirement`
- “No exact ticket; related landscape…” then top 1–3 Epics/issues with browse links (often ANSI-related / SPARK-44444 neighborhood)
- Suggestion which Epic to attach a new Story under
- Must **not** dump every retrieved chunk
- Must **not** refuse with only “run Ingestion” when landscape rows are present

---

## Q3 — Scenario C (decision)

**Ask:**

```
Why did they decide to enable ANSI mode by default in Spark 4.0?
```

**Seeded key:** `SPARK-44444` (Use ANSI SQL mode by default)

**Expect:**

- Intent: `DecisionRationale`
- Quote/paraphrase decision comment(s) and/or parent issue with Epic links
- One short “rationale” summary; sources listed as links including SPARK-44444
- Must **not** invent decisions not present in context

---

## Anti-patterns (fail the demo)

- Pasting the full CONTEXT block or long Jira descriptions
- Citing keys without browse URLs / inventing `jira.example.com`
- Inventing tickets or decisions when CONTEXT is empty (should suggest Ingest instead)
- Refusing Q2/Q3 with “run Ingestion” when CONTEXT already lists related issues
