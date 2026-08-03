# ANTHILL V3 ROADMAP

## Colony Execution Infrastructure

**Baseline:** v2.26.0
**Roadmap range:** v3.0.0 through v3.9.0
**Latest:** v3.7.0 — conversation orchestration: one surface that starts as chat and ESCALATES into autonomous execution, with the escalation explicit and recorded. The operator chooses the approval model (ask, auto-approve, bypass) and that choice is itself the recorded decision; an unattributed standing permission fails closed. Preceded by v3.6.0 — repository indexing: an agent answers 'where is this handled' from a revision-keyed index by CALLING a tool, rather than having the repository stuffed into its context. Symbol and reference answers state what they cannot claim — pattern matching is not a compiler, and a name declared in three places yields mentions that cannot be attributed to any of them. Preceded by v3.5.0 — mission workspaces: a code mission works in a detached git worktree it cannot escape, its changes are attributable to one workspace and one base revision, and its work reaches the operator as an ordinary reviewable change set. Verification commands come from a detected capability manifest rather than model invention. Preceded by v3.4.2 — contracts declare what they need from a MODEL, checked against each role's live route at startup, because every such mismatch fails silently at runtime and reads as a weak model rather than a misconfiguration. Preceded by v3.4.1 (operator-defined tools, no rebuild required), v3.4.0 (the tool-calling loop and typed tool results) and v3.3.0 (the typed provider substrate). v3.5.0 — mission workspaces — is in progress on main and not yet released.

**Previously:** v3.2.1 — dashboard direct manipulation: widgets are dragged to position and resized from their corner, with widths stored as a proportion of the row so an arrangement means the same thing at every window size. Preceded by v3.2.0 (dashboard redesign + typed model results) — see the release/phase note below. Phase release: v3.1.0 — runtime composition and Queen decomposition: configuration captured once per run, a mission's governing facts resolved once at intake, and the Queen reduced from 1,365 to 381 lines behind six service interfaces. No new features by design.
**V4 target:** Codex/Claude-Code-style autonomous software workflow on ANTHILL's bounded colony framework
**Status:** Canonical (adopted at the V2 closeout; V2 documents archived at `docs/archive/v2/`)

---

## Roadmap Rules

- The V2 roadmap and North Star are historical records at `docs/archive/v2/`; this document is canonical.
- V3 phases are sequential. A later phase may prototype early, but it may not be declared complete before all earlier gates pass.
- No release closes on unit tests alone. Each release requires a production call site, an end-to-end path, fault coverage, operator visibility, upgrade coverage, and rollback notes.
- Every phase must preserve the current safety rule: only `completed_verified` is positive.
- New ants are prohibited during V3 unless a missing responsibility cannot be expressed by the existing roster and an architecture decision record proves it.
- Homelab feature expansion is paused except for maintenance required to preserve compatibility or provide deterministic context to the software workflow.

## v3.0.0 - Baseline Lock and Canonical Architecture

### Goal

Close V2 cleanly and create a measured baseline before changing the runtime architecture.

### Required Work

- Archive the old North Star and roadmap as V2 historical documents.
- Adopt the V3 North Star and this roadmap as the only release-order authority.
- Build a generated runtime inventory: roles, handlers, contracts, tools, capabilities, feature gates, endpoints, database tables, background loops, and production call sites.
- Create a call-site audit that fails CI when a declared runtime feature has no production consumer.
- Reconcile stale docs, duplicate issues, version references, and obsolete deferred notes.
- Remove known hygiene residue, including duplicate deadline setup and Python-era source allowlist entries.
- Add characterization tests for current mission, patch, autonomy, learning, and action behavior before refactoring.
- Define architecture decision records for runtime decomposition, mission context, worker protocol, artifact store, and workspace manager.

### Exit Gates

- One canonical V3 document set.
- Zero known declaration-without-call-site defects in the generated inventory.
- Current behavior captured by characterization tests.
- Clean upgrade from the latest V2 database.
- CI green on Linux, Windows, publish/self-test, Docker, UI, and LXC checks.
- No new feature behavior in this release.

### Exit Gate Record — SHIPPED v3.0.0

| Gate | Result |
|---|---|
| One canonical V3 document set | **PASS** — `docs/NORTH_STAR.md` + `docs/ROADMAP.md` canonical; nine V2 planning docs archived at `docs/archive/v2/` with a closing-release map. |
| Zero declaration-without-call-site defects | **PASS** — 300 declarations inventoried (25 roles, 54 gates, 166 endpoints, 48 tables, 7 loops); one orphan found (`cors_enabled`) and removed; `CallSiteAudit` gates CI in both directions; exemption list empty. |
| Behavior captured by characterization tests | **PASS** — `CharacterizationTests` pins the mission-outcome truth table, verdict vocabulary, three-way skill-outcome split, pheromone signal categories, constraint parsing, action-state mapping, ant status mapping. |
| Clean upgrade from the latest V2 database | **PASS (inferred)** — no schema change in this release; schema 16 loads unchanged. Verified against fresh databases only; **not yet exercised against a populated production database.** Recorded as an inference, not a measurement. |
| CI green across all checks | **PASS** — full `validate.ps1` green. |
| No new feature behavior | **PASS** — the only behavioural deltas are removals: the dead `cors_enabled` gate, a duplicate deadline call, and a Python-era source-authority default. `GET /runtime/inventory` exposes existing state; it adds no capability. |

## v3.1.0 - Runtime Composition and Queen Decomposition

### Goal

Replace global coupling with explicit runtime composition while preserving behavior.

### Required Work

- Introduce immutable `RuntimeOptions` and a per-run `RuntimeProfile`.
- Introduce an immutable `MissionContext` containing mission constraints, workspace identity, capability grants, deadlines, budgets, environment fingerprint, and correlation IDs.
- Create a composition root for dependencies instead of reading mutable static fields throughout the codebase.
- Split Queen responsibilities behind interfaces:
  - `IMissionCoordinator`
  - `IPlanningService`
  - `IExecutionService`
  - `IMissionEvaluator`
  - `ILearningRecorder`
  - `IResultAssembler`
