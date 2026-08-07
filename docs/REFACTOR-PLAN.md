# ANTHILL Core Refactor — Migration Plan

**Status:** proposed
**Baseline:** v3.8.2, `main`
**Goal:** a smaller, stable core that runs with no AI provider and no UI, while preserving public behavior.

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
`dashboard-grid.js` 37 KB, `dashboard-grid.css` 26 KB.

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
Anthill.Modules.*  — providers, homelab, integrations, shell, git, filesystem, vision,
                     analytics. Reference SDK. NEVER referenced by Core.
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

### Phase 0 — SDK scaffold (additive; nothing moves) — **DONE, pending local build**

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

**Gate:** `dotnet build Anthill.sln` succeeds; test counts unchanged. *Not yet run — no .NET SDK in
the assistant sandbox. Run locally before Phase 1.*

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

### Phase 1 — Event bus behind `LogEvent` — **DONE, pending local gate** (v3.8.3)

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

#### Phase 2a — contracts to the SDK — **DONE, pending local gate** (v3.8.4)

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

#### Phase 2b — implementations to the module — NEXT

- [ ] SDK: `IReasoningProviderFactory` — `CanServe(providerId)` + `Create(context)` — the inversion
      point, so `ModelRouter` stops naming provider types
- [ ] SDK: `IModelCapabilityProbe`, so Core can ask what a model supports without depending on the
      Ollama cache that answers
- [ ] Split `ModelRouter.cs`: routing and the circuit breaker stay; `OllamaClient` moves out
- [ ] Core: `UnavailableProvider` (from `PlaceholderClient`) as the null object when nothing is
      registered — a typed failure, never a throw
- [ ] Move to `Anthill.Modules.Reasoning`: the Ollama client, `ProviderClients.cs`,
      `ProviderWireFormat.cs`, `OllamaCapabilityCache.cs`, plus `ReasoningModule : IAnthillModule`
- [ ] Reconcile the three `OllamaCapabilityCache.Warm` call sites (`ApiHost` ×2, `Cli`)
- [ ] Keep in Core: `ModelRouter` (routing *policy* is a core scheduling concern),
      `ModelRoutingPolicy`, `ModelCircuitBreaker`, `ModelCallOutcome`, `ModelCallScope`,
      `ModelCapabilities`, `ModelProtocol`
- [ ] Core's `Router` becomes nullable-by-design; assert a mission can be planned and a task
      dispatched with **zero** providers registered
- [ ] Reconcile the 10 outside-`Models/` referencing files: `Planning/Planner.cs`,
      `Tools/ToolSchemaProjection.cs`, `Autonomy/Strategist.cs`, `Agents/Ants.cs`,
      `Agents/AntModelFitness.cs`, `Agents/ToolCallingLoop.cs`, `Memory/SqliteMemory.Providers.cs`,
      `Orchestration/Queen.cs`, `Orchestration/ExecutionService.cs`,
      `Orchestration/ResultAssembler.cs`
- [ ] Provider registration moves to `Anthill.Api` composition root

**Gate:** build + tests; **and** boot the API with every provider disabled — it must start, accept a
mission, and degrade gracefully rather than throw. This is success criterion "core runs without any
AI provider."

---

### Phase 3 — Segregate `SqliteMemory`

The unglamorous phase that makes phases 4–6 possible. Interfaces first; the class is not split yet.

- [ ] Carve role interfaces over the existing partials — `IPheromoneMemory`, `IEventLog`,
      `IMissionStore`, `IWorkerStore`, `IWorkspaceStore`, `ISkillStore`, `IJobStore` — and have
      `SqliteMemory` implement all of them. Zero behavior change; purely additive.
- [ ] Retarget consumers to the narrowest interface they need instead of the concrete class
- [ ] Module-only partials (`Providers`, `Shadow`, `RepositoryIndex`, `FaultInjection`,
      `Workspaces`) get their own store interfaces, so later extraction does not drag `SqliteMemory`
      into a module
- [ ] `IModuleContext` exposes only `IEventBus` + the store interfaces a module legitimately needs —
      never `SqliteMemory` itself

**Gate:** build + tests. Verify by inspection that no module-facing type names `SqliteMemory`.

---

### Phase 4 — Homelab + Integrations → module

Measured, not estimated: `Homelab/` is 4,259 lines across 19 files and `Integrations/` is 2,290
across 13 — **6,549 lines**, plus 1,441 in `Anthill.Api/Homelab/`. Split in two.

#### Phase 4a — prerequisites — **DONE, pending local gate** (v3.8.7)

