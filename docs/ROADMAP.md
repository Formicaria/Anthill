# ANTHILL V3 ROADMAP

## Colony Execution Infrastructure

**Baseline:** v2.26.0
**Roadmap range:** v3.0.0 through v3.9.0
**Latest:** v3.0.1 — generation-integrity mission scoring: a model-unavailable / all-fallback run can no longer report `completed_verified` (found by live end-to-end testing; additive, default-safe).
**In flight:** v3.1.0 increment 1 (immutable runtime composition + `MissionContext`) — see the v3.1.0 phase-progress table below. Unreleased.
**V4 target:** Codex/Claude-Code-style autonomous software workflow on ANTHILL's bounded colony framework
**Status:** Canonical (adopted at the V2 closeout; V2 documents archived at `docs/archive/v2/`)

---

## Roadmap Rules

- The V2 roadmap and North Star are historical records at `docs/archive/v2/`; this document is canonical.
- V3 phases are sequential. A later phase may prototype early, but it may not be declared complete before all earlier gates pass.
- No release closes on unit tests alone. Each release requires a production call site, an end-to-end path, fault coverage, operator visibility, upgrade coverage, and rollback notes.
- Every phase must preserve the current safety rule: only `completed_verified` is positive.
- New ants are prohibited during V3 unless a missing responsibility cannot be expressed by the existing roster and an architecture decision record proves it.
- Homelab feature expansion is paused except for maintenance required to preserve compatibility or provide deterministic context to the software workflow.

## v3.0.0 - Baseline Lock and Canonical Architecture

### Goal

Close V2 cleanly and create a measured baseline before changing the runtime architecture.

### Required Work

- Archive the old North Star and roadmap as V2 historical documents.
- Adopt the V3 North Star and this roadmap as the only release-order authority.
- Build a generated runtime inventory: roles, handlers, contracts, tools, capabilities, feature gates, endpoints, database tables, background loops, and production call sites.
- Create a call-site audit that fails CI when a declared runtime feature has no production consumer.
- Reconcile stale docs, duplicate issues, version references, and obsolete deferred notes.
- Remove known hygiene residue, including duplicate deadline setup and Python-era source allowlist entries.
- Add characterization tests for current mission, patch, autonomy, learning, and action behavior before refactoring.
- Define architecture decision records for runtime decomposition, mission context, worker protocol, artifact store, and workspace manager.

### Exit Gates

- One canonical V3 document set.
- Zero known declaration-without-call-site defects in the generated inventory.
- Current behavior captured by characterization tests.
- Clean upgrade from the latest V2 database.
- CI green on Linux, Windows, publish/self-test, Docker, UI, and LXC checks.
- No new feature behavior in this release.

### Exit Gate Record — SHIPPED v3.0.0

| Gate | Result |
|---|---|
| One canonical V3 document set | **PASS** — `docs/NORTH_STAR.md` + `docs/ROADMAP.md` canonical; nine V2 planning docs archived at `docs/archive/v2/` with a closing-release map. |
| Zero declaration-without-call-site defects | **PASS** — 300 declarations inventoried (25 roles, 54 gates, 166 endpoints, 48 tables, 7 loops); one orphan found (`cors_enabled`) and removed; `CallSiteAudit` gates CI in both directions; exemption list empty. |
| Behavior captured by characterization tests | **PASS** — `CharacterizationTests` pins the mission-outcome truth table, verdict vocabulary, three-way skill-outcome split, pheromone signal categories, constraint parsing, action-state mapping, ant status mapping. |
| Clean upgrade from the latest V2 database | **PASS (inferred)** — no schema change in this release; schema 16 loads unchanged. Verified against fresh databases only; **not yet exercised against a populated production database.** Recorded as an inference, not a measurement. |
| CI green across all checks | **PASS** — full `validate.ps1` green. |
| No new feature behavior | **PASS** — the only behavioural deltas are removals: the dead `cors_enabled` gate, a duplicate deadline call, and a Python-era source-authority default. `GET /runtime/inventory` exposes existing state; it adds no capability. |

