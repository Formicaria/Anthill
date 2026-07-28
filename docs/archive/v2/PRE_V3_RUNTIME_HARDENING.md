# Pre-V3 Runtime Hardening — Audit and Execution Record

**As of:** v2.26.0 (this release) · branch `feat/pre-v3-runtime-hardening`
**Source:** external engineering deep-dive, verified claim-by-claim against the code before any
change was made. This document records what was confirmed, what was already fixed, what was
invalid, and exactly what this release does about each confirmed item.

**Governing principle (adopted verbatim):** One outcome. One verification authority. One durable
stop. One task lifecycle. One learning boundary. One action lifecycle.

---

## 1. Claim-by-claim audit

Every claim was verified against the source before implementation. Status vocabulary:
**CONFIRMED** (real defect, fixed in this release), **ALREADY FIXED** (addressed by v2.19–v2.25),
**PARTLY VALID** (real but narrower than stated), **INVALID** (not present in this codebase),
**NARROWED** (valid; implemented in reduced form in this release, with the reduction and its
reason stated — not deferred to a phase).

### §3 Canonical mission evaluation — CONFIRMED
`MissionVerification.IsSatisfied`/`IsSatisfiedFromRows` had six independent call sites deciding
success: `Queen.FinalizeMission` (×2), `ColonyDirector.ReadOutcome` (row re-derivation),
`SqliteMemory.UpdateMissionPheromones` (its own re-derivation), `ObjectiveVerification`, and
`AdaptiveMissionController` (mid-mission — legitimate, see below). Live and restored evaluation
could disagree because task rows lack fields the live path uses. **Fix:** `MissionEvaluation`
(typed outcome vocabulary) computed exactly once at finalization by `MissionEvaluator`, persisted
on `missions` (migration 16), consumed by every downstream positive path. The helpers survive
only as internals of the evaluator plus the adaptive controller's *mid-mission progress* check,
which is not a mission-final authority and is documented as such.

### §4 Verification integrity — CONFIRMED (two defects)
1. `VerificationBundle.Promotable` did not require deterministic evidence — a separate
   `HasDeterministicEvidence` property existed that callers had to remember. **Fix:** the
   invariant is now intrinsic to `Promotable`.
2. `Queen.MissionEvidenceBundle` fabricated `new VerificationResult("mission_verifier",
   Passed: true, Deterministic: false, ...)` and used it for skill credit. **Fix:** removed.
   Mission evidence is built from the actual verifier task outputs; a verified mission without
   deterministic evidence records a **neutral** skill observation, never a success.

### §5 Durable STOP — CONFIRMED
`ColonyDirector.Start()` called `AutonomyControl.Resume()` — and the `--autonomous` boot path
calls `Start()`, so a process restart cleared a durable operator STOP. **Fix:** `Start()` never
clears STOP. The boot autostart constructs the director, reports STOP engaged, and launches
nothing. `POST /autonomy/start` (an explicit operator action) is the only path that resumes,
and it now does so via an explicit `AutonomyControl.Resume()` at the endpoint with an audit
event. Restart tests added. Homelab STOP reviewed: `HomelabActionControl` has no equivalent
auto-clear (nothing calls its resume on boot) — no change needed.

### §6 Structured concurrency — PARTLY VALID
Claim "cancellation does not reach model calls" is **INVALID**: `ModelCallScope` carries the
mission token, each task runs under a linked per-task CTS with `CancelAfter(MaxTaskSeconds)`,
and providers observe it. The **CONFIRMED** part: on mission timeout/cancel,
`ExecuteTasksParallel` returned immediately while `Task.Run` futures were still executing — a
terminal mission could contain running tasks, and late results could land after finalization.
**Fix:** bounded drain — stop dispatch, wait a shutdown grace period for running futures, mark
non-terminating tasks `timed_out`/`cancelled` with a persisted cancellation reason, and assert
at finalization that no task remains running (violation = logged internal runtime defect,
mission fails closed). The `Thread.Sleep(50)` scheduler poll is retained deliberately: it is
bounded, cheap, and replacing the scheduler loop wholesale is exactly the "reckless async
rewrite" the review warns against.

### §6 API job correctness — CONFIRMED
`job.Status = job.Cancelled ? "cancelled" : "complete"` — a timed-out or failed mission produced
`status=complete, outcome=timed_out`. **Fix:** job status is mapped from the mission outcome
(`timed_out`/`failed`/`partial`→ their own statuses; only genuinely completed missions are
`complete`).

### §7 Planner statelessness — CONFIRMED
`Planner._offeredSkillIds` was a mutable instance field on a planner shared across concurrent
missions; concurrent planning could cross-contaminate skill provenance. (Introduced by us in
v2.21.0.) **Fix:** offered skill ids are local to the call and passed explicitly through
parsing. Deterministic interleaved-plan test added.

