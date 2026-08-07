# ANTHILL Core Refactor — Migration Plan

**Status:** **CLOSED at v3.8.18.** Phases 0–7 shipped by v3.8.17; v3.8.18 closed the acceptance gap
an external review found. Two plan items were superseded on measurement and said so; three success
criteria remain honestly unmet or partial — including the no-UI gate, whose CI job failed twice and
was withdrawn rather than made non-blocking.
**Baseline:** v3.8.2, `main` · **Final:** v3.8.18
**Goal:** a smaller, stable core that runs with no AI provider and no UI, while preserving public behavior.

| Measure | Baseline (v3.8.2) | Final (v3.8.17) |
|---|---:|---:|
| `Anthill.Core` | 34,247 | 24,973 |
| `Anthill.SDK` | 0 | 3,152 |
| `Anthill.Modules.*` | 0 | Reasoning, Homelab, Tools |
| `Anthill.Api/ApiHost.cs` | 3,227 | **535** (+ 6 partials) |
| `Anthill.UI` | — | 5 assets, no `.csproj` |

Core is down **9,274 lines, 27%**, with nothing deleted — every line moved to the SDK or to a
module. `ApiHost.cs` grew to 3,294 across the refactor before phase 6 split it, which was the
expected shape while it lasted: it is the composition root, and each extracted module is composed
there.

**Status keys used below:** a phase marked DONE names the release it shipped in. "SURVEYED" means
the measurement is recorded here and the work has not started. Sections headed "superseded" are kept
because the reasoning that replaced them is worth reading against them, not because they describe
the plan.

This is an *architectural* refactor, not a feature rewrite. No capability is removed unless it is
redundant and replaced.

---

## 1. Baseline survey

Measured against the working tree, not assumed.

| Project | LOC | Notes |
|---|---:|---|
| `Anthill.Core` | 34,247 | 35 top-level namespaces |
| `Anthill.Api` | 6,242 | `ApiHost.cs` alone is 3,227 |
| `Anthill.Cli` | 180 | thin |
| `Anthill.Tests` (+ Homelab) | 22,595 | good coverage to refactor against |

Embedded UI assets in `Anthill.Api/Ui/`: `app.js` 498 KB, `index.html` 231 KB,
`dashboard-grid.js` 37 KB, `dashboard-grid.css` 26 KB. *(These moved to `src/Anthill.UI/` in phase 6.)*

### 1.1 What the survey changes about the plan

Four findings materially reorder the original phase list.

**Finding A — there is no event bus, but there is a latent one.**
No `IEventBus`, no publish/subscribe, no SSE or WebSocket anywhere in the solution.
But `SqliteMemory.LogEvent(missionId, eventType, message, taskId, antName, metadata)` is already
doing the job: **70+ distinct event types** across **~85 call sites**, and `GetRecentEvents` is how
`ApiHost` and `Queen.Views` read them back.

Existing event vocabulary (partial): `mission_created`, `mission_started`, `mission_classified`,
`mission_evaluated`, `mission_outcome`, `task_created`, `task_ready`, `task_started`,
`task_completed`, `task_failed`, `task_blocked`, `task_drained`, `tool_called`, `tool_denied`,
`tool_completed`, `escalation_refused`, `model_call`, `pheromone_scored`, `approval_request_created`,
`patch_proposal_created`, `workspace_ready`, `shadow_outcome_recorded`, `skill_outcome_recorded`,
`worker_permission_audited`.

The implication: **the bus is a wrap, not a rewrite.** `LogEvent` keeps persisting and additionally
publishes to an in-process bus. All ~85 call sites are untouched. This is the single highest
payoff-to-risk step in the whole refactor, and it unblocks "UI observes events."

**Finding B — `SqliteMemory` is the real core bloat, not the AI providers.**
20 partial-class files, **177 public methods**, spanning pheromones, jobs, shadow runs, workspaces,
users, provider credentials, skills, readiness, repository index, fault injection, conversations,
evaluation, and task results. Every module extracted later will still want to reach into it, so
segregating it is a *prerequisite* for real modularity, not a nice-to-have. The original plan does
not name this; it should be a first-class phase.

**Finding C — Homelab + Integrations is the largest clean extraction.**
`Homelab/` (~2,900 LOC, incl. a 1,458-line `HomelabRepository`) plus `Integrations/` (Proxmox,
vSphere, Hyper-V, Docker, Arr, Media, MediaRequests, Download, Monitoring). Inbound references from
outside the cluster are only: `Health/HealthCheckRunner`, `Health/HealthModels`,
`Incidents/IncidentManager`, `Shadow/LiveIncidentObserver`, and `Anthill.Api/ApiHost.cs`. That is a
narrow, nameable seam — five files to reconcile, not fifty.

**Finding D — AI provider extraction is nearly free.**
`Anthill.Core.csproj` carries **no AI SDK packages** (only `Microsoft.Data.Sqlite`,
`Caching.Memory`, `Logging.Abstractions`). `IModelClient` already exists (ADR-006), wire encoding is
already isolated in the pure, testable `ProviderWireFormat`, and providers are already catalog
entries via `ProviderCatalog`. Extraction is mechanical. Because it is low-risk and proves the
module pattern end to end, it stays early — but after the bus, since providers should publish
`model_call` through it.

### 1.2 Constraint

`dotnet` is not available in the assistant sandbox. **Every phase gate below must be run locally by
the maintainer**: `dotnet build Anthill.sln` and `dotnet test`. No phase is complete until its gate
passes on your machine.

---

## 2. Target project boundaries

```
Anthill.SDK        — contracts only. No implementations, no I/O, no dependencies
                     beyond BCL + Logging.Abstractions. Referenced by everything.
Anthill.Core       — Queen, Objective→Mission→Task→Action, scheduler, task queue,
                     event bus, worker manager, pheromone memory, worker framework.
                     References SDK only.
Anthill.Modules.*  — providers, homelab, integrations, tools. Reference SDK. NEVER
                     referenced by Core. (The original line also named git, vision and
                     analytics modules; none of those capabilities exist, and naming
                     unbuilt modules in a boundary sketch is how a plan acquires scope.)
Anthill.Api        — HTTP + SSE surface. References Core + SDK. Composes modules at
                     startup by configuration.
Anthill.UI         — assets only; consumes the SSE event stream. No Core reference.
```

**The dependency rule, stated once:** arrows point toward `Anthill.SDK`. `Core` must never name a
module type. If Core needs behavior a module provides, Core declares an interface in the SDK and a
module implements it. When in doubt, it is a module.

---

## 3. Phases

Each phase is independently shippable and independently revertible. Do not start a phase until the
previous gate is green.

### Phase 0 — SDK scaffold (additive; nothing moves) — **DONE (v3.8.3)**

- [x] `src/Anthill.SDK/Anthill.SDK.csproj` — no package refs beyond `Logging.Abstractions`
- [x] `Events/ColonyEvent.cs` — mirrors the `LogEvent` shape field for field (Id, MissionId, TaskId,
      AntName, EventType, Message, Metadata, CreatedAt) so the Phase 1 wrap is lossless
- [x] `Events/IEventBus.cs` — `Publish`, `Subscribe`, `Subscribe(eventType)`
- [x] `Events/EventTypes.cs` — all 70+ observed names as constants, read out of the tree
- [x] `Modules/IAnthillModule.cs` — `Name`, `Version`, `Register(IModuleContext)`
- [x] `Modules/IModuleContext.cs` — the *only* surface a module gets onto the core
- [x] `Memory/IPheromoneMemory.cs` — `Reinforce`, `Top`, `ListAll`, `Prune` + `PheromoneTrail`
- [x] `Memory/IEventLog.cs` — `Append`, `Recent`
- [x] `Anthill.SDK` added to `Anthill.sln`; `Anthill.Core` references it (unused, but proven to compile)
- [x] `src/Anthill.Modules/README.md` — boundary rules and per-domain project layout

