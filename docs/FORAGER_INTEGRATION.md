# FORAGER INTEGRATION

How ANTHILL consumes canonical knowledge from FORAGER, and — just as important — what each side
is forbidden to do to the other.

**Status:** design of record. Written after a full audit of both repositories at the versions
named below. Every claim about FORAGER in this document was checked against a running instance,
not against its README.

| | |
| --- | --- |
| ANTHILL | `v0.3.8.120`, .NET 9 / C# 13, branch `release/v0.3.8.120` |
| FORAGER | `0.1.0`, TypeScript / Node 22.13+, canonical schema v1, Anthill package v1 |
| Audited | 216/216 FORAGER tests passing; live instance seeded with the Falcon demo (9 sources, 66 knowledge items, 11 entities, 5 open conflicts, FTS5 backend) |

---

## 1. The division that matters

```
FORAGER   raw information  ->  structured, traceable knowledge
ANTHILL   structured knowledge  ->  reasoning  ->  action
```

FORAGER owns ingestion, canonical representation, and provenance. ANTHILL owns reasoning,
mission state, and tool permissions. Neither reaches across.

Concretely, this repository must never grow a document parser, a chunker, an entity resolver, a
conflict detector, or a second knowledge schema. Where this integration needs one of those, it
asks FORAGER.

---

## 2. What already existed (audit)

The single most useful finding of the audit is that **most of what a naive reading of the brief
would have had us build already exists on the FORAGER side.** Building it again in C# would have
been the largest possible mistake.

### 2.1 FORAGER already provides

| Capability | Where |
| --- | --- |
| Support taxonomy `direct_fact` / `supported_inference` / `uncertain_inference` / `unverified_claim` | `pipeline/extractors/`, persisted on every knowledge item |
| Evidence with source id, chunk id, location, excerpt, excerpt hash, extractor + version | `GET /api/knowledge/:id/evidence` |
| Conflict detection (`attribute_mismatch`, `contradiction`, `duplicate_source`) with suggested resolution and reason | `pipeline/stages/`, `GET /api/projects/:id/conflicts` |
| Entity resolution with aliases, merge history, and review candidates | `pipeline/resolution.ts` |
| Lifecycle states `active` / `superseded` / `disputed` / `unresolved` / `stale` / `archived` | canonical schema |
| Asynchronous ingestion, 11 persisted stages, checkpointed resume, cancel, retry | `POST /api/projects/:id/process`, `GET /api/jobs/:id` |
| Review audit trail that survives reprocessing | `POST /api/knowledge/:id/review`, `GET /api/projects/:id/review-events` |
| Search with a swappable backend (FTS5 / LIKE today, vector later) behind one response shape | `services/search-service.ts` |
| Deterministic operation with **no** model provider configured | verified: live instance reports `model_provider: "none (deterministic mode)"` |
| An **Anthill package export** with a tenant/general confidentiality split | `adapters/exports/anthill.ts` |
| OpenAPI 3.1 description, 35 paths, 23 schemas | `GET /api/openapi.json` |

So sections of the brief covering RAG classification, provenance, conflict surfacing, temporal
state and async ingestion are, on the FORAGER side, **already satisfied**. This integration's job
is to carry those properties across the boundary without degrading them — not to reimplement them.

### 2.2 ANTHILL already provides

| Capability | Where |
| --- | --- |
| Module system: modules reference `Anthill.SDK` and nothing else of ours | `IAnthillModule`, `IModuleContext`, enforced by `ModuleBoundaryTests` |
| Tool contract, registry, dispatch chokepoint, audit, pheromone reinforcement | `ITool`, `ToolRegistry.RunTool` |
| Layered authorization: role contract allowlist, capability grant, mission authority ceiling, escalation gate | `ToolAuthorization`, `MissionAuthorityGate`, `CapabilityGrant` |
| Evidence lanes that keep repo inspection separate from world retrieval | `ToolEvidence` |
| Citation gate that resolves claims against real artifacts | `CitationIntegrity`, `ArtifactSchemas.CitableRecords` |
| Mission context, budgets, project id on missions, ambient workspace scope | `MissionContext`, `MissionWorkspaceScope` |
| Path containment with symlink resolution | `IWorkspacePathGuard`, `PathContainment` |
| Config catalog with generated `config.example.json` and docs | `AnthillConfig`, `ConfigCatalog` |
| Envelope API with per-route permission gate | `ApiJson`, `RequireAuth` |
| Static console with an IA-driven nav registry | `src/Anthill.UI/` |

