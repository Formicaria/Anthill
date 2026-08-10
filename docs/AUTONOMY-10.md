# ANTHILL — THE 10/10 AUTONOMY PROGRAM

**The forward program, in phases with exit gates.** Baseline reviewed: v3.8.30. Adopted: v3.8.32.

This is the long arc from "structurally complete and test-backed" to "production-qualified autonomous
assistant". It answers **what is left and in what order**.

[`PLAN.md`](PLAN.md) remains the single record of **where the colony measurably is today**. These two
documents answer different questions on purpose, and the split is what keeps them from drifting the
way the four documents PLAN.md replaced did:

| Document | Question | Rule |
|---|---|---|
| [`PLAN.md`](PLAN.md) | Where is the colony **now**? | Everything MEASURED against the tree |
| This file | What is left, in what **order**? | Every phase has an EXIT GATE, and a phase is not done until the gate passes through the real composed runtime |

`CHANGELOG.md` remains the complete record of what shipped.

---

## What "10/10" means

Three levels, all three demonstrated:

1. **Autonomous mission runner.** The Queen plans, dispatches, supervises, repairs, verifies and
   closes a real mission through the fully composed runtime. All twelve ants are available and
   functional when their capabilities are relevant — they do not all run on every mission. Failure
   recovery, cancellation, restart recovery, evidence handling and memory promotion work end to end.

2. **Autonomous coding agent.** Anthill inspects a repository, proposes an atomic patch set,
   materializes it in isolation, tests it, repairs failures, performs a security review, verifies the
   result, and produces a reviewable branch or pull request. It never silently overwrites files,
   bypasses failed checks, or treats an unverified change as success.

3. **General autonomous assistant.** Safe operation across research, knowledge, personal operations,
   software, infrastructure, security and data workflows. External writes use scoped credentials,
   idempotency, dry runs, verification, rollback and risk-based approval. Long-running operation is
   observable, budgeted, recoverable, and proven through a production soak.

---

## Non-negotiable design rules

- **"All ants active" means every role is AVAILABLE to the Queen**, not that every role must be
  invoked for every objective. Role selection follows capability and mission need, never a fixed
  ceremonial sequence.
- Safety-critical stages may be inserted by deterministic policy even when the planner omits them.
- **Model output is never authoritative** for a security verdict, test result, policy decision or
  verification verdict.
- Memory receives positive reinforcement only from **consumed** artifacts that contributed to a
  **verified** outcome.
- Proposed code is DATA until the trusted runtime materializes it in an isolated workspace.
- A patch set is ATOMIC: every operation succeeds, or the entire set is rejected and rolled back.
- No direct writes to the protected/default branch.
- Every success claim points to durable evidence.
- **Unit tests written by the same implementation are necessary but not sufficient.** Every major
  capability requires a composed-runtime test.

That last rule is the one this repository has paid for most. See `PLAN.md` §6: eleven defects of the
form "a check that answers a question adjacent to the one asked, and passes", and eight subsystems
implemented, tested and unreachable.

---

## Phase status at v3.8.32

Phase 1 was partially delivered by v3.8.32, which was written against an external review rather than
against this plan — the overlap is real and is recorded honestly rather than re-litigated.

| Phase | State at v3.8.32 |
|---|---|
| 1. Correctness and safety invariants | **PARTIAL** — see the table below |
| 2. Real full-roster runtime profile | PARTIAL — profiles and readiness exist; the composed-runtime fixture does not |
| 3. Typed artifact collaboration | PARTIAL — artifacts travel alongside prose (v3.8.29); prose is still primary |
| 4. Structurally enforced workflows | PARTIAL — tester/soldier inserted by policy (v3.8.26); mandatory retest unproven |
| 5. Real qualification | **NOT STARTED** — no composed-runtime scenario matrix |
| 6. Consequential pheromone memory | PARTIAL — attribution correct (v3.8.32); nothing routes on reputation |
| 7. Autonomous coding lifecycle | NOT STARTED |
| 8. Colonies and connectors | NOT STARTED |
| 9. Safe self-improvement | NOT STARTED |
| 10. Production qualification | NOT STARTED |