**Gate:** `dotnet build Anthill.sln` succeeds; test counts unchanged. *Passed locally, v3.8.3.*

#### Three decisions made during implementation

**No `IReasoningProvider` yet.** The original checklist had Phase 0 define `IReasoningProvider`
plus `ReasoningRequest`/`ReasoningResponse`. On inspection that would have been a mistake:
`Anthill.Core/Models/ModelProtocol.cs` already defines `ModelRequest`, `ModelResponse`,
`ModelMessage`, `ModelToolSpec`, `ModelToolCall`, `ModelContentPart` and `ModelUsage` — typed,
provider-agnostic, and covering tool calling, structured output, vision parts, reasoning content and
token accounting. `IModelClient` is already the reasoning interface the plan asks for. Declaring a
parallel set in the SDK would create exactly the duplication requirement #9 says to eliminate.
Phase 2 therefore **moves** `ModelProtocol.cs`, `ModelCallOutcome.cs` and `IModelClient` into the
SDK and renames the interface, rather than inventing a second protocol beside the good one.

**No `RegisterTool` on `IModuleContext` yet.** `ITool` lives in Core until Phase 5. The two ways to
have tool registration now are `RegisterTool(string, object)` — which abandons the type system at
the seam whose whole job is enforcing types — or a duplicate SDK tool interface. Both are worse than
waiting one phase. Recorded as a comment in `IModuleContext` so the gap reads as a decision.

**No `Anthill.Modules.csproj`.** Modules are one project per domain, not one shared assembly;
a shared one would let modules compile against each other and would drag every module's transitive
dependencies into any deployment loading any of them. `src/Anthill.Modules/README.md` records the
layout and the boundary rules.

---

### Phase 1 — Event bus behind `LogEvent` — **DONE (v3.8.3)**

- [x] `Anthill.Core/Events/InProcessEventBus.cs` — bounded drop-oldest channel, single-reader pump
      preserving publication order, subscriber faults caught and logged, disposable handles
- [x] `Anthill.Core/Events/NullEventBus.cs` — so `IEventBus?` never appears in the colony
- [x] `SqliteMemory.EventBus` property; `LogEvent` persists **then** publishes
- [x] Default is the null bus → behaviour byte-identical when unwired
- [x] `Queen.Events` constructs and exposes the bus, wired before any component is composed
- [x] `GET /events/stream` (SSE) in `ApiHost.EventStream.cs` — replay then live, one serialiser for
      both, 20s heartbeat, subscribe-before-replay so nothing falls in the gap
- [x] `app.js` consumes the stream and invalidates the `/events/json` cache on arrival; polling
      retained as fallback
- [x] 12 tests in `tests/Anthill.Tests/EventBusTests.cs`

**Gate:** `dotnet build Anthill.sln && dotnet test`. Then run a mission and confirm the dashboard
updates without waiting for a poll.

#### Decisions made during implementation

**Version is v3.8.3, not v3.9.0.** `docs/ROADMAP.md` already reserves `## v3.9.0` for "Artifact,
Evidence, and Context Graph". Taking that number would have left the roadmap's v3.9.0 section
describing something that did not ship — exactly the drift `DocsConsistencyTests` exists to catch,
and it would not have been caught mechanically because the heading would still be present and
unique. The refactor track is numbered as patch releases against the current line and the V3 feature
phases keep their numbers.

**`EventBus` is a settable property, not a constructor parameter.** `Queen` accepts an optional
pre-built `SqliteMemory`, so a constructor argument would only reach instances the Queen built
herself; every caller supplying its own memory — the CLI, several hundred tests — would get a
silently unwired bus. One mechanism covering both construction paths beats two mechanisms covering
one each.

**The client uses `fetch` streaming, not `EventSource`.** `EventSource` cannot set request headers,
so adopting it would have required accepting the auth token as a query parameter — putting a live
credential into proxy logs, browser history and `Referer` headers to save a few lines of frame
parsing. Not a trade worth making.

**Full replacement of the pollers is deferred to Phase 6.** `app.js` is 498 KB and unstructured;
rewriting its seven `/events/json` call sites belongs with the UI decoupling work, not here. What
Phase 1 delivers is that the data is now *pushed* and the cache is invalidated on arrival, so panels
stop showing stale data while keeping their existing refresh logic.

---

### Phase 2 — Reasoning providers → module

Split in two on contact with the code. `ModelRouter.cs` turned out to contain `OllamaClient` and
`PlaceholderClient` as well as the router, and `OllamaCapabilityCache` is a static called from Core,
Api *and* Cli — so "move the providers" is really "invert construction, then move", and doing both
in one step would have been a large unverifiable change.

#### Phase 2a — contracts to the SDK — **DONE (v3.8.4)**

- [x] Moved to `Anthill.SDK/Reasoning/`: `ModelProtocol.cs`, `ModelCallOutcome.cs`,
      `ModelCapabilities.cs`, `ProviderCatalog.cs`, `ModelCallScope.cs` (all five were
      dependency-free — namespace declaration and nothing more)
- [x] `IReasoningProvider` in the SDK, from `IModelClient`
- [x] `IModelClient` kept in Core as `interface IModelClient : IReasoningProvider {}` — no members,
      so implementers and consumers compile untouched. Not `[Obsolete]` yet; that goes on when the
      last in-tree caller has migrated, and the alias is deleted a release after that.
- [x] `global using Anthill.SDK.Reasoning;` in Core, Api and Tests rather than ~20 per-file imports

**Gate:** `dotnet build Anthill.sln && dotnet test`.

**Decision:** the checklist above originally said "define `IReasoningProvider`" as new work.
Inspection showed `IModelClient` *was* already that interface — typed both ways, covering tool
calling, structured output, vision parts, reasoning content and token accounting, with wire encoding
already outside it. Writing a second one beside it would have created the duplication requirement #9
exists to remove, so the existing contract was moved and renamed instead.

#### Phase 2b — implementations to the module — **DONE (v3.8.5)**

- [x] SDK: `IReasoningProviderFactory` — `CanServe(providerId)` + `Create(context)` — the inversion
      point, so `ModelRouter` stops naming provider types
- [x] SDK: `IModelCapabilityProbe`, so Core can ask what a model supports without depending on the
      Ollama cache that answers
- [x] Split `ModelRouter.cs`: routing and the circuit breaker stay; `OllamaClient` moves out
- [x] Core: `UnavailableProvider` (from `PlaceholderClient`) as the null object when nothing is
      registered — a typed failure, never a throw
- [x] Move to `Anthill.Modules.Reasoning`: the Ollama client, `ProviderClients.cs`,
      `ProviderWireFormat.cs`, `OllamaCapabilityCache.cs`, plus `ReasoningModule : IAnthillModule`
- [x] Reconcile the three `OllamaCapabilityCache.Warm` call sites (`ApiHost` ×2, `Cli`)
- [x] Keep in Core: `ModelRouter` (routing *policy* is a core scheduling concern),
      `ModelRoutingPolicy`, `ModelCircuitBreaker`, `ModelCallOutcome`, `ModelCallScope`,
      `ModelCapabilities`, `ModelProtocol`
- [x] Core's `Router` becomes nullable-by-design; assert a mission can be planned and a task
      dispatched with **zero** providers registered