Again: extend these. Do not build alongside them.

---

## 3. The integration decision

### 3.1 HTTP, not embedded

**Decision: ANTHILL consumes FORAGER over its HTTP API.**

This is not a preference, it is forced by the audit. The brief left room for an embedded
integration "if the current FORAGER implementation makes direct embedding substantially cleaner".
It does not, and cannot:

- **FORAGER is TypeScript on Node; ANTHILL is .NET 9.** There is no in-process boundary to share.
  The `ProjectReference`-to-a-sibling-repository pattern this repository already uses for
  MICROMOUND works only because `Micromound.Protocol` is a .NET assembly. It has no analogue here.
- FORAGER's storage is `node:sqlite` with FTS5 virtual tables probed at runtime. Reading that file
  from .NET would mean reimplementing its schema, its resolution rules and its search ranking —
  precisely the duplication Rule 13 forbids.
- FORAGER is already designed to be a service: it binds `127.0.0.1:8790`, serves its own UI, and
  publishes an OpenAPI description.

The rejected alternative is recorded here so nobody relitigates it: *shelling out to `node` per
query* would give us process-per-request latency, no connection reuse, no readiness signal, and a
second copy of the argument-marshalling problem. It is worse than HTTP in every dimension.

### 3.2 Two providers behind one contract

The Anthill-facing contract is `IKnowledgeProvider` in `Anthill.SDK`. It has three implementations,
and the abstraction is what stops ANTHILL from coupling to FORAGER's wire format:

| Implementation | Use |
| --- | --- |
| `ForagerHttpKnowledgeProvider` | The live integration. Queries a running FORAGER. |
| `ForagerPackageKnowledgeProvider` | Reads an exported **Anthill package** (`anthill-package.json` + JSONL) from disk. For air-gapped installs, and for missions that must run against a pinned, immutable snapshot. |
| `NullKnowledgeProvider` | Registered when knowledge is disabled or unconfigured. Every call returns a typed "unavailable" result. This is what makes Rule 15 hold. |

The package provider is not a fallback for a failed HTTP call — silently answering a live query
from a stale snapshot would be a correctness bug. It is selected by configuration, deliberately.

### 3.3 Where the code lives

```
src/Anthill.SDK/Knowledge/          contracts + canonical context types   (no FORAGER concepts)
src/Anthill.Modules/Anthill.Modules.Knowledge/
                                    FORAGER client, providers, context assembly, the tools
src/Anthill.Api/Knowledge/          ApiHost.Knowledge.cs — the console's HTTP surface
src/Anthill.UI/knowledge.js         the Knowledge area
```

The module references `Anthill.SDK` and nothing else of ours, exactly as `Anthill.Modules.Tools`
does. `ModuleBoundaryTests` discovers it automatically and will fail the build if that slips.

**Why the module and not the core:** knowledge retrieval is capability, not coordination. The core
must not name FORAGER, and after this change it does not — it names `IKnowledgeProvider`.

---

## 4. Data flow

### 4.1 Retrieval

```
Mission / Agent / Chat
        |
        v
knowledge.* tool           (ToolRegistry.RunTool -> authorization, audit, evidence)
        |
        v
IKnowledgeProvider         (Anthill.SDK — no FORAGER types cross this line)
        |
        v
ForagerHttpKnowledgeProvider
        |
        +-- GET /api/projects/{p}/search          ranked candidates
        +-- GET /api/knowledge/{id}               item + entities + conflict ids
        +-- GET /api/knowledge/{id}/evidence      provenance
        +-- GET /api/projects/{p}/conflicts       conflicting statements
        |
        v
KnowledgeContextAssembler  classification, conflict attachment, budget-aware truncation
        |
        v
KnowledgeContext           deterministic, inspectable, provenance-complete
        |
        v
Anthill reasoning layer
```

