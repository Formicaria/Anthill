# RETRIEVAL

How the colony turns a question into evidence-backed context, and why it is not the usual thing.

---

## 1. What this is not

The default shape of retrieval-augmented generation is:

```
query -> embed -> nearest neighbours -> paste chunks into the prompt
```

What the model receives from that is text with no accountability. It cannot tell a signed decision
from a hallway rumour that happened to be written down, and its only way to judge either is
plausibility. If two documents disagree, similarity search returns both and says nothing; the model
picks whichever it read first, or averages them into a sentence that is true of neither.

ANTHILL does not do this, and the reason is not quality — it is that a colony which *acts* on what
it retrieves cannot afford a retrieval layer that launders provenance.

---

## 2. What it is instead

```
question
   |
   v
normalize                  collapse whitespace, bound length. Nothing else.
   |
   v
rank candidates            FORAGER's search backend (FTS5 today, vector later)
   |
   v
filter                     scope, confidentiality band, support floor, historical
   |
   v
fetch evidence             per candidate: the excerpts that support it
   |
   v
expand entities            who and what the statements are about
   |
   v
attach conflicts           scope-wide, then narrowed to the facts in hand
   |
   v
order + budget             deterministic sort, whole facts dropped from the tail
   |
   v
KnowledgeContext           labelled, sourced, conflict-aware
```

**Evidence comes before assembly.** An item whose evidence cannot be resolved has to be *labelled*,
not dropped — and you cannot label what you have already flattened into a prompt.

**Conflicts come before assembly** for the same reason: a contested fact must never be rendered as
settled, and the assembler needs to know it is contested before it writes it down.

---

## 3. Every statement is labelled

Four levels, carried from FORAGER, never inferred and never upgraded:

| Level | Means |
| --- | --- |
| `DIRECT FACT` | The source states it. The excerpt says the thing. |
| `SUPPORTED INFERENCE` | Inferred, and the evidence carries the inference. |
| `UNCERTAIN INFERENCE` | Inferred with a gap the evidence does not close. |
| `UNVERIFIED CLAIM` | Asserted somewhere, supported by nothing. Hedged and reported speech land here. |

A level this build does not recognise renders as `UNKNOWN SUPPORT`. That happens when FORAGER is
newer than ANTHILL, and the honest outcome is a statement a model treats carefully — not one
silently promoted to `DIRECT FACT` because that was the first enum member.

---

## 4. What the model actually sees

```
KNOWLEDGE CONTEXT
Query: "Why was the Falcon launch date changed?"
Scope: project proj_ef42d498ae1e

CONFLICTS DETECTED — the sources disagree. Do not report either side as settled.

[CONFLICT-1] attribute_mismatch on falcon|launch_date — UNRESOLVED
  2 different values were stated for "launch_date" of falcon: 2026-03-03 vs 2026-04-10
  Competing statements: FACT-1, FACT-2
  Suggested (NOT APPLIED): Only this statement comes from a dated source (2026-02-18)

FACTS

[FACT-1] The launch date for Project Falcon is March 3, 2026.
  Support: DIRECT FACT   Confidence: 0.90   Status: DISPUTED   Effective: 2026-03-03
  Evidence:
    - 08-design-review.docx (Section: Falcon Design Review Notes > Schedule)
      "The launch date for Project Falcon is March 3, 2026."

[FACT-2] The launch date for Project Falcon has moved to April 10, 2026.
  Support: DIRECT FACT   Confidence: 0.90   Status: DISPUTED
  Evidence:
    - 02-schedule-update.eml (message body)
      "The launch date has moved to April 10 because the Northwind parts slipped."

RELATED ENTITIES
  Project Falcon (project) — also: Falcon

SOURCES
  02-schedule-update.eml
  08-design-review.docx
```

Conflicts are printed **before** the facts. A model that reads the statements first has already
formed an answer; one that learns they are contested first reasons about the disagreement. The test
`TheRenderedContext_LeadsWithTheConflictAndNeverPresentsItAsSettled` asserts the ordering, not just
the presence.

---

## 5. Rules the assembler enforces