## v3.1.0 - Runtime Composition and Queen Decomposition

### Goal

Replace global coupling with explicit runtime composition while preserving behavior.

### Required Work

- Introduce immutable `RuntimeOptions` and a per-run `RuntimeProfile`.
- Introduce an immutable `MissionContext` containing mission constraints, workspace identity, capability grants, deadlines, budgets, environment fingerprint, and correlation IDs.
- Create a composition root for dependencies instead of reading mutable static fields throughout the codebase.
- Split Queen responsibilities behind interfaces:
  - `IMissionCoordinator`
  - `IPlanningService`
  - `IExecutionService`
  - `IMissionEvaluator`
  - `ILearningRecorder`
  - `IResultAssembler`
- Remove API-host static ownership of runtime services where practical; expose a host-scoped runtime container.
- Make test fixtures create isolated runtime instances without saving and restoring global flags.
- Preserve one final mission authority: decomposition must not create competing lifecycle owners.

### Exit Gates

- Two runtime instances can execute tests in the same process without configuration leakage.
- Queen no longer directly implements planning, execution, learning, and result formatting details.
- Existing mission behavior and persisted outcomes remain compatible.
- Restart, cancellation, and STOP tests remain green.
- No phase feature is activated yet.

### Phase Progress — IN PROGRESS (not released; version marker stays at v3.0.1 until the gates pass)

The phase is being delivered in increments so each lands behaviour-preserving and independently
reviewable, rather than as one 1,300-line mechanical rewrite of the highest-risk surface in the
project. A phase is complete when its exit gates are recorded, not when its first commit merges.

| Increment | Scope | Status |
|---|---|---|
| 1. Immutable configuration + mission context | `RuntimeOptions`, `RuntimeProfile`, `MissionContext`, wired through the Queen's mission engine | **Landed** |
| 2. Constraint resolution beyond the engine | Planner, `MissionEvaluator`, `ObjectiveVerification`, plan preview | **Landed** |
| 3a. Queen decomposition — planning | `IPlanningService` | **Landed** |
| 3b. Queen decomposition — learning | `ILearningRecorder` | **Landed** |
| 3c. Queen decomposition — the rest | `IMissionCoordinator`, `IExecutionService`, `IMissionEvaluator`, `IResultAssembler` | Pending |
| 4. Composition root | Host-scoped container; `ApiHost` stops owning runtime services as statics | Pending |
| 5. Isolation proof | Remove the `[Collection]` attributes added for global state; two runtimes in one process | Pending |

**Increment 1 — what landed.**

- `RuntimeOptions` (immutable, `Capture()`d once per run) and `RuntimeProfile` (per-run executable
  roles, tool grants, write permissions, verification policy), the latter validated at construction
  by the v2.26.0 `RuntimeConfigValidator` — findings are *carried*, not thrown, because the
  validator's contract is to degrade loudly and never refuse boot.
- `MissionContext`: mission id, correlation id, goal, constraints, capability grants, an **absolute
  UTC deadline**, budgets, and environment fingerprint. Constructed once at intake and passed
  explicitly. Persisted to the event log as `mission_context_resolved`, so an operator can read a
  mission's boundaries without inferring them.
- The Queen's mission engine consumes the context at all four sites that previously re-parsed the
  goal (planning admission, per-task runtime resolution, handoff admission, adaptive admission) and
  at every mission-path feature gate. `MissionConstraints.Parse` now appears **zero** times in
  `Queen.cs` and exactly once in `MissionContext.Create`, guarded by a test.
- The deadline moved from a duration re-measured in two dispatch loops to one absolute instant both
  loops compare against.

**Increment 1 — what did not change.** No behaviour, by construction. Construction-time reads
(building the ant roster, the tool registry, the model router) still read the live runtime; those
move with the composition root in increment 4.