The retrieval pipeline is **evidence-first**, not similarity-first. Ranking selects candidates;
evidence decides what is presented and how it is labelled. An item whose evidence cannot be
resolved is not silently dropped — it is carried with its `unresolved` status intact.

### 4.2 Ingestion

Ingestion is always asynchronous. ANTHILL never blocks an HTTP request on parsing.

```
operator / authorized mission
        |
        v
POST /api/knowledge/sources         (ANTHILL, gated on manage_knowledge)
        |
        v
workspace boundary check            IWorkspacePathGuard.ResolveSafePath + IsBlockedPath
        |
        v
POST /api/projects/{p}/sources      (FORAGER)
POST /api/projects/{p}/process      -> 202, job id
        |
        v
ANTHILL returns the job id immediately
        |
        v
GET /api/knowledge/jobs/{id}        -> proxies FORAGER's persisted stage state
```

Progress is read from FORAGER's persisted rows. ANTHILL never synthesizes a progress number.

---

## 5. Ownership boundaries

| Concern | Owner |
| --- | --- |
| Source registration, parsing, chunking | FORAGER |
| Canonical knowledge representation and its schema | FORAGER |
| Evidence and provenance | FORAGER |
| Entity resolution, conflict detection, review state | FORAGER |
| The knowledge database | **FORAGER, exclusively** |
| Retrieval ranking within a project | FORAGER |
| Support classification of a statement | FORAGER (ANTHILL maps, never invents) |
| Context assembly and presentation to a model | ANTHILL |
| Which agent may retrieve, and what it may retrieve | ANTHILL |
| Mission state, budgets, scope | ANTHILL |
| Knowledge mutation approval | ANTHILL proposes, an operator approves, FORAGER applies |
| Audit of who asked for what | Both, independently |

### 5.1 Database ownership

ANTHILL does not open FORAGER's SQLite file. Not read-only, not for reporting, not "just for the
UI". The only access path is the HTTP API.

ANTHILL's own database gains **no** knowledge tables. Retrieved knowledge is written into the
existing artifact and evidence stores as `source_set` records so `CitationIntegrity` can resolve
citations against it — that is a use of existing schema, not new schema. This keeps the migration
story trivial (see §9).

---

## 6. Authentication and the security boundary

**FORAGER has no authentication.** It binds `127.0.0.1` and expects to be the only tenant of its
own machine. Its own documentation says to put a reverse proxy in front before exposing it. This
is a real property of the system and the integration is built around it rather than pretending
otherwise:

1. **ANTHILL's endpoint is the authenticated one.** Every `/knowledge/*` route in `ApiHost`
   requires `read_knowledge` or `manage_knowledge` through the existing `RequireAuth` gate. The
   console never talks to FORAGER directly; it talks to ANTHILL, which talks to FORAGER.
2. **The configured FORAGER endpoint is SSRF-checked.** The base URL is validated at
   configuration time and re-validated per call. A non-loopback endpoint requires the operator to
   set it explicitly and is reported in readiness output, so "my knowledge base is on another
   host" is a visible decision rather than an accident.
3. **An optional bearer token** (`knowledge_forager_token`) is sent when configured, for operators
   who have put FORAGER behind a proxy. It is `ConfigSecurity.Secret` and never rendered.
