# KNOWLEDGE SECURITY

The knowledge subsystem's threat model, the fences, and the things it deliberately cannot do.

---

## 1. What is being protected

Two properties, and everything below serves one of them:

1. **Knowledge does not cross a tenant boundary.** Project A's knowledge never reaches a mission,
   agent, cache entry or console response belonging to project B.
2. **The colony cannot be talked into reading the filesystem.** An agent acting on instructions it
   read in a document must not be able to point the ingestion pipeline at `/etc`, `~/.ssh`, or
   anything outside its workspace.

The second is not hypothetical. A knowledge base is *made of documents the colony was told to read*,
and a document is untrusted input. Ingestion is the one place where content the colony ingested can
influence what the colony ingests next.

---

## 2. FORAGER has no authentication

Stated plainly because the design is built around it rather than around a hope.

FORAGER binds `127.0.0.1` and expects to be the only tenant of its own machine. Its own
documentation says to put a reverse proxy in front before exposing it. Consequences:

- **ANTHILL is the authenticated edge.** Every `/knowledge/*` route requires `read_knowledge` or
  `manage_knowledge` through the existing `RequireAuth` gate. The console never talks to FORAGER
  directly.
- **Non-loopback endpoints are refused by default.** `knowledge_forager_allow_remote` is `false`,
  and the check runs on the parsed address (so all of `127/8` and `::1` work) at configuration time
  *and again per call* — the second check catches a path that somehow carried an absolute URL past
  the first.
- **An optional bearer token** (`knowledge_forager_token`, `ConfigSecurity.Secret`) is sent when
  configured, for operators who have put a proxy in front. It is never rendered in
  `config.example.json`, never returned by any route, and never published in a module-registration
  event — `RegistrationPublishesNoSecret` asserts it.

If you run FORAGER on another host, you are choosing to put an unauthenticated knowledge base on a
network. The flag exists so that is a decision somebody made, not one a copied config made for them.

---

## 3. Tenant isolation

### The scope is not a parameter

`ITool.Run` receives arguments and nothing else, so a knowledge tool learns its scope from an
argument or from ambient state. **An argument is not an option**: tool arguments are chosen by a
model, so a `project_id` parameter would make retrieval scope a model's choice — and Rule 12 would
be enforced by the model's discretion, which is not enforcement.

Scope arrives through `KnowledgeScopeContext`, an `AsyncLocal` entered by the core at mission intake.
It only narrows. The default is `Unresolved`, which retrieves nothing.
`NoKnowledgeTool_TakesAProjectArgument` is the test that stops one being added by helpfulness.

### The map is the boundary

Console callers name an **ANTHILL** project; `knowledge_project_map` translates it to a FORAGER
project. That indirection is the containment: no request can reach a knowledge base the operator has
not deliberately mapped, whatever it puts in the query string. An unmapped project resolves to
`Unresolved` — a refusal, never a fallback to the default base.

### The upstream is not project-scoped, so we check the response

**Verified against a running FORAGER:** `GET /api/knowledge/{id}` takes no project and happily
returns another project's row with HTTP 200. So the provider compares `project_id` on the **response**
and answers `NotFound` on a mismatch. The same check guards `GET /api/jobs/{id}`.

`NotFound` rather than a denial, deliberately: confirming that an id exists in a project the caller
cannot see is itself a disclosure.

### The cache is partitioned

Every entry is keyed on `KnowledgeScope.CacheKey`, and there is no read path that does not name a
scope. A shared cache is the classic way an isolation rule gets broken by accident — one query warms
an entry, a differently-scoped query hits it, and nothing in the call path looks wrong.
Partitioning by construction is cheaper than auditing for it.

### Tenant vs general

FORAGER separates customer-identifying knowledge (`scope: tenant`) from generalized operational
learning (`scope: general`). ANTHILL honours it:

- A **global** scope may read `general` material only.
- Anything narrower may read both.
- `tenant` material never enters shared or global colony memory, and never becomes a pheromone trail
  keyed on customer-identifying content.

The rule lives in one method, `KnowledgeScope.Allows`. It was written wrong first — as an ordering
comparison on the enum, which silently admitted tenant material into a global scope because `Tenant`
is 1 and `General` is 2 and the numbers mean nothing. Any ordering-based check on that enum is a bug
waiting for a reader who assumes otherwise.

---

## 4. The ingestion fence

Two independent fences, and neither is trusted to be the only one.

### Near side — ANTHILL

Every requested path is resolved through `IWorkspacePathGuard` **before anything is sent**:

- `ResolveSafePath` canonicalizes component by component, following symlinks and junctions, and
  throws on an escape. Throwing rather than returning a bool is deliberate — a bool a caller forgets
  to check is a path traversal that succeeds.
- Containment requires a separator boundary, so an allowed root of `/srv/project` does not admit
  `/srv/project-secret`.
- `IsBlockedPath` refuses `.git`, `data`, virtualenvs and caches.

A path outside the workspace is `403 permission_denied` and no request leaves the process.

### Far side — FORAGER