- [x] `AnthillTime.cs` and `Json.cs` → `Anthill.SDK/Common/`. The survey's useful finding: Homelab
      and Integrations import `Anthill.Core.Common` twenty times but use only these two helpers
      (56 and 10 call sites), and both are dependency-free and I/O-free. The rest of `Common` stays.
- [x] `HomelabRepository.RecordEvent` persists **then** publishes to `IEventBus`, gated on rows
      actually written because the insert is `OR IGNORE`
- [x] Homelab event types prefixed `homelab_` on the colony stream; original type kept in metadata
- [x] Wired at the API composition root, to the same bus the mission log publishes to

**Gate:** `dotnet build Anthill.sln && dotnet test`, both suites.

#### Phase 4b — the move itself — NEXT

Remaining coupling to resolve first, measured from the imports:

| Needs | Where it goes |
|---|---|
| `Health/` (272 LOC) | follows homelab into the module — it *is* homelab health |
| `SafeAction/` (333 LOC) | assess: approval is coordination, so it may be core |
| `Security` (1 import) | credential handling — likely an SDK contract |
| `AnthillRuntime` (3 imports) | pass values in, as `ReasoningProviderContext` does |
| `SqliteMemory` (3 uses) | narrow interfaces, as phase 3 did |

- [ ] SDK contracts first: `IHomelabRepository`, `IIntegrationDefinition`, `IInventoryProvider`,
      `IHomelabActionRunner`, `IHomelabTargetGuard`
- [ ] `IHomelabEventSink` deleted — 4a made it redundant as an announcement path; it is now only
      persistence, and `IHomelabRepository` already carries that
- [ ] Move `Homelab/**` and `Integrations/**` to `Anthill.Modules.Homelab`
- [ ] Reconcile `Incidents/IncidentManager.cs`, `Shadow/LiveIncidentObserver.cs`, `ApiHost.cs`
- [ ] `Anthill.Api/Homelab/*` endpoints register through the module

- [ ] SDK contracts first: `IHomelabRepository`, `IIntegrationDefinition`, `IInventoryProvider`,
      `IHomelabActionRunner`, `IHomelabTargetGuard` (several already exist in Core — move the
      declarations, keep the shapes)
- [ ] `IHomelabEventSink` is deleted in favor of `IEventBus` — it was a single-purpose bus
- [ ] Move `Homelab/**` and `Integrations/**` to `Anthill.Modules.Homelab`
- [ ] Reconcile the five outside references: `Health/HealthCheckRunner.cs`, `Health/HealthModels.cs`,
      `Incidents/IncidentManager.cs`, `Shadow/LiveIncidentObserver.cs`, `Anthill.Api/ApiHost.cs`
- [ ] Decide `Health/`, `Incidents/`, `Inventory/`, `Power/`, `Backups/`: if they only serve homelab,
      they follow it into the module (default-to-module rule)
- [ ] `Anthill.Api/Homelab/*` endpoints register through the module, not directly

**Gate:** build + `Anthill.Tests.Homelab` in full; exercise the homelab dashboard manually.

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

#### Phase 5b — `ToolResult` + `ITool` to the SDK — SURVEYED, ready to execute

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

- [ ] Extract `ToolResult` from `Domain/Models.cs` → `Anthill.SDK/Tools/ToolResult.cs`
- [ ] `global using Anthill.SDK.Tools;` in Core, Api and both test projects
- [ ] Disambiguate the two files above; rewrite the 8 `Domain.ToolResult`
- [ ] Extract `ITool` from `Tools/Tools.cs` → `Anthill.SDK/Tools/ITool.cs`
- [ ] Add `IModuleContext.RegisterTool(ITool)` — the phase-0 deferral, finally typed
- [ ] `IToolKindExecutor` waits for 5c: it needs `ToolDefinition`, entangled with
      `ToolAuthorization` and `ToolInventory`

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

4. The seven implementations → `Anthill.Modules.Tools.*`, registered through
   `IModuleContext.RegisterTool` (already wired in v3.8.10 — buffered by `ModuleHost`, drained by
   `ApiHost` into `Queen.Tools`)
5. `ToolRegistry`, `ToolAuthorization`, `ToolInventory`, workspaces and sandbox all stay

**Original outline, superseded by the above:**

- [ ] Split `Tools/Tools.cs` (498 LOC) the way `TaskContracts.cs` was split in 5a: `ToolRegistry`
      stays (registration and dispatch are coordination); the shell/git/filesystem tool
      IMPLEMENTATIONS move to `Anthill.Modules.Tools.*`
