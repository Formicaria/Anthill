# ANTHILL — NORTH STAR 2.0

> **Canonical project roadmap**
>
> **Rebased from:** ANTHILL v2.7.0
> **Target:** ANTHILL v3.0.0 — Bounded Autonomous Homelab Operator
> **Status:** Active
>
> This document replaces the previous ordered roadmap as the canonical source for future ANTHILL development.
>
> Older roadmap documents may remain as subsystem design history, but they must link back to this document and must not define a conflicting release order.

---

# 1. Mission

ANTHILL is a local-first autonomous operations platform for software projects and homelab infrastructure.

Its purpose is not merely to generate recommendations, run isolated tools, or display infrastructure status.

Its purpose is to become a dependable operator that can:

1. Observe an environment.
2. Detect meaningful change or failure.
3. Diagnose the likely cause.
4. Produce a bounded plan.
5. Evaluate risk and blast radius.
6. Select an approved capability.
7. Execute through deterministic tools.
8. Verify the real-world result.
9. Roll back or compensate when necessary.
10. Record evidence.
11. Learn from the verified outcome.
12. Improve future decisions without exceeding operator-defined authority.

ANTHILL must earn autonomy through demonstrated reliability.

The system must never treat autonomy as permission to act without boundaries.

---

# 2. Current Baseline

ANTHILL v2.7.0 already includes substantial foundations.

## Existing autonomy

* Persistent objectives and autonomy run history.
* Colony Director objective loop.
* Strategist-generated mission goals.
* Concurrent mission execution.
* Resource-aware concurrency reduction.
* Priority scheduling with anti-starvation aging.
* Mission budgets and kill switches.
* Follow-up objective generation.
* Objective success scoring.
* Low-value and looping-objective retirement.
* Patch proposals and approval workflows.
* Gated autonomous patch application.
* Build-and-test verification.
* Rollback of failed auto-applied patches.
* Pheromone and mission memory.
* Fresh-install training missions.
* Model routing and circuit breaking.

## Existing homelab platform

* Host and service inventory.
* Dependency mapping.
* Health checks.
* Notifications.
* Proxmox integration.
* Backup awareness.
* Network and security awareness.
* Incidents and change history.
* Approval-gated infrastructure actions.
* Automation rules.
* DNS, DHCP, and firewall control foundations.
* Full homelab operations layer.
* Permissions, credential storage, target allowlists, and audit records.

These capabilities are valuable, but their existence alone does not make ANTHILL a trustworthy autonomous operator.

The remaining work is primarily framework work:

* durable execution,
* deterministic task contracts,
* typed tools,
* isolated execution,
* independent verification,
* real skill learning,
* recovery,
* and operator qualification.

---

# 3. Permanent Operating Doctrine

Every autonomous action must follow:

```text
Observe
  ↓
Classify
  ↓
Diagnose
  ↓
Plan
  ↓
Risk Score
  ↓
Authorize
  ↓
Execute
  ↓
Verify
  ↓
Keep, Retry, Compensate, or Roll Back
  ↓
Record Evidence
  ↓
Learn
```

No stage may be skipped merely because a model reports confidence.

## Permanent rules

1. **Partial completion is not success.**
2. **Model output is not evidence.**
3. **A completed task is not verified until an independent verifier confirms it.**
4. **No irreversible action may depend only on an LLM judgment.**
5. **All autonomous writes occur in an isolated or recoverable environment.**
6. **Every side effect must have an idempotency strategy.**
7. **Every destructive or state-changing tool must declare recovery behavior.**
8. **The operator controls the maximum authority level.**
9. **Fail closed when authorization, verification, or environment state is uncertain.**
10. **Learning may influence selection, but may not bypass policy.**
11. **The system must distinguish recommendation, proposal, execution, and verification.**
12. **ANTHILL must be able to explain what it observed, what it changed, and how it proved the result.**

---

# 4. Outcome Semantics

ANTHILL must stop treating broad structural completion as operational success.

All missions, tasks, actions, and playbook executions must use explicit outcomes.

```text
queued
claimed
running
waiting_for_tool
waiting_for_approval
waiting_for_verification
completed_unverified
completed_verified
partial
failed_retryable
failed_permanent
timed_out
cancelled
compensating
compensated
rollback_failed
orphaned
```

## Success definition

Only the following state may:

* reinforce positive learning,
* increase skill confidence,
* generate autonomous follow-up work,
* permit autonomous patch retention,
* permit autonomous infrastructure change retention,
* or count toward operator certification:

```text
completed_verified
```

`partial`, `completed_unverified`, or model-reported success must never be treated as verified success.

---

# 5. Autonomy Levels

ANTHILL must expose a single consistent autonomy model across code and homelab operations.

## Level 0 — Observe

ANTHILL may:

* collect inventory,
* run read-only checks,
* correlate incidents,
* summarize findings,
* and record memory.

No changes are proposed or executed.

## Level 1 — Recommend

ANTHILL may:

* diagnose,
* produce plans,
* recommend playbooks,
* estimate risk,
* and prepare evidence.

No action proposal is created automatically.

## Level 2 — Propose

ANTHILL may:

* create patch proposals,
* create infrastructure action proposals,
* calculate blast radius,
* prepare rollback plans,
* and request approval.

Execution requires human approval.

## Level 3 — Bounded Auto-Execution

ANTHILL may automatically execute actions only when:

* the capability is explicitly allowlisted,
* the action is low-risk,
* the target is allowlisted,
* verification is deterministic,
* compensation or rollback exists,
* the cumulative budget is available,
* and the action remains inside the configured blast-radius limit.

## Level 4 — Supervised Autonomous Operation

ANTHILL may:

* diagnose incidents,
* select certified playbooks,
* execute bounded multi-step recovery,
* verify results,
* roll back failures,
* and place itself into cooldown or degraded mode.

High-risk actions continue to require approval.

## Level 5 — Reserved

Fully unrestricted autonomy is not a planned project goal.

ANTHILL must always operate within operator-defined permissions, budgets, targets, and recovery rules.

---

# 6. Release Sequence

```text
V2.7.0   Full homelab operations layer                 [CURRENT BASELINE]

V2.8.0   Durable Mission Runtime
V2.9.0   Contracted Tasks and Typed Capability Tools
V2.10.0  Sandboxed Agent Execution
V2.11.0  Independent Verification and Evidence
V2.12.0  Procedural Skills and Evaluated Learning
V2.13.0  Safe Action Engine and Recovery Orchestration
V2.14.0  Shadow Operations and Operator Qualification

V3.0.0   Bounded Autonomous Homelab Operator
```

V3.0 may not begin until all V2.8–V2.14 release gates are satisfied.

---

# PHASE 1 — V2.8.0 DURABLE MISSION RUNTIME

## Goal

Make mission execution survive process crashes, service restarts, host reboots, worker loss, and partial execution.

The runtime must no longer depend on in-memory job state for operational correctness.

## Required capabilities

### Persistent mission queue

Persist:

* mission ID,
* objective ID,
* task graph,
* current task,
* mission status,
* attempt number,
* assigned worker,
* claim timestamp,
* lease expiration,
* heartbeat timestamp,
* cancellation request,
* tool calls,
* produced artifacts,
* verifier state,
* compensation state,
* and final outcome.

### Atomic objective claims

Selecting an objective and claiming it must occur in one transaction.

Two Directors or workers must not launch the same objective concurrently unless the objective explicitly permits parallelism.

### Worker leases

Workers must:

* claim work for a fixed lease period,
* renew the lease with heartbeats,
* release the lease on completion,
* and allow safe reclamation after expiration.

### Startup reconciliation

At startup, ANTHILL must inspect incomplete work and classify it as:

* resumable,
* retryable,
* compensating,
* orphaned,
* waiting for operator review,
* or permanently failed.

### Idempotency

Every mission and side-effecting action must have an idempotency key.

Repeated delivery of the same work must not create duplicate:

* patches,
* snapshots,
* VM operations,
* DNS records,
* firewall rules,
* notifications,
* objectives,
* incidents,
* or Git commits.

### Attempt history

Retries must create separate attempts while preserving the original mission identity.

Each attempt must record:

* reason,
* worker,
* environment,
* model,
* tool versions,
* inputs,
* outputs,
* errors,
* and duration.

## Required tests

* Kill the process while a mission is queued.
* Kill it while a worker owns a lease.
* Kill it during a tool call.
* Kill it after a side effect but before outcome recording.
* Kill it during verification.
* Kill it during rollback.
* Restart with multiple expired leases.
* Run two Directors against the same database.
* Replay the same idempotency key.
* Confirm no task is silently lost or executed twice.

