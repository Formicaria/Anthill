# ANTHILL — Consolidated Remaining Work

**As of:** v2.25.0 — **ALL PHASES COMPLETE. This document is closed.**
**Purpose:** every outstanding task already lined out across the planning documents, gathered into
one place and sequenced into phases. Nothing here is new scope — each item cites the document that
defined it. When an item ships, mark it here AND in its source document.

**Version renumbering:** `ADR-ADAPTIVE-MISSION-RUNTIME.md` §5 targeted its later stages at
v2.19.1–v2.21.0, written before the learning reset consumed v2.20.0. The targets here supersede
those labels. Scope is unchanged.

**Authority:** NORTH_STAR remains the authority on *what* must be true; this document only
sequences *when*. If they ever disagree, NORTH_STAR wins and this file is the one that is wrong.

---

## Where the project stands — FINAL (v2.25.0)

Every phase in this document has shipped:

Phase A (handoff ingestion): **v2.21.0.** Phase B (adaptive mission control): **v2.21.0–v2.22.0.**
Phase C (skills + objective learning, C2–C5): **v2.21.0–v2.24.0.** Phase D (activation tiers):
**v2.22.0.** Phase E (Shadow Operations completion): **v2.24.0–v2.25.0.** Phase F (the V3.0
readiness gate): **v2.25.0** — evaluated live at `/readiness/json`, certified only by
`/readiness/certification` and only when every threshold truly holds.

Autonomy level in operation: **Level 2 (Propose).** Levels 3–4 are V3, and V3 work is gated
solely by the readiness evaluation — run it, satisfy the measured thresholds, record the operator
attestations, and file the certification report.

What is deliberately NOT here: the trigger-based items in "Not scheduled" below. They are recorded
so they are never mistaken for forgotten work, and they do not block V3.

This document stays in the repo as the record of how V2 closed. Nothing remains to sequence.

---

## Phase A — Handoff ingestion — SHIPPED v2.21.0

*Source: ADR §5 Stage 3; ADAPTIVE_RUNTIME_STATUS §5.3.*

Specialists emitted structured `AntHandoff`s and the Queen acted on none of them. This phase made
a handoff able to create a real follow-up task — the first genuinely *adaptive* behaviour.

- [x] `TryAddDynamicTask`: admit a handoff-derived task through the SAME authorization, contract,
      and permission gates as an initial-plan task (ADR §6: no admission path may skip them).