`FORAGER_ALLOWED_INPUT_ROOTS` names the directories FORAGER may scan.

> **This integration fixed a real defect here.** The check read
> `if (roots.length && !roots.some(...))` — so an unset `FORAGER_ALLOWED_INPUT_ROOTS`, which is the
> shipped default, skipped containment **entirely** and any absolute path on the machine could be
> scanned through an unauthenticated API. Confirmed live: `POST /sources/directory` with `/etc`
> reached `readdir` and returned HTTP 500 from an EACCES rather than refusing.
>
> Now: no configured roots means directory import is refused with 403. Containment is checked on the
> resolved **real** path, so a symlink planted inside an allowed root cannot escape it, and an
> unreadable subdirectory is skipped and reported rather than escaping as a 500.
> `FORAGER_ALLOW_ANY_INPUT_ROOT=true` restores the old behaviour for an operator who wants it, and
> logs a warning every time it is used. `tests/integration/input-roots.test.ts` pins all of it.

Uploads are unaffected — only server-side directory scanning is fenced.

---

## 5. Mutation is gated

Retrieval is read-only. The one tool that writes anything writes a **proposal**:

```
agent proposes  ->  operator reviews  ->  FORAGER applies  ->  history retained
```

`knowledge_review` records intent into ANTHILL's approval lane and never calls FORAGER. It requires a
rationale of substance — an unexplained proposal cannot be reviewed, so accepting one would produce
approval requests an operator has no basis to decide. Its success message states that the knowledge
base has **not** changed, so a model cannot proceed as though it had.

No role has `knowledge_review` in its contract by default. Granting it is a deliberate edit.

Provenance is never destroyed: FORAGER's review model supersedes rather than deletes, keeps merge
history, and re-applies reviewer decisions on every subsequent run.

---

## 6. Role permissions

Knowledge tools are dispatchable only by roles whose contract lists them. Today that is
**`researcher` alone**, and the restraint is intentional.

The obvious next candidates are `verifier` (to check a claim against evidence) and `archivist` (to
avoid re-learning what is already known). Both were left out because `verifier` declares
`AllowedTools: S()` and `archivist` declares `AllowsModelCalls: false` — neither handler can dispatch
anything today, so granting them would declare reach that does not exist. That is the drift this
repository warns about in `AntExecution.cs`: a role whose declared surface stops matching its real
one, looking like a working feature while being inert.

`web` is excluded for a different reason: its lane is the public internet, and keeping *what the
world says* separate from *what the organization knows* is the same distinction `ToolEvidence` draws
between its retrieval and inspection lanes.

---

## 7. Logging

Structured, and deliberately thin.

**Logged:** request id, mission id, project scope, knowledge ids, source ids, duration, result count,
failure reason, FORAGER's upstream `request_id` (the field that turns a colony-side failure into a
findable line in FORAGER's log).

**Never logged:** the configured token, excerpt bodies, whole documents, or a malformed response
payload. That last one is easy to miss — the natural instinct on a parse failure is to log what came
back, and what came back can be confidential source text. The client logs the parser's message and
not the body.

---

## 8. Model independence

No provider is named anywhere in the knowledge model. FORAGER runs deterministically with no LLM
configured — verified: the live instance reports `model_provider: "none (deterministic mode)"` and
produces entity resolution, conflict detection, evidence and provenance without one. Where semantic
processing is wanted, FORAGER uses its own adapter interface and its output is schema-validated and
quote-located before anything is persisted.

Customer knowledge does not become training data, shared colony memory, or a global pheromone trail
by being retrieved. Scope is carried on the record, not inferred at the point of use.

---

## 9. Failure posture

Every failure is a typed refusal, never an invention.

| Condition | Result |
| --- | --- |
| Disabled | `Disabled` — no tools are even registered |
| Unreachable / timeout / upstream 5xx | typed, retryable, mission continues |
| Malformed response | `Malformed`, not retryable, nothing partial persisted |
| Scope unresolvable | refusal naming the config key — never a widened query |
| Path outside workspace | `AuthorizationFailure` before any network call |
| Unauthorized upstream | `Unauthorized`, not retryable without operator action |

The unavailable text ends with an instruction, and it is load-bearing rather than decorative:

> Do NOT substitute recalled, assumed, or generally-known facts for it, and do not present anything
> from this attempt as sourced.

A model told only that retrieval failed will commonly answer from training as though it had
retrieved something. That is the failure this subsystem exists to prevent, arriving through the error
path instead of the success path.

---

## 10. Not implemented, on purpose

- **Cross-project retrieval.** Not expressible in `KnowledgeScopeKind` at all, so it cannot be
  reached by a bug, a misconfiguration, or a model that asks nicely. If it is ever wanted it needs
  its own permission and its own audit lane.
- **Agent-authored knowledge.** Agents propose review actions; they do not create canonical
  knowledge.
- **Automatic promotion of learning into knowledge.** Designed, gated, not built.
- **ANTHILL reading FORAGER's database.** Not read-only, not for the console. HTTP only.