**Every fact has evidence, or says it does not.**

```
  Evidence: NONE — this statement is UNRESOLVED. Its supporting text could not be
            located. Do not rely on it without checking the source yourself.
```

`KnowledgeContext.FactsWithoutProvenance()` must return empty. A non-empty result is a defect in the
assembler, and the tests assert it stays empty for every fixture including the deliberately broken
ones.

**Conflicts are presented, never resolved.** FORAGER's suggested resolution is carried and marked
`NOT APPLIED`. Presenting `prefer ki_3943b4` to a reasoning layer invites compliance; presenting
*"only this statement comes from a dated source"* invites judgement, which is the point.

**Truncation is declared.** Whole facts are dropped from the tail — never truncated mid-statement,
because a half-quoted excerpt is a misquotation and this pipeline's entire claim is that the quotes
are real. When it happens the context says so, so absence is not read as non-existence.

**Absence is not silence.** An empty result says the base was searched and had nothing, and that
this is not permission to assume an answer. An *unavailable* base says something different again —
see §7.

**The render is deterministic.** Facts sort by support, then confidence, then id. Same knowledge
base, same query, same bytes.

---

## 6. Iterative retrieval

Agents are not required to get everything in one call, and should not try.

```
"Determine why the authentication deployment failed last month."

  knowledge_retrieve  "authentication deployment failure"   -> incident INC-1842, a procedure
  knowledge_entity    "INC-1842"                            -> the people and services involved
  knowledge_retrieve  "authentication integration test"     -> the procedure's verification step
  knowledge_evidence  ki_...                                -> the exact text of the step
                                                            -> answer, with citations
```

Each call is cheap, scoped, cached briefly, and charged against the mission's budget. The
`knowledge_search` tool exists for exactly this: a candidate list without evidence expansion, so an
agent can decide what is worth the full retrieval.

---

## 7. Retrieval strategies

The Anthill-facing API is deliberately backend-agnostic. Today FORAGER answers with:

- **FTS5** — stemming, prefix matching, bm25 ranking. The normal case.
- **LIKE fallback** — case-insensitive substring with field weighting, on a Node build whose SQLite
  lacks FTS5. Same response shape; thinner results.

Which one answered is reported in `Metadata.Backend` and shown in the console, because a thin result
set means something different under each.

**Vector search is not implemented, and that is a decision.** FORAGER's `SearchBackend` seam is
where it belongs and it does not exist there yet. When it lands, nothing in this document's API
changes — the provider, the tools, the context and the console all keep working, because ranking is
FORAGER's job and the boundary carries results rather than scores. Forcing a vector database in now
would add a dependency for retrieval quality FORAGER cannot currently deliver.

The architecture supports, at the seam rather than in this repository: BM25/FTS, vector, hybrid,
knowledge-graph expansion, entity-aware and temporal retrieval.

---

## 8. Temporal questions

Status survives the whole pipeline, so history stays answerable:

| Question | How |
| --- | --- |
| *What is the latest state?* | default retrieval — superseded and archived are excluded |
| *What was the procedure in March?* | `include_historical: true`, read `effective_date` |
| *What changed between March and August?* | historical retrieval; compare the superseding chain |
| *What did we believe before the incident?* | historical retrieval bounded by effective date |

A superseded fact renders as `Status: SUPERSEDED` with `Superseded by: FACT-n`. It was true of its
time, and saying so is different from hiding it.

---

## 9. When it cannot answer

The failure text is part of the safety design, not a message:

```
Knowledge retrieval is unavailable: the knowledge service did not respond within 5000ms.
The mission can continue without organizational knowledge, but evidence-backed context
could not be retrieved. Do NOT substitute recalled, assumed, or generally-known facts for
it, and do not present anything from this attempt as sourced.
```

Without that last sentence, a model told only that retrieval failed will commonly proceed to answer
from training as though it had retrieved something — confidently, and with no way for a reader to
tell. `KnowledgeFailure` distinguishes disabled, unavailable, timeout, unauthorized, scope-unresolved
and not-found, and each renders its own lead sentence.
