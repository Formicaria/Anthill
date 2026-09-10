# KNOWLEDGE ARCHITECTURE

How organizational knowledge reaches the colony's reasoning layer, and which type owns which
decision. The *why* of the boundary is in [`FORAGER_INTEGRATION.md`](FORAGER_INTEGRATION.md); this
is the *what*.

---

## 1. Layers

```
Mission / Agent / Chat / Console
        |
        |  knowledge_* tools          |  /knowledge/* routes
        v                             v
  Anthill.SDK.Knowledge — IKnowledgeProvider, KnowledgeContext, KnowledgeScope
        |
        v
  Anthill.Modules.Knowledge — ForagerKnowledgeProvider
        |
        v
  ForagerClient (HTTP)
        |
        v
  FORAGER :8790 — canonical knowledge, evidence, entities, conflicts
```

Nothing above the SDK line knows FORAGER exists. Nothing below it knows what a mission is.

| Project | Holds | May reference |
| --- | --- | --- |
| `Anthill.SDK/Knowledge` | contracts, vocabulary, canonical context, ambient scope | nothing of ours |
| `Anthill.Modules.Knowledge` | FORAGER client, provider, mapping, tools | `Anthill.SDK` only |
| `Anthill.Api/Knowledge` | the console's HTTP surface | core + module (composition root) |
| `Anthill.Core` | tool inventory, authorization, evidence lane | `Anthill.SDK` only |

`ModuleBoundaryTests` discovers the module from disk and fails the build if it grows a reference to
`Anthill.Core` or to another module.

**The SDK may not reference `System.Net.Http`** — `ModuleBoundaryTests` forbids it, because
everything references the SDK and its dependencies are inherited colony-wide. That is why the
contracts carry no HTTP concepts and the module carries all of them.

---

## 2. The types

### `KnowledgeScope`

Which knowledge a caller may read. Resolved once, passed explicitly, and required by **every**
provider method — there is no unscoped call to write.

```
Mission -> Project -> Workspace -> Global      (most specific wins)
```

`KnowledgeScope.Unresolved` is a refusal, not a wildcard: it retrieves nothing. A mission whose
project is not in `knowledge_project_map` gets exactly that, rather than a default scope.

`Allows(band)` is the confidentiality rule, and it lives in one method on purpose — the first draft
compared enum values, which silently admitted tenant material into a global scope because `Tenant`
is 1 and `General` is 2 and the numbers mean nothing.

### `KnowledgeContext`

What the reasoning layer receives. Deterministic and inspectable.

```
Facts          statement, support, confidence, status, effective date, evidence ids
Evidence       source id, name, location, excerpt, excerpt hash, extractor
Entities       canonical name, type, aliases
Relationships  typed, with evidence
Conflicts      both sides, and whether anyone has ruled
Metadata       query, scope, backend, counts, elapsed, truncation, degradation
```

`Render()` produces the plain-text block a model is given. Plain text rather than JSON because a
labelled prose block with explicit support levels is read more reliably by every model class,
including the small local models this project targets — which is the case that decides it.

Two runtime invariants, both tested:

- `FactsWithoutProvenance()` must be empty. A fact carries evidence, or `Status == Unresolved`.
  There is no third state.
- `Render()` is byte-stable for the same inputs.

### `KnowledgeScopeContext`

The ambient scope, `AsyncLocal`, mirroring `MissionWorkspaceScope`.

`ITool.Run` receives arguments and nothing else, so a tool learns its scope from an argument or from
ambient state. **An argument is not an option:** tool arguments are chosen by a model, so a
`project_id` parameter would make retrieval scope a model's choice and Rule 12 a matter of its
discretion. `NoKnowledgeTool_TakesAProjectArgument` is the test that keeps it that way.

It only ever narrows. The default, when nobody has entered a scope, is `Unresolved`.

---

## 3. What each side owns

| Decision | Owner |
| --- | --- |
| Is this a fact, an inference, or a claim? | FORAGER |
| What text supports it, and where is that text? | FORAGER |
| Do two statements conflict? | FORAGER |
| Are these two names the same person? | FORAGER |
| Which knowledge may this caller see? | ANTHILL |
| What does the model get told, and how is it labelled? | ANTHILL |
| May this agent retrieve at all? | ANTHILL |
| Should this knowledge change? | An operator |

ANTHILL **maps** FORAGER's classifications and never upgrades one. An unrecognised support level
becomes `Unknown` and renders as `UNKNOWN SUPPORT`, not as a fact.

---

## 3b. The console (v0.3.8.157)

One card carries the whole ordinary path: the FORAGER connection with its on/off switch, then a row
saying which knowledge base the selected project reads, with **Import**, **Study** and **Unbind**
beside it — or the field to bind one when it reads nothing. Sources, conflicts, review proposals,
import jobs and the full binding table are folded below it.

The reasoning the page used to carry in prose lives here instead:

- **A project with no binding refuses rather than guessing.** A mission never falls back to the
  default knowledge base; the default exists for a console operator with no project selected. A
  mission reading a knowledge base that is not its own is the single failure the mapping prevents.
- **The knowledge base is typed, not chosen from a list.** FORAGER publishes no way to enumerate its
  projects — P11 in `docs/FORAGER_SHARED_CONTRACT.md` — and inventing an endpoint here would be a
  second implementation of the same rule. When P11 lands the field becomes a select.