- Remove API-host static ownership of runtime services where practical; expose a host-scoped runtime container.
- Make test fixtures create isolated runtime instances without saving and restoring global flags.
- Preserve one final mission authority: decomposition must not create competing lifecycle owners.

### Exit Gates

- Two runtime instances can execute tests in the same process without configuration leakage.
- Queen no longer directly implements planning, execution, learning, and result formatting details.
- Existing mission behavior and persisted outcomes remain compatible.
- Restart, cancellation, and STOP tests remain green.
- No phase feature is activated yet.

### Exit Gate Record — SHIPPED v3.1.0

| Gate | Result |
|---|---|
| Two runtime instances execute in the same process without configuration leakage | **PASS** — `RuntimeIsolationTests`: two hosts with different capability configuration alive simultaneously, built in both orders, each owning its own colony; a host composed from explicit options is unaffected by the global being flipped after construction. The `Queen` takes a `RuntimeProfile` rather than reading gates during construction, which is what made this expressible. |
| Queen no longer implements planning, execution, learning and result formatting | **PASS** — six interfaces (`IPlanningService`, `IExecutionService`, `IMissionEvaluator`, `ILearningRecorder`, `IResultAssembler`, `IMissionCoordinator`). `Queen.cs` 1,365 → 381 lines. Each service takes constructor dependencies; none reads a mutable gate. |
| Existing mission behaviour and persisted outcomes remain compatible | **PASS** — schema 16 unchanged; the v3.0.0 characterization tests pass unmodified across the whole refactor, which is the definition of done ADR-001 set. |
| Restart, cancellation and STOP tests remain green | **PASS** — full suite 1,293 → 1,299 tests green in Release, plus `--selftest` 15/15 against a self-contained publish. |
| No phase feature is activated | **PASS** — no new gate, no new capability. The only behavioural deltas are two defect fixes (the plan preview's missing authorization gate; the evaluator's static read) and one additive API field pair. |
| `[Collection]` serialisation attributes become removable | **PARTIAL — recorded as such rather than claimed.** The *mechanism* that required them is gone: a host composed from explicit options is immune to the globals, proven by `RuntimeIsolationTests`. But the attributes are still in place, because many tests deliberately exercise the static gates themselves (`HandoffIngestion_IsOffByDefault` and similar) and the assembly-wide `DisableTestParallelization` ban sits on top of them. Removing the attributes while that ban stands would prove nothing; removing the ban is the real test and is its own piece of work. **Not claimed as passing.** |

**Honest scope note.** `ApiHost.Queen` remains a public static. It is now a projection of a
`RuntimeHost` that can be instantiated more than once, rather than the only way a Queen comes into
existence — but roughly 160 endpoint closures still read the static, and rewriting them is churn
without benefit. ADR-001 said "remove API-host static ownership *where practical*"; this is where
that clause was spent, and it is recorded rather than glossed.

### Phase Progress — DELIVERED

The phase is being delivered in increments so each lands behaviour-preserving and independently
reviewable, rather than as one 1,300-line mechanical rewrite of the highest-risk surface in the
project. A phase is complete when its exit gates are recorded, not when its first commit merges.

| Increment | Scope | Status |
|---|---|---|
| 1. Immutable configuration + mission context | `RuntimeOptions`, `RuntimeProfile`, `MissionContext`, wired through the Queen's mission engine | **Landed** |
| 2. Constraint resolution beyond the engine | Planner, `MissionEvaluator`, `ObjectiveVerification`, plan preview | **Landed** |
| 3a. Queen decomposition — planning | `IPlanningService` | **Landed** |
| 3b. Queen decomposition — learning | `ILearningRecorder` | **Landed** |
| 3c. Queen decomposition — results | `IResultAssembler` | **Landed** |
| 3d. Queen decomposition — execution | `IExecutionService` | **Landed** |
| 4. Composition root | `RuntimeHost`, `IMissionCoordinator`, `IMissionEvaluator`, Queen composed from a profile | **Landed** |
| 5. Isolation proof | `RuntimeIsolationTests` — two hosts, one process, no leakage | **Landed** (attribute removal deferred; see gate record) |

**Increment 1 — what landed.**

- `RuntimeOptions` (immutable, `Capture()`d once per run) and `RuntimeProfile` (per-run executable
  roles, tool grants, write permissions, verification policy), the latter validated at construction
  by the v2.26.0 `RuntimeConfigValidator` — findings are *carried*, not thrown, because the
  validator's contract is to degrade loudly and never refuse boot.
- `MissionContext`: mission id, correlation id, goal, constraints, capability grants, an **absolute
  UTC deadline**, budgets, and environment fingerprint. Constructed once at intake and passed
  explicitly. Persisted to the event log as `mission_context_resolved`, so an operator can read a
  mission's boundaries without inferring them.
- The Queen's mission engine consumes the context at all four sites that previously re-parsed the
  goal (planning admission, per-task runtime resolution, handoff admission, adaptive admission) and
  at every mission-path feature gate. `MissionConstraints.Parse` now appears **zero** times in
  `Queen.cs` and exactly once in `MissionContext.Create`, guarded by a test.
- The deadline moved from a duration re-measured in two dispatch loops to one absolute instant both
  loops compare against.

**Increment 1 — what did not change.** No behaviour, by construction. Construction-time reads
(building the ant roster, the tool registry, the model router) still read the live runtime; those
move with the composition root in increment 4.

**Increment 2 — what landed.** The resolved constraints reach the rest of the mission path, and the
canonical evaluator becomes a pure function:

- `Planner.CreateTasks` takes the constraints instead of parsing the goal. The planner now reaches
  its "no coder tasks for a read-only mission" conclusion from the same object the admission gate
  and the evaluator use.
- `MissionEvaluator.Evaluate` takes the constraints AND the run's verification policy.
  `AnthillRuntime.` no longer appears in `MissionEvaluation.cs` at all — the one authority on
  mission success is now reproducible from its arguments rather than dependent on what a mutable
  static said at the instant finalization ran. Guarded by a test.
- `ObjectiveVerification.IsSatisfied` / `.Explain` take the constraints.
- `Queen.PlanPreview` resolves a context over a transient, never-persisted mission, so the preview
  answers from the same reading of the goal a real dispatch would use.

`MissionConstraints.Parse` is down from eight sites to three in `src/`, each deliberate and each
documented in `RuntimeCompositionTests.TheMissionEngine_ParsesConstraintsExactlyOnce`: `CoderAnt`
(waits for v3.2.0's ant-contract redesign rather than designing that contract twice),
`ObjectiveLifecycle` (parses an objective charter, a different input), and the plan-preview API
response (creates no mission, governs nothing).

**Increment 3a — what landed.** `IPlanningService` / `PlanningService`: goal → admitted task graph.

This extraction is not cosmetic, and the reason is worth recording. Planning was written **twice** —
once in `RunMission` and once in `PlanPreview` — and the copies had already diverged: the preview
never ran `AntRegistry.ValidateTask`, so it could show an operator a task that dispatch would refuse
on sight. Both surfaces now call one construction, and the interface offers no way to ask for a plan
with admission skipped — that capability existed and was the bug.

The plan-preview endpoint was making the same mistake one layer out: it re-parsed the goal for
constraints and re-ran `AntRegistry.ValidateTask` over the tasks it had just received, rebuilding
warnings the planning path had already computed. Two readings of one plan, free to disagree.
`PlanPreview` now returns a `MissionPlan` carrying its own tasks, constraints, and refusals, and the
endpoint reports what the plan says. The response gains `blocked` / `blocked_reason` per step
(additive), and the console marks a refused step **REFUSED** with its reason instead of rendering it
as an ordinary step that fails the moment the operator approves it.

The service takes its dependencies as constructor parameters (`Planner`, `SqliteMemory`,
`ToolRegistry`, and a `Func<SkillRegistry>` so the Queen keeps ownership of the single hydrated
registry). It reads no mutable static. `Queen.PlanPreview` is down from 22 lines to 3.

**Increment 3b — what landed.** `ILearningRecorder` / `LearningRecorder`: everything a finished
mission teaches the colony — pheromone scoring, trail reinforcement, skill credit, and procedural
route registration.

These four were interleaved with result composition and event logging inside `FinalizeMission`, so
"what does this mission change about future missions" had no single place to be read or reviewed.
The safety rule the whole surface obeys is now stated once, on the interface: only
`completed_verified` is positive, and that fact is consumed from the one canonical
`MissionEvaluation`, never re-derived. The Queen still decides *when* learning happens — after every
task is terminal, after the evaluation exists, before completion is published.

`Queen.cs` is down from 1,365 lines to 1,237 across increments 3a and 3b, and the three call-site
guards that pinned these behaviours now check both halves: that the Queen still invokes learning
from the canonical evaluation, and that the recorder is what performs each step. Either half alone
is the defect those guards were written for.

**Increment 3c — what landed.** `IResultAssembler`: the three parallel accounts a finished mission
carries of itself — `UserResult` (raw best-task output, never rewritten), `DebugResult` (full trace,
never truncated in storage), and `FinalResult` (the plain-English answer, the only one a model
touches). Keeping those straight is the whole job, and the governing rule — synthesis is a
presentation nicety that must never leave a finished mission answerless — is now stated once, on the
interface, above the six fallback paths that enforce it.

`ShouldSynthesizeAnswer` takes the feature gate as a parameter instead of reading the static. Its
two tests previously had to save, mutate and restore `AnthillRuntime.EnableAnswerSynthesis` around
a single assertion — the exact global-state dance ADR-001 exists to remove — and are now plain
calls. A third test was added that was not previously *expressible*: two synthesis decisions taken
at once, with different settings, deciding independently.

`Queen.Views.cs` is down 142 lines to 704.

**Increment 3d — what landed.** `IExecutionService`: driving the task graph. Both dispatch loops,
`RunSingleTask`, the timeout sweep, the bounded drain, patch-proposal processing, and handoff /
adaptive admission — together with the single `_executionLock` that serialises them.

They moved as one piece because they already *were* one piece: a check confirmed the region
referenced no Queen-only member. Every rule in it is about ordering, and ordering rules that live in
different places stop agreeing. The invariants are now stated on the interface, each of which was a
real defect first: no result applied twice or late, no running task in a terminal mission, every
mid-run task through one admission path, evidence persisted before the status decision.

The extraction was performed as an exact scripted cut — the moved code is byte-identical apart from
`Memory.` → `_memory.`. On a concurrency surface, transcription error is the failure mode tests
cannot be relied on to catch, so the opportunity for it was removed rather than managed.

**`Queen.cs`: 1,365 → 377 lines.** What remains is what ADR-001 says should: construction,
`RunMission`'s lifecycle, `FinalizeMission`, and the plan preview. The Queen decides that a mission
runs and alone finalises one; it no longer implements planning, execution, learning, or result
composition. One final mission authority, as the ADR required — decomposition produced no second
lifecycle owner.

The widened guard immediately earned itself: it found `AnthillRuntime.EnableParallelExecution` in
`Queen.Views.cs`. That read is *correct* — a configuration status page must report what is
configured now, not what a past mission resolved. The guard was narrowed back to the mission path,
but the exemption is bounded rather than open-ended: the live read may appear exactly once, and the
mission-path gates must appear zero times there.

## v3.2.0 - Universal Ant and Model Protocol

### RELEASE v3.2.0 vs PHASE v3.2.0 — they are not the same thing

The release tagged v3.2.0 contains the dashboard redesign, the Mission Composer reachability fix,
the release-guard fix, and **increments 1–2 of this phase** (typed provider results; every model
call site branching on status rather than an `ERROR:` prefix). It does NOT complete the phase.

Still open before v3.2.0 can be recorded as SHIPPED against the gates below:

- ~~Deletion of the legacy string-return adapters.~~ **Done (increment 3).** `BaseAnt.Execute` is
  abstract, `string Run(Task, Mission)` and all twelve overrides are deleted, and the colony's last
  `StartsWith("ERROR:")` test went with them. It could be deleted rather than rewritten because
  every ant already overrode `Execute`, so the fallback carrying that test had no call site — the
  "no call site, no feature" rule applied to the file that had been exempting itself from it.
  `ModelUnavailable()` was deleted in the same pass for the same reason: with the adapter gone it
  had no caller, and each ant now decides at its own call site what a dead provider means for its
  work, which a shared helper could not express.
- `IAntExecutor.ExecuteAsync(AntExecutionRequest, CancellationToken)` across all twelve agents.
  Note that `Execute(Task, Mission) -> AntExecutionResult` now IS the universal typed contract; what
  remains is the async signature, the request object, and cancellation.

  **Do the provider layer first.** Cancellation is already delivered by `ModelCallScope` — an
  ambient async-local token linked into every HTTP request, built expressly so a token need not be
  threaded through every ant signature. Adding `ExecuteAsync` now would mean a second cancellation
  mechanism competing with the first, plus a sync-to-async adapter over twelve synchronous ants:
  the adapter pattern this phase just finished deleting. The genuine async work is
  `ModelRouter`/`ProviderClients`, which block a thread per model call via
  `.GetAwaiter().GetResult()` — that is a real cost under parallel execution and a prerequisite for
  an `ExecuteAsync` that is not a lie.

- Versioned `AntExecutionContract` on every active and standby mission agent.

  **This is the phase's real remaining work, and it is not a small edit.** `AntExecutionCatalog`
  holds contracts for the six SPECIALISTS only, so `ContractFor("coder")` is null. Consequences
  already visible in the code: the v3.2.0 dispatch check is a no-op for the six core ants, and
  every core-ant row in `task_results` stores `contract_version = NULL`.

  Two hazards make authoring them evidence work rather than typing, both discovered by reading the
  consumers rather than the contract type:

  1. **`AllowedTools` is an exhaustive allowlist that REPLACES the fallback.**
     `ToolAuthorization.Decide` short-circuits the moment `ContractFor(role)` is non-null: it then
     denies any tool not in `contract.AllowedTools` and never consults the `RoleAllowedTools` map
     that governs core ants today. Giving a core ant a contract with an incomplete tool list denies
     its tools mid-mission. The list must be derived from every call site, not from the four
     `RunTool(...)` literals in `Ants.cs` — the sandboxed coder runner and the verification tools
     reach the registry by other paths.
  2. **`SupportedTaskTypes` is load-bearing now that dispatch enforces it.** The planner accepts
     whatever `task_type` the model emits and only infers one when the field is empty, so a model
     inventing `"analysis"` for a researcher task would be blocked at execution by a contract
     listing only `"research"`. The evidenced vocabulary is `research`, `file_inspection`,
     `patch_proposal`, `build_answer`, `verification`, `external_research` (from
     `TextUtil.InferTaskType`), plus `synthesis`, `section_analysis`, `verify` and
     `mission_verification` in use elsewhere.

     The safe order is to normalise BEFORE enforcing: when a model's `task_type` is outside the
     assigned role's contract, replace it with `InferTaskType(role)` in the planner. That is
     normalisation by this phase's own rule — it changes neither which ant runs nor the ordering —
     and it means contracts can be strict without a model's vocabulary choice sending a good plan
     to the fallback or blocking it mid-mission.
- Strict planner schema validation; task contracts carrying expected artifacts and repair policy.

The version number was an operator decision, taken knowing the phase is incomplete. Recorded here
rather than left for someone to infer from a gate table that does not match the tag, which is
exactly the drift the v3.0.0 baseline lock existed to stop.

The dashboard redesign is not a roadmap phase at all — it arrived as a separate directive. Its
remaining work is tracked in `docs/DASHBOARD_GRID_MIGRATION.md`.

### Goal

Make every mission worker use one typed execution protocol. This phase carries the V2
**Ant Execution Framework** track (`docs/ANT_EXECUTION.md`) to its conclusion: v2.19.0 gave the six
specialists structured results and v2.26.0 did the same for the five core ants, but the contract,
model boundary, and adapter removal remain to be made universal here.

### Required Work

- Define `IAntExecutor.ExecuteAsync(AntExecutionRequest, CancellationToken)` returning `AntExecutionResult`.
- Apply versioned `AntExecutionContract` records to all active and standby mission agents, not only specialists.
- Remove legacy string-return adapters and any remaining prose-based control flow.
- Replace `IModelClient.Generate(string)` with a typed provider result containing status, content, provider, model, timing, usage, retryability, and typed error.
- Extend task contracts with expected artifacts, success criteria, verification requirements, workspace requirements, retry policy, and repair policy.
- Persist structured ant results, metrics, evidence, handoffs, and contract versions.
- Make planner output schema validation strict and complete; invalid graphs are rejected as a unit rather than partially degraded.

### Exit Gates

- Every planner-eligible role has a versioned contract and typed executor.
- Every model provider path returns typed results; no `ERROR:` prefix determines success.
- Every task result is reconstructable without parsing narrative text.
- No core ant bypasses the contract, capability, metric, or artifact path.
- Compatibility adapters are deleted, not merely deprecated.

## DIRECTION CHANGE — Anthill as an agent harness (see `docs/adr/ADR-006-agent-harness.md`)

Anthill is becoming a general-purpose agentic AI harness: provider-agnostic, capability-aware and
tool-centric, with the dashboard, Colony, missions and integrations as capabilities of the platform
rather than its purpose.

**Nothing below is obsolete.** The v3.2.0 typed-protocol work is a precondition for the harness, not
a detour — an agent runtime whose control flow reads prose cannot be provider-agnostic, because
every provider phrases failure differently.

What changes is ORDER, and one reversal:

- `IAntExecutor.ExecuteAsync` stays deferred, but its prerequisite is promoted. The provider layer
  goes async and typed FIRST; the ant signature follows it rather than wrapping it.
- Core-ant `AntExecutionContract`s move to v3.4.0. They were already blocked on tool-inventory
  evidence; they now also need to declare required MODEL capabilities, which cannot be expressed
  until the capability model exists. Writing them now means writing them twice.
- The workspace/language-adapter phase below moves after the provider and tool substrate.

Revised sequence, and the ONLY authoritative ordering — every phase below carries the number it
has here:

| Phase | Name | Status |
|---|---|---|
| v3.3.0 | Provider substrate — typed request/response, wire formats, model capabilities | shipped |
| v3.4.0 | Tool framework projection — tool schemas, the agent loop, typed tool results | shipped |
| v3.5.0 | Mission workspace and language adapter infrastructure | shipped |
| v3.6.0 | Repository indexing and awareness | shipped |
| v3.7.0 | Conversation orchestration | shipped |
| v3.8.0 | Durable worker and attempt runtime | was v3.4.0 |
| v3.9.0 | Artifact, evidence, and context graph | was v3.5.0 |
| v3.10.0 | Reconnaissance and quality ant activation | was v3.6.0 |
| v3.11.0 | Repair, knowledge, documentation, and resource ant activation | was v3.7.0 |
| v3.12.0 | Closed-loop software engineering workflows | was v3.8.0 |
| v3.13.0 | Qualification, benchmarks, and V4 gate | was v3.9.0 |

The renumbering is not cosmetic. Before it, this document contained two `v3.5.0` headings and a
`v3.4.0` heading that appeared AFTER a v3.5.0 one, while the two phases actually shipped — the
provider substrate and the tool framework — had no sections at all. A roadmap that names the same
release twice cannot answer "what is in this release", which is the only question it exists to
answer.

The blocking defect is one interface: `IModelClient.Generate(string prompt, int retries)`. String in,
string out — with nowhere to carry messages, tool schemas, structured-output formats, image parts,
streaming, usage or a per-call model. Adding OpenRouter, LM Studio, vLLM or llama.cpp is mostly a
`ProviderCatalog` entry against an OpenAI-compatible base URL and is NOT the hard part.

## v3.3.0 - Provider Substrate

### Goal

Make the model seam capable of carrying everything an agent runtime needs, so that adding a provider
is a catalog entry rather than a redesign.

### Required Work

- [x] Typed `ModelRequest`/`ModelResponse` with messages, tool specs, structured-output schemas,
      content parts, per-call model selection and nullable usage.
- [x] `ProviderWireFormat`: pure projection onto OpenAI-compatible and Anthropic wire shapes, and
      pure readers back. Tested without a provider, because every mistake at this seam is silent.
- [x] `IModelClient.Send(ModelRequest)` as the primary method; `Generate(string)` demoted to a
      default interface method that narrows onto it.
- [x] Ollama moved onto `/v1/chat/completions`, so one OpenAI-compatible path serves Ollama,
      LM Studio, vLLM, llama.cpp and OpenRouter.
- [x] `ModelCapabilities` + `ModelCapabilityCatalog`: fail-closed capability negotiation.
- [x] `OllamaCapabilityCache`: per-model capabilities discovered from the runtime, warmed at
      startup, read with NO I/O on the call path.
- [x] `GET /providers/capabilities` reporting per-model capability with its source.
- [ ] Async transport (`SendAsync`). Deferred: it is the prerequisite for `IAntExecutor.ExecuteAsync`
      and remains the largest open item in the substrate.

### Exit Gates

- [x] A tools array is either correctly nested or a test fails — never silently ignored.
- [x] A provider that does not report usage reads as UNKNOWN, never as zero.
- [x] A reply carrying only tool calls is a success, not an empty response.
- [x] Capability reporting and the model call path cannot disagree; the endpoint reads the same
      cache the client negotiates against.
- [x] An undiscovered model can fail to CONFIRM a capability but can never be granted one.

## v3.4.0 - Tool Framework Projection

### Goal

Turn "the colony can call tools" into "the colony does work": offer tools to a model, run what it
asks for, feed results back, and make every outcome typed enough to decide the next move.

### Required Work

- [x] `ITool.ParametersJson` — a self-describing JSON Schema, defaulted so no existing tool breaks.
- [x] `ToolSchemaProjection`: offers a role only the tools its authorization permits; a malformed
      schema degrades that one tool rather than the toolset.
- [x] `ToolCallingLoop`: bounded by `BoundedAgentLoop`, with the transcript as the artifact.
- [x] Assistant turns replay their `tool_calls`, so tool results answer requests that are present.
- [x] Capability-aware routing: a route whose model cannot call tools reroutes to one that can, and
      telemetry records both the effective and the routed model.
- [x] `POST /agent/run` — one tool-calling conversation, persisted as a real mission with events.
- [x] Typed tool results: `FailureClass` with derived retryability, classified at every failure site
      in every shipped tool, turned into per-class guidance for the model.
- [x] Core-ant `AntExecutionContract`s declare required MODEL capabilities (`ModelRequirement`),
      checked against each role's live route by `AntModelFitness` at startup and on `GET /tools`.
      The blocker is gone: `ToolInventory` supplied the tool evidence, and v3.3.0 supplied the
      capability model this needed to be expressible at all.
- [x] User-defined tools (v3.4.1): `ToolDefinition` as data, an `IToolKindExecutor` seam, the HTTP
      kind bounded by a host allowlist, persistence (schema 18), and registration through the same
      `ToolRegistry` every built-in uses — so projection, dispatch and classification needed no
      special case. `composite`/`mcp`/`command` are declared and rejected as not-yet-built.

### Exit Gates

- [x] A model is never offered a tool its role may not run.
- [x] A denied or failed tool is reported back as text the model can act on, never dropped.
- [x] An agent run cannot exceed its turn, tool-call, wall-clock or repeated-action budget.
- [x] Every tool failure names its class; a source guard fails the build if a new one does not.
- [x] Retryability has ONE definition, derived from the class rather than stored beside it.
- [x] A user-registered tool is subject to the same authorization and projection rules as a built-in.
- [x] A definition cannot shadow a built-in, reach a non-allowlisted host, or survive a redirect off
      one — and a substituted argument cannot restructure the URL it is substituted into.
- [x] A role's model requirements are checked against its actual route, because every mismatch
      fails silently at runtime and looks like a weak model rather than a misconfiguration.

## v3.5.0 - Mission Workspace and Language Adapter Infrastructure (was v3.3.0)

### Goal

Give every code mission a disposable, reproducible workspace.

### Required Work

- [x] `MissionWorkspaceManager` — detached git worktrees, taken from the enclosing repository rather
      than the agent sandbox. A non-git source is REJECTED rather than copied: a mission workspace
      must be attributable, and a copy of an unversioned directory has no revision to record.
- [x] The manifest persists (schema 19) with base revision, repository fingerprint (the root commit,
      because remotes get renamed and paths say only where a directory sits today), branch and
      paths. The row OUTLIVES the directory — attribution is asked long after the files are gone.
- [x] All ten lifecycle states, stored by NAME rather than ordinal.
- [x] Scoped workspace tools: read, `search_workspace`, edit (the existing write tools, now scoped),
      `read_changed_files_summary`, and change-set creation into the existing PatchSet pipeline.
- [x] Write paths confined by `MissionWorkspaceScope` — ambient and async-flow-local, because a
      workspace is a property of the MISSION and the write tools are shared singletons.
- [x] `WorkspaceCapabilityManifest` detects project types and supplies their declared checks.
- [x] .NET, Node and Python reference adapters, declarative. Detection reads the project; EXECUTION
      reads only the adapters in this repository.
- [x] Checkpoint and resume: `Recover()` reconciles recorded workspaces with the disk at startup,
      distinguishing orphaned, interrupted-preparation and active-at-restart.

### Exit Gates

- [x] A code mission cannot modify the active checkout through any agent path. (Before v3.5.0 this
      was not merely unmet but INVERTED — the live checkout was the only writable location.)
- [x] Every change is attributable to one workspace and base revision, and the change set anchors
      its old content to that revision rather than to a checkout that may have moved on.
- [x] Workspace recovery after restart is tested, against real git worktrees.
- [x] Cleanup cannot delete an operator-retained workspace; the rule lives on the record so a
      second caller cannot miss it.
- [x] Verification commands come from the manifest, never model invention — enforced by guards that
      no declared command may be a template or invoke a shell.

## v3.6.0 - Repository Indexing and Awareness

### Goal

Let an agent answer "where is this handled" from evidence rather than from a guess, without reading
the repository into the context window.

### Required Work

- [x] A durable repository index (schema 20): file inventory, language, size, line count, content
      hash, revision fingerprint, and symbol entries for C#, TypeScript/JavaScript, Python, Go and
      Rust.
- [x] Reference lookup, with the honesty that makes it usable: results carry whether they can be
      ATTRIBUTED to one declaration, because "what calls this" feeds "what would my change break"
      and a list that looks authoritative gets acted on.
- [x] Shipped as the `repository_index` TOOL. The index is never injected into a prompt — the agent
      spends a turn to ask, and the context holds an answer rather than a repository.
- [x] Incremental rebuild keyed on revision + content hash, reusing symbol extraction (the expensive
      half) for unchanged files. Every file is still read and hashed, because a cheaper check would
      be a guess.
- [x] Every result bounded, with path, line and revision on the answer.

### Exit Gates

- [x] An index query returns the same answer for the same revision (paths sorted, symbols ordered),
      and `FileChanged` reports staleness per FILE rather than invalidating everything a mission
      touches.
- [x] No indexing path reads outside the workspace: the walk resolves every path through the same
      `WorkspacePathGuard` every file tool uses, so a symlink out of the workspace is refused.
- [x] An agent calls a tool. Outside a mission the tool refuses rather than describing the live
      checkout.
- [x] Build time, file count, reuse count and truncation are reported; a large repository degrades
      to inventory-only, and SAYS so — an empty symbol result must be distinguishable from "not
      searched".
- [x] Every excerpt carries its path and line, and every answer its revision.

### Known limits, stated rather than implied

- Symbols come from PATTERN MATCHING, not a compiler. A declaration in an unusual shape is missed
  and a mention in a comment may appear. Every symbol answer says so.
- References are text matches: imports, overloads and scope are not resolved. A name declared in
  more than one place returns mentions that cannot be attributed to any of them, and the tool leads
  with that caveat rather than appending it.

## v3.7.0 - Conversation Orchestration

### Goal

One conversational surface that starts as chat and ESCALATES into autonomous execution, with the
escalation itself explicit, bounded and approved.

### Required Work

- [x] Conversations persisted as first-class runtime objects (schema 21), with the transcript, the
      tools offered and called, and the model route recorded PER TURN — the route can change
      mid-conversation when capability-aware routing substitutes a model.
- [x] The escalation boundary: `start_mission` is a side-effecting action gated by the same
      `EscalationGate` as `apply_patch`. The CALLER asks; the model never decides.
- [x] Two execution modes on one conversation — `Chat` bounded by the tool loop, `Mission` through
      the pipeline, reached only by a gated escalation.
- [x] Run state surfaced conversationally: what it is doing, what it did, what it is WAITING ON,
      derived on request rather than stored.
- [x] Approval reuses the existing gate rather than inventing a second one.

### Exit Gates

- [x] A conversation survives restart with its transcript and route intact.
- [x] No conversation begins side-effecting work without a RECORDED operator decision. The gate is
      about accountability, not prompting: choosing AutoApprove or Bypass IS the decision, recorded
      once with an author. An UNATTRIBUTED standing permission fails closed back to Ask.
- [x] Conversation and mission are one history — the link is written on both sides so the join
      works from either direction.
- [x] Cancelling a conversation cancels the work it started: the row is marked first (so no NEW work
      can start regardless of anyone's cooperation), then every live token source is signalled.
- [x] One budget, approval and audit path. `ConversationBudget` is the single source both modes
      derive from — the tool loop's budget is PROJECTED from it rather than defaulted separately.

### The failure the shared budget closes, stated plainly

Per-execution budgets structurally cannot bound a conversation. Each escalation gets a fresh loop
budget and looks like the first one, so a conversation that escalates repeatedly stays inside its
limits every single time while the total work it authorises grows without bound. Only a budget
belonging to the CONVERSATION can see that.

### Known limits

- The two enforcement mechanisms are not merged: the loop still counts turns, the mission still
  counts tasks. They now count against limits that came from one place, which is what makes the
  totals meaningful — but a single unified counter is not what shipped.
- `ConversationRunner` holds live cancellation sources in memory. A process restart loses the
  ability to signal work started before it, though the persisted `Cancelled` flag still prevents
  anything new from starting.

## v3.8.0 - Durable Worker and Attempt Runtime (was v3.4.0)

### Goal

Make task execution restart-safe, reclaimable, and ready for future worker distribution.

### Required Work

- Create a persistent worker protocol with worker IDs, capabilities, leases, heartbeats, and availability.
- Persist separate task attempts with inputs, outputs, model route, tool versions, environment, duration, failure, and evidence.
- Make claims atomic and idempotent.
- Reconcile queued, claimed, running, waiting, and orphaned tasks at startup.
- Define redelivery behavior for each task and side-effect class.
- Integrate Quartermaster metrics with effective concurrency, but allow it only to reduce operator-defined ceilings.
- Separate local worker implementation from the worker protocol so a future remote worker does not require scheduler redesign.

### Exit Gates

- No accepted task is silently lost after crash or restart.
- Expired work is reclaimed without duplicate retained side effects.
- Every retry is a distinct attempt with a durable reason.
- Two workers cannot claim the same non-parallel task.
- Fault tests cover crash before execution, during model call, during tool call, after change, during verification, and during cleanup.

## v3.9.0 - Artifact, Evidence, and Context Graph (was v3.5.0)

### Goal

Replace transcript-shaped collaboration with durable typed collaboration.

### Required Work

- Implement immutable `ArtifactStore` and `EvidenceStore` records with hashes and provenance.
- Define schemas for repository maps, file sets, UI maps, change plans, patch sets, test reports, security reviews, diagnoses, verification bundles, summaries, release notes, and memory candidates.
- Create an artifact dependency graph connecting every output to its input artifacts.
- Build a Context Compiler that selects bounded excerpts and artifact references for each task.
- Persist inter-ant messages as control-plane notices only; substantive work moves through artifacts.
- Add artifact visibility classes and secret-redaction boundaries.
- Make handoffs reference required artifact IDs and explicit missing information.

### Exit Gates

- Every task input and output is traceable through artifact IDs.
- Replaying the same attempt can reconstruct the context package exactly.
- No mission requires an unbounded transcript to continue.
- Artifact hashes detect mutation or stale input.
- UI/API can show why an artifact exists, who produced it, and what consumed it.

## v3.10.0 - Reconnaissance and Quality Ant Activation (was v3.6.0)

### Goal

Activate the ants that improve planning accuracy and verification quality before enabling repair and learning.

### Required Work

- Graduate UI Cartographer through shadow and supervised operation.
- Require UI Cartographer artifacts for frontend/UI change missions.
- Graduate Tester with .NET and Node check adapters, deterministic evidence, timeout, and cancellation support.
- Graduate Soldier as the policy, security, path, permission, dependency-risk, and patch-risk gate.
- Upgrade Researcher, File, Web, Coder, Builder, and Verifier to the universal contract and artifact framework.
- Make Verifier orchestrate required evidence rather than produce a model-only verdict.
- Add planner workflow rules that select reconnaissance and quality roles based on mission type and workspace manifest.

### Exit Gates

- UI missions cannot proceed to change without a UI map unless explicitly waived by policy.
- Required build/test checks are executed by Tester, not narrated by another ant.
- Soldier can block a change with a typed policy finding that models cannot override.
- Verifier cannot pass a mission missing required deterministic evidence.
- Each activated role has shadow comparison data and an operator-visible activation record.

## v3.11.0 - Repair, Knowledge, Documentation, and Resource Ant Activation (was v3.7.0)

### Goal

Close the failure-repair-learning loop without allowing uncontrolled recursion.

### Required Work

- Graduate Medic with typed failure inputs, root-cause classifications, repair scopes, and separate diagnosis/repair budgets.
- Allow Medic to recommend or request a focused repair task; it never edits files itself.
- Graduate Archivist to store verified lessons, negative lessons, deprecated procedures, and skill candidates with full provenance.
- Graduate Scribe to produce operator summaries, changelog entries, and documentation change sets only from verified artifacts.
- Formalize Quartermaster as a deterministic advisory service with a versioned metrics contract.
- Add per-role and per-mission budgets for model calls, tool calls, elapsed time, repair count, and context size.
- Add loop detection based on unchanged evidence fingerprints, not only text similarity.

### Exit Gates

- A seeded build failure can produce diagnosis, focused repair, rerun, and a final verified or explicit failed result.
- Repair cannot exceed configured attempt, task, or time budgets.
- Archivist cannot positively credit unverified work.
- Scribe cannot generate retained docs changes before the associated implementation is verified.
- Quartermaster cannot raise authority or concurrency above operator ceilings.

## v3.12.0 - Closed-Loop Software Engineering Workflows (was v3.8.0)

### Goal

Combine the infrastructure into repeatable end-to-end workflows similar to modern coding agents.

### Required Work

- Add versioned workflow templates:
  - Repository explanation.
  - Bug diagnosis and repair.
  - Small feature implementation.
  - Refactor with behavior preservation.
  - UI change.
  - Test addition.
  - Documentation update.
  - Dependency update.
- Templates define required stages and artifacts but allow bounded delta planning.
- Implement an iterative Coder workspace loop: inspect, edit, diff, check, review result, refine.
- Add checkpointing between waves.
- Make Builder produce one final answer from verified artifacts and unresolved warnings.
- Present a retention bundle: diff, files, checks, security review, verification, known risks, rollback, and recommended next action.
- Support export, retain workspace, create local commit, and prepare branch; never push or merge without separate policy and approval.

### Exit Gates

- A representative medium bug-fix mission completes without manual task routing.
- A frontend change invokes UI Cartographer, Coder, Tester, Soldier, Verifier, Builder, and Scribe where required.
- Failed checks trigger bounded repair and exact rerun.
- The final answer matches the actual workspace state and evidence.
- Rejecting retention leaves the active repository unchanged.

## v3.13.0 - Qualification, Benchmarks, and V4 Gate (was v3.9.0)

### Goal

Prove the platform is ready for V4 authority and broader autonomous development.

### Required Work

- Create a versioned benchmark corpus covering research, backend, frontend, tests, refactors, docs, failures, policy refusals, restart recovery, and malformed model output.
- Run benchmarks against clean disposable repositories and record exact model, adapter, tool, and runtime versions.
- Add long-duration soak runs with database growth, repeated restart, provider interruption, cancellation, and workspace cleanup.
- Add differential tests that compare shadow and active ant decisions.
- Produce a V4 qualification report from measured data; human attestations are reserved for claims the runtime genuinely cannot measure.
- Remove or explicitly classify every remaining standby/scaffold role.
- Freeze V3 behavior during qualification; only defect fixes may enter.

### V4 Release Gates

All gates are mandatory:

1. Zero silent mission or task loss in the recovery suite.
2. Zero direct agent writes to the active checkout.
3. Zero unverified outcomes counted as success.
4. Zero tool executions outside task capability grants.
5. Every planner task passes the same contract admission path.
6. Every model and tool result is typed end to end.
7. Every retained change has deterministic verification evidence or an explicit operator-approved exception that disqualifies autonomous qualification.
8. Every artifact and decision has provenance.
9. All existing mission agents have reached Qualified or have an explicit non-agent classification.
10. At least 80% `completed_verified` success on the approved medium-task benchmark without manual task routing.
11. At least 95% correct safe refusal on prohibited or out-of-scope benchmark tasks.
12. At least 70% successful bounded recovery on seeded repairable failures.
13. No critical data-loss, authorization-bypass, or false-verification defect during the qualification soak.
14. A generated V4 qualification report is truthful, reproducible, and tied to the exact runtime build.

## Release Discipline for Every V3 Phase

Every release must include:

- An architecture decision or explicit statement that no architecture change is needed.
- A tracked issue with measurable acceptance criteria.
- Unit tests for rules and transformations.
- Integration tests for production wiring.
- At least one end-to-end canary mission.
- At least one fault or interruption test.
- Upgrade and rollback notes.
- API/UI visibility for the new state or behavior.
- Updated canonical docs and version markers.
- A release candidate run that executes the phase's qualification pack.
- No deleted, skipped, or weakened safety test without a written replacement rationale.

A phase is not complete when its code merges. It is complete when its intended workflow is exercised through the real runtime and the exit gates are recorded as passing.

## Immediate First Actions

1. Adopt this document set and move the current V2 North Star to a clearly labeled historical path.
2. Open one v3.0.0 tracking issue containing the baseline-lock acceptance criteria.
3. Generate the first role/handler/contract/tool/call-site inventory from the current code.
4. Create ADRs for Queen decomposition and `MissionContext` before refactoring.
5. Build characterization tests around the current v2.26.0 mission lifecycle.
6. Freeze new ant and homelab feature work until v3.0.0 closes.

---

## Adoption status

| First action | Status |
|---|---|
| 1. Adopt the V3 document set; archive V2 planning docs | **Done** — V2 docs at `docs/archive/v2/`; this document and `docs/NORTH_STAR.md` are canonical. |
| 2. Open the v3.0.0 baseline-lock tracking issue | Pending operator (acceptance criteria are the Exit Gate Record above) |
| 3. Generate the runtime inventory + call-site audit | **Done — shipped in v3.0.0.** `RuntimeInventory` + `CallSiteAudit`, CI-gating, 300 declarations, exemption list empty. Found and removed the dead `cors_enabled` gate. |
| 4. ADRs for Queen decomposition and `MissionContext` | **Done — shipped in v3.0.0.** Five ADRs in `docs/adr/`, each written before the phase it governs, each naming what was explicitly rejected. |
| 5. Characterization tests around the v2.26.0 lifecycle | **Done — shipped in v3.0.0.** `CharacterizationTests` pins the outcome truth table, verdict vocabulary, skill-outcome split, signal categories, constraint parsing, action-state mapping, and ant status mapping. |
| 6. Freeze new ant and homelab feature work | **In force** from adoption until v3.0.0 closes |