**Increment 2 — what landed.** The resolved constraints reach the rest of the mission path, and the
canonical evaluator becomes a pure function:

- `Planner.CreateTasks` takes the constraints instead of parsing the goal. The planner now reaches
  its "no coder tasks for a read-only mission" conclusion from the same object the admission gate
  and the evaluator use.
- `MissionEvaluator.Evaluate` takes the constraints AND the run's verification policy.
  `AnthillRuntime.` no longer appears in `MissionEvaluation.cs` at all — the one authority on
  mission success is now reproducible from its arguments rather than dependent on what a mutable
  static said at the instant finalization ran. Guarded by a test.
- `ObjectiveVerification.IsSatisfied` / `.Explain` take the constraints.
- `Queen.PlanPreview` resolves a context over a transient, never-persisted mission, so the preview
  answers from the same reading of the goal a real dispatch would use.

`MissionConstraints.Parse` is down from eight sites to three in `src/`, each deliberate and each
documented in `RuntimeCompositionTests.TheMissionEngine_ParsesConstraintsExactlyOnce`: `CoderAnt`
(waits for v3.2.0's ant-contract redesign rather than designing that contract twice),
`ObjectiveLifecycle` (parses an objective charter, a different input), and the plan-preview API
response (creates no mission, governs nothing).

**Increment 3a — what landed.** `IPlanningService` / `PlanningService`: goal → admitted task graph.

This extraction is not cosmetic, and the reason is worth recording. Planning was written **twice** —
once in `RunMission` and once in `PlanPreview` — and the copies had already diverged: the preview
never ran `AntRegistry.ValidateTask`, so it could show an operator a task that dispatch would refuse
on sight. Both surfaces now call the same construction. The remaining difference is a named method
(`CreatePlan` vs `PreviewPlan`) rather than a silent one, with the discrepancy documented on the
interface and left for v3.8.0's workflow templates to resolve — changing it now would change an API
response, which this phase is not permitted to do.

The service takes its dependencies as constructor parameters (`Planner`, `SqliteMemory`,
`ToolRegistry`, and a `Func<SkillRegistry>` so the Queen keeps ownership of the single hydrated
registry). It reads no mutable static. `Queen.PlanPreview` is down from 22 lines to 3.

**Increment 3b — what landed.** `ILearningRecorder` / `LearningRecorder`: everything a finished
mission teaches the colony — pheromone scoring, trail reinforcement, skill credit, and procedural
route registration.

These four were interleaved with result composition and event logging inside `FinalizeMission`, so
"what does this mission change about future missions" had no single place to be read or reviewed.
The safety rule the whole surface obeys is now stated once, on the interface: only
`completed_verified` is positive, and that fact is consumed from the one canonical
`MissionEvaluation`, never re-derived. The Queen still decides *when* learning happens — after every
task is terminal, after the evaluation exists, before completion is published.

`Queen.cs` is down from 1,365 lines to 1,237 across increments 3a and 3b, and the three call-site
guards that pinned these behaviours now check both halves: that the Queen still invokes learning
from the canonical evaluation, and that the recorder is what performs each step. Either half alone
is the defect those guards were written for.

## v3.2.0 - Universal Ant and Model Protocol

### Goal

Make every mission worker use one typed execution protocol. This phase carries the V2
**Ant Execution Framework** track (`docs/ANT_EXECUTION.md`) to its conclusion: v2.19.0 gave the six
specialists structured results and v2.26.0 did the same for the five core ants, but the contract,
model boundary, and adapter removal remain to be made universal here.

### Required Work

- Define `IAntExecutor.ExecuteAsync(AntExecutionRequest, CancellationToken)` returning `AntExecutionResult`.
- Apply versioned `AntExecutionContract` records to all active and standby mission agents, not only specialists.
- Remove legacy string-return adapters and any remaining prose-based control flow.
- Replace `IModelClient.Generate(string)` with a typed provider result containing status, content, provider, model, timing, usage, retryability, and typed error.
- Extend task contracts with expected artifacts, success criteria, verification requirements, workspace requirements, retry policy, and repair policy.
- Persist structured ant results, metrics, evidence, handoffs, and contract versions.
- Make planner output schema validation strict and complete; invalid graphs are rejected as a unit rather than partially degraded.

### Exit Gates

- Every planner-eligible role has a versioned contract and typed executor.
- Every model provider path returns typed results; no `ERROR:` prefix determines success.
- Every task result is reconstructable without parsing narrative text.
- No core ant bypasses the contract, capability, metric, or artifact path.
- Compatibility adapters are deleted, not merely deprecated.

## v3.3.0 - Mission Workspace and Language Adapter Infrastructure

### Goal

Give every code mission a disposable, reproducible workspace.

### Required Work

- Implement `MissionWorkspaceManager` for Git worktrees, temporary clones, and operator-defined isolated workspaces.
- Persist a workspace manifest with base revision, repository fingerprint, branch, paths, environment, adapter versions, and cleanup policy.
- Add lifecycle states: requested, preparing, ready, active, checkpointed, retained, rejected, cleanup_pending, cleaned, orphaned.
- Add scoped workspace tools for read, search, edit, diff, and change-set creation.
- Prohibit Coder and Scribe write paths outside the mission workspace.
- Add a `WorkspaceCapabilityManifest` that detects project types and declares safe build/test/format commands.
- Ship .NET and Node/frontend reference adapters; adapters are declarative and replace hard-coded assumptions.
- Add checkpoint and resume behavior for process restart.

### Exit Gates

- A code mission cannot modify the active checkout through any agent path.
- Every change is attributable to one workspace and base revision.
- Workspace recovery after restart is tested.
- Cleanup cannot delete an operator-retained workspace.
- Verification commands come from the manifest or operator configuration, never model invention.

## v3.4.0 - Durable Worker and Attempt Runtime

### Goal

Make task execution restart-safe, reclaimable, and ready for future worker distribution.

### Required Work

- Create a persistent worker protocol with worker IDs, capabilities, leases, heartbeats, and availability.
- Persist separate task attempts with inputs, outputs, model route, tool versions, environment, duration, failure, and evidence.
- Make claims atomic and idempotent.
- Reconcile queued, claimed, running, waiting, and orphaned tasks at startup.
- Define redelivery behavior for each task and side-effect class.
- Integrate Quartermaster metrics with effective concurrency, but allow it only to reduce operator-defined ceilings.
- Separate local worker implementation from the worker protocol so a future remote worker does not require scheduler redesign.

### Exit Gates

- No accepted task is silently lost after crash or restart.
- Expired work is reclaimed without duplicate retained side effects.
- Every retry is a distinct attempt with a durable reason.
- Two workers cannot claim the same non-parallel task.
- Fault tests cover crash before execution, during model call, during tool call, after change, during verification, and during cleanup.

## v3.5.0 - Artifact, Evidence, and Context Graph

### Goal

Replace transcript-shaped collaboration with durable typed collaboration.

### Required Work

- Implement immutable `ArtifactStore` and `EvidenceStore` records with hashes and provenance.
- Define schemas for repository maps, file sets, UI maps, change plans, patch sets, test reports, security reviews, diagnoses, verification bundles, summaries, release notes, and memory candidates.
- Create an artifact dependency graph connecting every output to its input artifacts.
- Build a Context Compiler that selects bounded excerpts and artifact references for each task.
- Persist inter-ant messages as control-plane notices only; substantive work moves through artifacts.
- Add artifact visibility classes and secret-redaction boundaries.
- Make handoffs reference required artifact IDs and explicit missing information.

### Exit Gates

- Every task input and output is traceable through artifact IDs.
- Replaying the same attempt can reconstruct the context package exactly.
- No mission requires an unbounded transcript to continue.
- Artifact hashes detect mutation or stale input.
- UI/API can show why an artifact exists, who produced it, and what consumed it.

## v3.6.0 - Reconnaissance and Quality Ant Activation

### Goal

Activate the ants that improve planning accuracy and verification quality before enabling repair and learning.

### Required Work

- Graduate UI Cartographer through shadow and supervised operation.
- Require UI Cartographer artifacts for frontend/UI change missions.
- Graduate Tester with .NET and Node check adapters, deterministic evidence, timeout, and cancellation support.
- Graduate Soldier as the policy, security, path, permission, dependency-risk, and patch-risk gate.
- Upgrade Researcher, File, Web, Coder, Builder, and Verifier to the universal contract and artifact framework.
- Make Verifier orchestrate required evidence rather than produce a model-only verdict.
- Add planner workflow rules that select reconnaissance and quality roles based on mission type and workspace manifest.

### Exit Gates

- UI missions cannot proceed to change without a UI map unless explicitly waived by policy.
- Required build/test checks are executed by Tester, not narrated by another ant.
- Soldier can block a change with a typed policy finding that models cannot override.
- Verifier cannot pass a mission missing required deterministic evidence.
- Each activated role has shadow comparison data and an operator-visible activation record.

## v3.7.0 - Repair, Knowledge, Documentation, and Resource Ant Activation

### Goal

Close the failure-repair-learning loop without allowing uncontrolled recursion.

### Required Work

- Graduate Medic with typed failure inputs, root-cause classifications, repair scopes, and separate diagnosis/repair budgets.
- Allow Medic to recommend or request a focused repair task; it never edits files itself.
- Graduate Archivist to store verified lessons, negative lessons, deprecated procedures, and skill candidates with full provenance.
- Graduate Scribe to produce operator summaries, changelog entries, and documentation change sets only from verified artifacts.
- Formalize Quartermaster as a deterministic advisory service with a versioned metrics contract.
- Add per-role and per-mission budgets for model calls, tool calls, elapsed time, repair count, and context size.
- Add loop detection based on unchanged evidence fingerprints, not only text similarity.

### Exit Gates

- A seeded build failure can produce diagnosis, focused repair, rerun, and a final verified or explicit failed result.
- Repair cannot exceed configured attempt, task, or time budgets.
- Archivist cannot positively credit unverified work.
- Scribe cannot generate retained docs changes before the associated implementation is verified.
- Quartermaster cannot raise authority or concurrency above operator ceilings.

## v3.8.0 - Closed-Loop Software Engineering Workflows

### Goal

Combine the infrastructure into repeatable end-to-end workflows similar to modern coding agents.

### Required Work

- Add versioned workflow templates:
  - Repository explanation.
  - Bug diagnosis and repair.
  - Small feature implementation.
  - Refactor with behavior preservation.
  - UI change.
  - Test addition.
  - Documentation update.
  - Dependency update.
- Templates define required stages and artifacts but allow bounded delta planning.
- Implement an iterative Coder workspace loop: inspect, edit, diff, check, review result, refine.
- Add checkpointing between waves.
- Make Builder produce one final answer from verified artifacts and unresolved warnings.
- Present a retention bundle: diff, files, checks, security review, verification, known risks, rollback, and recommended next action.
- Support export, retain workspace, create local commit, and prepare branch; never push or merge without separate policy and approval.

### Exit Gates

- A representative medium bug-fix mission completes without manual task routing.
- A frontend change invokes UI Cartographer, Coder, Tester, Soldier, Verifier, Builder, and Scribe where required.
- Failed checks trigger bounded repair and exact rerun.
- The final answer matches the actual workspace state and evidence.
- Rejecting retention leaves the active repository unchanged.

## v3.9.0 - Qualification, Benchmarks, and V4 Gate

### Goal

Prove the platform is ready for V4 authority and broader autonomous development.

### Required Work

- Create a versioned benchmark corpus covering research, backend, frontend, tests, refactors, docs, failures, policy refusals, restart recovery, and malformed model output.
- Run benchmarks against clean disposable repositories and record exact model, adapter, tool, and runtime versions.
- Add long-duration soak runs with database growth, repeated restart, provider interruption, cancellation, and workspace cleanup.
- Add differential tests that compare shadow and active ant decisions.
- Produce a V4 qualification report from measured data; human attestations are reserved for claims the runtime genuinely cannot measure.
- Remove or explicitly classify every remaining standby/scaffold role.
- Freeze V3 behavior during qualification; only defect fixes may enter.

### V4 Release Gates

All gates are mandatory:

1. Zero silent mission or task loss in the recovery suite.
2. Zero direct agent writes to the active checkout.
3. Zero unverified outcomes counted as success.
4. Zero tool executions outside task capability grants.
5. Every planner task passes the same contract admission path.
6. Every model and tool result is typed end to end.
7. Every retained change has deterministic verification evidence or an explicit operator-approved exception that disqualifies autonomous qualification.
8. Every artifact and decision has provenance.
9. All existing mission agents have reached Qualified or have an explicit non-agent classification.
10. At least 80% `completed_verified` success on the approved medium-task benchmark without manual task routing.
11. At least 95% correct safe refusal on prohibited or out-of-scope benchmark tasks.
12. At least 70% successful bounded recovery on seeded repairable failures.
13. No critical data-loss, authorization-bypass, or false-verification defect during the qualification soak.
14. A generated V4 qualification report is truthful, reproducible, and tied to the exact runtime build.

## Release Discipline for Every V3 Phase

Every release must include:

- An architecture decision or explicit statement that no architecture change is needed.
- A tracked issue with measurable acceptance criteria.
- Unit tests for rules and transformations.
- Integration tests for production wiring.
- At least one end-to-end canary mission.
- At least one fault or interruption test.
- Upgrade and rollback notes.
- API/UI visibility for the new state or behavior.
- Updated canonical docs and version markers.
- A release candidate run that executes the phase's qualification pack.
- No deleted, skipped, or weakened safety test without a written replacement rationale.

A phase is not complete when its code merges. It is complete when its intended workflow is exercised through the real runtime and the exit gates are recorded as passing.

## Immediate First Actions

1. Adopt this document set and move the current V2 North Star to a clearly labeled historical path.
2. Open one v3.0.0 tracking issue containing the baseline-lock acceptance criteria.
3. Generate the first role/handler/contract/tool/call-site inventory from the current code.
4. Create ADRs for Queen decomposition and `MissionContext` before refactoring.
5. Build characterization tests around the current v2.26.0 mission lifecycle.
6. Freeze new ant and homelab feature work until v3.0.0 closes.

---

## Adoption status

| First action | Status |
|---|---|
| 1. Adopt the V3 document set; archive V2 planning docs | **Done** — V2 docs at `docs/archive/v2/`; this document and `docs/NORTH_STAR.md` are canonical. |
| 2. Open the v3.0.0 baseline-lock tracking issue | Pending operator (acceptance criteria are the Exit Gate Record above) |
| 3. Generate the runtime inventory + call-site audit | **Done — shipped in v3.0.0.** `RuntimeInventory` + `CallSiteAudit`, CI-gating, 300 declarations, exemption list empty. Found and removed the dead `cors_enabled` gate. |
| 4. ADRs for Queen decomposition and `MissionContext` | **Done — shipped in v3.0.0.** Five ADRs in `docs/adr/`, each written before the phase it governs, each naming what was explicitly rejected. |
| 5. Characterization tests around the v2.26.0 lifecycle | **Done — shipped in v3.0.0.** `CharacterizationTests` pins the outcome truth table, verdict vocabulary, skill-outcome split, signal categories, constraint parsing, action-state mapping, and ant status mapping. |
| 6. Freeze new ant and homelab feature work | **In force** from adoption until v3.0.0 closes |