## Success criteria

* No accepted mission disappears after restart.
* No objective is launched twice because of a race.
* Expired work is reclaimed safely.
* Completed work is not repeated.
* Side effects can be correlated to a durable attempt.
* The runtime can explain every incomplete mission after recovery.

---

# PHASE 2 — V2.9.0 CONTRACTED TASKS AND TYPED CAPABILITY TOOLS

## Goal

Replace loosely defined prompt tasks and string-based tool results with machine-readable contracts.

## Task contract

Every task must define:

```json
{
  "id": "task-id",
  "title": "Human-readable title",
  "objective": "What must be achieved",
  "task_type": "diagnose|change|verify|research|recover",
  "required_capabilities": [],
  "inputs": {},
  "constraints": {},
  "expected_artifacts": [],
  "success_criteria": [],
  "verification_plan": [],
  "dependencies": [],
  "side_effect_class": "none|reversible|destructive",
  "risk_class": "low|medium|high|critical",
  "idempotency_key": "",
  "retry_policy": {},
  "compensation_plan": {},
  "timeout_seconds": 0
}
```

## Typed tool interface

Every tool must declare:

* name,
* description,
* version,
* JSON input schema,
* JSON output schema,
* required permissions,
* supported targets,
* side-effect class,
* risk class,
* whether it is idempotent,
* idempotency-key support,
* timeout support,
* cancellation support,
* retry behavior,
* compensation capability,
* audit fields,
* and evidence produced.

## Structured tool result

Tools must return structured results:

```json
{
  "status": "succeeded|failed_retryable|failed_permanent|cancelled",
  "summary": "",
  "data": {},
  "artifacts": [],
  "evidence": [],
  "warnings": [],
  "error": {
    "code": "",
    "message": "",
    "retry_after_seconds": 0
  },
  "side_effects": [],
  "compensation_token": ""
}
```

## Capability model

Permissions must attach to capabilities, not ant names.

Examples:

```text
repo.read
repo.search
repo.write.sandbox
repo.patch.propose
repo.patch.apply
process.execute.readonly
process.execute.workspace
network.http.public
network.http.homelab
proxmox.read
proxmox.vm.start
proxmox.vm.stop
proxmox.snapshot.create
dns.record.write
firewall.rule.write
backup.restore
credential.use
```

Ants may receive temporary capability grants for one mission.

## Failure taxonomy

Errors must be classified consistently:

* validation failure,
* authorization failure,
* target rejection,
* transient provider failure,
* rate limit,
* timeout,
* conflict,
* dependency failure,
* verification failure,
* unsafe state,
* compensation failure,
* and internal defect.

## Success criteria

* Planner output is schema validated.
* Invalid tasks cannot enter the execution queue.
* Tools no longer depend on unstructured text parsing for control flow.
* Permissions can be evaluated before execution.
* Every state-changing tool declares recovery behavior.
* Retry decisions use typed failure classes.

---

# PHASE 3 — V2.10.0 SANDBOXED AGENT EXECUTION

## Goal

Allow ants to work iteratively without modifying the active ANTHILL installation or uncontrolled host state.

## Required execution environments

### Code missions

Run in one of:

* disposable Git worktree,
* temporary clone,
* ephemeral container,
* or operator-defined isolated workspace.

Never perform autonomous code changes directly in the live production checkout.

### Infrastructure missions

Use:

* read-only observation context,
* a proposed action plan,
* a bounded execution session,
* action-specific credentials,
* target allowlists,
* and explicit compensation metadata.

## Agent execution loop

Agents must support bounded iteration:

```text
Observe
→ Choose capability
→ Execute tool
→ Inspect structured result
→ Update working state
→ Replan if necessary
→ Continue or stop
```

Each loop must be constrained by:

* maximum turns,
* maximum tool calls,
* maximum elapsed time,
* token budget,
* action budget,
* risk budget,
* repeated-action detection,
* and cancellation.

## Required agent tools

For code:

* repository tree,
* semantic code search,
* exact text search,
* file reader,
* diff reader,
* compiler,
* test runner,
* formatter,
* linter,
* static analyzer,
* dependency inspector,
* Git status,
* and artifact collector.

For homelab:

* inventory lookup,
* dependency graph lookup,
* incident history,
* change history,
* health checks,
* provider status,
* target validation,
* action preview,
* blast-radius calculation,
* and rollback availability.

## Prompt-injection boundaries

Tool output, repository content, logs, web pages, and infrastructure metadata must be treated as untrusted input.

Untrusted content must not:

* grant capabilities,
* alter system policy,
* disable verification,
* change approval requirements,
* reveal credentials,
* or expand target allowlists.

## Loop controls

Detect:

* repeated identical tool calls,
* no-progress cycles,
* oscillating edits,
* repeated failing patches,
* repeated diagnosis without new evidence,
* and planner-executor disagreement.

## Success criteria

* Autonomous code edits occur only in isolated workspaces.
* Agents can inspect, act, test, and revise.
* The active checkout remains unchanged until promotion.
* Tool loops stop predictably when budgets expire.
* Untrusted content cannot elevate authority.

---

# PHASE 4 — V2.11.0 INDEPENDENT VERIFICATION AND EVIDENCE

## Goal

Separate execution from verification and require real evidence before declaring success.

The ant or model that performed a change must not be the only entity deciding whether it worked.

## Verification framework

Introduce:

```text
IVerifier
VerificationRequest
VerificationResult
VerificationEvidence
VerificationPolicy
VerificationBundle
```

## Required verifier types

### BuildVerifier

Confirms compilation or packaging succeeds.

### TestVerifier

Runs required test suites and records exact results.

### DiffVerifier

Checks that the resulting diff matches approved scope.

### ArtifactVerifier

Confirms required files, reports, backups, snapshots, or outputs exist.

### ServiceHealthVerifier

Checks HTTP, TCP, process, service, or application health.

### InfrastructureStateVerifier

Confirms actual provider state matches the intended state.

### DependencyVerifier

Checks dependent services and systems after a change.

### SecurityPolicyVerifier

Checks:

* secret exposure,
* permission expansion,
* unsafe paths,
* credential leakage,
* dangerous commands,
* firewall exposure,
* and policy violations.

### RollbackVerifier

Proves rollback or compensation restored the expected previous state.

### SemanticJudgeVerifier

Uses a model for semantic review only when deterministic verification is insufficient.

A semantic judge may supplement deterministic evidence but must not replace it for state-changing operations.

## Evidence requirements

Verification evidence may include:

* command,
* exit code,
* stdout and stderr digests,
* test counts,
* file hashes,
* diffs,
* API responses,
* health-check results,
* provider task IDs,
* screenshots,
* timestamps,
* target identity,
* and before/after state.

## Verification policy

A task must specify which verifiers are required.

Example:

```text
Code patch:
- DiffVerifier
- BuildVerifier
- TestVerifier
- SecurityPolicyVerifier

VM restart:
- InfrastructureStateVerifier
- ServiceHealthVerifier
- DependencyVerifier

Firewall change:
- InfrastructureStateVerifier
- ConnectivityVerifier
- SecurityPolicyVerifier
- RollbackVerifier availability
```

## Promotion rule

A code or configuration change may be promoted from its sandbox only after:

* all required verifiers pass,
* the final diff remains within approved scope,
* cumulative risk remains within policy,
* no secret is detected,
* and the evidence bundle is persisted.

## Success criteria

* Structural task completion cannot create a verified success.
* Every retained autonomous change has an evidence bundle.
* Verification can be rerun independently.
* Failed verification causes retry, compensation, rollback, or escalation.
* Model confidence is never stored as proof.

---

# PHASE 5 — V2.12.0 PROCEDURAL SKILLS AND EVALUATED LEARNING

## Goal

Make ANTHILL improve from verified experience without allowing uncontrolled self-training.

## Learning layers

### Episodic memory

Store what occurred during a specific mission:

* context,
* plan,
* tool calls,
* decisions,
* evidence,
* outcome,
* and operator feedback.

### Semantic memory

Store stable facts:

* repository architecture,
* environment inventory,
* service ownership,
* dependency relationships,
* policies,
* and known constraints.

### Procedural memory

Store reusable methods:

* diagnosis sequences,
* repair playbooks,
* verification routines,
* rollback routines,
* and environment-specific runbooks.

### Causal memory

Store supported relationships:

* change X preceded failure Y,
* action A restored service B,
* rollback C reversed side effect D.

