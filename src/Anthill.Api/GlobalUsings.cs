// v3.8.4 — the reasoning protocol moved from Anthill.Core.Models to Anthill.SDK.Reasoning.
// ApiHost consumes these types directly (the provider settings surface, the "Test Connection"
// action, the model-fitness report), so it picks them up here for the same reason Anthill.Core
// does: the move is a change of declaring assembly, not of meaning, and it should not read as a
// twenty-file edit.
global using Anthill.SDK.Reasoning;

// v3.8.5 — the API is a composition root, so it is ALLOWED to name a module; the core is not.
// It registers the reasoning factory and capability probe at startup and warms the probe off the
// request path. If this import ever appears in Anthill.Core, the boundary has been broken.
global using Anthill.Modules.Reasoning;

// v3.8.6 — the module lifecycle: ModuleHost hands each module an IModuleContext, which is the only
// surface a module gets onto the colony.
global using Anthill.Core.Modules;
global using Anthill.Core.Events;

// v3.8.7 — AnthillTime and Json moved from Anthill.Core.Common to Anthill.SDK.Common.
// They were the only two Common helpers with no dependency on the core and no I/O, and the
// only two that Homelab and Integrations actually use (56 and 10 call sites) — so they are
// what has to move first for anything else to follow them out of the core.
global using Anthill.SDK.Common;

// v3.8.7 — the action vocabulary (ActionLifecycle, RiskEngine, ChangeSetTransaction,
// RecoveryOrchestrator, RiskLevel) moved to Anthill.SDK.Actions. All five were fully pure —
// no core imports at all — and they are SHARED: shadow mode is in the core, the homelab is a
// module, and both speak this vocabulary. Shared pure vocabulary is exactly what the SDK is for.
global using Anthill.SDK.Actions;

// v3.8.7 — the homelab left the core. The API is a composition root, so it may name the module;
// Anthill.Core may not, and its own tests never load it.
global using Anthill.Modules.Homelab;
#if MICROMOUND
global using Anthill.Modules.Micromound;
#endif
global using Anthill.Core.Security;

// v3.8.9 — Capability, FailureClass, FailureClassify, ToolDescriptor and ToolCatalog moved to
// Anthill.SDK.Contracts. TaskContract, ContractGate and Contracts.ToolResult stayed in the core:
// the first two take Domain.Task, and the third collides by name with the SDK's ToolResult, which
// call sites disambiguate against by namespace.
global using Anthill.SDK.Contracts;

// v3.8.10 — ToolResult and ITool moved to Anthill.SDK.Tools. ToolResult could only follow once
// v3.8.9 put FailureClass/FailureClassify in the SDK, since those are its only dependencies.
// Anthill.Core.Contracts still declares its OWN ToolResult; the five `Contracts.ToolResult`
// call sites are deliberately untouched.
global using Anthill.SDK.Tools;

// v3.8.16 — the six tools that act on the machine left the core, so the composition root names one
// more module. No type in Anthill.Modules.Tools collides with one in Anthill.Core.Tools: the six
// that moved are gone from the core, and everything that decides whether they RUN stayed.
global using Anthill.Modules.Tools;