- [x] Reconcile the 10 outside-`Models/` referencing files: `Planning/Planner.cs`,
      `Tools/ToolSchemaProjection.cs`, `Autonomy/Strategist.cs`, `Agents/Ants.cs`,
      `Agents/AntModelFitness.cs`, `Agents/ToolCallingLoop.cs`, `Memory/SqliteMemory.Providers.cs`,
      `Orchestration/Queen.cs`, `Orchestration/ExecutionService.cs`,
      `Orchestration/ResultAssembler.cs`
- [x] Provider registration moves to `Anthill.Api` composition root

**Gate:** build + tests; **and** boot the API with every provider disabled — it must start, accept a
mission, and degrade gracefully rather than throw. This is success criterion "core runs without any
AI provider." *Passed, v3.8.5, with a test class for exactly that case.*

**What the execution found that the checklist did not say.** The core could not merely run without a
provider — it could not COMPILE without one. `ModelRouter` named `OllamaClient`,
`OpenAiCompatibleClient` and `AnthropicClient` in two switch statements, so "the colony runs with no
AI" had been claimed as a goal since phase 0 while being structurally impossible. The module's only
real coupling back to the core turned out to be one `using` for the call timeout, which now arrives
through the context and is read live.

---

### Phase 3 — Segregate `SqliteMemory` — **DONE (v3.8.6), narrower than planned and deliberately so**

The unglamorous phase that makes phases 4–6 possible. Interfaces first; the class is not split yet.

- [x] Carve role interfaces over the existing partials — `IPheromoneMemory` and `IEventLog` — and
      have `SqliteMemory` implement them EXPLICITLY, in `SqliteMemory.SdkContracts.cs`. Zero
      behavior change; purely additive.
- [x] `IModuleContext` exposes only `IEventBus` + those two store views — never `SqliteMemory`
      itself. A module holds two narrow views of a class with 177 public methods.
- [ ] `IMissionStore`, `IWorkerStore`, `IWorkspaceStore`, `ISkillStore`, `IJobStore` — **not
      written, and not currently needed.** Each was to be carved so a later module extraction would
      not drag `SqliteMemory` with it; phases 4 and 5 then extracted the homelab, the providers and
      the tool contracts without any of them, because none of those modules needed to persist
      anything through the core. An interface with no implementer-side demand is a guess about a
      future consumer, and this repository's own history says those guesses go stale before they are
      used. They get written when a module needs one.
- [ ] Retarget in-core consumers to the narrowest interface they need instead of the concrete class
      — deferred. The value is real but it is internal tidiness, not boundary enforcement, and it
      touches a very large number of call sites for no change in what is possible.

**Gate:** build + tests. Verify by inspection that no module-facing type names `SqliteMemory`.
*Passed, v3.8.6, and since v3.8.8 it is enforced mechanically rather than by inspection:
`ModuleBoundaryTests` reads assembly references.*

**Why the phase was forced to be real.** It was written as preparation and would have stayed
theoretical, because nothing yet consumed the interfaces. `ModuleHost` arriving in the same release
is what made it concrete — the moment a module had to be handed something, the question of what it
may touch stopped being a design note.

---

### Phase 4 — Homelab + Integrations → module

Measured, not estimated: `Homelab/` is 4,259 lines across 19 files and `Integrations/` is 2,290
across 13 — **6,549 lines**, plus 1,441 in `Anthill.Api/Homelab/`. Split in two.

#### Phase 4a — prerequisites — **DONE (v3.8.7)**

- [x] `AnthillTime.cs` and `Json.cs` → `Anthill.SDK/Common/`. The survey's useful finding: Homelab
      and Integrations import `Anthill.Core.Common` twenty times but use only these two helpers
      (56 and 10 call sites), and both are dependency-free and I/O-free. The rest of `Common` stays.
- [x] `HomelabRepository.RecordEvent` persists **then** publishes to `IEventBus`, gated on rows
      actually written because the insert is `OR IGNORE`
- [x] Homelab event types prefixed `homelab_` on the colony stream; original type kept in metadata
- [x] Wired at the API composition root, to the same bus the mission log publishes to

**Gate:** `dotnet build Anthill.sln && dotnet test`, both suites.

#### Phase 4b — the move itself — **DONE (v3.8.7)**

*(This section carried the same checklist twice, written in two sittings and never reconciled. The
duplicate is deleted; what follows is the single list, with what actually happened.)*

Coupling measured from the imports before starting, and how each was resolved:

| Needs | Planned | What happened |
|---|---|---|
| `Health/` (272 LOC) | follows homelab into the module | moved — it *is* homelab health |
| `SafeAction/` (333 LOC) | assess: approval may be core | **SDK.** Four files with no core imports at all, and shared with shadow mode, so it belonged on neither bank |
| `Security` (1 import) | likely an SDK contract | `IFieldCipher` in the SDK |
| `AnthillRuntime` (3 imports) | pass values in | `HomelabOptions`, eleven settings, built at the composition root |
| `SqliteMemory` (3 uses) | narrow interfaces, as phase 3 did | the module persists through its own repository; no core store needed |

- [x] SDK contracts first: `IHomelabRepository`, `IIntegrationDefinition`, `IInventoryProvider`,
      `IHomelabActionRunner`, `IHomelabTargetGuard` (several already existed in Core — the
      declarations moved, the shapes were kept)
- [x] `IHomelabEventSink` deleted in favour of `IEventBus` — it was a single-purpose bus, and 4a
      made it redundant as an announcement path
- [x] Move `Homelab/**` and `Integrations/**` to `Anthill.Modules.Homelab`
- [x] Reconcile the five outside references: `Health/HealthCheckRunner.cs`, `Health/HealthModels.cs`,
      `Incidents/IncidentManager.cs`, `Shadow/LiveIncidentObserver.cs`, `Anthill.Api/ApiHost.cs`
- [x] Decide `Health/`, `Incidents/`, `Inventory/`, `Power/`, `Backups/` — all followed the homelab
      into the module under the default-to-module rule
- [x] `Anthill.Api/Homelab/*` endpoints register through the module, not directly

**Gate:** build + `Anthill.Tests.Homelab` in full; exercise the homelab dashboard manually. *Passed,
v3.8.7.*

**Result: 6,549 lines out of the core, the largest single extraction in the refactor.**
`LiveIncidentObserver` was the one file that fit nowhere — it reads a module type and writes core
types — so it moved to the composition root rather than to either side.

---

### Phase 5 — Tools → modules — SURVEYED; harder than phase 4, and split accordingly

Measured before starting. Recorded here so it is not rediscovered.

**The tool layer is coordination, not capability.** `Anthill.Core.Tools` (1,913 LOC) is consumed by
`Agents/Ants`, `SpecialistAnts`, `ToolCallingLoop`, `Queen`, `ExecutionService`, `PlanningService`,
`Verification` and `SqliteMemory.UserTools` — because `ITool`, `ToolRegistry` and `ToolResult` ARE
the dispatch vocabulary. Only the tool IMPLEMENTATIONS are module material. Core keeps
`ToolRegistry`, `ToolAuthorization`, `ToolDefinition`, `ToolInventory`.

**Workspaces going out is a hot-path change** (decided: they follow the tools). `Sandbox/` (381 LOC)
is used by `Ants` — a core agent running sandboxed code — and `SqliteMemory.Workspaces.cs` persists
workspace records. Unlike homelab, this lands on the mission execution path.