### §8 Skill registry concurrency — CONFIRMED
`Memory.SaveSkillRegistry(Skills)` rewrote the whole registry at mission finalization —
last-writer-wins across concurrent missions. **Fix:** per-skill row upsert with a `revision`
column (optimistic concurrency); `RecordOutcome` persists the one skill it touched atomically.
Whole-registry save survives only for initial seeding. Learning rule enforced: only canonical
`completed_verified` with promotable (deterministic) evidence records success; verified without
deterministic evidence records a neutral observation; everything else unverified/failure.

### §9 Core-ant structured results — PARTLY VALID → CONFIRMED for nuance
The claim "nonempty prose counts as success" was already half-fixed: the v2.19 default `Execute`
classifies in-band `ERROR:` as retryable provider failure and empty output as failure. But the
five core ants (Researcher, Web, File, Coder, Builder) still could not distinguish their own
degraded/skip/zero-result cases, and our own v2.19 comment says "two execution paths must not
outlive v2.19.x". **Fix:** explicit `Execute` per ant with the review's classification rules
(web zero-sources ≠ ordinary success; coder zero-patch on a file-change task = failure; file
inspection with all reads failed = failure; researcher fallback = success-with-warnings;
builder fallback discloses degradation).

### §10 Typed model call results — CONFIRMED
`ModelRouter` communicates failure via in-band `"ERROR:"` strings; `ModelCallOutcome` classifies
them back out of the prose. **Fix (narrowed):** `ModelCallResult` record with typed status is
introduced at the router boundary and consumed by the ants' `Execute` paths; the string `Route`
API remains as a compatibility wrapper over the typed call so the ~dozen remaining prose-level
call sites keep working. The narrowing is deliberate: rewriting every call site in one release
multiplies regression risk for zero behavioural gain — the *authority* (what counts as failure)
is now typed everywhere it matters.

### §11 Telemetry vs learning — NARROWED
Fully re-architecting pheromones into five signal stores is a V3-scale change. What this release
does: every `UpdatePheromoneTrail` write site now stamps a `signal_category`
(`operational_telemetry` / `reliability_signal` / `quality_signal` / `procedural_learning` /
`routing_preference`), positive `procedural_learning` reinforcement is gated on the canonical
evaluation, and the planning path reads only `procedural_learning`/`routing_preference` trails.
Raw observation storage with derived preferences is recorded as the intended V3 shape — the
category column is forward-compatible with it.

### §12 Structured verifier reports / follow-up policy — CONFIRMED (second half)
`EvidenceFollowUps` (v2.24) already parses findings behind known section headers and gates on
verified missions with budgets — the evidence-derived path matches the review's policy.
**CONFIRMED defect:** Strategist-proposed follow-ups were saved directly as executable
objectives (`ColonyDirector.SaveFollowUps` → `SaveObjective`). **Fix:** strategist proposals are
saved with status `suggested` and require explicit operator approval
(`POST /objectives/{id}/approve`) before entering the executable backlog. Evidence-derived
follow-ups (verified mission + structured finding + budgets) remain auto-admitted, as the review
prescribes.

### §13 Runtime profile validation — NARROWED
Full named-profile system replaced by a `RuntimeConfigValidator` that detects exactly the listed
incompatible combinations (adaptive repair without Medic, handoff ingestion with no executable
destinations, auto-apply without objective verification, auto-apply keep-without-verify, sandbox
without workspace, specialist enabled without executor) and reports them via `/config/health` +
startup events. Invalid combinations degrade loudly, never silently. Named profiles are a
packaging convenience over this validator and can be added without re-architecture.

### §14 Specialist activation order — ALREADY FIXED
Activation tiers (v2.22.0) are a ceiling; specialists ship gated off; the v2.19.0 mandate
"do not activate additional specialists globally" stands. No change.

