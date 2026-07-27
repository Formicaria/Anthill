# ANTHILL — IMPLEMENTATION ROADMAP

> **Tactical companion to:** `docs/NORTH_STAR.md`
> **Rebased from:** ANTHILL v2.7.0
> **Target:** ANTHILL v3.0.0 — Bounded Autonomous Homelab Operator
> **Status:** Active
> **Latest:** v2.18.0 — Shadow Operations & Operator Qualification (Phase 7) Stage 2: the fault-injection scenario catalog + simulation harness (Stage 1, v2.17.0, shipped the non-executing recommendation engine + qualification scoreboard).
>
> This document replaces the previous objective-lifecycle and patch-review roadmap.
>
> `NORTH_STAR.md` defines the destination, release order, architectural principles, autonomy doctrine, and V3 qualification gates.
>
> This document defines the concrete systems, implementation tracks, dependencies, operator interfaces, technical debt, and supporting work required to execute that direction.

---

# 1. Document Authority

ANTHILL uses the following planning hierarchy:

```text
docs/NORTH_STAR.md
    Strategic direction, release order, permanent rules, V3 gates
        ↓
docs/ROADMAP.md
    Tactical implementation sequence and cross-cutting work
        ↓
GitHub milestones and epic issues
    Release-level ownership and acceptance criteria
        ↓
GitHub issues and pull requests
    Individual implementation work
```

When documents conflict:

1. `NORTH_STAR.md` wins on project direction and release order.
2. `ROADMAP.md` wins on tactical sequencing within a release.
3. Architecture-specific documents define implementation contracts.
4. GitHub issues may refine work but may not silently weaken North Star requirements.

Subsystem documents must not create independent competing roadmaps.

---

# 2. Current Baseline

ANTHILL v2.7.0 is the starting point for this roadmap.

Existing foundations include:

* objective-based autonomous missions,
* Colony Director,
* Strategist-generated goals,
* concurrent mission execution,
* resource-aware scheduling,
* mission budgets,
* kill switches,
* follow-up objectives,
* objective scoring and retirement,
* patch proposals,
* approval workflows,
* gated auto-apply,
* build-and-test verification,
* patch rollback,
* mission and pheromone memory,
* homelab inventory,
* health monitoring,
* Proxmox integration,
* backup awareness,
* incidents,
* dependency mapping,
* security findings,
* approval-gated homelab actions,
* automation rules,
* network control foundations,
* and a homelab operations interface.

The remaining work before V3 is primarily concerned with making these capabilities:

* durable,
* typed,
* isolated,
* recoverable,
* independently verifiable,
* evidence-backed,
* reusable,
* and qualified for unattended operation.

---

# 3. Release Map

```text
V2.7.0   Full homelab operations layer                 [BASELINE]

V2.8.0   Durable Mission Runtime                       [SHIPPED v2.8.0]
V2.9.0   Contracted Tasks and Typed Capability Tools   [SHIPPED v2.9.0]
V2.14.x  Topology-first Dashboard console track  [COMPLETE at v2.15.0]
         Canonical status + next steps: docs/DASHBOARD_WORKSPACE.md ("WHERE WE ARE").
         Done: workspace state model + kill switch · panel shell · drag/resize/snap · chambers as
         a canvas layout · map prefs on the canvas · per-ant pheromone truth · draggable +
         renamable chambers · chamber SVG retired (one renderer) · seven functional chambers ·
         dashboard cards registered as panels · editable Ant Inspector · topology as the
         dashboard canvas · hideable/re-anchorable topology overlays · workspace sanitizer wired
         into /ui/state · nine panels incl. inspector + jobs · persistent topology via the Colony
         redirect · standby chambers legible.
         v2.15.0 completed the track: tab groups · docking to all four edges with server-enforced
         rail budgets · one writer for ui_state.json · default layout · responsive/a11y ·
         dashboard_workspace_enabled DEFAULT ON; v2.15.1 made the map full-page, promoted the last six cards to panels, and replaced docking with edge/corner snapping; v2.15.2 fixed the workspace containing block and the fixed-chrome vertical budget; v2.15.3 fixed the classic-page hide rule; v2.16.0 added plain-English mission answers and the Missions conversation view, still an instant rollback when set false.

V2.9.x   Ant Execution Framework tactical track (docs/ANT_EXECUTION.md):
         runtime classification · execution contracts · structured results ·
         capability-enforced dispatch · canary activation (ui_cartographer, tester,
         soldier, scribe, medic, archivist — gated off by default) · planner routing ·
         bounded handoffs · truthful UI status · validation gates
V2.10.0  Sandboxed Agent Execution                    [SHIPPED v2.10.0 — primitives; agent wiring v2.10.x]
V2.11.0  Independent Verification and Evidence
V2.12.0  Procedural Skills and Evaluated Learning
V2.13.0  Safe Action Engine and Recovery Orchestration
V2.14.0  Shadow Operations and Operator Qualification

V3.0.0   Bounded Autonomous Homelab Operator
```