**Only one `ToolKind` (`Http`) is registered**, so shell/git/filesystem tools are NOT behind
`IToolKindExecutor` yet. That indirection must be built before it can be used.

#### Phase 5a — contract vocabulary split — DONE (v3.8.9)

`Capability`, `FailureClass`, `FailureClassify`, `ToolDescriptor`, `ToolCatalog` →
`Anthill.SDK.Contracts`. `TaskContract`, `ContractGate` and `Contracts.ToolResult` stayed: the first
two take `Domain.Task` and reach `Agents.AntRegistry`; the third collides by name with
`Domain.ToolResult`.

#### Phase 5b — `ToolResult` + `ITool` to the SDK — **DONE (v3.8.10)**

5a unblocked this: `Domain.ToolResult` depends on `FailureClass` and `FailureClassify` and nothing
else, and both are now in the SDK. `ITool.Run` returns `ToolResult` and needs nothing further.

Every qualification form enumerated — the check whose absence cost four build cycles in 5a:

| Form | Count | Action |
|---|---:|---|
| bare `ToolResult` | 138 | resolves via `global using Anthill.SDK.Tools` — no edit |
| `Domain.ToolResult` | 8 | rewrite to bare `ToolResult` |
| `Contracts.ToolResult` | 5 | **leave** — a different type, stays in the core |
| `: ITool` / `interface ITool` | 13 | no edit; implementers are unaffected by the move |

**Exactly two files go ambiguous**, and both must be handled BEFORE the move — they import
`Anthill.Core.Contracts` (which still declares its own `ToolResult`) *and* use the bare name:

- `src/Anthill.Core/Tools/ToolDefinition.cs`
- `tests/Anthill.Tests/TaskContractTests.cs`

Qualify their bare `ToolResult` as `Anthill.SDK.Tools.ToolResult`, or alias — `ToolFailureClassTests.cs`
already demonstrates the alias pattern for precisely this collision.

- [x] Extract `ToolResult` from `Domain/Models.cs` → `Anthill.SDK/Tools/ToolResult.cs`
- [x] `global using Anthill.SDK.Tools;` in Core, Api and both test projects
- [x] Disambiguate the two files above; rewrite the 8 `Domain.ToolResult`
- [x] Extract `ITool` from `Tools/Tools.cs` → `Anthill.SDK/Tools/ITool.cs`
- [x] Add `IModuleContext.RegisterTool(ITool)` — the phase-0 deferral, finally typed, held open
      three phases rather than declared as `object`
- [x] `IToolKindExecutor` waits for 5c: it needs `ToolDefinition`, entangled with
      `ToolAuthorization` and `ToolInventory` — **done in 5c step 3, v3.8.15; the entanglement
      turned out to be three lines**

The forecast held exactly: of 151 `ToolResult` references, 138 resolved through the global using
untouched, 8 were rewritten, 5 were deliberately left alone as a different type, and exactly two
files needed disambiguation. This is the release that made the qualification-form survey a rule
rather than a lesson.

#### Phase 5c — SURVEYED: the "workspaces follow the tools" decision needs revisiting

The decision to move workspace lifecycle out with the tools was taken before this was measured.
Measured now, it does not hold up, and the plan should say so rather than route around it.

`Sandbox/` (381 LOC) and `Workspaces/` (1,556 LOC) import, between them:

| Import | What it is |
|---|---|
| `Anthill.Core.Domain` | `Mission` and `Task` — the entities the scheduler IS |
| `Anthill.Core.Memory` | `SqliteMemory` — workspace records are persisted colony state |
| `Anthill.Core.Tools` | `ToolRegistry` — dispatch |
| `Anthill.Core.Pheromones` | the learning signal |
| `Anthill.Core.Security` | path guarding |

Moving them to a module therefore requires `Mission` and `Task` in the SDK. That is not a
prerequisite to work through; it is the proposal collapsing. Those two types are what "the core is
scheduling, memory and coordination" MEANS — an SDK that declares them is the core, renamed.

**DECIDED (operator, this session): workspaces and sandbox stay in the core.** Every one of their
five imports is a core concern, and the alternative required `Mission` and `Task` in the SDK.

**Scope, and what the survey says it costs.** Splitting `Tools/Tools.cs` is structurally clean —
`ToolRegistry` is lines 18–179, and seven `ITool` implementations follow it (`SystemInfoTool`,
`DirectoryListTool`, `ReadTextFileTool`, `WriteTextFileTool`, `ShellCommandTool`, `WebSearchTool`,
`ApplyPatchTool`). But the implementations are NOT free-standing. They reference:

- **17 `AnthillRuntime` settings** — `EnableShellTool`, `EnableFileWriting`, `EnablePatchApplication`,
  `MaxFileReadChars`, `PatchAllowedSuffixes`, `WebSearchProvider`, `ScriptDir`, `BackupDir`, … Each
  is a capability gate or a limit, so none can simply be dropped.
- **`TextUtil`, `UrlSafety`, `Validation`** from `Anthill.Core.Common` — and all three read
  `AnthillRuntime` themselves (SSRF blocklists, patch suffix allow-lists, summary caps). Moving them
  is its own config-passing exercise, exactly like `HomelabOptions` was.
- **`Native.NativeKernel`** and **`ToolRegistry.ClassifyThrown`** — core.

So this is a `HomelabOptions`-shaped job, not a `ToolResult`-shaped one: a `ToolOptions` record, three
more `Common` helpers rebased onto it, then the move. Comparable in size to phase 4b, and it touches
the file-writing and patch-application gates — the ones that decide whether an agent may modify disk.

**Recommended sequencing when resumed:**

1. `ToolOptions` in the SDK carrying the 17 settings; `ToolRuntime.Configure` at the composition
   root, mirroring `HomelabRuntime`
