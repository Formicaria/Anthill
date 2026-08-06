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