Each pre-V3 release contains:

1. A **gating track** required by the North Star.
2. An **operator track** exposing the capability safely.
3. A **validation track** proving the capability works.
4. A **platform track** addressing supporting reliability and maintenance.
5. Documentation and migration requirements.

---

# 4. Status Definitions

Every roadmap item must use one of these statuses:

```text
PLANNED
SPECIFIED
ACTIVE
BLOCKED
VALIDATING
SHIPPED
DEFERRED
REMOVED
```

## Status rules

### PLANNED

The work is accepted into the roadmap but does not yet have a complete implementation specification.

### SPECIFIED

Interfaces, dependencies, risks, migrations, and acceptance criteria are documented.

### ACTIVE

Implementation is underway.

### BLOCKED

A named dependency prevents progress.

### VALIDATING

Implementation exists but release gates, tests, or operator validation are incomplete.

### SHIPPED

Code, tests, documentation, migration behavior, observability, and release notes are complete.

### DEFERRED

Work remains valid but is intentionally moved beyond its original release.

### REMOVED

The work no longer aligns with the project direction.

A feature must not be marked shipped merely because its primary class or UI panel exists.

---

# 5. V2.8.0 — DURABLE MISSION RUNTIME

## Release purpose

Replace process-local mission execution with a durable runtime that survives crashes and restarts without losing or duplicating work.

## Gating work

### Durable job records

Persist:

* queue state,
* objective claims,
* mission claims,
* worker ownership,
* leases,
* heartbeats,
* attempts,
* cancellation requests,
* recovery state,
* compensation state,
* and terminal outcomes.

### Transactional objective claiming

Objective selection and ownership must occur atomically.

### Worker lease manager

Implement:

* lease acquisition,
* heartbeat renewal,
* clean release,
* lease expiration,
* safe reclamation,
* and lease-conflict reporting.

### Startup reconciliation

Classify incomplete work during service startup:

```text
resume
retry
compensate
orphan
operator_review
permanent_failure
```

### Idempotency framework

Create persistent idempotency records for:

* mission launches,
* task attempts,
* action proposals,
* provider operations,
* patch promotions,
* notifications,
* incident creation,
* and follow-up objective creation.

### Attempt isolation

Retries must create distinct attempts without replacing prior evidence.

## Operator work

### Runtime Operations page

Add an operator interface showing:

* queued missions,
* claimed missions,
* active workers,
* lease expiration,
* heartbeat health,
* retries,
* orphaned work,
* startup reconciliation results,
* and cancellation state.

### Recovery controls

Allow authorized operators to:

* retry,
* resume,
* abandon,
* compensate,
* force release,
* or mark a mission for review.

All controls must create audit events.

### Mission timeline foundation

Create a unified timeline joining:

```text
Objective
→ Autonomy run
→ Mission
→ Task attempts
→ Tool calls
→ Artifacts
→ Verification
→ Actions
→ Rollback
→ Outcome
```

This begins as an operational data model in V2.8. Full evidence rendering lands in V2.11.

## Platform work

### Colony database backup

Add operator-supported backup and restore for ANTHILL’s own database and configuration.

Requirements:

* consistent SQLite backup,
* encrypted-secret preservation,
* pre-migration backup,
* restore validation,
* and version compatibility reporting.

### Migration safety

Database migrations must be:

