# Anthill.Modules

Everything that is not scheduling, memory, or coordination lives here.

## There is no `Anthill.Modules.csproj`

Deliberately. Modules are **one project per domain**, not one shared assembly:

```
src/Anthill.Modules/
  Anthill.Modules.Reasoning/     # Ollama, OpenAI-compatible, Anthropic   (Phase 2)
  Anthill.Modules.Homelab/       # homelab repository + integrations      (Phase 4)
  Anthill.Modules.Tools.Shell/                                            (Phase 5)
  Anthill.Modules.Tools.Git/                                              (Phase 5)
  Anthill.Modules.Tools.FileSystem/                                       (Phase 5)
```

A single shared `Anthill.Modules` assembly would defeat the point. Modules would compile against
each other's internals for free, the homelab module would drag the reasoning providers' HTTP
dependencies into any deployment that loaded either one, and "which module owns this?" would stop
having a checkable answer. Separate assemblies make the dependency graph enforceable by the
compiler rather than by discipline.

## Rules

1. A module references `Anthill.SDK` **only**. Never `Anthill.Core`, never another module.
2. `Anthill.Core` must never reference a module. Composition happens in `Anthill.Api`.
3. A module reaches the colony through `IModuleContext` — the event bus, the pheromone memory, its
   own scoped configuration. Nothing else.
4. `IAnthillModule.Register` does no I/O. Connect lazily; report failures as events.
5. When it is unclear whether something belongs in Core or in a module, it is a module. Core earns
   code by being required for scheduling, memory, or coordination.

See `docs/REFACTOR-PLAN.md` for the phase each module arrives in.
