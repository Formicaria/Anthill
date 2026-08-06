// Match Anthill.Core: bind the bare `Task` identifier to the domain entity so test code
// reads naturally (new Task { ... }) without colliding with System.Threading.Tasks.Task.
global using Task = Anthill.Core.Domain.Task;
// .NET 9 implicit usings expose System.Threading.Tasks.TaskScheduler and TaskStatus — pin
// the Anthill types globally so all test files resolve to the correct symbols.
global using TaskScheduler = Anthill.Core.Scheduling.TaskScheduler;
global using TaskStatus = Anthill.Core.Domain.TaskStatus;
// v3.8.4 — mirrors Anthill.Core: the reasoning protocol now lives in Anthill.SDK.Reasoning.
global using Anthill.SDK.Reasoning;
// v3.8.5 — the provider implementations (OllamaClient, OpenAiCompatibleClient, AnthropicClient,
// OllamaCapabilityCache) moved out of the core into their module. Their tests follow them.
global using Anthill.Modules.Reasoning;

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

// v3.8.9 — the task/tool contract vocabulary (FailureClass, FailureClassify, ToolDescriptor,
// ToolCatalog, TaskContract, ContractGate, Capability) moved to Anthill.SDK.Contracts. 196 lines,
// one System.Text.Json.Serialization import and nothing else — pure shared vocabulary, which is
// what the SDK is for. ToolResult depends on FailureClass, so this is what unblocks ITool.
global using Anthill.SDK.Contracts;