* additive where possible,
* idempotent,
* transaction-wrapped,
* backed up before execution,
* and tested against the previous supported release.

### Runtime metrics

Expose:

* queue depth,
* oldest queued age,
* active leases,
* expired leases,
* retry rate,
* orphan count,
* task duration,
* and recovery count.

### Data retention

Define retention policies for:

* attempts,
* tool outputs,
* logs,
* evidence,
* events,
* and abandoned missions.

Deletion must not remove required audit relationships.

## Validation work

* Process-kill test suite.
* Host-reboot test.
* Duplicate Director test.
* Lease-expiration test.
* Idempotent replay test.
* Side-effect-before-recording failure test.
* Migration rollback test.
* Database backup and restore test.

## Exit gate

V2.8 cannot ship until a mission can be interrupted at every major execution stage and recovered without silent loss or duplicate side effects.

---

# 6. V2.9.0 — CONTRACTED TASKS AND TYPED CAPABILITY TOOLS

## Release purpose

Replace loosely structured prompt tasks and string-based tool control with validated contracts.

## Gating work

### Task contract schema

Implement versioned schemas for:

* tasks,
* constraints,
* required capabilities,
* expected artifacts,
* success criteria,
* verification plans,
* retry policies,
* and compensation plans.

### Planner validation

Planner output must pass:

* schema validation,
* DAG validation,
* capability validation,
* constraint validation,
* side-effect validation,
* and verification-plan validation.

Invalid plans must never enter execution.

### Typed tool registry

Every executable tool must register:

* input schema,
* output schema,
* version,
* permissions,
* side effects,
* supported targets,
* timeout behavior,
* cancellation support,
* idempotency behavior,
* compensation support,
* and evidence production.

### Structured error taxonomy

Standardize error codes for:

* validation,
* authorization,
* unavailable capability,
* target rejection,
* timeout,
* rate limit,
* provider conflict,
* transient failure,
* permanent failure,
* unsafe state,
* verification failure,
* and compensation failure.

### Capability grants

Issue mission-scoped capability grants rather than relying on ant identity alone.

## Operator work

### Capability Explorer

Show:

* registered capabilities,
* tools providing each capability,
* ants eligible to request them,
* risk level,
* target restrictions,
* side-effect class,
* verification requirements,
* and current enabled state.

### Task Inspector

Allow operators to view the complete validated task contract rather than only its natural-language description.

### Plan validation report

Show why a plan was:

* accepted,
* modified,
* rejected,
* or stripped of unsafe tasks.

## Platform work

### Contract versioning

Support schema evolution without silently changing the meaning of previously queued work.

Persist the exact contract version used by each mission.

### Compatibility adapters

Temporary adapters may wrap legacy tools, but they must:

* declare reduced guarantees,
* never pretend to support rollback,
* and be removed before V3 qualification.

### Provider SDK foundation

Define a standard provider interface for future integrations.

The provider SDK must cover:

* typed requests and responses,
* authentication references,
* target validation,
* health state,
* rate limiting,
* dry-run support,
* idempotency,
* audit data,
* and compensation.

### API schema publishing

Publish machine-readable API and task schemas for:

* internal validation,
* UI generation,
* contract tests,
* and external integration development.

## Validation work

* Invalid task rejection.
* Unknown capability rejection.
* Unauthorized capability test.
* Contract-version compatibility test.
* Tool cancellation test.
* Typed-error retry test.
* Compensation-token test.
* Legacy-adapter restriction test.

## Exit gate

V2.9 cannot ship while any autonomous state-changing path still depends on parsing free-form tool text to decide whether an operation succeeded.

---

# 7. V2.10.0 — SANDBOXED AGENT EXECUTION

## Release purpose

Allow iterative ant behavior while isolating autonomous work from the production checkout and uncontrolled host state.

## Gating work

### Workspace manager

Support:

* disposable Git worktrees,
* temporary clones,
* operator-defined writable sandboxes,
* workspace ownership,
* cleanup,
* and abandoned-workspace recovery.

### Container execution provider

Provide an optional ephemeral container executor with:

* CPU limits,
* memory limits,
* process limits,
* network policy,
* mounted workspace boundaries,
* execution timeout,
* and artifact extraction.

