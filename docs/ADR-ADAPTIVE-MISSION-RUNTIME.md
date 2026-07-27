# ADR: Adaptive Mission Runtime

**Status:** Accepted (audit complete) — implementation staged across v2.19.x
**Date:** 2026-07-26
**Baseline audited:** `ad3e743` (v2.18.2)
**Supersedes:** nothing. First ADR in the repository; NORTH_STAR §9 has required one for changes to
outcome semantics, authorization, task contracts, verification, and durable execution since v1.8.27.

---

## 1. Context

ANTHILL already contains, as shipped and unit-tested code:

| Subsystem | Shipped | File |
|---|---|---|
| Execution contracts + structured results | v2.9.1 | `Agents/AntExecution.cs` |
| Runtime classification catalog | v2.9.1 | `Agents/AntExecutorCatalog.cs` |
| Bounded handoff gate | v2.9.1 | `Agents/HandoffGate.cs` |
| Independent verification + evidence | v2.12.0 | `Verification/Verification.cs` |
| Procedural skills + selection | v2.13.0 | `Skills/SkillRegistry.cs` |
| Recovery / compensation | v2.14.0 | `Recovery/RecoveryOrchestrator.cs` |
| Durable mission runtime | v2.8.0 | `Memory/SqliteMemory.Jobs.cs` |

The problem is not absence. It is that these subsystems are **adjacent to** the execution path
rather than **inside** it. The audit below establishes that with call-site evidence.

This is the same failure shape the console track hit three times (v2.14.12 functions with no
definitions; v2.14.14 a validator with no call site; v2.18.2 an endpoint that never returned the
field its client read). Here it occurs in the execution core, where the consequences are not
cosmetic.

## 2. Confirmed defects

All fourteen suspected defects were verified against `ad3e743`. **All fourteen are confirmed.**

### 2.1 Structured results are decorative

| # | Claim | Evidence |
|---|---|---|
| 1 | `BaseAnt.Run` returns only a string | `Agents/Ants.cs:18` — `public abstract string Run(Task task, Mission mission);` |
| 2 | Structured results are serialised into tagged text | `Agents/SpecialistAnts.cs:99` — `Compat(AntExecutionResult r)` returns `$"{r.Summary}\n\nUI_MAP_JSON:\n{payload}"`, embedding `status`, `success`, `handoffs`, `evidence` as JSON inside prose |
| 3 | The executor never reads `Success`/`StatusCode`/`Failure`/`Evidence`/`Handoffs` | `Orchestration/Queen.cs` `RunSingleTask` — `result = ant.Run(...)`, then `task.Result = result`, then `scheduler.MarkComplete(...)`. The returned value is never inspected |
| 4 | A specialist can return `failed_retryable` and be recorded complete | Follows from 2 + 3. `SpecialistAnts.cs:39,68,273` return `Compat(AntExecutionResult.Failed(...))`; the only non-complete paths in `RunSingleTask` are a thrown exception, a timeout, or pre-execution runtime denial |

**This is the root defect.** Task status is currently a function of *"did the C# method throw?"*,
not of what the agent actually reported. Every downstream signal — pheromones, mission status,
objective EMA, auto-apply eligibility — is computed on top of statuses that can be wrong.

### 2.2 Handoffs are never ingested

| # | Claim | Evidence |
|---|---|---|
| 5 | `HandoffGate.Evaluate` is not called by the production executor | Repository-wide grep: the only references are `HandoffGate.cs` itself and `tests/Anthill.Tests/HandoffAndRoutingTests.cs`. **Zero production call sites** |

`HandoffGate` implements depth limits, mission task budgets, dedupe keys, and destination gating —
and is invoked by nothing. `AntExecutionResult.Handoffs` is populated by specialists, serialised
into `UI_MAP_JSON`, and discarded.

### 2.3 Planning is static and role-blind

