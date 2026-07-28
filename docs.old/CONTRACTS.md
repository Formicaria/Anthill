# ANTHILL Task Contracts & Typed Capability Tools (v2.9.0)

NORTH_STAR V3-track Phase 2. Machine-readable contracts replace loose prompt tasks and
string-parsed tool results as the control-flow surface. Everything here lives in
`src/Anthill.Core/Contracts/TaskContracts.cs` and is exercised by `tests/Anthill.Tests/TaskContractTests.cs`.

## The admission gate

Every path out of the planner funnels through `ContractGate.Admit` (end of
`Planner.AssignDefaultWorkers`). Each planned task is projected to its `TaskContract` and
schema-validated; a task that fails validation **cannot enter the execution queue**, and every
rejection is written to stderr with its full error list — loud, never silent.

## TaskContract

Projection of a planner task (`TaskContract.FromTask`): id, title, objective, task_type
(`diagnose|change|verify|research|recover`), required_capabilities, side_effect_class
(`none|reversible|destructive`), risk_class (`low|medium|high|critical`), dependencies,
idempotency_key (task identity at this layer). Validation rejects: missing id/title/objective,
out-of-schema enum values, self-dependencies, and any task with **no declared capabilities**
(a task that cannot be permission-checked cannot run).

Unknown-ant projection fails toward caution: `destructive` + `critical` + zero capabilities →
rejected. A role the AntRegistry says is executable+enabled but the catalog does not know yet gets
a cautious fallback declaration (model.invoke only, reversible/high/manual-compensation) so a
newly enabled role is never silently un-plannable.

## ToolCatalog & capabilities

Permissions attach to **capabilities, not ant names** (`repo.read`, `repo.patch.propose`,
`network.http.public`, `proxmox.vm.start`, …). Each executable caste has a `ToolDescriptor`
declaring: required capabilities, side-effect class, risk class, idempotency, cancellation/timeout
support, and compensation behavior — every state-changing tool declares recovery.
`ToolCatalog.CanRun(ant, grantedCapabilities)` evaluates permission **before** execution; unknown
tools and partial grants refuse.

## ToolResult & the failure taxonomy

`ToolResult` carries `status` (`succeeded|failed_retryable|failed_permanent|cancelled`), a typed
`FailureClass` (validation, authorization, target rejection, transient provider, rate limit,
timeout, conflict, dependency, verification, unsafe state, compensation, internal defect),
warnings, and evidence. Retry decisions come from `FailureClassify.IsRetryable` — only transient
provider / rate-limit / timeout / conflict retry automatically; unknown fails toward NOT
retryable. Control flow never parses free-text errors.

## What arrives later (per NORTH_STAR)

Full JSON input/output schemas per tool, per-tool idempotency keys and compensation tokens, and
temporary per-mission capability grants deepen in V2.10.0 (sandboxed execution) and V2.13.0
(safe action engine). This release ships the contract surface, the admission gate, the capability
model, and the failure taxonomy — the foundation those phases build on.