### Iterative agent loop

Agents may perform bounded:

```text
observe
→ select tool
→ execute
→ inspect
→ update state
→ replan
→ verify progress
```

### Loop detection

Detect:

* identical repeated calls,
* edit oscillation,
* repeated compiler failure without change,
* repeated diagnosis without new evidence,
* no-progress loops,
* and planner-executor disagreement.

### Untrusted-content handling

Repository content, logs, provider responses, tool output, websites, and issue text must not alter:

* system policy,
* permissions,
* target allowlists,
* approval requirements,
* or verification rules.

## Operator work

### Sandbox Sessions page

Show:

* active sandboxes,
* owning mission,
* current branch,
* resource usage,
* recent tool calls,
* generated artifacts,
* expiration,
* and cleanup state.

### Live execution trace

Expose a bounded live trace without revealing:

* credentials,
* hidden prompts,
* secret-bearing environment variables,
* or unredacted sensitive tool output.

### Sandbox diff review

Show the cumulative sandbox change, not only individual patch proposals.

## Platform work

### Artifact store

Store mission-produced artifacts separately from model output.

Examples:

* diffs,
* build reports,
* test results,
* logs,
* snapshots,
* configuration exports,
* and verification bundles.

### Resource-aware scheduling v2

Extend the Resource Governor with:

* GPU utilization,
* VRAM availability,
* loaded-model state,
* model queue depth,
* sandbox resource demand,
* and provider concurrency.

### Execution profiles

Add profiles such as:

```text
read_only
code_analysis
code_patch
test_execution
homelab_diagnostic
homelab_change
recovery
```

Each profile defines capability, network, filesystem, and resource boundaries.

### Dependency caching

Support safe package and build caching without sharing writable mission state.

## Validation work

* Production-checkout immutability test.
* Sandbox escape tests.
* Resource-limit tests.
* Prompt-injection boundary Sandbox escape tests.
* Loop-detection tests.
* Abandoned-workspace cleanup.
* Cancellation during tool execution.
* Concurrent sandbox isolation.

## Exit gate

V2.10 cannot ship until autonomous code modification can complete without writing to the active production checkout.

---

# 8. V2.11.0 — INDEPENDENT VERIFICATION AND EVIDENCE

## Release purpose

Create independent proof of outcomes and prevent structural completion from being treated as success.

## Gating work

### Verifier framework

Implement:

* verifier registration,
* typed requests,
* typed results,
* verifier policies,
* evidence collection,
* verifier timeouts,
* and independent reruns.

### Core deterministic verifiers

Required:

* build,
* test,
* diff,
* artifact,
* service health,
* infrastructure state,
* dependency health,
* security policy,
* rollback,
* and configuration-state verification.

### Evidence bundles

Persist immutable verification bundles containing:

* verifier identity and version,
* target identity,
* before state,
* intended state,
* observed after state,
* commands or requests,
* timestamps,
* hashes,
* result data,
* and pass/fail rationale.

### Outcome correction

Only `completed_verified` may count as operational success.

Correct all code paths that currently treat partial or merely completed work as success.

### Verification independence

The executing ant may suggest verification, but the runtime selects and runs the required verifiers according to policy.

## Operator work

### Mission Timeline v2

Render the complete mission history in one view:

* plans,
* task attempts,
* tool calls,
* patches,
* actions,
* approvals,
* verifier results,
* evidence,
* retries,
* rollback,
* and final outcome.

### Approval Center v2

Add:

* batch approval,
* risk grouping,
* duplicate detection,
* superseded-proposal handling,
* verification-readiness status,
* compensation availability,
* cumulative diff preview,
* and dependency-impact preview.

### Evidence viewer

Allow an operator to inspect and export verification bundles.

### Verification replay

Allow authorized operators to rerun verification without rerunning the original action.

## Platform work

### Evidence integrity

Use hashes and immutable references to detect later mutation.

### Secret redaction

Evidence storage must redact:

* credentials,
* tokens,
* private keys,
* sensitive headers,
* and configured secret patterns.

### Evidence retention

Define longer retention for:

