# KNOWLEDGE API

The colony's own knowledge surface. Base path `/knowledge`, ANTHILL's standard envelope, ANTHILL's
standard auth.

**The console never talks to FORAGER directly.** ANTHILL is the authenticated edge: the browser
holds an ANTHILL session, ANTHILL holds the FORAGER credential, and the two are never the same
secret. FORAGER authenticates its own routes as of 0.6 (`Authorization: Bearer`, operator key or an
`fgr_…` integration token, loopback included) — v0.3.8.158 corrected an earlier claim here that it
had no authentication at all. That does not make a browser-to-FORAGER path acceptable: it would put
the knowledge base on the operator's network under a credential the console would then have to hold.

---

## Envelope

Every response is the standard `{success, message, data}` shape (`ApiJson`). Errors carry a code:

| Status | `error` | When |
| --- | --- | --- |
| 400 | `bad_request` | Missing query, malformed body, or a provider rejection |
| 401 | `unauthorized` | Not signed in |
| 403 | `permission_denied` | Role lacks the permission, or the path is outside the workspace |
| 404 | `not_found` | Unknown id **in this scope**, or no knowledge base mapped for the project |

An id belonging to another project answers `not_found`, never `permission_denied` — telling a caller
that an id exists somewhere they cannot see is itself a disclosure.

## Permissions

| Permission | Covers |
| --- | --- |
| `read_knowledge` | status, search, retrieve, items, evidence, entities, conflicts, sources, job reads |
| `manage_knowledge` | starting, cancelling and retrying ingestion |

Both default to granted at the permission layer; the capability gate is `knowledge_enabled`, which
ships **off**. Two gates, and the outer one is closed.

Since v0.3.8.124 that outer gate can be opened from Tools › Knowledge, which posts
`{"knowledge_enabled": true}` to `POST /settings` under `manage_settings`. It is the **only**
knowledge key the settings surface will write. The endpoint, the token, `knowledge_forager_allow_remote`
and `knowledge_project_map` stay file-only: they decide which service the colony trusts and which
knowledge a mission may read, and a console compromise must not be able to change either. The
switch only starts using what the file already says.

## Scope

Every route accepts `?project=<anthill-project-id>`, translated to a FORAGER project through
`knowledge_project_map`. **A caller cannot name a FORAGER project directly**, so no request can reach
a knowledge base the operator has not deliberately mapped. Omitting it uses
`knowledge_default_project`; an unmapped project is `not_found`, never a silent fallback.

### `POST /knowledge/project-map` — `manage_knowledge`

Binds an ANTHILL project to a FORAGER one. Added at **v0.3.8.153** to the documentation and at
**v0.3.8.148** to the API — the gap between those two numbers is the point of this section existing.

```json
{ "project": "colony-docs", "knowledge_base": "proj_7f2a" }
```

| Body | Meaning |
|---|---|
| `project` set, `knowledge_base` set | bind or rebind that project |
| `project` empty, `knowledge_base` set | set `knowledge_default_project` |
| `project` set, `knowledge_base` empty | **unbind** — the project then refuses, it does not fall back |

Persists immediately: `KnowledgeOptions` re-reads the runtime per call, so the next retrieval sees
it without a restart. The response echoes the whole `project_map` and `default_project`.

**Deliberately not a tool.** `FORAGER_SHARED_CONTRACT.md` §3 calls project mapping "an authorized
server operation", and §1 insists an agent may never choose scope — no role's `AllowedTools` names
this and none should. It widens nothing on its own: every read still goes through
`ResolveKnowledgeScope` and the provider's `RequireScope`.

The console renders it on the Knowledge page under **Knowledge bases**. The knowledge base is typed
rather than picked from a list because FORAGER publishes no project listing yet — that is **P11** in
the shared contract, producer-side, and inventing an endpoint for it consumer-side would be the
second implementation §1 forbids.

---

## Routes

### `GET /knowledge/status`

The one route that answers usefully when knowledge is off — the console must distinguish *not
configured*, *unreachable* and *working*, and a 404 collapses all three into a blank panel.