### Phase 1, item by item

| Item | State |
|---|---|
| 1. One shared patch application engine | **DONE v3.8.32** — `PatchApply`; three divergent implementations collapsed into one |
| 2. Atomic patch sets | PARTIAL — `PatchSetMaterializer` fails as a unit into a sandbox; there is no staged transaction over the LIVE tree |
| 3. Exact operation semantics | PARTIAL — add/modify exact and guarded; **delete and rename are not implemented**, and `add` onto an existing file overwrites (backed up) rather than failing |
| 4. Terminal retry behaviour | **DONE v3.8.32** — the handoff gate reads the scheduler's terminal-failure signal |
| 5. Canonical failure classification | **DONE v3.8.32** for the string form (`FailureClassNames`, one converter, guarded). The taxonomy itself is not yet the plan's thirteen classes |
| 6. Fail closed on configuration | PARTIAL — readiness corrected v3.8.32; **empty auto-apply allowlist is not yet proven to fail closed** |
| 7. Correct readiness checks | **DONE v3.8.32** — `RoleReadiness`, `RoleGateStatus.NotGated`, core ants no longer blocked by gates they do not have |

**Base hashes are the largest single gap in Phase 1.** The plan requires `modify`/`delete` to fail
when the target's expected base hash differs. Nothing in the tree carries a base hash per proposal
today, so a patch built against a stale read applies silently. That is the next correctness item.

---

## Phase 1 — correctness and safety invariants

Remove the remaining places where runtime paths can disagree about patching, retries, failure
classes, readiness or automatic application.

1. One shared `PatchApplicationEngine`. Queen materialization, validation, test fixtures and any
   future branch/PR workflow call the same implementation. Delete duplicates after migration.
2. Atomic patch sets: stage everything, validate paths / operation types / base hashes / expected
   file state / policy BEFORE writing, apply to an isolated transaction area, commit only after every
   operation succeeds, and on any failure retain the original tree exactly.
3. Exact operation semantics.
   - `add` — fail if the target exists, unless an explicitly authorized create-or-replace operation
     is introduced later.
   - `modify` — fail if the target is absent or its expected base hash differs.
   - `delete` — fail if the target is absent or its expected base hash differs.
   - `rename` — require an existing source, an absent destination, validated source content.
   - Reject path traversal, symlink escape, absolute paths, protected paths, binary/text mismatches
     and ambiguous casing.
4. Terminal retry behaviour: when a recovery role exhausts its budget the scheduler emits ONE
   explicit terminal result. No task remains pending, running or recoverable afterwards.
5. Canonical failure taxonomy across providers, tools, ants, scheduler, evidence, memory, UI and
   telemetry. Minimum classes: `transient_provider`, `permanent_provider`, `tool_failure`,
   `policy_denial`, `invalid_artifact`, `patch_conflict`, `test_failure`, `security_failure`,
   `verification_failure`, `timeout`, `cancellation`, `dependency_failure`, `internal_runtime_failure`.
6. Fail closed on configuration: an empty auto-apply allowlist means nothing auto-applies; missing or
   malformed policy cannot widen authority; readiness fails when the selected profile's dependencies
   are missing even though the process is alive.

**Exit gate.** All patch entry points pass one shared conformance suite. Atomic rollback demonstrated
for failure at every operation position. Empty allowlists, malformed configuration, path escapes,
stale base hashes and add-overwrite attempts rejected. Exhausted recovery always produces one durable
terminal result. Liveness and readiness report different states correctly.

---

## Phase 2 — a real full-roster runtime profile

Make the complete colony an intentional, supported configuration and prove it runs outside isolated
role tests.

1. Explicit roster profiles in configuration and the operator UI: `core` (smallest safe
   local/research profile), `full` (all twelve with production policy enforcement), `qualified` (the
   exact deterministic profile CI uses).
2. The full profile enables and validates all twelve: researcher, web, file, coder, builder,
   verifier, ui_cartographer, tester, soldier, scribe, medic, archivist.