* destructive actions,
* rollback failures,
* security events,
* and V3 qualification runs.

### Verification performance

Parallelize independent verifiers where safe.

## Validation work

* False structural-success test.
* Verifier independence test.
* Evidence mutation-detection test.
* Secret-redaction test.
* Verification replay test.
* Dependency regression test.
* Rollback verification test.
* Partial outcome reinforcement test.

## Exit gate

V2.11 cannot ship while model text, task completion count, output length, or executor self-assessment can independently produce a verified-success outcome.

---

# 9. V2.12.0 — PROCEDURAL SKILLS AND EVALUATED LEARNING

## Release purpose

Move from simple memory and scalar scoring toward reusable, versioned, evidence-backed operational skills.

## Gating work

### Memory classes

Separate:

* episodic memory,
* semantic memory,
* procedural memory,
* causal observations,
* negative memory,
* policy memory,
* and environment facts.

### Hybrid retrieval

Combine:

* exact metadata,
* full-text search,
* embeddings,
* freshness,
* environment compatibility,
* provenance,
* and verified outcome history.

### Skill registry

Implement versioned skills containing:

* preconditions,
* required capabilities,
* procedure,
* checkpoints,
* expected evidence,
* verification policy,
* compensation plan,
* compatibility information,
* and performance history.

### Skill lifecycle

Support:

```text
candidate
experimental
certified
degraded
retired
blocked
```

### Promotion and demotion

Use verified outcomes, not executor confidence, to change skill status.

### Negative learning

Record and retrieve:

* failed fixes,
* unsafe plans,
* operator rejections,
* rollback failures,
* incompatible environments,
* and outdated procedures.

## Operator work

### Skills page

Show:

* skill versions,
* certification state,
* supported environments,
* success and failure history,
* verifier coverage,
* last validation,
* and replacement skills.

### Memory provenance viewer

Show where retrieved information came from and whether it is:

* current,
* verified,
* inferred,
* operator-provided,
* or model-generated.

### Skill comparison

Allow comparison between competing procedures for the same objective.

### Operator feedback

Provide structured feedback:

* correct diagnosis,
* incorrect diagnosis,
* useful recommendation,
* unsafe recommendation,
* unnecessary action,
* incomplete verification,
* and preferred procedure.

## Platform work

### Model benchmark harness

Evaluate configured models by role using repeatable scenarios.

Track:

* plan validity,
* tool-selection accuracy,
* patch quality,
* hallucination rate,
* verifier agreement,
* latency,
* token usage,
* and resource demand.

### Model routing intelligence

Use benchmark and verified mission data to recommend role assignments.

Automatic routing changes must:

* remain within configured model allowlists,
* be reversible,
* and never alter authorization policy.

### Model Assignment UI

Expose:

* current model per role,
* benchmark performance,
* fallback order,
* circuit-breaker state,
* and recent routing changes.

### Training-data governance

Prepare a sanitized trajectory-export format for possible future offline training.

Do not add continuous online self-fine-tuning before V3.

### Memory maintenance

Add:

* duplicate detection,
* stale-memory retirement,
* compression,
* provenance preservation,
* and environment invalidation.

## Validation work

* Skill promotion threshold test.
* Skill demotion test.
* Stale-environment test.
* Negative-memory retrieval test.
* Model benchmark repeatability.
* Secret-scrubbed trajectory export.
* Retrieval provenance test.
* Learning-policy bypass test.

## Exit gate

V2.12 cannot ship until ANTHILL can distinguish a reusable certified procedure from an unverified piece of model-generated advice.

---

# 10. V2.13.0 — SAFE ACTION ENGINE AND RECOVERY ORCHESTRATION

## Release purpose

Unify code changes and homelab operations under one action, approval, verification, and recovery lifecycle.

## Gating work

### Unified action engine

Support a consistent lifecycle for:

* code changes,
* configuration changes,
* Proxmox actions,
* service operations,
* DNS changes,
* DHCP changes,
* firewall changes,
* backup actions,
* and future provider actions.

### Risk engine v2

Calculate risk using:

* target criticality,
* action reversibility,
* capability privilege,
* dependency impact,
* change novelty,
* backup freshness,
* downtime,
* environment type,
* skill confidence,
* verifier quality,
* and cumulative changes.