2. `TextUtil`, `UrlSafety`, `Validation` → SDK — **SURVEYED, and it is not one job but two**

   Their settings split cleanly, and the split decides the design:

   | Setting | Form | Implication |
   |---|---|---|
   | `MaxResultSummaryChars`, `TokenEstimateCharsPerToken`, `ApprovalIdMaxChars`, `PatchIdMaxChars`, `SourceIdMaxChars` | `const` | immutable — pass as plain values or leave as SDK constants |
   | `WebSearchKeywords`, `SsrfBlockedHostnames`, `BlockedPathParts` | `static readonly HashSet` | the REFERENCE is readonly, the CONTENTS are not — an operator or test can add an SSRF block at runtime, so these need live reads like the tool gates |

   **Blast radius is very uneven.** `UrlSafety` and `Validation` have 4 consuming files each —
   comparable to the v3.8.11 change. `TextUtil` has **18**, and it is used well outside the tool
   layer, so it should move on its own, not bundled with the other two.

   Suggested order: `UrlSafety` + `Validation` first (small, and they carry the SSRF and path
   guards the tools actually need), then `TextUtil` separately.

   **`TextUtil` SURVEYED (v3.8.13 session), not yet moved.** Smaller than its consumer count implies.

   | Measure | Result |
   |---|---|
   | qualification forms | 119 bare (resolve through the existing global using — no edit), 2 `Common.TextUtil` (rewrite) |
   | consuming files | 18 in `src` — matching the figure above, same core-files convention — plus `JsonSafetyTests.cs` |
   | mutable settings | ONE: `WebSearchKeywords` |
   | const settings | `MaxResultSummaryChars`, `TokenEstimateCharsPerToken` → SDK constants, as the id caps were in v3.8.12 |
   | collisions | none; single declaration |

   `WebSearchKeywords` belongs on `IToolRuntimeOptions`, which already carries `WebSearchEnabled` —
   the same reasoning that put `BlockedPathParts` there rather than in a rival interface.

   The one thing genuinely wider than v3.8.12: 19 files touched instead of 8, and `Anthill.Api/ApiHost.cs`
   is among them, which neither earlier helper reached. Nothing about that changes the design; it
   changes how long the edit takes and how much a mistake costs.

   Note the trap this survey already avoided once: `SsrfBlockedHostSuffixes` appears in the tool
   layer's reads but does NOT exist under that name in `AnthillRuntime`. Resolve every setting name
   against the declaration before designing the contract around it.

   **CORRECTED WHEN EXECUTED (v3.8.12). The table above is incomplete and the trap note is wrong.**
   Both were written from the tool layer's reads rather than from the two files' own bodies.

   - `SsrfBlockedHostSuffixes` DOES exist — `AnthillRuntime.cs:96` — but as `static readonly string[]`,
     not a `HashSet` like `SsrfBlockedHostnames` on the line above it. The warning was right that
     something was off about the name and wrong about what. Matched by `EndsWith` and ordered, so it
     is an `IReadOnlyList<string>` on the contract while its sibling is an `IReadOnlySet<string>`.
   - The table omits `BlockedFileSuffixes` and `PatchAllowedSuffixes`, which `ValidateSafePatchPath`
     reads. Neither needed a new contract member: **`IToolRuntimeOptions` already declared both**, so
     `Validation` takes that interface whole and only `BlockedPathParts` was added to it. A second
     interface re-declaring them would have been two contracts for one setting, free to drift.
   - "4 consuming files each" held exactly, and it means CORE files. Add `SecurityTests.cs` for both
     and `HomelabFoundationTests.cs` for `UrlSafety`. The three `UrlSafety` hits inside
     `Anthill.Modules.Homelab` are XML doc comments, not code — the SDK-only boundary is intact.

   **The measurement that decided the shape.** Of 21 call sites, only two methods read mutable
   config at all: `UrlSafety.IsBlockedOutboundUrl` and `Validation.ValidateSafePatchPath`. Everything
   else — `DecodeSearchUrl`, `ExtractDomain`, `NormalizeUrlForDedupe`, `SourceIdFromUrl`,
   `IsLoopbackBindHost`, the id validators — is pure or const-only. The config surface is five
   settings, not the whole of both files.

   **DECIDED (operator, this session): optional options argument, live default.** The impure methods
   take a trailing optional argument; `null` resolves to a settable default that reads `AnthillRuntime`
   through. Instance types with constructor injection were the consistent-looking alternative and were
   rejected: they would have rewritten all 21 call sites and forced `Queen`, `SelfTest` and
   `PheromoneEngine` to carry options objects they have no other use for. All four projects already
   carry `global using Anthill.SDK.Common;`, so landing the types in that namespace changed zero call
   sites.

   **The hazard that design creates, and what closes it.** A settable default can be left unset. The
   SDK's built-in fallbacks are byte-identical to `AnthillRuntime`'s declared defaults, so an unset
   process is never MORE PERMISSIVE — it simply stops tracking later edits, and nothing fails until an
   operator or a test changes a setting the guard then ignores. That is the v3.8.11 wrong-green shape
   exactly. Closed with a `[ModuleInitializer]` in `Anthill.Core` rather than a composition-root call,
   because `SelfTest`, `PheromoneEngine`, `Queen.Views` and most of the test suite reach these helpers
   without building a colony. `SafetyPolicyTests` pins it: a host blocked AFTER first use changes the
   answer on the next call.
3. `IToolKindExecutor` + `ToolDefinition` → SDK — **DONE (v3.8.15)**

   The entanglement this plan flagged, measured: **three lines, all inside `Validate()`** —
   `ToolInventory.Implemented`, `ToolAuthorization.MissionAgentForbidden`, and `ToolKinds.Buildable`.
   The record's other 130 lines are pure. Every qualification form was already bare (60 references,
   six files, zero qualified forms, zero collisions), so the move itself cost no call-site edits.

   | Moved to `Anthill.SDK.Tools` | Stayed in `Anthill.Core.Tools` |
   |---|---|
   | `ToolDefinition`, `ToolKind`, `ToolKinds.Parse`, `IToolKindExecutor`, `UserDefinedTool` | `HttpToolKind`, `UserToolGrants`, `UserToolRegistrar`, `ToolRegistry`, `ToolAuthorization`, `ToolInventory` |

   **DECIDED (operator, this session): the three core-owned checks arrive through
   `IToolDefinitionPolicy`**, an optional argument on `Validate()` whose `null` resolves through
   `SafetyPolicy` — the v3.8.12 mechanism, reused rather than re-invented, and installed by the
   `[ModuleInitializer]` that was already there.

   The alternative was to split `Validate()`: shape in the SDK, reserved names in `UserToolRegistrar`
   (which is already "the single place that decides whether one is fit to register"). It needed no
   mirrored list, which is a real advantage, and it was rejected anyway: `UserDefinedToolTests`
   asserts `ADefinition_MayNotShadowABuiltIn` on the DEFINITION's own method, and retargeting a
   security test to accommodate a refactor is how a guard starts checking something easier than what
   it was written for.

   The cost of the chosen design is a mirror of the core's tables in the SDK, for the unconfigured
   case. Kept because the alternative default is an EMPTY reserved-name set — strictly more
   permissive — and closed by `ToolDefinitionPolicyTests`, which asserts the mirror equals the live
   tables and that the live policy reads them by reference rather than snapshotting.

   `ToolKinds.Buildable` is gone as a declared constant: it is now derived from the executors
   `UserToolRegistrar.Default()` constructs, so a kind cannot be declared buildable with no executor
   or built while every definition naming it is refused.

   **`IModuleContext` gained no `RegisterToolKind`** (operator decision). The contracts now permit
   one; the buffering and drain wait for something that actually ships a second kind.