```json
{ "enabled": true, "reachable": true, "usable": true,
  "version": "0.1.0", "schema_version": 1,
  "search_backend": "sqlite-fts5",
  "model_provider": "none (deterministic mode)",
  "endpoint": "http://127.0.0.1:8790",
  "reason": null,
  "projects": ["falcon"],
  "configured_endpoint": "http://127.0.0.1:8790",
  "allow_remote": false,
  "auto_study": "suggest",
  "open_changes": 3,
  "gate_env_var": "ANTHILL_KNOWLEDGE_ENABLED",
  "gate_env_pinned": false }
```

`endpoint` is reported; the token never is.

The last four are what the console's on/off control needs to tell the truth (v0.3.8.124).
`endpoint` is the endpoint that was **probed**, and a disabled provider probes nothing — so with
knowledge off it is empty and the page could not say what it was about to point at;
`configured_endpoint` is the configured value either way. `allow_remote` is reported because
enabling knowledge against a non-loopback endpoint with it `false` fails at the client, and an
operator shown an *Enable* button should know that before pressing it. `gate_env_pinned` is true
when `gate_env_var` is set in the process environment: the runtime projects that gate as
env-over-file, so a settings write would persist and then lose to the variable. The console
withholds the control in that case and names the variable instead of shipping a button that appears
to do nothing.

`auto_study` is `off` | `suggest` | `on` (v0.3.9.3) and `open_changes` is how many findings are
waiting on the operator. Both are on **status** rather than behind the section that shows them,
because a badge belongs on the tab: a count an operator has to open a panel to discover is a count
that tells them nothing.

### `GET /knowledge/search?q=&limit=&include_historical=`

Ranked candidates, no evidence expansion. `limit` clamps to 1–50.

```json
{ "query": "launch date", "backend": "sqlite-fts5", "took_ms": 3,
  "hits": [ { "knowledge_id": "ki_68c6b4a77fc81cb5",
              "statement": "The launch date for Project Falcon is March 3, 2026.",
              "type": "fact", "support": "DirectFact", "status": "Disputed",
              "confidence": 0.9, "score": 11.649,
              "why": "Full-text match in title and statement of this fact",
              "evidence_count": 3, "contested": true } ],
  "entities": [ … ] }
```

### `POST /knowledge/retrieve`

The main path: evidence, entities and conflicts assembled into a context.

```json
{ "query": "why did the launch date change", "project": "falcon",
  "top_k": 8, "include_historical": false }
```

Returns the structured context **and** `rendered` — the exact text a model is given, verbatim. The
console shows it, which is the difference between a knowledge feature you can audit and one you have
to trust.

There is no `include_conflicts` parameter. Rule 10 says conflicts are never hidden, and an option to
hide them is a way to hide them.

A POST rather than a GET because the body carries options and a retrieval is expensive enough that
it should not be repeated by a cached re-issue.

### `GET /knowledge/items/{id}`

One item: statement, support, status, confidence, effective date, evidence ids, conflict ids,
`has_provenance`, `contested`.

### `GET /knowledge/items/{id}/evidence`

The *"why do we believe this?"* call. Each link carries `source_name`, `location`, `excerpt`,
`excerpt_hash`, `extractor` and `missing_excerpt` — the last surfaced rather than hidden, because an
evidence link whose text can no longer be found is the strongest signal a claim needs re-checking.

### `GET /knowledge/entities?name=`

Canonical entities with the aliases they resolved from — how *Bob Smith* and *Robert Smith* turn out
to be one person.

### `GET /knowledge/conflicts`

Open conflicts in scope, with both sides, the suggested resolution and whether anyone has ruled.

### `GET /knowledge/sources`

Registered sources: hash, type, size, processing status, document date, duplicate and superseded
markers.

---

## Ingestion

Asynchronous, always. No ANTHILL request ever waits on a document being parsed.

```
POST /knowledge/jobs            -> queue work, return a job id immediately
GET  /knowledge/jobs            -> recent jobs
GET  /knowledge/jobs/{id}       -> real persisted stage state
POST /knowledge/jobs/{id}/cancel
POST /knowledge/jobs/{id}/retry -> resumes from checkpoints, not from the start
```

### `POST /knowledge/jobs` — `manage_knowledge`