| # | Claim | Evidence |
|---|---|---|
| 6 | Planner role prompt is hard-coded to the original core ants | `Planning/Planner.cs:20` computes `AllowedAnts` from `AntRegistry.ExecutableRoleIds`, but lines 57/68/91/93 hard-code *"researcher, web, file, coder, builder, verifier"* into the prompt. The prompt and the validator disagree: an enabled tester/medic/soldier is never offered to the model |
| 7 | Auto-dependency wiring understands only the original sequence | `Planner.cs:270-290` resolves index/title references; the only role-aware wiring is a single `ui_cartographer → coder` special case at 234-244. No general capability-ordering exists |
| 8 | The plan is built once with no delta pass | `Planner` has no production call site that re-plans; the DAG is constructed pre-execution and never revised |
| 9 | Skills are not used by the normal Planner | `SkillRegistry.PreferredFor` is called only by `Shadow/ShadowOperator.cs:61`. The mission Planner never consults it |

### 2.4 Outcome semantics are wrong, and unsafely so

| # | Claim | Evidence |
|---|---|---|
| 10 | The Director treats `partial` as success | `Api/ColonyDirector.cs:383` — `var success = status is "complete" or "partial";` |
| 11 | Partial missions create follow-ups and reach auto-apply | That one flag drives four consequences: `SaveFollowUps` (224), `RecordObjectiveRunOutcome` (228), `EvaluateObjectiveLifecycle` (240), and **auto-apply of patches** (246) |
| 12 | Follow-ups are speculative and pre-execution | `_strategist.GenerateGoal(objective)` runs at line 172, *before* the mission executes. Its `strategy.FollowUps` are persisted at 224 on the basis of the later broad status — not on discovered evidence |
| 13 | Positive learning without verified outcomes | `Pheromones/PheromoneEngine.cs:19` `ScoreMission` counts `Complete`/`Failed`/`Skipped` task statuses plus a **text-length heuristic** (`builderBonus` = combined result length > 1500 chars). No verification evidence participates |
| 14 | Mission completion is structural, not evidence-based | `Queen.cs:729-732` — `criticalFailed ? Failed : degraded ? Partial : Complete`. Queen.cs contains **zero** references to `IVerifier`, `VerificationBundle`, or `VerificationPolicy` |

**Compounded severity.** Defects 3, 10 and 11 interact: a specialist reports `failed_retryable` →
the task is recorded `Complete` (3) → the mission grades `Complete` or `Partial` (14) → the Director
reads that as success (10) → patches auto-apply (11). A failing agent can therefore drive an
automatic code change. This is the finding that sets the implementation order below.

## 3. Decision

Introduce an **adaptive mission runtime** in which structured results are authoritative, handoffs
enter the live scheduler through the existing gate, replanning is bounded and delta-only, and only
`completed_verified` counts as success.

### 3.1 Vocabulary — these are deliberately different things

The brief warns against using "replanning" as cover for unbounded task creation. This ADR fixes
the distinctions:

| Term | Trigger | Creates | Bound |
|---|---|---|---|
| **Static plan** | Mission start | Initial DAG | Planner task cap |
| **Handoff** | A completed task's structured result proposes one | One task, gated by `HandoffGate` | `MaxHandoffDepth = 2` |
| **Delta plan** | Post-wave assessment finds an unmet criterion | Only the *missing* tasks | `max_replans = 2` generations |
| **Retry** | `failed_retryable` | Re-runs the *same* task | Existing per-task retry policy |
| **Repair loop** | Deterministic check failed | medic → focused fix → re-check | `max_repair_cycles = 2` |
| **Objective follow-up** | Post-mission, from verified evidence | A *new objective*, not a task | Requires evidence; depth-limited |
| **Separate mission** | Operator or Director decision | A new mission | Governor budgets |

A handoff is not a replan. A repair cycle is not a follow-up. Each has its own counter, and
exhausting one does not borrow budget from another.

### 3.2 Component boundary

`Queen.cs` is already large and owns too much. The adaptive loop goes in a new
`AdaptiveMissionController`:

```
execute wave → inspect structured outcomes → admit handoffs → assess →
  (continue | delta-plan | retry | repair | escalate | finish)
```

The Queen remains the authority for mission lifecycle, persistence and policy. The controller owns
only the loop and returns a typed decision. Scheduler mutation happens exclusively through a new
`TryAddDynamicTask(...)` admission API — orchestration code never touches the scheduler's internal
collections.

### 3.3 Success rule

`completed_verified` is the **only** outcome that may drive positive pheromone reinforcement,
objective success EMA, skill success, follow-up creation, patch retention, auto-apply eligibility,
or objective closure. `partial` reinforces nothing.

Mission-level `completed_verified` requires: every critical task succeeded; required handoffs
resolved; required artifacts present; required deterministic verifiers passed; policy checks
passed; no unresolved blocking warning; and a persisted evidence bundle. A model saying "looks
good" is not evidence.

## 4. Rejected alternatives

**Flip the specialist gates on and ship.** Rejected: with defect 3 unfixed, activating tester and
medic adds agents whose failure reports are discarded. It would increase apparent capability and
actual risk simultaneously.

**Keep `string Run` and parse `UI_MAP_JSON` in the executor.** Rejected: it makes prose
load-bearing permanently and leaves two execution systems. A temporary adapter is acceptable during
the migration; it must not survive it.

**Let the Planner re-plan freely on each wave.** Rejected: unbounded recursive task creation. Delta
plans only, with generation counters and no-progress fingerprints.

**One `adaptive_*` setting per knob.** Rejected as micro-settings; a small clamped set plus a
`core | adaptive | full` profile.

## 5. Implementation order

Ordered by dependency, and by the compounded-severity finding above.

| Stage | Scope | Release |
|---|---|---|
| **1** | Structured results authoritative: `BaseAnt` contract, migrate 12 ants, canonical result→status mapping, remove `Compat` | v2.19.0 |
| **2** | Outcome semantics: `completed_verified`, Director `partial` fix, pheromone/EMA/auto-apply gating | v2.19.0 |
| **3** | Handoff ingestion: `TryAddDynamicTask`, `HandoffGate` wired, persistence + restart | v2.19.1 |
| **4** | `AdaptiveMissionController`: assessment, typed decisions, bounded delta planning, no-progress detection | v2.20.0 |
| **5** | Runtime-aware Planner + deterministic routing policies | v2.20.0 |
| **6** | Skill selection in normal planning | v2.20.1 |
| **7** | Objective progress model + evidence-derived follow-ups | v2.20.1 |
| **8** | Controlled specialist activation (`core`/`adaptive`/`full`) | v2.21.0 |

**Stages 1 and 2 must ship together.** Stage 1 alone would begin surfacing genuine failures into a
Director that still treats `partial` as success — briefly *increasing* the rate of auto-applied
patches from failing missions. They are one release for that reason.

## 6. Safety invariants (non-negotiable)

Unchanged by this work: mission agents never receive `apply_patch` or unrestricted shell; a handoff
may never grant a capability; a model may never alter policy, widen an allowlist, or raise a budget;
replanning may not increase its own bounds; an archivist may not self-certify a skill; auto-apply
never precedes independent verification; control-plane roles stay non-executable; homelab collectors
remain deterministic providers and are never scheduled as LLM workers.

Every runtime-added task passes the *same* authorization, contract and permission gates as an
initial-plan task. There is no admission path that skips them.

## 7. Consequences

**Positive.** Failures become visible instead of being recorded as success; the repair loop closes;
already-built subsystems finally participate; "success" becomes evidence-backed.

**Negative / accepted.** Apparent success rates will drop once failures stop being mislabelled —
this is the metric becoming correct, not a regression, and it should be stated plainly in the
release notes. Missions gain latency from verification. Historical pheromone and EMA data was
computed under the defective rule and is not retroactively trustworthy.

**Not claimed.** Adaptive execution is not V3 qualification. NORTH_STAR must not record V3 as
satisfied on the strength of this work.