4. The tool implementations → `Anthill.Modules.Tools`, registered through
   `IModuleContext.RegisterTool` (already wired in v3.8.10 — buffered by `ModuleHost`, drained by
   `ApiHost` into `Queen.Tools`) — **DONE (v3.8.16). Phase 5 ends here.**

   The survey below held. Three things it did not predict, all found by reading rather than by
   running, and all of the same shape — a change that compiles, boots, and gives a wrong answer:

   - **`Queen.Profile` is resolved in the constructor from `Tools.Names`**, and module tools arrive
     after it. Registering them would have left `Profile.ToolGrants` naming five tools while eleven
     were dispatchable, so `/status`, the runtime profile and every mission context would have
     described a colony less capable than the one running. Nothing would have failed.
     `Queen.AdoptModuleTools` now does the registration AND the re-resolve, so a composition root
     cannot do the first and forget the second.
   - **The SDK cannot name `HttpRequestException.`** `ToolRegistry.ClassifyThrown` matched on it, and
     moving that logic to the SDK would have emitted a `System.Net.Http` assembly reference —
     forbidden by `ModuleBoundaryTests` precisely because everything inherits what the SDK depends
     on. `ToolFailure.Classify` matches by type name and walks the base chain. The alternative was to
     relax the guard for a carve-out it cannot express.
   - **Registration gating had to move with the tools.** `Queen.BuildToolRegistry` held the first of
     the colony's two gates. Had `ToolsModule.Register` offered everything unconditionally and left
     the call-time re-check to catch it, the two gates would have silently become one and every
     existing test would still have passed.

   **And one the test suite caught that the survey did not.** `Queen.BuildToolRegistry` gated
   registration on the host's own `RuntimeOptions`; `ToolsModule` gates on `IToolRuntimeOptions`,
   and both production roots hand it the AMBIENT runtime. For a process with one colony those are
   the same answer. For two hosts in one process they are not — which is precisely ADR-001's exit
   gate, "two runtime instances can execute tests in the same process without configuration
   leakage", asserted in `RuntimeIsolationTests` through `Profile.HasTool("read_text_file")`.

   Left alone those tests would have gone GREEN, both hosts simply having no file tools at all. They
   now compose the way a multi-host root would have to, giving each host a gates view of its own
   options. The production roots are unchanged and remain correct for one colony per process; if a
   second host ever has to coexist, it needs its own `ToolsModule` instance rather than a shared one.

   The three guards were repaired as predicted, and two of them were redesigned rather than
   repointed: they now read two NAMED files — `Queen.cs` and `ToolsModule.cs` — because globbing the
   tree would let any `new ShellCommandTool(` in a test satisfy them.

   `Tools.cs` splits cleanly: `ToolRegistry` is lines 18–178, the implementations are 180–536. Core
   names none of the tool TYPES outside `Queen.BuildToolRegistry` — `Ants`, `SpecialistAnts` and
   `Queen.Views` dispatch by NAME through the registry — so the move is type-clean and every
   qualification form is bare.

   **Six move, not seven. `SystemInfoTool` stays** (operator decision). It reports
   `Native.NativeKernel.UsingNative`, `EnableParallelExecution`, `MaxParallelWorkers` and
   `EnableFtsMemory` — core introspection, not a capability gate, and moving it would mean an SDK
   contract whose only consumer is one tool's output dictionary. Keeping it also leaves the two
   source-scanning guards below a real anchor in `Queen.cs`.

   Four core dependencies to resolve, and they are not the ones the phase-5c survey listed:

   | Dependency | Used by | Resolution |
   |---|---|---|
   | `WorkspacePathGuard` | 5 of the 6 | reaches `MissionWorkspaceScope`, which stays in the core. **DECIDED: `IWorkspacePathGuard` in the SDK, injected through the module constructor** as `HomelabModule` takes `HomelabOptions` — not added to `IModuleContext`, which only one module would need |
   | `ToolRegistry.ClassifyThrown` | all 6, plus 3 tools that stay | `internal static`, already returns the SDK's `FailureClass`; needs an SDK home with the core delegating |
   | `MaxFileReadChars`, `MaxDirectoryItems`, `MaxWebResults`, `WebSearchTimeoutSeconds`, `WebSearchProvider` | 3 tools | all `const` → SDK constants, as the id caps were in v3.8.12 |
   | `ToolRuntime.Live` as the injected default | all 6 | **no new plumbing needed** — `SafetyPolicy.ToolOptions` is already installed with it by `SafetyPolicyBootstrap` |

   **Two things that would regress silently.** `Anthill.Cli/Program.cs` is a composition root that
   loads modules but NEVER drains `ContributedTools` — only `ApiHost.cs:138` does — so `anthill
   --mission` would lose every moved tool. And `Queen.Views` calls `apply_patch` by name at lines 87
   and 127: the operator approval pipeline would depend on a module-supplied tool being present.

   **Three source guards break, and two need redesign rather than a path edit**, because both encode
   "the composition root is `Queen.BuildToolRegistry`":
   - `CallSiteAuditTests.EveryImplementedTool_IsRegisteredByTheCompositionRoot` — regexes
     `new XxxTool(` out of `Queen.cs`
   - `ToolInventoryTests.TheInventory_MatchesWhatTheCompositionRootRegisters` — parses the
     `BuildToolRegistry` body AND scans `src/Anthill.Core/Tools/*.cs` for each `Name` literal
   - `ToolFailureClassTests` — `[InlineData("src/Anthill.Core/Tools/Tools.cs")]`, a path edit only

5. `ToolRegistry`, `ToolAuthorization`, `ToolInventory`, `UserToolGrants`, `UserToolRegistrar`,
   `HttpToolKind`, workspaces and sandbox all stay

**Original outline, superseded by the above:**

- [ ] Split `Tools/Tools.cs` (498 LOC) the way `TaskContracts.cs` was split in 5a: `ToolRegistry`
      stays (registration and dispatch are coordination); the shell/git/filesystem tool
      IMPLEMENTATIONS move to `Anthill.Modules.Tools.*` — *superseded: one module, not one per kind;
      see step 4 above*
- [ ] Same for the process-spawning parts of `WorkspaceTools.cs` and `CheckRunner.cs` — *not doing.
      `CheckRunner` is the tester ant's only execution surface and `WorkspaceTools` reads the
      mission workspace; both are on the coordination side of the line workspaces settled on*
- [x] `IToolKindExecutor` + `ToolDefinition` to the SDK, so a module can register a tool KIND and not
      just an instance — **v3.8.15**
- [x] **Workspaces and sandbox stay in the core.** A mission executes in a workspace; the Queen
      reconciles them at startup against what is on disk. That is coordination, and the survey says
      so — every one of its five imports is a core concern.

#### The rename check, for every phase from here

A file is only as movable as its most qualified reference, and `grep` for `using` will not find
them. Before moving type `T`, enumerate the FULL qualified strings, not the suffix:

```
grep -rno "[A-Za-z.]*\bT\b" --include="*.cs" src tests | sed 's/.*://' | sort | uniq -c
```

Then decide per form. In 5a, matching the suffix `Contracts.FailureClass` silently rewrote twelve
`Anthill.Core.Contracts.FailureClass` into a namespace that never existed, and an earlier blanket
strip ate `AntExecutionCatalog.Contracts.Keys` — a dictionary field that merely shares the name.

### Phase 5 (original scope note) — SUPERSEDED, kept for the two places it was wrong

Written before the tool layer was measured. Both errors are worth keeping visible, because both are
the kind a plan makes when it reasons from names instead of from imports.

- [x] `ITool` and `IToolKindExecutor` move to the SDK (already clean interfaces) — *right, but "clean"
      was an assumption: `ITool` was clean, `IToolKindExecutor` dragged `ToolDefinition` and three
      lines of core tables with it (5b, 5c step 3)*
- [ ] Split by kind into `Anthill.Modules.Tools.{Shell,Git,FileSystem,Http,Vision}` — **wrong.**
      Five projects for six tools, and `Http` is not a tool at all but a user-tool KIND that stays in
      the core because the SDK may not carry `System.Net.Http`. One `Anthill.Modules.Tools`.
- [ ] Core keeps only `ToolRegistry`, `ToolAuthorization`, `ToolDefinition`, `ToolInventory` —
      **`ToolDefinition` is wrong**; it reached the SDK in v3.8.15, because a module cannot declare a
      tool kind against a record it cannot see. The other three are right and unchanged.
- [ ] `UserToolRegistrar` and `WorkspaceTools` assessed individually against the default-to-module
      rule — done: both stay. The registrar decides what may register, and `WorkspaceTools` reads the
      mission workspace.

**Gate:** build + tests; run a mission that calls a shell tool and a git tool. *There is no git tool
and never was — the name came from the original outline rather than from `ToolInventory`.*

---

### Phase 6 — UI decoupling — **DONE (v3.8.17)**, with one item superseded on measurement

Surveyed before starting, which the plan had never done for this phase. The numbers reordered it:

| Item | Measured | Outcome |
|---|---|---|
| `src/Anthill.UI/` holds the assets | 5 files, 808 KB | small — done |
| Split `ApiHost.cs` by resource | 3,294 lines, **102 endpoints** | done — 529 lines + 6 partials |
| Review the three runners | 1,052 lines | done — and the answer was "they stay" |
| UI reads only read-only REST | **44 mutating endpoints** | **superseded — see below** |