3. Preserve meaningful role design. Tool-less does not mean inactive. Coder proposes patch artifacts
   and trusted runtime code applies them; soldier uses a deterministic policy service; verifier reads
   authoritative evidence; medic diagnoses and recommends recovery without erasing failure evidence;
   archivist promotes only eligible verified memory.
4. A startup capability report: every registered role, whether enabled, its contracts, tools or
   deterministic services, provider requirements, policy constraints and current readiness. **A role
   must not appear healthy if its only required capability is absent.**
5. Exercise the real host. Build tests through the production DI composition root — real Queen,
   Director, scheduler, modules, tool registry, policy engine, evidence store and database.
   Substitute only external nondeterminism, through a scripted provider and controlled tool adapters.

**Exit gate.** The full profile starts with all twelve available and correctly reported. At least one
composed mission invokes every role through a legitimate trigger across the qualification scenario
set. No role is called merely to satisfy a count. Disabled, degraded, unavailable and ready are
distinct observable states.

---

## Phase 3 — typed artifact collaboration

Replace loosely interpreted prose handoffs with validated, traceable artifacts.

**Required types:** `objective_spec`, `context_brief`, `source_set`, `file_set`, `ui_map`,
`patch_set`, `build_report`, `test_report`, `security_review`, `failure_context`, `diagnosis`,
`repair_plan`, `verification_bundle`, `mission_summary`, `memory_candidate`.

**Every artifact carries:** schema name and version; artifact ID; mission / objective / task /
producing-role IDs; creation time; content hash; references to parent and input artifacts; declared
assumptions and limitations; provider / tool / runtime provenance; consumption records; validation
status; evidence references; sensitivity and retention classification.

**Runtime behaviour.** Validate at production AND consumption boundaries. Reject incompatible schemas
explicitly — never silently coerce them into success. Keep large content in the artifact/evidence
store and pass references. Record which downstream role consumed which exact artifact version. Make
provenance queryable from the colony UI and the mission record.

**Exit gate.** Every cross-role handoff in the qualification scenarios uses a typed artifact or a
documented control signal. The runtime can reconstruct the complete producer-to-consumer graph for a
mission. Invalid, stale, tampered or missing artifacts fail with a canonical reason.

---

## Phase 4 — structurally enforced workflows

Make required quality and safety stages runtime invariants rather than things the planner must
remember.

**Policy insertion rules.** Any materialized code patch requires Tester before Verifier. Any
security-sensitive or externally writable change requires Soldier. Any UI-changing objective requires
ui_cartographer input before Coder. Any failed test/build/security stage requires a `failure_context`
artifact. **Any repaired patch requires a fresh test run — old evidence cannot verify a new artifact
hash.** Scribe may summarize only after a terminal outcome exists. Archivist may promote memory only
after verification and eligibility checks.

**The required repair loop.**

```
Coder patch proposal
  -> trusted sandbox materialization
  -> Builder
  -> Tester
  -> on failure: FailureContext
  -> Medic diagnosis and repair plan
  -> Coder repair proposal
  -> trusted rematerialization
  -> MANDATORY Builder/Tester rerun
  -> Soldier when policy requires it
  -> Verifier
  -> Scribe
  -> Archivist
```

**Operational requirements.** Bound repair attempts by policy, time, cost and repeated-failure
signature. Propagate cancellation to running model calls, tools, test processes and child tasks.
Enforce subprocess timeouts and kill the whole process tree. Detect loops where the same
patch/failure signature repeats without new information. Preserve failed attempts as evidence without
rewarding them as success.

**Exit gate.** A scripted mission demonstrates failure, diagnosis, repaired patch, mandatory retest,
security review, verification, summary and eligible memory promotion. Attempts to skip Tester,
Soldier or fresh evidence are blocked by policy. Cancellation and timeout leave no orphan process or
ambiguous mission state.

---

## Phase 5 — real qualification, not only unit tests

