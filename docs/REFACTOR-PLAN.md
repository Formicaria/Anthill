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

### Phase 5 — Shell / Git / Filesystem / Vision tools → modules

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