- [x] `src/Anthill.UI/` holds the assets; `Anthill.Api` serves them. Still EMBEDDED, with each
      `LogicalName` pinned in the csproj — `LoadUiAsset` matches by resource-name SUFFIX, so a move
      that changed the generated names would have served a blank console with no build error.
      `UiAbsenceTests` asserts each asset is still found.
- [x] Audit `ApiHost.cs` and split by resource. `ApiHost` had been `public static partial` across
      eight files since the homelab moved, so this is where it was always going to divide:
      `Routes`, `Auth`, `Dashboard`, `Providers`, `Autonomy`, `Reports`. Pure movement — same class,
      same behaviour, no route re-registration.
- [x] `ColonyDirector`, `AutoApplyRunner`, `PatchVerifyRunner` reviewed. **They stay in the API, and
      the reason is a finding rather than a preference.** The plan's condition was "if they hold
      orchestration logic". They do not: every decision they make is delegated to a core type —
      `AutoApplyPolicy.Evaluate`, `AutonomyControl`, `ObjectiveLearning.EvaluateRetirement` — and
      none of the three declares a policy predicate of its own. What is left in the API is the LOOP
      and the I/O around it. Phases 1–5 had already moved the policy out before phase 6 got round to
      asking. Moving them anyway would also risk ADR-001's explicit prohibition: `RuntimeIsolationTests`
      asserts the Queen is a host's ONE mission authority, and a Director sitting in Core beside her
      is an invitation to become a second one.
- [ ] **UI reads only the SSE stream plus read-only REST — SUPERSEDED, and this is the third plan
      item to fall the same way.** There are 58 `GET` and 44 `POST/PUT/DELETE` endpoints, and the
      console calls the mutating ones to start a mission, approve a patch, change a setting, stop
      the Director. Read literally, the item removes the console's ability to do anything; it was
      written in the abstract before anyone counted, exactly like `{Shell,Git,FileSystem,Http,Vision}`
      and "Core keeps `ToolDefinition`".

      **DECIDED (operator, this session): it means no BUSINESS LOGIC in endpoints.** The console may
      POST; what it may not do is drive orchestration. Endpoints validate, delegate to Core, and
      return. The split above is what makes that checkable — projections now sit in
      `ApiHost.Reports.cs`, separate from the routes, so an endpoint that starts deciding things is
      visible rather than buried in a 3,294-line file.
- [ ] Nine `/events/json` pollers in `app.js` — deliberately NOT removed. They are the fallback the
      SSE stream was shipped in front of in v3.8.3, and replacing them is a console change, not a
      boundary one. Recorded here so their survival is a decision rather than an oversight.

**Gate:** build + tests; full dashboard walkthrough. Then boot the API with the UI assets absent —
it must still serve the API. This is success criterion "core runs without UI." *The absence half is
now a test rather than a manual step: `UiAbsenceTests` asserts a missing asset degrades to its
fallback instead of throwing, because a manual gate is one nobody performs twice.*

---

### Phase 7 — Cleanup — **DONE (v3.8.16 – v3.8.17)**

- [x] Delete `test/` (superseded by `tests/`) and the empty root `test.txt` — v3.8.16
- [x] **Delete `py.old/` — done (v3.8.17), on an operator decision.** 4.2 MB, reachable in git
      history. Six references had to move with it: the CI `py.old is immutable on pull requests`
      job (deleted — it existed so an AGENT could not edit archived history, which is a different
      act from the operator deliberately removing it, and the job could not tell them apart), the
      companion `No Python files outside py.old` check (KEPT, and simplified — the ban is on Python
      being active here, which is now a plain statement with no exception to carve out),
      `RegressionGuardTests.NoPython_*`, `PolicyScan`'s `python_outside_archive` rule,
      `AntRegistry`'s forbidden-path list, `README.md`, the PR template and the issue template.
- [x] Remove dead code and abstractions with a single implementation and no seam value — **surveyed
      across all 35 interfaces in `src`, by counting implementations rather than by reading names.**

      One removed: **`IHomelabEventSink`**. One member, one derived interface, no independent
      implementer — and phase 4b had already RECORDED it as deleted, on the correct reasoning that
      once events reach `IEventBus` the sink is only persistence and `IHomelabRepository` carries
      that. It was never actually deleted; it survived as a base interface. `RecordEvent` now lives
      on `IHomelabRepository` and the plan's claim is true.

      Six deliberately KEPT, and worth naming because they match the description exactly — one
      implementation each, no test fake: `IExecutionService`, `ILearningRecorder`,
      `IMissionCoordinator`, `IMissionEvaluator`, `IPlanningService`, `IResultAssembler`. These are
      ADR-001's Queen decomposition. Their value is not substitutability; it is that they are the
      written record of what the Queen was split into, and deleting them would collapse that back
      into an undocumented god object. "No seam value" is not the same as "one implementation", and
      this is the case that shows the difference.

      The SDK's single-implementation interfaces — `IEventLog`, `IPheromoneMemory`, `IModuleContext`,
      `IFieldCipher`, `IModelCapabilityProbe`, `IToolKindExecutor`, `IWorkspacePathGuard` — are the
      module boundary itself and are seam value by construction.
- [x] Collapse duplicate logic surfaced by the moves — none found. The moves were extractions rather
      than copies: the qualification-form survey that preceded each one is what kept a second copy
      from being written, so there was nothing to collapse. Recorded as a checked result rather than
      quietly dropped, because "we found none" and "we did not look" read identically in a plan.
- [x] Add an architecture test asserting `Anthill.Core` references no `Anthill.Modules.*` assembly —
      this is what keeps the refactor from eroding. **Pulled forward to v3.8.8**, and it was right to
      pull it forward: every phase up to then had verified the boundary by hand with a grep, which
      would have passed right up until someone added a using statement. `ModuleBoundaryTests` reads
      assembly metadata, so an unused project reference fails it too.
- [x] Write ADR-007 recording the module boundary — `docs/adr/ADR-007-module-boundary.md`, v3.8.16

**Gate:** build + tests; re-measure Core LOC against the 34,247 baseline. *24,973 at v3.8.16 — down
9,274 lines, 27%, with nothing deleted.*

---

## 4. Success criteria, made checkable

| Criterion | Test | Status at v3.8.17 |
|---|---|---|
| Smaller core | Core LOC materially below 34,247; report the delta | **MET** — 24,973, down 9,274 (27%), nothing deleted |
| Core runs without AI provider | API boots and accepts a mission with all providers disabled (Phase 2 gate) | **MET** — v3.8.5, with a test class for the case |
| Core runs without UI | API boots and serves requests with UI assets absent (Phase 6 gate) | **NOT PROVEN** — v3.8.17 claimed it on `UiAbsenceTests`, which only proves a null check (§7). v3.8.18 added `-p:AnthillNoUi=true`, which genuinely drops the assets, and a `no-ui-boot` CI job to build and boot without them. The job failed twice — the API stayed up but never answered `/health` — and was withdrawn rather than made non-blocking. **The build flag ships; the gate does not.** Claiming this met on a gate that is not running would be the same defect a third time |
| Cleaner dependency graph | Architecture test: Core references no module assembly (Phase 7) | **MET** — `ModuleBoundaryTests`, v3.8.8, pulled forward |
| Functionality preserved | Full suite green at every gate; no test deleted without a named replacement | **PARTIAL, corrected v3.8.18** — the second clause holds: no test was deleted in seventeen releases, and the three that were re-composed said so. The first does NOT: v3.8.17 was merged with CI runs #196 and #197 red, and only #198 was green. v3.8.15 was tagged after a green run; v3.8.16 was not built before its PR. The final tree is green, which is not the same claim |
| Easier feature development | A new integration is added as a module with zero Core edits | **PARTIAL, now measured (v3.8.18)** — `ZeroCoreEditModuleTests` builds the fixture. A module written against the SDK alone registers a tool the core has never heard of, is offered to models, and runs on the system-internal and control-plane paths, with no core edit. It is REFUSED to every mission agent, because `ToolAuthorization`'s role allowlists and execution contracts are closed lists compiled into the core. So: extensible for capability, not for permission |