- [ ] Same for the process-spawning parts of `WorkspaceTools.cs` and `CheckRunner.cs`
- [ ] `IToolKindExecutor` + `ToolDefinition` to the SDK, so a module can register a tool KIND and not
      just an instance
- [ ] **Workspaces and sandbox stay in the core.** A mission executes in a workspace; the Queen
      reconciles them at startup against what is on disk. That is coordination, and the survey says
      so — every one of its five imports is a core concern.

The v3.8.10 plumbing already supports the good half: `IModuleContext.RegisterTool(ITool)` exists,
`ModuleHost` buffers contributions, and `ApiHost` drains them into `Queen.Tools`. A shell-tool
module needs no further wiring.

#### The rename check, for every phase from here

A file is only as movable as its most qualified reference, and `grep` for `using` will not find
them. Before moving type `T`, enumerate the FULL qualified strings, not the suffix:

```
grep -rno "[A-Za-z.]*\bT\b" --include="*.cs" src tests | sed 's/.*://' | sort | uniq -c
```

Then decide per form. In 5a, matching the suffix `Contracts.FailureClass` silently rewrote twelve
`Anthill.Core.Contracts.FailureClass` into a namespace that never existed, and an earlier blanket
strip ate `AntExecutionCatalog.Contracts.Keys` — a dictionary field that merely shares the name.

### Phase 5 (original scope note) — Shell / Git / Filesystem / Vision tools → modules

- [ ] `ITool` and `IToolKindExecutor` move to the SDK (already clean interfaces)
- [ ] Split by kind into `Anthill.Modules.Tools.{Shell,Git,FileSystem,Http,Vision}`
- [ ] Core keeps only `ToolRegistry`, `ToolAuthorization`, `ToolDefinition`, `ToolInventory` —
      registration, authorization, and dispatch are coordination concerns
- [ ] `UserToolRegistrar` and `WorkspaceTools` assessed individually against the default-to-module rule

**Gate:** build + tests; run a mission that calls a shell tool and a git tool.

---

### Phase 6 — UI decoupling

- [ ] `src/Anthill.UI/` holds the assets; `Anthill.Api` serves them
- [ ] UI reads **only** the SSE stream plus read-only REST queries — no endpoint that mutates
      colony state on the UI's behalf
- [ ] Audit `ApiHost.cs` (3,227 lines) and split by resource; anything driving architecture from the
      UI side is a bug to be fixed here
- [ ] `ColonyDirector`, `AutoApplyRunner`, `PatchVerifyRunner` reviewed — if they hold orchestration
      logic, it belongs in Core, not the API host

**Gate:** build + tests; full dashboard walkthrough. Then boot the API with the UI assets absent —
it must still serve the API. This is success criterion "core runs without UI."

---

### Phase 7 — Cleanup

- [ ] Delete `py.old/`, `test/` (superseded by `tests/`), root `test.txt`
- [ ] Remove dead code and abstractions with a single implementation and no seam value
- [ ] Collapse duplicate logic surfaced by the moves
- [ ] Add an architecture test asserting `Anthill.Core` references no `Anthill.Modules.*` assembly —
      this is what keeps the refactor from eroding
- [ ] Write ADR-007 recording the module boundary

**Gate:** build + tests; re-measure Core LOC against the 34,247 baseline.

---

## 4. Success criteria, made checkable

| Criterion | Test |
|---|---|
| Smaller core | Core LOC materially below 34,247; report the delta |
| Core runs without AI provider | API boots and accepts a mission with all providers disabled (Phase 2 gate) |
| Core runs without UI | API boots and serves requests with UI assets absent (Phase 6 gate) |
| Cleaner dependency graph | Architecture test: Core references no module assembly (Phase 7) |
| Functionality preserved | Full suite green at every gate; no test deleted without a named replacement |
| Easier feature development | A new integration is added as a module with zero Core edits |

## 5. Risks

- **`SqliteMemory` sprawl (high).** 177 public methods is a wide blast radius. Phase 3 exists
  specifically to contain it, and it is interface-only for that reason.
- **`ApiHost.cs` at 3,227 lines (medium).** Touched in phases 1, 2, 4, and 6. Split it early in
  Phase 6 rather than late.
- **498 KB `app.js` (medium).** The SSE swap in Phase 1 lands in a very large unstructured file.
  Keep polling as a fallback until the stream is proven.
- **No sandbox build (medium).** Every gate depends on the maintainer running it locally. A phase
  that has not been built locally is not done.
- **Behavioral drift in `LogEvent` (low, but it is the keystone).** Persist-then-publish, never
  publish-then-persist, so a bus failure can never lose an event that used to be durable.