### Change-set transactions

Support multi-step operations with:

* checkpoints,
* step verification,
* stop conditions,
* compensation order,
* and partial-retention policy.

### Recovery orchestrator

Support:

* rollback,
* compensation,
* retry,
* cooldown,
* quarantine,
* failover,
* restore,
* capability revocation,
* and operator escalation.

### Circuit breakers

Allow circuit breakers by:

* target,
* provider,
* capability,
* action type,
* skill,
* automation rule,
* and model route.

### Pre-execution drift check

Before executing an approved proposal, confirm the current environment still matches the state against which it was approved.

## Operator work

### Unified Approval Center

Merge patch and infrastructure action approval into one queue with appropriate renderers.

### Recovery Center

Show:

* failed actions,
* available rollback,
* compensation progress,
* rollback verification,
* affected dependencies,
* cooldown state,
* and required operator decisions.

### Maintenance windows

Allow actions to be restricted by:

* target,
* capability,
* risk level,
* day,
* time,
* incident state,
* and operator presence.

### Safety profiles

Provide operator-selectable profiles:

```text
observe_only
recommend_only
approval_required
low_risk_autonomy
maintenance_window_autonomy
emergency_recovery
```

### Self-modification safety lanes

Classify autonomous code changes into:

```text
docs_only
tests_only
configuration
noncritical_source
dependency_change
database_migration
security_sensitive
runtime_critical
```

Each lane receives separate:

* permissions,
* approval requirements,
* verifier policies,
* diff limits,
* promotion rules,
* and rollback behavior.

## Platform work

### Promotion manager

Promote verified sandbox changes through controlled Git operations.

Requirements:

* protected-branch awareness,
* clean worktree validation,
* commit identity,
* cumulative diff validation,
* optional draft pull request,
* and no automatic push unless explicitly configured.

### ANTHILL update and rollback

Create a supported self-update mechanism with:

* release validation,
* configuration backup,
* database backup,
* binary replacement,
* health verification,
* and version rollback.

### Secrets lifecycle

Add:

* credential rotation metadata,
* expiration warnings,
* last-use auditing,
* provider-specific scopes,
* and revocation checks.

### Configuration profiles

Support exportable configuration profiles without exporting secret values.

## Validation work

* Environment-drift rejection.
* Multi-step compensation test.
* Rollback-failure circuit breaker.
* Stale approval expiration.
* Critical-file classification.
* Protected-branch rejection.
* Cumulative-diff risk test.
* Maintenance-window enforcement.
* Self-update rollback test.

## Exit gate

V2.13 cannot ship until every enabled state-changing capability either has verified recovery behavior or is explicitly restricted to human-approved execution.

---

# 11. V2.14.0 — SHADOW OPERATIONS AND OPERATOR QUALIFICATION

## Release purpose

Prove ANTHILL’s readiness through observation, simulation, replay, and controlled failure testing before granting V3 authority.

## Gating work

### Shadow operator

ANTHILL must generate complete proposed responses to real events without executing them.

Record:

* diagnosis,
* selected skill,
* proposed actions,
* risk,
* expected result,
* verification plan,
* and recovery plan.

### Incident replay

Replay historical incidents through the current:

* planner,
* skill selector,
* risk engine,
* verifier policy,
* and action engine.

### Fault-injection harness

Create repeatable failures for:

* services,
* VMs,
* storage,
* backups,
* DNS,
* networking,
* credentials,
* providers,
* mission workers,
* verification,
* and rollback.

### Qualification profiles

Certify each combination of:

* capability,
* provider,
* target class,
* skill,
* verification policy,
* and recovery strategy.

### Autonomy grant records

Store exactly which operations passed qualification and may be enabled at Level 3 or Level 4.

## Operator work

### Qualification Dashboard

Show:

* certified capabilities,
* failed scenarios,
* shadow accuracy,
* rollback performance,
* unsafe-action rate,
* verifier false positives,
* policy violations,
* and unresolved blockers.

### Autonomy Scope Editor

Allow the operator to enable autonomy per:

* capability,
* action,
* target,
* target class,
* skill,
* schedule,
* and risk threshold.