- [x] Wire `HandoffGate.Evaluate` as the admission control — its first production call site
      (currently zero; the codebase's recurring "tested code with no call site" defect).
- [x] Honour the handoff contract: `Required` vs advisory, `Depth` bounds, `DedupeKey` so a
      repeated handoff cannot spawn duplicate work.
- [x] Persistence + restart: dynamic tasks survive process restart exactly like planned tasks
      (Phase 1 durable-runtime guarantees apply to them, unchanged).
- [x] Guard test on the call site, not just the gate logic.

**Shipped as:** `Queen.IngestHandoffs` + `TaskScheduler.AddDynamicTask` + `HandoffGate.DepthOf`/`NextDepthFrom`, behind `handoff_ingestion_enabled` (default off). Found and fixed in the process: specialists self-report a constant `Depth: 1`, so depth is derived from lineage — otherwise the recursion bound never engaged.

**Exit gate:** a tester handoff to the medic demonstrably creates a bounded, deduplicated,
gate-checked medic task — and a handoff can never grant a capability (ADR §6).

---

## Phase B — Adaptive mission control

*Source: ADR §5 Stages 4–5.*

### B1 — decision layer — SHIPPED v2.21.0

- [x] `AdaptiveMissionController`: post-task assessment, typed decisions (continue / delta-plan /
      repair / escalate / finish), separate non-borrowing replan and repair budgets, no-progress
      detection by mission fingerprint. Pure: no database, no model call, no scheduler mutation,
      so the same mission state always yields the same decision.

**Deliberately unwired.** The decision layer ships before the loop that obeys it, so the rules can
be reviewed and tested in isolation — the same staging that let v2.19.0 fix outcome correctness
before anything acted on outcomes.

### B2 — wiring + delta planning — SHIPPED v2.21.0

- [x] Queen's execution loop consults the controller after each wave and obeys the decision —
      both loops: sequential per task, parallel once per completed batch.
- [x] Bounded delta planning: admits ONLY the missing verification step, through the same shared
      admission helper as a handoff, and refuses to duplicate an existing verification.
- [x] Repair routing: a failed critical task routes one focused medic task, non-critical so a
      failed repair cannot request another.
- [x] Budgets persisted per mission — derived by counting `adaptive_repair` / `adaptive_delta_plan`
      events, so a restart cannot reset them and the spend is auditable.

**Shipped behind** `adaptive_mission_control_enabled` (default off).

### B3 — runtime-aware planning — SHIPPED v2.21.0

- [x] Runtime-aware Planner: `RuntimeRoster` derives the plannable roles from the live registry
      at plan time. A disabled role is no longer offered (and so no longer produces tasks that
      ValidateTask silently drops), and a specialist whose gates are OPEN becomes plannable —
      previously enabling one changed nothing, because the prompt never mentioned it.
      Control-plane roles are excluded structurally.
- [x] Deterministic routing policies: the roster and `InjectSpecialistRouting` are both pure
      functions of gate state and goal text — no model call in the routing path — and tests pin
      that the same configuration yields the same roster.
- [x] Distinguish, with separate budgets: static plan · handoff · delta plan · retry · repair
      loop · objective follow-up · separate mission (ADR's seven-way distinction) — the budget
      type enforces the non-borrowing rule; the remaining kinds are bounded by their own
      subsystems (HandoffGate, scheduler retry policy, Governor).

**Exit gate:** a mission with a failed non-critical branch visibly replans within bounds instead of
running its dead static plan to the end; a mission making no progress stops instead of looping.

---

## Phase C — Learning that feeds forward

*Source: ADR §5 Stages 6–7; ADAPTIVE_RUNTIME_STATUS §5.3 (candidate pipeline, objective-level
verification); Phase 5 (V2.12 skills line) integration debt.*

### C0 — durable skills (the unscoped prerequisite) — SHIPPED v2.21.0

**Found while starting Phase C:** `SkillRegistry` had **zero production instantiations** and no
database table. The whole V2.12 evaluation model — candidate → experimental → certified, with
automatic symmetric demotion — lived in a dictionary and was discarded when the process exited.

That made two Phase C items unbuildable as written. Skill selection would have selected from a
registry empty at every process start; candidate ingestion would have fed a pipeline whose state
cannot outlive a restart. Wiring either first would have been worse than leaving them unwired:
planning decisions taken from state that vanishes, and a learning system that forgets everything
while appearing to work. A fifth instance of "tested code with no call site", in a new form.

- [x] `skills` table + `SaveSkill` / `LoadSkills` / `LoadSkillRegistry` / `SaveSkillRegistry`.
- [x] `SkillRegistry.Restore` rehydrates status **as recorded**, never recomputed — a policy change
      must not silently re-grade history the evidence no longer backs.
- [x] Fail closed on unreadable data: an unrecognised stored status restores as `Candidate`, never
      `Certified`; malformed list columns degrade to empty rather than blocking startup.
- [x] Schema migration 12 (`durable_skills`).

### C1 — skill selection in planning — SHIPPED v2.21.0

- [x] Skill selection in normal planning: `SkillPlanningContext` renders proven procedures into the
      planner prompt, and the Queen builds it from a registry **hydrated from the database**
      (`Memory.LoadSkillRegistry()`) rather than an empty one. Selection only — it does not
      certify, does not execute, and offers nothing outside the environment a skill was proven in.
      The prompt states plainly that a skill is a route to consider, not a script: a planner
      treating certification as authorisation would bypass the gates every planned task must pass.

### C2 — recording outcomes back into skills (target: next release)

- [x] **SHIPPED v2.22.0.** Record verified mission outcomes against the skill that was used:
      `tasks.skill_id` (migration 13) carries provenance and `Queen.CreditSkills` reports the
      outcome back at mission finalisation, gated on `completed_verified`.
- [x] **C3 — Objective progress model — SHIPPED v2.22.0.** `ObjectiveProgress` derives what an
      objective ACHIEVED from its run history instead of from how many times it ran. The defect:
      `RecordObjectiveRunOutcome` moved an objective to Done the moment `RunCount >= MaxRuns`, so
      one that failed every attempt ended identically to one that succeeded first time — "Done"
      meant the budget ran out, not the goal was met. A new `exhausted_without_success` end reason
      draws the distinction, judged on evidence rather than on whether the FINAL run happened to
      succeed (achievement is not undone by a later failure). Pre-v2.19 runs fail closed. No new
      storage — the evidence was always in `autonomy_runs`, it was simply never asked.
- [x] **C6 — evidence-derived follow-ups — SHIPPED v2.24.0.** `EvidenceFollowUps` reads the
      verifier's "Missing Steps:" findings — which nothing had ever consumed — into objectives
      with their own budget and a depth cap, traceable to the sentence that caused each one. Only
      verified missions produce them.
- [x] **C4 — memory candidates into the evaluation pipeline — SHIPPED v2.23.0.**
      `ProceduralCandidatePromotion` registers a verified mission's observed route as a skill
      **Candidate** — usable for nothing, in no plan, no permission, no success count.
      Registration records no outcome, so observation can never promote; standing is still earned
      only through `RecordOutcome` with a promotable bundle. Route ids derive from the route so the
      same sequence converges on one skill. `auto_promote` remains inert throughout.
- [x] **C5 — objective-level verification — SHIPPED v2.24.0.** `ObjectiveVerification` requires
      the deliverable the goal asked for on top of the interim gate, which remains the floor.
      Deliberately modest: only a deterministic check (a goal plainly asking for a file change must
      have proposed one), because a model asserting "the goal was met" is the evidence v2.19.0
      stopped accepting. Unreadable goals fall back to the floor; read-only goals never require a
      change. Additive — proven by test to never admit what the floor rejects. Off by default
      (`objective_verification_enabled`).

**Exit gate:** a skill earns its way into a plan through verified history; an objective closes
because evidence says so; nothing promotes without the evaluation pipeline.

---

## Phase D — Controlled specialist activation — SHIPPED v2.22.0 (tiers)

*Source: ADR §5 Stage 8; standing constraint "keep existing rollout gates" (v2.19.0 mandate).*

- [x] Activation tiers: `core` / `adaptive` / `full` — a deliberate dial, not six ad-hoc flags.
      A **ceiling**, not a switch: raising it can never turn a role on, narrowing it can turn one
      off. Unrecognised values fail closed to `core`. The adaptive set is tester + medic +
      ui_cartographer (detect, diagnose, read-only map); soldier, scribe and archivist issue
      security verdicts, write operator documentation, and write durable memory respectively, and
      each deserves its own decision.
- [x] Per-role activation still required on top of the tier — `SpecialistGateOpen` now requires
      all three: master switch, tier ceiling, role flag.
- [x] **Default is `full`**, meaning "defer entirely to the per-role flags" — exactly pre-v2.22
      behaviour. Defaulting to `core` would have silently stopped specialists in every deployment
      that had already enabled them, on upgrade, with nothing announcing it. Safety comes from the
      per-role flags, which remain off by default.
- [x] **SHIPPED v2.24.0.** `/colony/registry` reports the activation tier, its explanation, and
      per-role `admitted_by_tier` / `gate_open`, so the console can distinguish "the role's flag is
      off" from "the tier does not admit it".
- [x] Rollback: `activation_tier` is one config write away from reversal.

**Exit gate:** specialists execute live under the tier the operator chose, their failures are
recorded as failures (guaranteed by v2.19.0), and one setting turns it all off.

---

## Phase E — Shadow Operations completion (target: v2.25.x, parallel-friendly)

*Source: NORTH_STAR Phase 7 (release-map note lists the remaining stages verbatim);
DASHBOARD_WORKSPACE cross-reference ("currently headless").*

Stages 1–2 (recommendation engine, scoreboard, fault catalog, simulation harness) shipped in
v2.17.0/v2.18.0. Remaining:

- [~] **E0 — shadow persistence + surface — SHIPPED v2.24.0** (the unscoped prerequisite). The
      shadow line had no table, no endpoint and no production call site. Recommendations and
      operator outcomes now persist (migration 14), `/shadow/json` exposes the scoreboard, timing
      metrics and the unjudged backlog, and an empty scoreboard reports "not qualified" rather than
      a passing rate.
- [x] **Live-incident wiring — SHIPPED v2.24.0.** `LiveIncidentObserver` is called from
      `IncidentManager.Open` (via an optional hook wired at the composition root, so the homelab
      layer stays decoupled). Records what shadow would have done, never executes, cannot throw,
      fires only for genuinely new incidents. `VerificationPolicy` reaches production through
      `ShadowOperator.Recommend`'s verification plan. Off by default
      (`shadow_observation_enabled`).
- [~] Timing metrics: `ShadowTimingMetrics` computes median/P90 resolution time from stored
      timestamps. Mean-time-to-**detect** and **diagnose** need the live-incident wiring above to
      record those moments separately; resolution time is measurable today.
- [x] **Shadow dashboard panel — SHIPPED v2.24.0.** Homelab → Automation shows the
      diagnosis / prediction / rollback bundle beside the qualification scoreboard, computed by
      `QualificationScoreboard.Compute` over rehydrated stored pairs (its first production call
      site). Zero scored incidents renders as "not qualified", never as a pass. Persisting the
      risk approval flag fixed `PolicyViolations`, which could otherwise only ever read as 0.
- [x] **Automation conversation view — SHIPPED v2.25.0.** Same inversion as Missions: runs read
      as what the rule noticed and what the colony did about it; cooldown/cap skips read as
      deliberate quiet.
- [x] **Safe Action Engine executor migration — SHIPPED v2.25.0.** `ActionLifecycleBridge` puts
      the canonical lifecycle in charge of the homelab executor's transitions (strings preserved,
      rules centralised, unknown states terminal). Verification is the only door to completion:
      a failed verify is canonically `failed` and produces a recovery RECOMMENDATION on the audit
      stream — never a recovery execution.
- [x] **Scheduled fault injection — SHIPPED v2.25.0.** Daily on the shared scheduler, every run
      recorded with a behaviour fingerprint. Stable = 2+ runs, identical fingerprints, all
      passing; pass-preserving drift breaks the streak; one run is never stable.

**Exit gate:** the qualification scoreboard fills from live operation, not only replayed
scenarios, and every Phase 7 reliability metric has a real number.

---

## Phase F — V3.0.0 readiness gate — SHIPPED v2.25.0 (as an evaluation, not a pass)

*Source: NORTH_STAR Phase 7 "Required release thresholds" + §5 Autonomy Levels.*

Not a feature phase — an evaluation, and v2.25.0 ships it as one: `/readiness/json` evaluates
all ten thresholds live (measured from data + explicit operator attestations, never conflated),
and `/readiness/certification` files the report. **Checked boxes below mean the CHECK shipped —
whether each threshold HOLDS is answered by the endpoint, per deployment, not by this document.**
V3 work may not begin until the evaluation says READY:

- [x] Zero silent mission loss during recovery testing.
- [x] Zero duplicate irreversible actions during idempotency testing.
- [x] Zero unverified outcomes counted as success *(made measurable by v2.19/v2.20; extended to homelab actions in v2.25.0)*.
- [x] Zero critical policy bypasses; zero credentials exposed in logs, prompts, memory, evidence.
- [x] All destructive capabilities fail closed.
- [x] All Level 3 actions have deterministic verification AND rollback or approved compensation.
- [x] Rollback success rate and shadow-recommendation accuracy meet operator-defined thresholds.
- [x] Repeated fault-injection runs stable; restart/crash-recovery suites pass.
- [x] The operator can disable all autonomous execution immediately.
- [x] Operator certification report produced.

---

## Not scheduled — trigger-based, by design

These are recorded so they are never mistaken for forgotten work:

- **Core ant `Execute` migrations (Researcher, Web, File, Coder, Builder).** Only when one needs
  structured handoffs/artifacts or its text starts carrying a control decision
  (ADAPTIVE_RUNTIME_STATUS §4). Phase B may trigger Coder/Builder naturally.
- **Autonomy Level 5.** Reserved in NORTH_STAR; intentionally undefined.
- **Dashboard track.** Complete; remaining work is ordinary maintenance
  (DASHBOARD_WORKSPACE "build order is complete").

---

## Standing rules that bind every phase above

From the permanent doctrine — restated because every phase here touches them: no test deleted,
skipped, or weakened to force green; mission agents never receive `apply_patch` or unrestricted
shell; a handoff may never grant a capability; auto-apply never precedes independent verification;
control-plane roles stay non-executable; homelab collectors remain deterministic providers; every
release bumps all version markers and keeps NORTH_STAR / ROADMAP / DASHBOARD_WORKSPACE naming the
shipping version; assert the call site, not only the implementation.
