// v3.8.7 — AnthillTime and Json moved from Anthill.Core.Common to Anthill.SDK.Common, mirroring
// the change in Anthill.Core. This project had no GlobalUsings file before, because until now
// nothing it referenced had ever moved assemblies.
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