### §15 Homelab action lifecycle — MOSTLY ALREADY FIXED (v2.25.0), one CONFIRMED remainder
v2.25.0 shipped the canonical lifecycle bridge, verification-gated completion, and recovery
recommendations. **CONFIRMED remainder:** `ExecuteAsync` still returned `Ok=true` when
post-execution verification failed. **Fix:** verify-failure now returns failure ("command
issued" ≠ "desired state achieved"). The v2.25.0 test asserting the old return value is updated
— a deliberate behavioural correction, not a weakening; the property it pinned (canonically
`failed`, never completed) is unchanged and still pinned.

### §16 Auto-apply safety — CONFIRMED
`autonomy_autoapply_keep_without_verify` kept unverified changes as an ordinary config option.
**Fix:** break-glass semantics — critical warning on use, installation reported unqualified by
the V3 readiness evaluation (new measured disqualifier), the kept change never records verified
success and never reinforces learning. Auto-apply eligibility consumes the canonical evaluation.

### §17 Task lifecycle persistence — CONFIRMED
`tasks` lacked `critical`, `outcome_code`, `cancellation_reason` — row-based evaluation could
not reproduce live evaluation. **Fix:** columns added (migration 16), persisted through the one
task write path, dynamic/handoff tasks included.

### §18 Queen decomposition — NARROWED, deliberately
The review itself warns against "a cosmetic file split that leaves hidden coupling unchanged."
The coupling that matters — independent mission-success derivation — is removed by the canonical
evaluator (§3): `MissionEvaluator` IS the extracted `MissionEvaluationService`, and
`LearningRecorder` semantics live in the skill/pheromone gating. The mechanical decomposition of
the remaining ~9 responsibilities is not attempted in this release because it is the highest
regression-risk item on the list with zero behavioural gain, in a release that already touches
mission finalization. This is a scope decision, not a phase plan.

### §19 Workspace capability manifests — INVALID as claimed, small real remainder
No Python-specific defaults exist in the auto-apply/verification profile (grep confirmed). The
real remainder — a declarative workspace manifest — is packaging over existing config
(allowlisted paths, verify command, max lines already exist as config). Not implemented; the
claimed drift it would fix does not exist.

### §20 Source freshness years — CONFIRMED (small)
`RecentHints` hard-coded `"2025"/"2026"`. **Fix:** derived from the current UTC clock.

### §21 Self-introspection — CONFIRMED (gap)
No deterministic self-answer path. **Fix:** `/colony/introspection` composes live registry,
executor catalog, STOP states, config health, job registry, and qualification state.

### §22 Database write performance — PARTLY VALID
WAL + busy_timeout already present, but `journal_mode=WAL` was re-executed on every `Connect()`
(it is a persistent database property). **Fix:** journal mode set once at init;
per-connection pragmas keep only the genuinely per-connection ones (`busy_timeout`,
`foreign_keys`). The bounded single-writer queue is not added: `_writeLock` already serialises
writes; adding a queue mid-release to a working lock discipline is risk without a measured
problem.

### §23 Backup policy — CONFIRMED
A full DB backup ran before **every** mission. **Fix:** policy-based — backup when the last one
is older than a configurable interval, before schema migration, before autonomous write
execution, or on explicit request. Retention/permission hardening kept.

### §24 Allocation costs — CONFIRMED (one instance)
`TextUtil.EstimateTokenCount(new string('x', n))` allocated up to megabytes to divide by four.
**Fix:** arithmetic overload.

### §25–§27 Testing and gates — implemented as behavioural tests per fix (mission truth,
evidence, STOP restart, drain/cancellation, planner concurrency, skill atomicity, ant
classification, action verify-failure, job mapping). Source-string call-site guards remain only
where they pin *wiring*, *in addition to* behavioural tests, per this codebase's discipline.

### §26 Qualification report — CONFIRMED (gap)
**Fix:** `POST /readiness/qualification-report` writes `data/reports/v3-qualification.json` +
`.md` from the live readiness evaluation, config validation findings, and break-glass state.
Measured gates only; the model/config cannot declare itself qualified.

---

## 2. Migrations

Migration 16 (`SchemaVersion = 16`):
`missions`: `outcome_code`, `stop_reason`, `verification_status`, `deliverable_status`,
`evaluator_version`, `evaluated_at`, `evidence_bundle_id` ·
`tasks`: `critical`, `outcome_code`, `cancellation_reason` ·
`skills`: `revision` ·
`pheromone_trails`: `signal_category` ·
`objectives`: `suggested` status value (no column change; status vocabulary extension).
All additive; legacy rows read as "unknown/legacy", which is never treated as verified.

## 3. Compatibility
Legacy mission rows without a persisted evaluation re-derive with `EvaluatorVersion = "legacy"`
and can never be `completed_verified` retroactively promoted. Job/API wire shapes keep existing
fields; only the incorrect `complete` mapping changes. Existing objective statuses unaffected;
`suggested` is additive. The action-executor return-value change on failed verification is a
deliberate correction (documented in §15).

## 4. Rollback
All changes additive at the schema level; a rollback to v2.25.0 reads the same tables (new
columns ignored). The STOP behaviour change is strictly safer than its predecessor. No data is
deleted or reset anywhere in this release.

## 5. V3 qualification criteria
Unchanged from the readiness gate (v2.25.0), plus one new measured disqualifier: break-glass
`keep_without_verify` enabled ⇒ NOT qualified. The qualification report is generated from
measured results only.
