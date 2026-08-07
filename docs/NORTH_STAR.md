# ANTHILL V3 NORTH STAR

## Colony Execution Infrastructure

**Baseline:** v2.26.0 · **Shipping release: v3.8.12** (phase 5c step 2, first half: the SSRF and patch-path guards move to `Anthill.SDK.Common` behind live-reading options, with every one of their 21 call sites unchanged; preceded by v3.8.11) (phase 5c step 1: the tool capability gates move behind a live-reading contract, preserving the colony's two-level gating; preceded by v3.8.10) (phase 5b: the tool contract joins the SDK and modules can finally contribute tools; preceded by v3.8.9) (phase 5a: the shared half of the contract vocabulary joins the SDK, with the Task-coupled half deliberately left in the core; preceded by v3.8.8) (the Core/Modules boundary is enforced by assembly-reference tests rather than by review; preceded by v3.8.7, the homelab leaving the core) (phase 4a: `AnthillTime` and `Json` move to `Anthill.SDK.Common` — the only two Common helpers Homelab and Integrations actually use — and homelab events reach the colony's live stream for the first time; see `docs/REFACTOR-PLAN.md`; preceded by v3.8.6) (the module lifecycle gets a caller: `ModuleHost` loads modules through `IModuleContext`, and `SqliteMemory` implements the SDK's `IPheromoneMemory` and `IEventLog` so a module holds two narrow views rather than the whole store — see `docs/REFACTOR-PLAN.md`; preceded by v3.8.5) (the colony runs without AI: provider construction inverted behind `IReasoningProviderFactory`, implementations extracted to `Anthill.Modules.Reasoning`, and nothing in `Anthill.Core` naming a provider type — see `docs/REFACTOR-PLAN.md`; preceded by v3.8.4) (the reasoning protocol and `IReasoningProvider` move to `Anthill.SDK.Reasoning`, so the provider contract no longer lives in the core that is required to run without providers — see `docs/REFACTOR-PLAN.md`; preceded by v3.8.3) (the Core/Modules refactor begins: `Anthill.SDK` as a contracts-only project, and a live event bus retrofitted behind `SqliteMemory.LogEvent` without touching one of its ~85 call sites — see `docs/REFACTOR-PLAN.md`; preceded by v3.8.2) (the agent harness: a typed provider substrate, a bounded tool-calling loop, operator-defined tools, and contracts that declare what they need from a model — v3.3.0 carried the provider substrate, v3.4.0 the tool framework, v3.4.1 user-defined tools; v3.2.1 was the last dashboard release)
**Target:** V4 Autonomous Software Engineering Colony
**Status:** Canonical (adopted at the V2 closeout; V2 documents archived at `docs/archive/v2/`)
**Document version:** 1.0

---

## 1. Purpose

V2 proved that ANTHILL can hold missions, plan work, route tasks, store history, propose changes, verify outcomes, manage approvals, observe homelab infrastructure, and enforce increasingly serious safety boundaries.

V3 will not be another feature-expansion cycle. V3 will turn those capabilities into one coherent execution platform.

The V3 objective is to build the colony operating system: a durable, typed, isolated, observable, restart-safe framework in which every ant performs its intended job through the same mission protocol. By the end of V3, ANTHILL should be able to complete medium-complexity software-engineering missions in a disposable workspace, coordinate multiple ants, test and repair its own work, produce a verified change set, explain every decision, and wait for an operator to retain or publish the result.

V4 will use that foundation to pursue broader autonomous software execution comparable in workflow quality to Codex or Claude Code. V3 must make that possible without pretending the framework is complete before it is proven.

## 2. V3 Mission

ANTHILL V3 exists to make colony execution dependable.

A V3 mission must be able to:

1. Accept a directive and resolve its constraints.
2. Inspect the repository and environment before planning changes.
3. Produce a small, explicit, contract-valid task graph.
4. Create an isolated mission workspace.
5. Assign work only to ants with proven handlers, contracts, tools, and budgets.
6. Move typed artifacts and evidence between ants instead of relying on prose transcripts.
7. Execute bounded agent loops with cancellation, retries, and durable attempts.
8. Verify the actual result through deterministic checks and independent review.
9. Diagnose failures and perform focused repair attempts.
10. Produce a final operator-facing result, change set, evidence bundle, and risk summary.
11. Retain nothing without the required approval and verification.
12. Archive only lessons supported by verified outcomes.
13. Resume or explain every interrupted mission after restart.

## 3. V3 End State

At the end of V3, ANTHILL is not yet an unrestricted autonomous developer. It is a qualified software-execution platform with the following properties:

- Every mission agent implements one versioned execution interface.
- Every task enters through one contract and capability admission gate.
- Every code-changing mission runs in a disposable or recoverable workspace.
- Every model call and tool call returns a typed result.
- Every task produces immutable artifacts, evidence, or an explicit typed failure.
- Every handoff is bounded, authorized, deduplicated, and traceable.
- Every repair is budgeted and tied to a concrete failed check.
- Every positive mission outcome requires independent verification.
- Every retained change has provenance, verification evidence, and rollback information.
- Every ant is either active for its intended purpose or explicitly classified as control-plane or deterministic service infrastructure.
- No feature is considered complete merely because its class, schema, or tests exist; it must have a production call site and an end-to-end qualification test.

## 4. What V3 Is Not

V3 is not:

- A new homelab feature expansion.
- A push toward unrestricted infrastructure control.
- A visual redesign project.
- A reason to add more ants before the existing roster is activated.
- A plugin marketplace.
- A distributed multi-node colony, although the worker interface must not prevent one later.
- Permission for agents to write directly to the active ANTHILL installation.
- Permission for model confidence to substitute for deterministic evidence.
- A release-number race.

## 5. Permanent V3 Doctrine

1. **Framework before intelligence.** A better model cannot repair an undefined execution protocol.
2. **One lifecycle authority.** Mission, task, action, workspace, artifact, and verification states each have one canonical owner.
3. **No call site, no feature.** Declarations, schemas, and tests without production wiring are incomplete.
4. **Typed boundaries end to end.** No control-flow decision may depend on string prefixes, prose parsing, or implied success.
5. **Artifacts over transcripts.** Ants exchange versioned artifacts and evidence references, not unbounded chat history.
6. **Deterministic gates around stochastic reasoning.** Models may propose, classify, summarize, and diagnose; policy, authorization, execution, and verification remain deterministic.
7. **Capabilities belong to tasks and tools.** Ant identity never grants hidden authority.
8. **All writes occur in a mission workspace.** The active checkout is never an agent scratchpad.
9. **Independent verification is mandatory.** The ant that creates a change cannot be the sole authority that declares it correct.
10. **Repair is bounded.** Every repair attempt has a reason, budget, scope, and stop condition.
11. **Learning follows verified outcomes only.** Observation may create a candidate; only proven use earns standing.
12. **Every run must be explainable and replayable.** Configuration, environment, tools, models, artifacts, decisions, and evidence are recorded.
13. **Activation is earned per ant.** A role graduates through explicit maturity gates; a global switch cannot create capability.
14. **No hidden fallback success.** Degraded paths are visible, typed, and never silently promoted.
15. **A release is evidence, not a version number.** Each phase closes only when its measured gates pass.

## 6. Target Architecture

### 6.1 Control Plane

The control plane decides what work exists and what authority it receives.

- **Queen:** mission authority, lifecycle coordination, final outcome publication.
- **Director:** objective scheduling and long-running autonomy policy.
- **Planner:** task-graph creation from live roster and workspace capability data.
- **Constraint service:** mission boundaries, risk ceilings, no-write/read-only rules, and policy interpretation.

The Queen must stop owning the implementation details of planning, execution, evaluation, learning, and presentation. V3 decomposes those responsibilities behind explicit interfaces while preserving one orchestration authority.

### 6.2 Execution Plane

The execution plane owns durable work.

- Mission queue and task claims.
- Worker identity, leases, heartbeats, attempts, and reclamation.
- Cancellation and deadline propagation.
- Mission workspace creation, checkpointing, recovery, and cleanup.
- Resource budgets and concurrency limits.
- Idempotency for all side effects.

### 6.3 Agent Plane

Every mission agent runs through the same typed protocol:

`Prepare -> Execute -> Produce Artifacts -> Request Handoff -> Report Metrics -> Stop`

The protocol is shared by Researcher, Web, File, Coder, Builder, Verifier, UI Cartographer, Tester, Soldier, Medic, Archivist, and Scribe. Specialization is expressed through contracts, tools, artifact types, and task types, not through separate execution frameworks.

### 6.4 Tool and Capability Plane

Every tool declares:

- Versioned input and output schemas.
- Required capabilities.
- Side-effect and risk class.
- Timeout and cancellation behavior.
- Idempotency behavior.
- Evidence produced.
- Compensation or rollback behavior.

No agent receives unrestricted shell or file-write authority. Code changes flow through scoped workspace tools and immutable change sets.

### 6.5 Artifact and Evidence Plane

The colony communicates through durable records such as:

- Repository map.
- File set.
- UI map.
- Change plan.
- Patch set.
- Test report.
- Security review.
- Failure diagnosis.
- Verification bundle.
- Operator summary.
- Release notes.
- Memory candidate.

Every artifact records schema version, producer, mission, task, workspace, source artifact IDs, content hash, timestamp, and visibility classification.

### 6.6 Verification and Recovery Plane

Verification is layered:

1. Contract and admission verification.
2. Tool execution verification.
3. Build/test/static verification.
4. Security and policy verification.
5. Deliverable verification.
6. Final mission evaluation.

A failure creates evidence. The Medic may diagnose that evidence and propose a focused repair. The repair returns to the Coder or another authorized role, then repeats the exact failed verification. Repair cannot rewrite the mission indefinitely.

### 6.7 Memory and Learning Plane

The Archivist stores:

- Verified lessons.
- Negative lessons from proven failure.
- Deprecated routes.
- Skill candidates with provenance.
- Operator rules.

Memory is not authority. It influences planning only through evaluated, environment-scoped skills and explicit policy-approved signals.

### 6.8 Operator Plane

The operator must be able to see:

- Current mission and task state.
- Workspace and branch identity.
- Active ants and why each was selected.
- Input and output artifacts.
- Handoffs and rejections.
- Model and tool calls.
- Verification requirements and current evidence.
- Repair attempts and remaining budgets.
- Pending approvals.
- Exact final change set and rollback path.

## 7. Canonical Colony Workflow

### Stage 1 - Intake and Constraints

The Queen creates the mission. The Constraint service resolves read-only, no-patch, language, path, risk, and approval boundaries. The mission receives an immutable `MissionContext`.

### Stage 2 - Reconnaissance

Researcher, File, Web, and UI Cartographer gather only the context required for the directive. They publish repository maps, file sets, source records, and UI maps.

### Stage 3 - Planning and Admission

The Planner creates a task graph against the live runtime roster and the workspace capability manifest. Every task declares inputs, outputs, capabilities, risk, success criteria, verification plan, dependencies, timeout, retry policy, and repair policy. The admission gate either accepts the complete graph or returns a structured rejection.

### Stage 4 - Workspace Preparation

The Workspace Manager creates a disposable worktree or clone, records the base revision, detects project capabilities, and exposes scoped read/change/check tools.

### Stage 5 - Execution Waves

Ants consume artifact references, perform bounded work, and publish new artifacts. Dynamic handoffs enter through the same authorization and contract gates as planned tasks.

### Stage 6 - Verification

Tester runs declared checks. Soldier evaluates policy and security risk. Verifier evaluates result completeness and evidence. Deliverable verification confirms that the requested change actually exists.

### Stage 7 - Repair

Medic classifies a failed check, identifies the smallest repair surface, and creates a bounded repair recommendation. The Planner or repair controller admits a focused task. The failed check is rerun.

### Stage 8 - Assembly

Builder produces the final result from verified artifacts. Scribe prepares release notes or documentation only after the change set is verified.

### Stage 9 - Retention

The Queen presents the change set, checks, evidence, risk summary, and rollback path. The operator approves retention, export, commit, or rejection. Agents never push directly to main.

### Stage 10 - Archive and Learning

Archivist records verified lessons and negative evidence. Quartermaster records resource and throughput data and adjusts future budgets within operator-defined ceilings.

## 8. Ant Activation Doctrine

Every mission agent moves through six maturity levels:

| Level | Name | Meaning |
|---|---|---|
| 0 | Scaffold | Display identity only; cannot receive work. |
| 1 | Implemented | Runtime handler exists but is not planner-visible. |
| 2 | Contracted | Versioned contract, tools, artifacts, limits, and tests exist. |
| 3 | Shadow | Receives mirrored tasks and records results without affecting mission flow. |
| 4 | Supervised | May affect missions with explicit operator or phase approval. |
| 5 | Qualified | Planner-eligible by default within its capability and risk ceiling. |

An ant may graduate only when all of the following exist:

- Runtime handler.
- Versioned execution contract.
- Typed input/output schemas.
- Capability and tool enforcement.
- Artifact and evidence definitions.
- Handoff policy.
- Budget and stop conditions.
- Unit, integration, fault, and end-to-end tests.
- Runtime metrics and operator visibility.
- Shadow comparison data.
- Rollback or safe no-side-effect proof.
- Explicit activation record.

A global specialist flag may narrow availability, but it may never substitute for these gates.

## 9. Role Intent at the End of V3

| Role | V3 responsibility | Required V3 change |
|---|---|---|
| Researcher | Repository and mission-context analysis | Migrate to universal execution contract and artifact outputs. |
| File | Precise workspace discovery and file reading | Produce versioned file-set artifacts and read provenance. |
| Web | Public-source research | Typed source records, authority scoring, freshness, and citation evidence. |
| UI Cartographer | Route, component, style, and UI-impact mapping | Activate as the required reconnaissance step for UI missions. |
| Coder | Iterative scoped changes in mission workspace | Replace proposal-only flow with workspace change sets while retaining approval gates. |
| Builder | Assemble verified mission result | Consume artifacts rather than raw task prose. |
| Verifier | Independent evidence and deliverable evaluation | Become verification orchestrator, never a model-only pass. |
| Tester | Run declared allowlisted checks | Activate with deterministic evidence and check adapters. |
| Soldier | Policy, security, scope, and patch-risk review | Activate as a blocking deterministic/model-assisted review layer. |
| Medic | Failure diagnosis and focused repair routing | Activate only after repair budgets and retry evidence are durable. |
| Archivist | Verified memory and skill-candidate extraction | Activate after artifact provenance and canonical outcomes are universal. |
| Scribe | Release notes, operator summaries, and docs change sets | Activate only after verified diffs; docs paths remain scoped. |
| Quartermaster | Resource pressure, queue depth, and concurrency control | Remain deterministic; gain a metrics contract and advisory control surface. |

Homelab inventory roles remain deterministic services in V3. They may provide structured context to missions but are not converted into free-form LLM workers.

## 10. V4 Entry Definition

V4 may begin only when ANTHILL can repeatedly complete a software mission through this closed loop:

`inspect -> plan -> change in sandbox -> test -> diagnose -> repair -> retest -> review -> summarize -> request retention`

The operator may still be required to approve retention. What must no longer require manual intervention is the internal workflow coordination.

V4 is where ANTHILL may expand authority, repository breadth, long-running development, autonomous pull-request preparation, richer language adapters, and supervised multi-mission execution. V3 is where the framework earns the right to attempt those things.

## Canonical documents

```text
docs/NORTH_STAR.md            This document — the V3 canonical direction (authority on WHAT)
docs/ROADMAP.md               The V3 release order, v3.0.0 through v3.9.0 (authority on WHEN)
docs/DASHBOARD_WORKSPACE.md   Console/workspace operational reference (complete; maintenance)
docs/ANT_EXECUTION.md         Ant execution framework reference
docs/APPROVALS.md             Approval pipeline contract
docs/AUTONOMY.md              Autonomy loop, STOP semantics, budgets
docs/CONTRACTS.md             Task and specialist contracts
docs/DEPLOYMENT.md            Deployment reference (LXC, systemd, Docker)
docs/HOMELAB.md               Homelab subsystem reference (maintenance only during V3)
docs/TRAINING_MISSIONS.md     Operator training mission catalog
docs/ADR-ADAPTIVE-MISSION-RUNTIME.md   Decision record: adaptive mission runtime (V2, retained)
```

V3 architecture decision records live in `docs/adr/` and are written BEFORE the phase they govern:

```text
docs/adr/ADR-001-runtime-composition.md   Runtime composition + Queen decomposition   (v3.1.0)
docs/adr/ADR-002-mission-context.md       Immutable per-mission MissionContext        (v3.1.0)
docs/adr/ADR-003-worker-protocol.md       Durable worker + attempt protocol           (v3.8.0)
docs/adr/ADR-004-artifact-store.md        Artifact + evidence store                   (v3.9.0)
docs/adr/ADR-005-workspace-manager.md     Mission workspace manager                   (v3.5.0)
docs/adr/ADR-006-agent-harness.md         Anthill as an agent harness                 (v3.3.0)
```

V2 planning documents (the V2 North Star, roadmap, remaining-work tracker, and completed project
records) are archived at `docs/archive/v2/` — history, not authority.
