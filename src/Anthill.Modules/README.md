# Anthill.Modules

Everything that is not scheduling, memory, or coordination lives here.

## There is no `Anthill.Modules.csproj`

Deliberately. Modules are **one project per domain**, not one shared assembly:

```
src/Anthill.Modules/
  Anthill.Modules.Reasoning/     # Ollama, OpenAI-compatible, Anthropic   (Phase 2, v3.8.5)
  Anthill.Modules.Homelab/       # homelab repository + integrations      (Phase 4, v3.8.7)
  Anthill.Modules.Tools/         # the file, shell, web and patch tools   (Phase 5c step 4)
```

The tool split was originally drawn as `Anthill.Modules.Tools.{Shell,Git,FileSystem,Http,Vision}`.
Measuring the layer corrected that: there is no git tool and never was, `Http` is a user-tool KIND
whose executor stays in the core, and the whole set is six classes in one file. One project, not
five — "one project per domain" means per domain, and these are one.

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

Rules 1 and 2 stopped depending on discipline in v3.8.8: `ModuleBoundaryTests` reads assembly
references and fails if the core names a module, if a module names anything of ours but the SDK, or
if the SDK acquires a database driver or an HTTP stack. A project reference that is present but
unused still fails — it is the reference that permits the coupling.

See `docs/REFACTOR-PLAN.md` for the phase each module arrives in.