### Go/No-Go report

Generate a V3 readiness report with:

* passed gates,
* failed gates,
* known limitations,
* deferred integrations,
* unsupported recovery paths,
* and recommended maximum autonomy level.

### Emergency controls

Verify that operators can:

* stop new autonomous actions,
* cancel active actions,
* revoke temporary capabilities,
* place the colony in observe-only mode,
* and disable a provider.

## Platform work

### Performance and reliability targets

Define service-level objectives for:

* API availability,
* queue recovery,
* health-check execution,
* action latency,
* verification completion,
* UI responsiveness,
* and event persistence.

### UI performance pass

Measure and correct:

* initial dashboard load,
* Patch and Approval Center load,
* large mission timelines,
* event-stream rendering,
* dependency graph rendering,
* and long-running browser sessions.

### Accessibility pass

Provide:

* keyboard navigation,
* sufficient contrast,
* reduced-motion behavior,
* readable status labels,
* screen-reader structure,
* and non-color-only status indicators.

### Mobile operator view

Provide a responsive read and emergency-control experience for:

* incidents,
* pending approvals,
* active actions,
* autonomy state,
* and emergency stop.

### Documentation qualification

Verify all operator instructions against a clean installation.

## Validation work

* Shadow-mode comparison suite.
* Historical incident replay suite.
* Fault-injection suite.
* Emergency-stop drills.
* UI load and endurance tests.
* Clean-install operator walkthrough.
* Restore-from-backup drill.
* Provider outage drill.
* Prompt-injection drill.
* Qualification-report reproducibility.

## Exit gate

V2.14 cannot ship as V3-ready until qualification evidence exists for each capability that will receive autonomous authority.

Capabilities that fail qualification remain at Observe, Recommend, or Propose level.

---

# 12. V3.0.0 — BOUNDED AUTONOMOUS HOMELAB OPERATOR

## Release purpose

Enable certified, recoverable, independently verifiable homelab operations without requiring approval for every low-risk action.

## V3 scope

V3 authority is granted per capability.

There is no global unrestricted-autonomy switch.

Examples of potentially certifiable V3 operations:

* restart an approved noncritical service,
* start a stopped approved VM,
* retry a failed backup,
* create a diagnostic snapshot,
* revert a recent approved configuration,
* repair a known DNS record,
* run an approved cleanup operation,
* or apply a certified low-risk code change through the sandbox and promotion pipeline.

## V3 supporting work

### Autonomous Operations page

Show:

* current autonomy level,
* enabled capabilities,
* allowed targets,
* current action budget,
* active actions,
* cooldowns,
* circuit breakers,
* recent autonomous outcomes,
* and qualification status.

### Explainability record

For every autonomous action, retain:

* triggering observation,
* diagnosis,
* selected skill,
* rejected alternatives,
* policy decision,
* authorization decision,
* execution trace,
* evidence,
* recovery behavior,
* and learning update.

### Degraded mode

Automatically reduce authority when:

* verification is unavailable,
* provider health is degraded,
* backups are stale,
* resource pressure is high,
* repeated failures occur,
* database integrity is uncertain,
* or the environment no longer matches certification.

## V3 release gate

All North Star V3 definition-of-done requirements must be satisfied.

Passing the software release gate does not automatically enable autonomous execution on an operator’s installation.

Each installation must explicitly configure:

* capabilities,
* targets,
* budgets,
* risk limits,
* maintenance windows,
* verification policies,
* and emergency controls.

---

# 13. CROSS-CUTTING OPERATOR EXPERIENCE

These features support multiple releases and must be scheduled alongside their dependencies.

## Colony Command Center

The Command Center should become the unified operational surface for:

* objectives,
* queued missions,
* running tasks,
* incidents,
* action proposals,
* approvals,
* active autonomous actions,
* verification,
* recovery,
* and colony health.

It must prioritize operational clarity over visual novelty.

## Mission Timeline

The mission timeline begins in V2.8 and becomes complete in V2.11.

It must support filtering by:

* objective,
* mission,
* task,
* ant,
* capability,
* tool,
* target,
* outcome,
* and time.