4. **Ingestion paths are guarded on the ANTHILL side before they are sent.** A mission cannot ask
   FORAGER to index `/etc`, `C:\`, `~/.ssh`, or anything else outside the mission workspace,
   because the request is resolved through `IWorkspacePathGuard` first and refused on escape.
   FORAGER's own `FORAGER_ALLOWED_INPUT_ROOTS` is the second, independent fence; the integration
   documents setting it, and does not rely on it alone.

### 6.1 Scope isolation

Every call from ANTHILL to FORAGER carries a project scope. There is no unscoped query path in
the provider — the FORAGER endpoints that matter (`/search`, `/knowledge`, `/entities`,
`/conflicts`) are all project-rooted, and the provider will not construct one without a resolved
project id.

Resolution order, most specific wins:

```
Mission  ->  Project  ->  Workspace  ->  Global
```

A mission's knowledge scope is resolved once, at intake, from the mission's `ProjectId` and the
configured scope map. A mission with no resolvable scope gets `NullKnowledgeProvider` behaviour
for tenant knowledge rather than a silent widening to "everything". Leaking project A's knowledge
into project B's mission is the failure this design exists to prevent, and it is tested directly.

### 6.2 The tenant / general split

FORAGER already separates customer-identifying knowledge (`scope: tenant`) from generalized
operational learning (`scope: general`), and says so in its package manifest. ANTHILL honours it:

- `tenant` knowledge may enter mission context and mission memory.
- `tenant` knowledge is **never** written to shared or global colony memory, and never becomes a
  pheromone trail keyed on customer-identifying content.
- Promotion from mission memory into durable colony learning is an explicit, provenance-preserving
  act — never a side effect of retrieval.

---

## 7. The knowledge context ANTHILL builds

Raw FORAGER JSON does not reach the model. The provider produces a canonical, deterministic
`KnowledgeContext`:

```
KnowledgeContext
  Facts          statement, support, confidence, status, effective date, evidence ids
  Evidence       source id, source name, location, excerpt, excerpt hash, extractor
  Entities       canonical name, type, aliases
  Relationships  typed, with evidence
  Conflicts      the competing statements, both sides, and the resolution state
  Metadata       query, scope, backend, counts, elapsed, truncation, degradation
```

Rendered for a model, it reads:

```
KNOWLEDGE CONTEXT
Query: "Why was the Falcon launch date changed?"
Scope: project falcon-demo

FACTS

[FACT-1] The launch date for Project Falcon is March 3, 2026.
  Support: DIRECT FACT   Confidence: 0.90   Status: DISPUTED
  Evidence: 01-kickoff-memo.md, 05-falcon-requirements.pdf (Pages 1-2), 08-design-review.docx

[FACT-2] The launch date for Project Falcon has moved to April 10, 2026.
  Support: DIRECT FACT   Confidence: 0.90   Status: DISPUTED
  Evidence: 02-schedule-update.eml (message body)

CONFLICTS

[CONFLICT-1] attribute_mismatch on falcon|launch_date — UNRESOLVED
  FACT-1 asserts 2026-03-03; FACT-2 asserts 2026-04-10.
  Suggested: 02-schedule-update.eml is newer by document date. Not applied.

RELATED ENTITIES
  Project Falcon (project), Robert Smith (person)

SOURCES
  01-kickoff-memo.md, 02-schedule-update.eml, 05-falcon-requirements.pdf, 08-design-review.docx
```

Four properties are non-negotiable and each is tested:

1. **Support classification is carried, never inferred.** ANTHILL maps FORAGER's four levels onto
   its own enum and refuses to upgrade one.
2. **Every fact carries evidence, or an explicit unresolved marker.** There is no third state.
3. **Conflicts are presented, never resolved by the retrieval layer.** The model reasons over the
   disagreement; the RAG layer does not get a vote.
4. **Superseded and historical items stay distinguishable.** Status survives the whole pipeline,
   so "what did we believe in March" remains answerable.

---

## 8. Failure behaviour

The colony must not depend on FORAGER being up. Every failure mode degrades to a *typed,
truthful* refusal — never to invention.

| Condition | Behaviour |
| --- | --- |
| Knowledge disabled in config | `NullKnowledgeProvider`; tools are not registered at all |
| FORAGER unreachable / connection refused | `FailureClass.ExternalUnavailable`, retryable; mission continues without knowledge |
| Timeout | Typed timeout failure, retryable, budget-charged |
| FORAGER returns 5xx | Typed failure carrying FORAGER's `request_id` for correlation |
| Malformed / unparseable response | Typed validation failure; nothing partial is persisted |
| Scope unresolvable | Refusal naming the missing scope, not a global query |
| Path outside the workspace on ingest | `FailureClass.AuthorizationFailure`, refused before the call |
| Job cancelled or partially ingested | Reported with real persisted stage state |
| Permission denied by ANTHILL role contract | Standard authorization denial, before any network call |

What the model sees when knowledge is unavailable:

```
Knowledge retrieval unavailable: the knowledge service did not respond within 5000ms.
The mission can continue without organizational knowledge, but evidence-backed context
could not be retrieved. Do not substitute recalled or assumed facts for it.
```

That last sentence is load-bearing. The failure text is part of the safety design, not a message.

---

## 9. Migration strategy

**Existing installations must keep working with no FORAGER configured, and they do.**

- The knowledge feature is **off by default** (`knowledge_enabled: false`). An existing
  `anthill.json` that has never heard of knowledge loads unchanged and starts normally.
- **No schema change to ANTHILL's SQLite database.** No new tables, no new columns, no new
  migration ledger entry. An existing database is byte-compatible in both directions, so a
  downgrade is also safe. Retrieved knowledge uses the existing artifact/evidence stores.
- The module is not loaded when disabled, so the `knowledge.*` tools are never registered, never
  offered to a model, and never appear in the tool inventory projection.
- The console's Knowledge section is present but reports the feature as unconfigured, with the
  reason — the same way the Micromound console reports an absent fleet.
- Rolling back is deleting the config section.

Upgrade is tested against a database fixture created before the change.

---

## 10. Deployment

FORAGER is a separate process, so `docker-compose.yml` gains an optional service. This is the case
where a second container is genuinely warranted — different runtime, different language, its own
storage — rather than an architectural preference.

```yaml
services:
  anthill:
    # unchanged
  forager:
    profiles: ["knowledge"]     # docker compose --profile knowledge up
    # 127.0.0.1-bound; reachable from anthill on the compose network only