**Deterministic CI fixture:** production RuntimeHost and DI graph; real Queen and Director; real
scheduler, modules, role registry, policy engine, evidence store, tool registry and database; a
scripted reasoning provider with ordered responses, failures, delays and token/cost data; controlled
local repositories and fake external adapters; seeded clocks and deterministic IDs where practical.

**Required CI scenarios.**

1. Research mission with source artifacts and cited synthesis.
2. Local file inspection with policy-limited access.
3. Documentation-only patch.
4. Code patch that builds and tests successfully.
5. UI patch requiring ui_cartographer.
6. Failing patch repaired through Medic and retested.
7. Security violation blocked by Soldier.
8. Provider outage with typed degradation or clean terminal failure.
9. Tool timeout and process-tree cancellation.
10. User cancellation during a running mission.
11. Runtime restart with mission recovery and no duplicate side effect.
12. Base-hash conflict that refuses unsafe application.
13. Empty auto-apply allowlist that applies nothing.
14. Memory candidate rejected because the outcome was unverified.
15. Full-roster mission set that legitimately reaches all twelve roles.

**Live qualification.** Scheduled tests against supported Ollama, OpenAI-compatible and Anthropic
paths, kept SEPARATE from deterministic merge-blocking CI. Capture provider/model/version, tool
versions, timings, token usage, cost, artifacts and exact failure class. Treat a fallback-generated
mission as degraded, never silently verified.

**Exit gate.** Deterministic scenarios are stable and merge-blocking. Live-provider qualification runs
on schedule and exposes regressions. `/colony` or its successor shows a real mission trace with
dispatch, artifacts, evidence, retries, costs and terminal verdict.

---

## Phase 6 — pheromone memory that changes behaviour safely

**Layers.** Episodic (mission-specific facts, attempts, artifacts, outcomes); semantic (stable
knowledge distilled from multiple verified episodes); procedural (validated workflows, repair
patterns, tool sequences, policy constraints).

**Promotion rules.** Only verified outcomes create positive reputation or reusable procedure. Credit
only artifacts actually CONSUMED by the successful path. Record negative outcomes and failure
signatures without treating them as universally bad worker reputation. Separate worker quality from
provider failure, tool failure, task difficulty, policy denial and infrastructure failure. Require
provenance, confidence, expiry/decay and invalidation links. Allow corrected evidence to supersede
older memory without deleting history.

**Routing and reputation.** Explore until each role/provider/tool route has enough observations.
Score by task type, not one global popularity number. Include quality, latency, cost, safety and
reliability. Apply recency weighting and decay. **A high score may never bypass contracts, policy or
required stages.** Show why a route was selected.

**Imported knowledge.** Imported ChatGPT conversations or other histories enter as UNTRUSTED memory
candidates. Extract claims and procedures with provenance. Replay or validate against current tools,
policy and environment before promotion. Never import a historical answer directly as verified
procedural memory.

**Exit gate.** A controlled benchmark shows routing improving after verified experience. Injected
provider/tool failures do not damage worker reputation. Unverified, stale, contradicted and unused
artifacts cannot earn positive pheromone weight. Operators can inspect, invalidate, export and purge
memory.

---

## Phase 7 — autonomous coding lifecycle

Carry a bounded software objective from issue to reviewable pull request while protecting the source
repository.

1. Resolve the objective and acceptance criteria.
2. Create a dedicated branch and isolated worktree/sandbox.
3. Record the exact base commit and repository policy.
4. Inspect code, dependencies, tests and local instructions.
5. Produce a typed `patch_set` with base hashes.
6. Materialize through the patch application engine.
7. Build, lint, format and test using project policy.
8. Repair within bounded attempts.
9. Run deterministic security/policy checks.
10. Verify evidence against the FINAL artifact hashes.
11. Create a coherent commit with a generated manifest.
12. Push a branch and open a pull request when authorized.
13. Consume CI results and review feedback.
14. Produce bounded follow-up fixes and requalify.
15. Merge only under the configured approval policy.