Causal claims must include evidence strength and provenance.

### Negative memory

Store:

* failed fixes,
* unsafe approaches,
* rejected patches,
* false diagnoses,
* expired procedures,
* and environment incompatibilities.

## Hybrid retrieval

Memory search must combine:

* exact filters,
* FTS/BM25,
* embeddings,
* metadata,
* freshness,
* environment compatibility,
* source reliability,
* and prior verified success.

## Skill registry

Introduce versioned skills:

```text
Skill ID
Version
Purpose
Supported environments
Required capabilities
Inputs
Preconditions
Procedure
Expected evidence
Verification policy
Compensation plan
Success count
Failure count
Confidence
Last validated
Status
```

Statuses:

```text
candidate
experimental
certified
degraded
retired
blocked
```

## Skill promotion

A candidate skill becomes certified only after:

* repeated verified successes,
* no unresolved rollback failures,
* acceptable environment coverage,
* passing regression scenarios,
* and operator-defined confidence thresholds.

## Skill demotion

Skills must be degraded or retired when:

* the environment changes,
* provider versions become incompatible,
* repeated failures occur,
* verification becomes unreliable,
* or a safer skill replaces them.

## Planner use

The Planner should prefer:

1. certified compatible skills,
2. experimental skills in sandbox or shadow mode,
3. generated plans when no suitable skill exists.

## Model routing learning

Outcome history may influence:

* role-to-model routing,
* prompt selection,
* tool ordering,
* retrieval weighting,
* and retry strategy.

It may not:

* grant new permissions,
* skip approvals,
* weaken verification,
* or expand allowed targets.

## Model training policy

ANTHILL must not continuously fine-tune its active production model from its own unreviewed outputs.

Any future adapter or fine-tuning pipeline must be:

* offline,
* secret-scrubbed,
* based on verified trajectories,
* split into train and holdout sets,
* evaluated against a stable baseline,
* tested for regression and policy compliance,
* deployed as a canary,
* and reversible.

Production self-replacement is outside the V3.0 scope.

## Success criteria

* ANTHILL learns reusable procedures rather than only scalar scores.
* Certified skills contain deterministic verification.
* Failed approaches reduce future selection probability.
* Retrieval accounts for environment and freshness.
* Learning cannot bypass authorization policy.

---

# PHASE 6 — V2.13.0 SAFE ACTION ENGINE AND RECOVERY ORCHESTRATION

## Goal

Unify code changes and homelab actions behind one safe execution framework.

## Action lifecycle

```text
draft
validated
risk_scored
waiting_for_approval
approved
scheduled
executing
verifying
completed_verified
failed
compensating
compensated
rollback_failed
escalated
```

## Action proposal

Every action proposal must include:

* target,
* intended state,
* current state,
* capability,
* exact operation,
* preconditions,
* risk class,
* blast radius,
* affected dependencies,
* expected downtime,
* verification plan,
* rollback or compensation plan,
* approval requirement,
* expiration time,
* and idempotency key.

## Risk engine

Risk scoring must consider:

* destructive potential,
* reversibility,
* target criticality,
* number of affected systems,
* dependency depth,
* credential privilege,
* production versus lab designation,
* backup freshness,
* maintenance window,
* recent changes,
* unresolved incidents,
* action novelty,
* skill confidence,
* verifier quality,
* and cumulative change size.

## High-risk categories

The following require explicit approval unless the operator creates a narrowly scoped exception:

* delete operations,
* storage destruction,
* restore operations,
* firewall exposure,
* credential or permission changes,
* authentication changes,
* network routing changes,
* database schema migrations,
* production service shutdown,
* cluster membership changes,
* destructive Git operations,
* and changes without deterministic rollback.

## Recovery orchestration

Recovery must support:

* immediate rollback,
* compensating action,
* retry after cooldown,
* failover,
* restore from snapshot or backup,
* quarantine,
* disable automation,
* revoke temporary capability,
* and escalate to the operator.

## Change-set transactions

Multi-step actions must define:

* step order,
* checkpoints,
* verification after each checkpoint,
* stop conditions,
* compensation order,
* and whether partial retention is allowed.

## Cooldowns and circuit breakers

Automatically pause an action type, target, provider, skill, or automation rule after:

* repeated failures,
* repeated verification failures,
* rollback failure,
* provider instability,
* resource pressure,
* or unexpected blast radius.