## 5. Risks

- **`SqliteMemory` sprawl (high).** 177 public methods is a wide blast radius. Phase 3 exists
  specifically to contain it, and it is interface-only for that reason. *Contained rather than
  resolved: modules see two narrow views, but the class is still 177 methods and every in-core
  consumer still holds the concrete type.*
- **`ApiHost.cs` at 3,283 lines (medium, and rising).** Touched in phases 1, 2, 4, 5 and 6, and it
  grows with each extraction because it is where modules are composed. Split it early in Phase 6
  rather than late.
- **498 KB `app.js` (medium).** The SSE swap in Phase 1 lands in a very large unstructured file.
  Keep polling as a fallback until the stream is proven.
- **No sandbox build (medium).** Every gate depends on the maintainer running it locally. A phase
  that has not been built locally is not done.
- **Behavioral drift in `LogEvent` (low, but it is the keystone).** Persist-then-publish, never
  publish-then-persist, so a bus failure can never lose an event that used to be durable.

---

## 6. Rules the execution produced

Each of these cost a build cycle or a wrong-green test before it was written down. They lived in
`docs/HANDOFF.md`, which is explicitly disposable; they belong here, where the reasoning that
produced them is.

1. **Before moving type `T`, enumerate every FULL qualified string** — not the suffix:

   ```
   grep -rno "[A-Za-z.]*\bT\b" --include="*.cs" src tests | sed 's/.*://' | sort | uniq -c
   ```

   Then decide per form. In 5a, matching the suffix `Contracts.FailureClass` silently rewrote twelve
   `Anthill.Core.Contracts.FailureClass` into a namespace that never existed, and an earlier blanket
   strip ate `AntExecutionCatalog.Contracts.Keys` — a dictionary field that merely shares the name.
   Where a name exists twice, determine which one each file means.

2. **Before adding a MEMBER to a published interface, enumerate its implementers the same way**,
   including test fakes. Adding `BlockedPathParts` to `IToolRuntimeOptions` broke the build on a
   private `Gates` class inside `ToolRuntimeOptionsTests`.

3. **A file is only as movable as its most qualified reference.** Checking `using` statements is not
   a purity check — partial qualification resolves through the enclosing namespace and leaves no
   import to find.

4. **One release in the working tree at a time.** Do not start edits for N+1 until N is committed.

5. **A guard that cannot fail is not a guard.** Where a check depends on configuration, make the
   configurations DISAGREE; where it depends on a build, build it. Five of the six findings in §7
   passed because ambient and injected policy happened to agree in the test process, or because the
   assertion was cheaper than the claim written above it.

6. **Measure the seam; do not estimate it.** Every phase that came in smaller than feared did so
   because the coupling was counted rather than inferred from names: twenty `Common` imports in the
   homelab were two helpers, 151 `ToolResult` references were 13 edits, and `ToolDefinition`'s
   "entanglement with `ToolAuthorization` and `ToolInventory`" was three lines. Every phase that
   surprised us did so because something was assumed — `ModelRouter` naming three provider types,
   `SsrfBlockedHostSuffixes` being an array rather than a set.

If a session proposes something that contradicts this document, this document is probably right —
it is the record of what was measured rather than what was assumed.


---

## 7. The sign-off review, and what it found

v3.8.17 declared the refactor complete. An external review disagreed with the framing —
"implementation complete, acceptance incomplete" — and was right on all six of its findings. They are
recorded here rather than quietly fixed, because five of the six are the SAME defect wearing
different clothes: **a check that answers a question adjacent to the one being asked, and passes.**

| Finding | Verdict | Closed by |
|---|---|---|
| `UiAbsenceTests` is wrong-green — assets are `EmbeddedResource`, so they cannot be absent; the test asks for a fabricated name and watches a null check | valid, and it had been used to flip a success-criteria row to MET | **partly**. `-p:AnthillNoUi=true` ships and does drop every UI resource; the `no-ui-boot` job that would boot such a build failed twice and was withdrawn. `UiAbsenceTests` no longer claims the criterion, and the criterion is back to NOT PROVEN — see the note below |
| `SafetyPolicy` is publicly mutable process-global state | valid — any assembly referencing the SDK could clear the SSRF blocklist for the whole process | `Configure`/`Reset` are `internal`, visible only to `Anthill.Core` and the test projects |
| Injected tool policy is bypassed at execution: `ApplyPatchTool` held `_options` and called `ValidateSafePatchPath(filePath)` without it; `WorkspacePathGuard.IsBlockedPath` read `AnthillRuntime` | valid, and wider than reported — `WebSearchTool` had it too, on the SSRF blocklist | options threaded end to end; `ToolPolicyIsolationTests` makes ambient and injected policy DISAGREE, which is the only way to catch it |
| The two-host test delegates shell/web/patch/suffix/blocklist back to global state, so it tests profile isolation, not execution isolation | valid — `HostGates` said so in its own comment | `HostGates` holds per-host values; the guard is constructed with them |
| Extensibility is structurally coupled to Core — contributed tool names must exist in `ToolInventory` | valid | `ZeroCoreEditModuleTests` measures exactly where it stops: capability yes, permission no |
| "Full suite green at every gate" is not accurate — v3.8.17 merged over red #196 and #197 | valid | the criteria table now says so |

**The pattern is worth more than the fixes.** Four of these passed because the process they ran in
had ambient and injected policy agreeing, or because the assertion was cheaper than the claim above
it. The rule that follows is rule 6 in §6: *a guard that cannot fail is not a guard.* Where a check
depends on configuration, make the configurations disagree; where it depends on a build, build it.

**The no-UI gate is unfinished, and saying so is the point.** `-p:AnthillNoUi=true` works and is
worth having: it is the mechanism that makes the criterion testable at all. The CI job that would
exercise it failed twice — the API process stayed alive but never served `/health` on the port it was
given — and rather than mark it `continue-on-error` and call the criterion met, the job was withdrawn
and the row set back to NOT PROVEN. A gate that cannot fail the build is the wrong-green this section
exists to record; shipping one to close out a review about wrong-greens would be absurd. Finishing it
needs the job's own log, which is the next small piece of work.

**What is still open at close.** Three criteria are partial and stay partial:

- `SafetyPolicy` remains process-global. v3.8.18 fixed WHO may write it, and threaded the tool path
  so a host's tools enforce that host's rules — but `UrlSafety` and `Validation` still resolve
  through it when called with no argument. Genuinely host-scoped policy means an options object on
  every call site, and that is a change to the core's shape rather than a defect to patch.
- A module can add capability without a core edit, and cannot grant permission for it. Lifting that
  means either module-supplied authorization grants — the mechanism operator-defined tools already
  have — or moving role allowlists out of compiled tables. Both change how permission works, which is
  coordination, which is core. The boundary refusing is arguably it working.