## Completed Objectives archive

Retired, completed, stopped, and failed objectives should appear in a consolidated archive with:

* ending reason,
* mission count,
* verified outcome,
* produced changes,
* related incidents,
* and follow-up objectives.

## Event stream

Provide:

* structured filters,
* correlation IDs,
* severity,
* target,
* mission links,
* action links,
* and export.

## Search

Global search should cover:

* objectives,
* missions,
* tasks,
* incidents,
* actions,
* approvals,
* skills,
* evidence,
* hosts,
* services,
* VMs,
* containers,
* and documentation.

## Configuration usability

Configuration must clearly distinguish:

* settings that only affect recommendations,
* settings that allow proposals,
* settings that permit execution,
* and settings that permit autonomous execution.

Dangerous settings must display their dependencies and current qualification state.

---

# 14. CROSS-CUTTING PLATFORM ENGINEERING

## API compatibility

Maintain explicit API versions for external or persisted contracts.

Breaking changes require:

* documented migration,
* compatibility window,
* and release notes.

## Observability

Provide structured metrics and logs for:

* runtime,
* planners,
* models,
* tools,
* providers,
* verifiers,
* action execution,
* recovery,
* memory,
* and UI performance.

## Performance budgets

Define budgets for:

* API latency,
* dashboard load,
* model-call queue time,
* mission startup,
* database queries,
* and evidence rendering.

## Test architecture

Maintain distinct test layers:

```text
unit
contract
integration
provider
migration
security
fault_injection
end_to_end
qualification
```

## Clean-install support

Every release must be tested on:

* clean installation,
* upgrade from the prior supported version,
* backup restore,
* and configuration migration.

## Packaging

Maintain supported installation and update paths for the project’s intended Linux deployment model.

## Security review

Security-sensitive changes require review of:

* authentication,
* authorization,
* credential handling,
* command execution,
* filesystem boundaries,
* network boundaries,
* evidence redaction,
* and audit completeness.

---

# 15. ITEMS DEFERRED BEYOND V3

The following are not required before V3:

* unrestricted self-fine-tuning,
* autonomous production-model replacement,
* unrestricted plugin installation,
* unrestricted shell execution,
* fully distributed multi-host colony leadership,
* unrestricted cloud management,
* offensive-security automation,
* cross-organization fleet management,
* marketplace-hosted skills,
* and unrestricted self-expansion.

These may receive research documents but must not interrupt the pre-V3 reliability sequence.

---

# 16. RELEASE MANAGEMENT RULES

Each release must have one GitHub milestone and one release epic.

The release epic must contain:

* objective,
* dependencies,
* included systems,
* excluded systems,
* database changes,
* API changes,
* security implications,
* operator-visible changes,
* migration plan,
* rollback plan,
* required tests,
* documentation changes,
* and release gate.

Each implementation issue must contain:

* problem statement,
* acceptance criteria,
* dependencies,
* affected contracts,
* required tests,
* observability requirements,
* and documentation requirements.

A pull request must not close a roadmap item unless all acceptance criteria are met.

Partial implementations remain active or validating.

---

# 17. DEVELOPMENT PRIORITY

Until V3 qualification:

```text
Durability
→ Safety
→ Verification
→ Recovery
→ Contract quality
→ Skill quality
→ Operator clarity
→ Integrations
→ Visual polish
```

New integrations and ants may be added when they exercise and validate the current framework phase.

They must not bypass or postpone the framework itself.

---

# 18. ROADMAP COMPLETION DEFINITION

This roadmap is complete when:

1. V2.8 through V2.14 are shipped.
2. Every V3 autonomous capability has qualification evidence.
3. No autonomous action depends solely on model self-assessment.
4. Execution survives restart and replay safely.
5. State-changing tools are typed and permissioned.
6. Changes occur in isolated or recoverable environments.
7. Verification is independent and evidence-backed.
8. Recovery has been fault tested.
9. Skills are promoted by verified outcomes.
10. Operators can restrict, inspect, stop, and audit the system.
11. `NORTH_STAR.md`, this roadmap, subsystem documents, and runtime version agree.
12. V3 is enabled by explicit operator scope rather than by unrestricted default authority.