```

The profile keeps the default `docker compose up` byte-identical to what it is today. Windows,
Linux, LXC/systemd and bare-metal installs continue to work; FORAGER is optional on all of them
and ships its own Windows package.

---

## 11. Architectural rules, mapped to enforcement

| Rule | Where it is enforced |
| --- | --- |
| 1-3. FORAGER owns ingestion, representation, provenance | No parser/chunker/schema in this repo; `ModuleBoundaryTests` keeps the module thin |
| 4-6. ANTHILL owns reasoning, mission state, permissions | `ToolAuthorization`, `MissionContext` unchanged by this work |
| 7. Retrieval is read-only by default | Read tools declare no mutating capability; mutation tools are separate names |
| 8. Mutation is explicitly gated | `manage_knowledge` permission + operator approval + FORAGER review endpoints |
| 9. Every fact has evidence or an explicit unresolved state | `KnowledgeContextAssembler`; tested |
| 10. Never hide conflicts | Conflicts are a required field of `KnowledgeContext`; tested |
| 11. Never fabricate | Typed failures; the unavailable message forbids substitution |
| 12. Never leak across projects | Scope required on every provider call; tested |
| 13. Do not duplicate FORAGER | HTTP boundary; no second parser, chunker, or vector store |
| 14. Cloud AI never mandatory | FORAGER runs deterministically with no model; ANTHILL uses its own provider abstraction |
| 15. Do not break a colony without FORAGER | Off by default; no schema change; `NullKnowledgeProvider` |

---

## 12. Deliberately not done

Recorded so these read as decisions rather than oversights.

- **No vector search in the first implementation.** FORAGER's `SearchBackend` seam is the correct
  place for it and does not exist yet there. The Anthill-facing API is unchanged when it lands, so
  adding it later is a FORAGER change plus a configuration flag. Forcing a vector database in now
  would add a dependency for no retrieval quality FORAGER can currently deliver.
- **No write-through knowledge creation from ANTHILL.** Agents may propose review actions; they
  may not author knowledge items. Promotion of a learned lesson into canonical knowledge is
  designed but gated behind operator approval.
- **No cross-project retrieval, at all.** Not even for an operator. If that is ever wanted it
  needs its own permission and its own audit lane.

---

## 13. Naming

`Forager` is already a role name in this repository — `MicromoundRoster.Forager` is the
"Forager Ant", responsible for requested physical action, with `--role-forager` in the console
theme. To avoid a collision that would confuse both the code and the UI:

- The **subsystem** is called *Knowledge* everywhere in ANTHILL: `knowledge.*` tools,
  `/knowledge/*` routes, the Knowledge console area, `knowledge_*` configuration.
- **FORAGER** is named only where it is literally the product being talked to: the client class,
  the provider implementation, the configuration key for its endpoint, and this document.

An operator reads "Knowledge". An engineer reading the module sees FORAGER.
