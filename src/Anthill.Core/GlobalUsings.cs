// The domain entity is named Task (faithful to the Python model). Under implicit usings
// that clashes with System.Threading.Tasks.Task, so we bind the bare identifier `Task` to
// the domain type across Anthill.Core. The handful of places that need the threading type
// (the parallel mission executor) reference System.Threading.Tasks.Task fully qualified.
global using Task = Anthill.Core.Domain.Task;
// .NET 9 implicit usings pull in System.Threading.Tasks.*, causing ambiguity with the
// Anthill-domain types of the same name. Pin them globally so every file gets the right one.
global using TaskScheduler = Anthill.Core.Scheduling.TaskScheduler;
global using TaskStatus = Anthill.Core.Domain.TaskStatus;

// v3.8.4 — the reasoning protocol moved from Anthill.Core.Models to Anthill.SDK.Reasoning:
// ModelRequest/ModelResponse/ModelMessage, ModelCallOutcome, ModelCapabilities, ProviderCatalog,
// ModelCallScope, and IReasoningProvider.
//
// Global rather than an added `using` in each of the ~20 affected files. The types did not change,
// their members did not change, and no call site's MEANING changed — only which assembly declares
// them. Touching twenty files to say so would put real edits and mechanical ones in the same diff,
// and the review that matters here is "did anything move that shouldn't have", which a diff full of
// import churn actively hides.
global using Anthill.SDK.Reasoning;

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