**Controls.** Never commit directly to the protected/default branch. Reconcile or reject when the base
branch moves. No retained change without final verification. No hidden test disabling, policy
weakening, generated secret or unrelated rewrite. Store objective→diff, diff→test and test→verdict
traceability. Support a dry-run mode that stops before any external write. Require explicit authority
for push, PR creation and merge.

**Targets.** ≥90% completion on a representative internal benchmark of bounded repository tasks. Zero
unverified retained patches. Zero direct writes to the protected branch. Recovery from restart with
no duplicate commits or PRs. Every PR includes scope, risks, evidence, unresolved limitations and
rollback guidance.

**Exit gate.** Anthill completes representative documentation, defect-fix, feature, UI and test-repair
tasks from objective through reviewable pull request. CI failure and review feedback produce bounded,
reverified follow-ups.

---

## Phase 8 — general assistant colonies and connectors

**Colonies.** Software Engineering; Research and Knowledge; Personal Operations; Infrastructure and
Reliability; Security and Compliance; Data and Automation.

**Connector SDK requirements.** Credential handles — models never receive raw secrets. Explicit OAuth
scopes and least privilege. Separate read and write capabilities. Typed inputs and outputs.
Idempotency keys for side effects. Dry-run/preview where supported. Post-action verification.
Compensating rollback where possible. Risk classification and approval policy. Rate limits, budgets,
timeouts and retry rules. Durable external action receipts. Event, webhook and schedule support.

**Risk tiers.** Tier 0 local read-only and reversible computation · Tier 1 external read-only · Tier 2
reversible external writes requiring policy and a visible receipt · Tier 3 high-impact writes
requiring explicit approval · Tier 4 prohibited.

**Exit gate.** At least one qualified workflow in every colony. Restarting a workflow cannot duplicate
an external side effect. Every external write is attributable, authorized, verified and visible.
Connector loss or permission denial produces a clean, recoverable state.

---

## Phase 9 — safe self-improvement

**Loop.** Mine verified failures, expensive successes, repeated repairs and operator corrections for
candidates → build deterministic replay cases → generate proposed prompt, policy-neutral routing,
procedure or code changes on an isolated branch → run the complete historical and synthetic benchmark
suite → compare quality, safety, latency, cost and regression metrics → canary on a limited workload
→ roll back automatically if guardrails regress → promote only after the configured review threshold.

**Boundaries.** Synthetic outcomes cannot create verified positive memory on their own. **The system
cannot widen its own credentials, approval thresholds, protected paths, risk tier or merge
authority.** Changes to policy, identity, authority, retention or security controls require human
approval. Benchmarks and evaluators are versioned separately from the proposals they judge. Preserve
enough historical behaviour to detect reward hacking and benchmark overfitting.

**Exit gate.** A measurable improvement on held-out tasks with no guardrail regression. A failed canary
rolls back automatically. Authority-changing proposals stop for human review.

---

## Phase 10 — production qualification

**Required.** A 30-day controlled soak on representative workloads. Fault injection for provider
outage, network loss, disk pressure, database interruption, tool hangs, corrupted artifacts, restart,
duplicate delivery and partial connector failure. External security review and threat-model
validation. Signed builds, SBOM, dependency scanning and release provenance. Backup, restore,
migration and disaster-recovery drills. Resource, token, cost, concurrency and external-action
budgets. Alerting for stuck missions, repeated repair loops, degraded providers, verification gaps,
memory anomalies and policy denials. Operator-visible evidence and a documented incident/runbook
process. Upgrade and rollback tests across supported database and configuration versions.

**Scorecard.** Task completion rate by mission class · verified success rate · **false-success rate** ·
unsafe-action count · mean repair attempts · mean time to recovery · cancellation latency · restart
recovery rate · duplicate side-effect count · cost and latency by verified outcome · memory precision
and invalidation rate · security and policy escape count · operator intervention rate.

**Exit gate.** No critical security or configuration findings open. Zero unsafe protected-branch
writes. Zero duplicate high-impact side effects. Backup/restore and rollback drills pass. SLOs hold
for the whole soak window. **The default full-autonomy profile is enabled only after these gates
pass.**