## Auto-apply correction

Autonomous patch application must:

* perform branch and workspace safety checks before writing,
* operate inside an isolated worktree,
* evaluate cumulative diff risk,
* inspect critical file classes,
* block secret-bearing changes,
* block unsafe dependency changes,
* block unapproved migrations,
* and promote verified commits rather than modifying the live checkout first.

## Success criteria

* All state-changing systems use the same lifecycle.
* Every action has a recovery path or is explicitly marked non-recoverable.
* Critical changes cannot qualify as low risk by line count alone.
* Rollback failure automatically suspends related autonomy.
* Multi-step recovery can stop safely at checkpoints.

---

# PHASE 7 — V2.14.0 SHADOW OPERATIONS AND OPERATOR QUALIFICATION

## Goal

Prove ANTHILL is ready to operate before granting V3 authority.

V2.14 is a qualification release, not a feature expansion release.

## Shadow mode

ANTHILL observes real incidents and produces:

* diagnosis,
* proposed action,
* chosen skill,
* risk score,
* predicted outcome,
* verification plan,
* and rollback plan.

It does not execute.

The operator records what action was actually taken and whether ANTHILL’s recommendation was correct.

## Simulation mode

Run against:

* mocked providers,
* disposable VMs,
* containers,
* test networks,
* temporary repositories,
* and replayed incident histories.

## Fault-injection scenarios

Required scenarios include:

* service crash,
* health-check false positive,
* full disk,
* failed backup,
* stale DNS record,
* unreachable Proxmox node,
* VM stuck in transition,
* firewall rule regression,
* dependency outage,
* expired credential,
* rate-limited provider,
* interrupted mission,
* failed verification,
* failed rollback,
* duplicate mission delivery,
* and malicious prompt injection in logs or repository content.

## Reliability metrics

Track:

* diagnosis precision,
* diagnosis recall,
* action-selection accuracy,
* unnecessary-action rate,
* verification false-positive rate,
* rollback success rate,
* duplicate execution rate,
* mean time to detect,
* mean time to diagnose,
* mean time to recover,
* operator override rate,
* policy violation count,
* and unverified-success count.

## Required release thresholds

Before V3.0:

* Zero silent mission loss during recovery testing.
* Zero duplicate irreversible actions during idempotency testing.
* Zero unverified outcomes counted as success.
* Zero critical policy bypasses.
* Zero credentials exposed in logs, prompts, memory, or evidence.
* All destructive capabilities fail closed.
* All Level 3 actions have deterministic verification.
* All Level 3 actions have rollback or approved compensation.
* Rollback success rate meets the operator-defined threshold.
* Shadow-mode recommendations meet the operator-defined accuracy threshold.
* Repeated fault-injection runs produce stable results.
* Restart and crash-recovery suites pass.
* The operator can disable all autonomous execution immediately.

## Operator certification report

ANTHILL must generate a readiness report containing:

* certified capabilities,
* certified skills,
* allowed targets,
* known limitations,
* unresolved risks,
* recovery test results,
* verifier coverage,
* shadow-mode performance,
* provider compatibility,
* and recommended autonomy level.

## Success criteria

* V3 readiness is evidence-based.
* Autonomy is granted per capability, not globally.
* Failed qualification areas remain at Level 0–2.
* The system can prove its recovery behavior.
* The operator receives a clear go/no-go decision.

---

# 7. V3.0.0 — BOUNDED AUTONOMOUS HOMELAB OPERATOR

## Goal

Allow ANTHILL to autonomously operate approved portions of a homelab through certified capabilities and playbooks.

V3.0 does not mean unrestricted control.

It means ANTHILL may independently complete approved, recoverable, machine-verifiable operations within a defined boundary.

## V3 execution loop

```text
Observe environment
→ Detect event or objective
→ Build evidence-backed diagnosis
→ Match certified skill
→ Validate preconditions
→ Calculate blast radius and cumulative risk
→ Confirm authority level
→ Claim durable execution lease
→ Execute bounded steps
→ Verify actual state
→ Roll back or compensate on failure
→ Enter cooldown when required
→ Persist evidence and outcome
→ Update skill and routing confidence
```

## Allowed V3 action classes

Examples may include:

* restart a noncritical failed service,
* start a stopped approved VM,
* renew a known-safe health check,
* retry a failed backup job,
* clean an approved temporary directory,
* restore a known-safe DNS record,
* revert a recently introduced approved configuration,
* create a diagnostic snapshot,
* apply a certified low-risk code patch in an isolated worktree,
* or execute another operator-certified skill.

Each action class must be enabled separately.

## Actions still requiring approval

By default:

* data deletion,
* backup restoration,
* firewall exposure,
* account or credential changes,
* privilege escalation,
* storage reconfiguration,
* cluster membership changes,
* network routing changes,
* database migrations,
* destructive code operations,
* and actions without deterministic recovery.

## V3 operator controls

The operator must be able to configure:

* maximum autonomy level,
* allowed capabilities,
* allowed targets,
* maintenance windows,
* risk threshold,
* per-action budget,
* daily action budget,
* concurrent action limit,
* required verifiers,
* cooldown duration,
* approval exceptions,
* and emergency stop behavior.

## V3 degraded mode

ANTHILL must automatically reduce authority when:

* a provider is unstable,
* verification is unavailable,
* backups are stale,
* the database is degraded,
* memory is inconsistent,
* resource pressure is high,
* multiple rollbacks occur,
* a critical incident is active,
* or the environment differs from the certified profile.

Degraded mode may reduce the system to:

* Observe,
* Recommend,
* or Propose.

## V3 definition of done

ANTHILL v3.0 is complete only when it can demonstrate:

1. Durable operation across restart and worker failure.
2. Typed, permissioned, cancellable tools.
3. Contracted tasks with explicit success criteria.
4. Iterative bounded agent execution.
5. Isolated code and configuration changes.
6. Independent deterministic verification.
7. Verified rollback or compensation.
8. Certified procedural skills.
9. Evidence-based learning.
10. Capability-scoped autonomy.
11. Shadow and fault-injection qualification.
12. Immediate operator control and emergency shutdown.

---

# 8. Explicit Non-Goals Before V3

The following are not required to declare V3 complete:

* unrestricted autonomous administration,
* uncontrolled recursive objective generation,
* online self-fine-tuning,
* automatic replacement of the production model,
* unrestricted shell access,
* unrestricted network access,
* bypassing approval for critical actions,
* eliminating human oversight,
* active exploitation or offensive security,
* or autonomous expansion of its own permissions.

These may not be added indirectly through plugins, prompts, skills, or generated code.

---

# 9. Documentation and Governance

## Canonical documents

```text
docs/NORTH_STAR.md       Ordered project roadmap
docs/AUTONOMY.md         Autonomy runtime and operator controls
docs/HOMELAB.md          Homelab architecture
docs/APPROVALS.md        Approval and authorization model
docs/TOOLS.md            Capability and tool contracts
docs/VERIFICATION.md     Verifier framework and evidence rules
docs/SKILLS.md           Procedural skill lifecycle
docs/RECOVERY.md         Rollback and compensation architecture
docs/QUALIFICATION.md    V3 readiness and fault-injection plan
```

## Roadmap consistency

Automated tests must verify:

* runtime version matches documented version,
* shipped releases are not marked future,
* future phases appear in the correct order,
* deprecated roadmap documents link to this file,
* required canonical documents exist,
* and V3 cannot be marked shipped unless qualification gates are recorded.

## Architecture decision records

Major changes to:

* outcome semantics,
* authorization,
* task contracts,
* tools,
* verification,
* memory,
* skill learning,
* model training,
* rollback,
* or durable execution

must receive an architecture decision record.

---

# 10. Priority Rule

Until V3.0 qualification is complete, development priority is:

```text
Reliability
→ Safety
→ Verification
→ Recovery
→ Learning quality
→ Operator usability
→ New integrations
→ Visual polish
```

New ants, dashboards, integrations, and visual features must not displace the framework phases required for dependable autonomy.

---

# 11. Final Direction

ANTHILL already has the beginnings of autonomy.

The next stage is not to give the colony more freedom.

The next stage is to make every action:

* durable,
* bounded,
* typed,
* isolated,
* recoverable,
* verifiable,
* evidence-backed,
* and teachable.

V3.0 is reached when ANTHILL no longer merely says that an operation succeeded.

It reaches V3.0 when it can prove what happened, recover when it is wrong, and stay inside the authority its operator granted.
