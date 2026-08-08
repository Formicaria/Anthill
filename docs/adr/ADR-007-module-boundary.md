# ADR-007 — The Core/Modules boundary

**Status:** Accepted — phases 0 through 5 shipped against it (v3.8.3 … v3.8.16)
**Supersedes:** nothing. **Related:** ADR-001 (runtime composition), ADR-006 (agent harness)
**Record of execution:** `docs/archive/v3/REFACTOR-PLAN.md`

## Context

`Anthill.Core` was 34,247 lines across 35 top-level namespaces. It held the scheduler and the
Proxmox client, the mission lifecycle and the Ollama wire format, pheromone memory and a DuckDuckGo
HTML scraper. Nothing was wrong with any one of those; what was wrong was that they were the same
assembly, so every claim about what the colony required in order to run was unfalsifiable.

Two claims in particular were made repeatedly and were false. "The colony runs without an AI
provider" — it could not compile without one: `ModelRouter` named `OllamaClient`,
`OpenAiCompatibleClient` and `AnthropicClient` in two switch statements. "The homelab is optional" —
it was 6,549 lines of the core, and the core's own tests loaded it.

An assembly boundary is the only version of that claim a compiler can check.

## Decision

Four projects, and the arrows point one way.

```
Anthill.SDK        contracts and pure helpers. No I/O, no implementations, no dependency
                   beyond the BCL and Logging.Abstractions. Referenced by everything.
Anthill.Core       scheduling, memory, coordination: Queen, Objective→Mission→Task→Action,
                   the task queue, the event bus, worker management, pheromones, and the
                   tool REGISTRY, AUTHORIZATION and INVENTORY. References the SDK only.
Anthill.Modules.*  capability: reasoning providers, the homelab, the tools that act on the
                   machine. Reference the SDK. NEVER referenced by Core.
Anthill.Api        the composition root. References Core, the SDK and every module, and is
                   the only place in the process that names a module type.
```

**The rule, stated once.** If Core needs behaviour a module provides, Core declares an interface in
the SDK and a module implements it. When it is unclear which side something belongs on, it is a
module: Core earns code by being required for scheduling, memory or coordination.

**The distinction that did the most work** is between capability and coordination, and it is sharper
than it sounds. The tool layer is the case that proves it: `ShellCommandTool` is capability and left
the core; `ToolRegistry`, `ToolAuthorization` and `ToolInventory` are coordination and stayed, even
though all four were in the same file. Deciding WHICH tool runs and whether the caller may run it is
scheduling. Running it is not.

## Consequences

**The boundary is enforced by the compiler's own metadata, not by review.** `ModuleBoundaryTests`
(v3.8.8) reads assembly references and fails if Core names a module, if a module names anything of
ours but the SDK, or if the SDK acquires a database driver or an HTTP stack. A project reference
that is present but unused still fails — it is the reference that permits the coupling.

Every phase before that test verified the boundary by hand with a grep, and every one of those greps
would have passed right up until the moment someone added a using statement.

**The SDK's dependency list is the colony's dependency list.** Everything references the SDK, so
anything it takes on is inherited by every module. This has a real cost and the cost is correct: in
v3.8.16 `ToolFailure.Classify` could not name `HttpRequestException`, because doing so would emit a
`System.Net.Http` reference. It matches by type name instead. The alternative was to relax the guard
for a carve-out it cannot express.

**Modules cannot schedule.** `IModuleContext` exposes the event bus, two narrow views of the memory
(`IPheromoneMemory`, `IEventLog`), scoped configuration, a logger factory, and typed registration for
reasoning providers, capability probes and tools. There is no `Queen`, no `TaskScheduler`, no
`SqliteMemory`. Colony intelligence emerges from scheduling in the core; a module that could schedule
would be a second, competing source of it.

Registration is typed rather than a generic `RegisterService<T>`, deliberately. A generic surface is
a service locator: it makes what a module can contribute unbounded and unreadable, and forces the
core to search by type at the point of use.

**A capability's absence must be a typed refusal, never a throw.** Composing without the reasoning
module means model calls return `UnavailableProvider`'s refusal and missions still plan and dispatch.
Composing without the tools module means six tools return "not registered" — a `ValidationFailure` —
and missions still run. This is what makes "the colony runs without X" testable rather than
aspirational.

**The composition root grows.** `ApiHost.cs` went from 3,227 lines to 3,283 across the refactor while
the core shrank by 8,980. That is the expected shape and not a defect: every module extracted has to
be wired somewhere, and one file that names every module is the boundary working. Splitting it is
phase 6.

**The cost that is paid on every extraction.** A module cannot be handed a core type, so anything it
needs becomes an SDK contract: `IReasoningProviderFactory`, `IFieldCipher`, `IToolRuntimeOptions`,
`IWorkspacePathGuard`, `IToolDefinitionPolicy`. Each is a small amount of indirection bought with a
guarantee. The discipline that keeps it from becoming ceremony is that no contract is written until a
module actually needs it — phase 3 proposed seven store interfaces and shipped the two that had a
consumer.

## What was measured, and what it cost to assume

Recorded at length in `docs/archive/v3/REFACTOR-PLAN.md` §6. In short: every phase that came in smaller than
feared did so because the coupling was counted rather than inferred from names — twenty
`Anthill.Core.Common` imports in the homelab were two helpers; 151 `ToolResult` references were 13
edits; `ToolDefinition`'s "entanglement with `ToolAuthorization` and `ToolInventory`" was three lines.
Every phase that surprised us did so because something was assumed.

The rule that came out of it: a file is only as movable as its most qualified reference, and checking
`using` statements is not a purity check.