```json
{ "project": "falcon", "paths": ["docs/procedures"], "force": false }
```

**Every path is resolved through `IWorkspacePathGuard` before anything is sent.** Containment follows
symlinks and refuses an escape by throwing; a blocked path (`.git`, `data`, caches) is refused too.
FORAGER's own `FORAGER_ALLOWED_INPUT_ROOTS` is the second, independent fence — neither is trusted to
be the only one. A path outside the workspace is `403 permission_denied` and no request leaves.

### Job payload

```json
{ "job_id": "job_7047a24dc7358c4b", "status": "running",
  "current_stage": "semantic_extraction", "progress": 0.45, "terminal": false,
  "stages": [ { "name": "chunking", "status": "completed",
                "processed": 8, "skipped": 0, "failed": 0, "warnings": 0 } ] }
```

`progress` is derived by FORAGER from its persisted stage rows. ANTHILL never interpolates it and no
timer advances it. The eleven stages are `source_registration`, `parsing_extraction`,
`normalization`, `chunking`, `semantic_extraction`, `entity_resolution`, `dedup_conflict`,
`provenance`, `validation`, `indexing`, `export_availability`.

`status` is one of `queued`, `running`, `completed`, `failed`, `cancelled`. Cancellation stops at the
next stage boundary and keeps completed work.

---

## Knowledge changes (v0.3.9.3, A4)

What has changed in a bound knowledge base since the colony last studied it, and the two things an
operator can do about it.

```
GET  /knowledge/changes?status=open   -> findings (read_knowledge). status=all for every status
POST /knowledge/changes/scan          -> run the analysis NOW and queue nothing (read_knowledge)
POST /knowledge/changes/{id}/queue    -> turn one finding into a mission (manage_knowledge)
POST /knowledge/changes/{id}/dismiss  -> record that it needs none (manage_knowledge)
```

```json
{ "id": "b2c1…", "project_ref": "falcon", "source_id": "src_91",
  "source_name": "onboarding.md", "kind": "changed",
  "previous_hash": "ab12…", "current_hash": "cd34…",
  "status": "open", "mission_id": null, "detected_at": "2026-09-10T14:02:11Z" }
```

**A finding is not a mission.** `kind` is `new` — never studied — or `changed` — studied at a
different content hash. `status` is `open`, `queued` or `dismissed`; a decided finding stays in the
table, because the record that somebody looked is the thing that stops the next pass asking again.

The comparison is a read of the seed receipts `POST /knowledge/seed` has been writing since
v0.3.8.154: `logical_content_hash` per studied source was already there and nothing had ever asked
for it. The **watermark** is derived from the newest finding rather than stored beside the rows, so
there is no second fact that can disagree with them.

`scan` and the timer run the same pass as `POST /knowledge/seed` with queueing off — see
`knowledge_auto_study` in `docs/CONFIGURATION.md`. Queueing carries `manage_knowledge` for the same
reason seeding does: it spends model calls, and deciding the colony should go and work is an
operator action, never an agent's.

---

## Agent tools

The same capability, reached by an agent instead of a browser. Always registered — with
`knowledge_enabled` off they refuse at call time rather than being absent, so role readiness does
not depend on a feature flag — and dispatchable only by roles whose contract lists them.

| Tool | Does |
| --- | --- |
| `knowledge_search` | ranked candidates |
| `knowledge_retrieve` | evidence-backed context |
| `knowledge_get` | one item |
| `knowledge_evidence` | the sources behind an item |
| `knowledge_entity` | look a name up |
| `knowledge_review` | **propose** a review decision — applies nothing |

**No tool takes a project or scope argument.** Tool arguments are chosen by a model; a `project_id`
parameter would make retrieval scope a model's choice. Scope arrives ambiently through
`KnowledgeScopeContext`, entered by the core at mission intake, and there is no supported way for a
tool call to widen it.

---

## OpenAPI

ANTHILL does not generate OpenAPI today, so these routes are documented here, in the console's own
conventions. FORAGER does: `GET http://127.0.0.1:8790/api/openapi.json` describes the upstream
contract (OpenAPI 3.1, 35 paths, 23 schemas) if you need to see what ANTHILL is talking to.