- **Study is one pass, not a subscription.** It runs the colony over documents that base has not
  studied at their current version, up to 25 per click. A schedule is `knowledge_auto_study`, off by
  default, in the config file — an automation decision belongs where a decision is made, not behind a
  click that does not look like one.
- **Accepting a review is not applying it.** FORAGER publishes no endpoint for applying a review
  (P13), so acceptance records that the operator agreed and changes nothing in the knowledge base.
- **Import paths are fenced twice.** `POST /knowledge/jobs` resolves every path through the colony's
  workspace guard before anything is sent, and FORAGER has its own allowed-roots fence on the far
  side. Neither is trusted to be the only one.
- **The credential lives here.** FORAGER 0.6 authenticates every route that carries knowledge, so
  the colony needs an integration token (`fgr_…`, made on FORAGER's Settings page with scopes
  `read` and `ingest`, plus `review` if the colony should raise review proposals). The Knowledge page
  writes it and can never read it back. A colony with no token reports **signed out** rather than
  connected — v0.3.8.158, after a probe that read only the producer's PUBLIC `/ready` route reported
  a healthy connection to a colony whose every retrieval was refused.
- **The remote permission is not togglable from the console.** `knowledge_forager_allow_remote` is a
  file decision. The far end can prove who it is now, so the old argument for that has gone; the
  remaining one has not — a console that could send the colony's questions to another host is the
  thing this switch exists to prevent.

## 4. Configuration

Three keys are console-writable — the on/off switch, the study schedule, and the endpoint **as a loopback address only**. Everything else is `FileOnly`. `knowledge_forager_token` is a credential and
`knowledge_forager_allow_remote` widens who the colony may talk to; neither is reachable from a
compromised console. `knowledge_enabled` only
decides whether the colony uses what the file already configured. `knowledge_auto_study` decides whether
this colony studies knowledge it has already been given, at an Observe ceiling — a labelled switch is the
deliberate choice its file-only argument was protecting. The endpoint is guarded by VALUE rather than by
exposure: `AnthillRuntime.RefusedSettingWrite` accepts only a loopback address from the console, so moving
FORAGER to another port here is a click and pointing the colony at a host across the network is still a file
edit.

Full table in [`CONFIGURATION.md`](CONFIGURATION.md). The load-bearing ones:

| Key | Default | Meaning |
| --- | --- | --- |
| `knowledge_enabled` | `false` | Master switch. Off means every knowledge tool refuses at call time (they stay registered, so role readiness does not depend on the flag). **The one key here the console can write** — Tools › Knowledge toggles it (v0.3.8.124). |
| `knowledge_forager_endpoint` | `http://127.0.0.1:8790` | Where FORAGER is. File-only. |
| `knowledge_forager_allow_remote` | `false` | Permit a non-loopback endpoint. File-only, deliberately: FORAGER has no auth of its own. |
| `knowledge_project_map` | `{}` | ANTHILL project id → FORAGER project id. **The scope boundary.** File-only. |
| `knowledge_default_project` | `""` | For callers with no project. Never a mission fallback. File-only. |

The line between those two groups is the one that matters: the switch decides whether the colony
**uses** what the file configured, and the rest decide **who it trusts** and **what a mission may
read**. Only the first is safe to hand a browser.

Settings are read **live, per call** through a delegate — never captured — so disabling knowledge
takes effect on the next request rather than the next restart.

---

## 5. Database

**ANTHILL's schema is unchanged.** No tables, no columns, no migration ledger entry.

FORAGER owns the knowledge database and ANTHILL never opens it — not read-only, not for the console.
The only access path is the HTTP API. Retrieved knowledge that needs to persist on the ANTHILL side
uses the existing artifact and evidence stores.

The consequence worth stating: enabling this feature and disabling it are both safe, and an existing
database stays compatible in both directions.

---

## 6. Evidence lane

Knowledge reads record `EvidenceKinds.SourceRetrieval`, alongside `web_search` — not
`Inspection`.

This looks wrong at first, since knowledge is the operator's own material and the inspection lane is
where reads of their own things live. But the distinction the lanes draw is what the row *licenses*.
`AssessmentObjective` requires inspection rows before an audit's conclusions are believed, and the
point of that requirement is that the colony **looked at the tree**. A knowledge query reads a
curated statement about the tree, possibly extracted months ago — so admitting it to the inspection
lane would let an audit of what is in the repository be satisfied without reading the repository.

`knowledge_review` records nothing: it proposes, and a proposal is not evidence of anything except
that an agent had an opinion.

---

## 7. Caching

Short TTL (default 30s), keyed on `KnowledgeScope.CacheKey`, and there is no read path that does not
name a scope. It exists to stop a console poll and an agent's iterative retrieval from asking the
same question repeatedly — not to be a read model. Ingestion and review invalidate the scope's
entries; another project's cache is untouched, because it is not stale.

---

## 8. Relationship to memory and learning

Three stores, deliberately not one:

```
FORAGER            what the organization knows          durable, sourced, external
Anthill memory     what a mission did and found         operational
Anthill learning   what execution taught the colony     pheromones
```

They may reference each other. They do not merge.

```
FORAGER:   "The documented deployment procedure requires X."
Memory:    "In mission 182 the agent found X failed because Y."
Learning:  "Future deployments should verify Y before executing X."
```

A lesson may one day be promoted into canonical knowledge. That promotion is explicit,
provenance-preserving, and operator-approved — never a side effect of retrieval.

`scope: tenant` material never reaches shared or global colony memory, and never becomes a pheromone
trail keyed on customer-identifying content.