---

## Release sequence

The source plan proposed v3.8.31 and v3.8.32 for phases 1 and 2. Both versions shipped against a
different program before this plan was adopted, so the sequence is renumbered from where the tree
actually is. Nothing else changed.

| Release | Content |
|---|---|
| ~~v3.8.31~~ | Shipped: trail vocabulary correction, per-task model metrics, documentation reconciliation |
| ~~v3.8.32~~ | Shipped: five externally-reported defects and their guards. Delivered Phase 1 items 1, 4, 5 (string form) and 7 |
| ~~v3.8.33~~ | Shipped: the hardcoded local model removed and resolved instead; the console's unusable-model state surfaced |
| ~~v0.3.8.34~~ | Shipped: version line renumbered to v0; console route agreement (PR #215) merged in; the retired default recognised in already-written config files; console executable attributes escaped for the interpreter; every /status field required to have a consumer |
| **v3.9.0** | **Finish Phase 1.** Base hashes on proposals; delete/rename semantics; `add` refuses an existing target; atomic staging over the live tree; empty auto-apply allowlist fails closed; the thirteen-class taxonomy |
| v3.9.1 | **Phase 2.** Roster profiles, startup capability report, production-composed runtime fixture, scripted reasoning provider, observed Queen dispatch, full-roster mission suite |
| v3.10.0 | **Phase 3.** Versioned artifacts, provenance, consumption graph, validation boundaries, evidence linkage |
| v3.10.1 | **Phase 4.** Policy insertion, mandatory retest, cancellation and process-tree handling, bounded repair loops |
| v3.11.0 | **Phases 5 and 6.** Deterministic scenario matrix, scheduled live-provider tests, memory layers, reputation routing, decay, invalidation, imported-memory validation |
| v3.12.0 | **Phase 7.** Branch/worktree execution, commit and PR lifecycle, CI and review feedback, conflict handling, receipts, merge approval policy |
| v4.0.0 | **Phases 8–10.** Colony framework, connector SDK, risk-tier approvals, events and schedules, self-improvement canaries, production qualification |

### Immediate order for v3.9.0

1. Base hashes on patch proposals, and `modify`/`delete` refusing a stale base.
2. Atomic staging over the live tree, with rollback proven at every operation position.
3. `delete` and `rename` semantics.
4. `add` refuses an existing target unless an authorized create-or-replace is requested.
5. Empty auto-apply allowlist fails closed.
6. The canonical thirteen-class failure taxonomy.

---

## Working rules while implementing

1. Work in phase order. Do not add broad autonomy on top of unresolved correctness invariants.
2. Before changing a workflow, trace the complete producer → artifact → consumer → evidence → memory
   path.
3. Use the production composition root in qualification tests. **Do not create a second miniature
   architecture that only exists in tests.**
4. Do not add tools merely to increase the number of tool-using ants. Give each role the minimum
   capability its contract genuinely requires.
5. Do not turn deterministic security, policy, test or verification decisions into model judgments.
6. Keep each change small enough to review, and give it a specific exit gate from this document.
7. Update configuration examples, operator UI, architecture docs, tests, migration notes and
   versioning with every behaviour change.
8. For every completion claim report: exact files changed, exact tests run, composed-runtime
   evidence, remaining gaps, and migration or compatibility impact.
9. **Do not mark a phase complete because all newly written unit tests pass.** Demonstrate its exit
   gate through the real runtime path.
10. If implementation reveals a conflict with this plan, preserve the safety invariant, document the
    conflict, and propose the smallest revised design.

---

## Milestones

- **Phases 1–5** — Anthill can credibly earn 10/10 as an autonomous mission runner.
- **Phases 6–7** — as an autonomous coding agent with useful pheromone learning.
- **Phases 8–10** — as a broad autonomous assistant platform.

The standard is **demonstrated closure, not roster size**: a real objective taken through bounded
action, failure handling, evidence-backed verification and safe learning, with no hidden manual
repair and no invented success.
