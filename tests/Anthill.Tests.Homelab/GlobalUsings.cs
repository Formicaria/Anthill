// v3.8.7 — AnthillTime and Json moved from Anthill.Core.Common to Anthill.SDK.Common, mirroring
// the change in Anthill.Core. This project had no GlobalUsings file before, because until now
// nothing it referenced had ever moved assemblies.
global using Anthill.SDK.Common;

// v3.8.7 — the action vocabulary (ActionLifecycle, RiskEngine, ChangeSetTransaction,
// RecoveryOrchestrator, RiskLevel) moved to Anthill.SDK.Actions. All five were fully pure —
// no core imports at all — and they are SHARED: shadow mode is in the core, the homelab is a
// module, and both speak this vocabulary. Shared pure vocabulary is exactly what the SDK is for.
global using Anthill.SDK.Actions;

// v3.8.9 — the task/tool contract vocabulary (FailureClass, FailureClassify, ToolDescriptor,
// ToolCatalog, TaskContract, ContractGate, Capability) moved to Anthill.SDK.Contracts. 196 lines,
// one System.Text.Json.Serialization import and nothing else — pure shared vocabulary, which is
// what the SDK is for. ToolResult depends on FailureClass, so this is what unblocks ITool.
global using Anthill.SDK.Contracts;
