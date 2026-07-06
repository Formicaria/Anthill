# ANTHILL — 24/7 Autonomy Design

> Status: **Phase 0–5 IMPLEMENTED — the autonomy roadmap is complete.** For the current ordered build order across the whole project, see **[docs/NORTH_STAR.md](NORTH_STAR.md)** — the canonical roadmap. Rails, Director loop,
> Strategist, Concurrency, Learning loop, and now Phase 5 gated auto-apply. The colony can now run autonomously:
> the Director works the objective backlog — up to `autonomy_concurrency` missions at once,
> sized down live by the ResourceGovernor under host/backend pressure — under budgets and the
> kill switch, with writes queued for human review. Mission goals are LLM-generated per
> objective (with a deterministic charter-as-goal fallback), deduped against recent mission
> history, and the colony can enqueue its own follow-up objectives within depth/rate caps.
> Scheduling is strict priority with anti-starvation aging plus an outcome-driven learning bias;
> objectives that stop producing value or loop are auto-paused for review. Target: ANTHILL v1.9.x.

## Phase 5 — what landed (gated auto-apply)

The Director can now **apply a coder patch without human review** — the answer to the approval
queue filling up with fixes nobody has time to click through. It is the highest-risk capability
in the system (autonomous writes to disk), so it is fail-closed and multiply gated, and the whole
safety story is *apply → verify → keep-or-rollback*.

- **`AutoApplyPolicy`** (`Anthill.Core/Autonomy/AutoApplyPolicy.cs`): the pure, side-effect-free
  eligibility gate. A patch is a candidate only when **all** hold: `autonomy_autoapply_enabled` is
  on; the change is `add`/`modify` (never delete/rename); the file path matches at least one
  operator glob in `autonomy_autoapply_paths` (an **empty allowlist means nothing is eligible**, so
  the feature is inert until deliberately widened); and the change is within
  `autonomy_autoapply_max_lines`. Glob supports `**` (subtree), `*` (within a segment), `?`.
- **`AutoApplyRunner`** (`Anthill.Api/AutoApplyRunner.cs`): runs on the Director thread after a
  **successful** mission's outcome is recorded. Filters the mission's proposals through the policy,
  applies the eligible ones to disk (each with a pre-apply backup via the existing `apply_patch`
  tool), then runs the **verify** step — built-in `dotnet build && dotnet test`, or an operator
  `autonomy_autoapply_verify_cmd` — in the workspace root, timeout-bounded
  (`autonomy_autoapply_verify_timeout`, default 900s). **Green** ⇒ the changes stay, the matching
  approval requests are marked `consumed` (so they leave the queue), and — if
  `autonomy_autoapply_git_commit` is on — a local `git add`+`commit` (never pushed). **Red or
  timeout** ⇒ every applied patch is rolled back (modify → restore backup, add → delete the file),
  marked `failed`, and logged.
- **Write-gate dependency**: auto-apply also requires `patch_application_enabled` +
  `file_writing_enabled`; if they're off it logs `autonomy_autoapply_skipped` and does nothing.
- **Full audit trail**: `autonomy_autoapply_started` / `_applied` / `_verified` / `_reverted` /
  `_rolled_back` / `_ineligible` / `_skipped` events, all replayable, and the applied/reverted
  patches show up in the mission report's "tangible changes" with their final status.
- **Config** (all clamped/whitelisted; forced off in every safety profile):
  `autonomy_autoapply_enabled` (false), `autonomy_autoapply_paths` ([]), `autonomy_autoapply_max_lines`
  (40), `autonomy_autoapply_verify_cmd` (""), `autonomy_autoapply_verify_timeout` (900),
  `autonomy_autoapply_git_commit` (false). All editable in **Configuration → Security → Autonomous
  Auto-Apply**.
- **Tests**: `AutoApplyPolicyTests` — eligibility matrix, glob semantics, size cap, change-type,
  disabled and empty-allowlist denial.

Operational note: the verify build blocks the Director thread (deliberately — no new launches
mid-verify), so keep `autonomy_concurrency` and the verify command in mind on a busy box.

**Deploying auto-apply for real self-modification.** On the hardened LXC/systemd install the ANTHILL
source tree (`/opt/anthill/src`) is **read-only** to the service (`ProtectSystem=strict` +
`ReadWritePaths=…/.anthill`), so auto-apply can only write inside the workspace — it cannot modify
the running source. That's a safe default. To actually let the Director ship changes to a codebase:
point `agent_workspace_dir` at a **writable checkout the `anthill` service user owns** (add it to
the unit's `ReadWritePaths`, or keep it under `…/.anthill/`), set `autonomy_autoapply_paths` to the
paths within it you trust, and turn on `autonomy_autoapply_git_commit` so verified changes land as
local commits. If the workspace root isn't writable when auto-apply runs, the Director logs a single
`autonomy_autoapply_skipped` (`reason: workspace_readonly`) rather than failing patch-by-patch.

## Phase 4 — what landed

- **`ObjectiveLearning`** (`Anthill.Core/Autonomy/ObjectiveLearning.cs`): three pure functions
  over the per-objective success EMA. `UpdateEma` folds each run's mission success score into
  `objectives.success_ema` (α = `autonomy_score_ema_alpha`, default 0.3; unscored runs count as
  0; always recorded even when learning is off). `PriorityBias` maps the EMA linearly to
  ±`autonomy_priority_bias_max` effective-priority points at selection time — read-time only,
  stored priorities never drift, new objectives (null EMA) are unbiased. `EvaluateRetirement`
  decides stale (`run_count ≥ autonomy_retire_min_runs` and EMA <
  `autonomy_retire_score_threshold`) and looping (last `autonomy_loop_window` generated goals
  all ≥ `autonomy_dedupe_similarity` keyword overlap — same containment metric as Strategist
  dedup, so a charter-fallback spiral is caught as identical goals run after run).
- **Retirement = auto-pause + `objective_retired` event** (code `stale_low_success` /
  `looping_goals`, with reason, EMA, run count) — mirrors the failure circuit breaker; a human
  reviews and resumes from the Autonomy page. Checks run on the director thread after each
  outcome, so they never race the objective's bookkeeping. Only Active objectives are considered.
- **Schema v11** (additive): `objectives.success_ema REAL`, `EnsureColumns` + migration 11.
- **Config knobs** (clamped, settings-whitelisted): `autonomy_learning_enabled` (default true —
  false restores exact Phase 3 behavior), `autonomy_priority_bias_max` (2),
  `autonomy_score_ema_alpha` (0.3), `autonomy_retire_min_runs` (5),
  `autonomy_retire_score_threshold` (0.25), `autonomy_loop_window` (4, 0 = off).
- **Observability**: Score (EMA) column in `/objectives` + the backlog table;
  `success_ema` on `autonomy_mission_finished` events; `learning_enabled` in `/autonomy/status`.
- **Tests**: `LearningTests` — EMA math and persistence, bias bounds/linearity, EMA-driven
  selection ordering, and every retirement branch (stale, looping, disabled, non-active).

## Phase 3 — what landed

- **`ResourceGovernor`** (`Anthill.Core/Autonomy/ResourceGovernor.cs`): sizes effective
  concurrency each Director cycle, starting from the configured `autonomy_concurrency` cap and
  only ever lowering it. Three cheap signals: normalized CPU load (1-min loadavg per core,
  soft ≥1.25 halves / hard ≥2.0 clamps to 1), available-memory fraction (soft ≤20% halves /
  hard ≤10% clamps to 1), and an Ollama latency probe (`GET /api/version`, cached 15s —
  unreachable clamps to 1, ≥2.5s halves). Failure posture: unreachable backend clamps (missions
  would fail anyway); an unreadable *host* signal is skipped, failing open to the configured cap
  (e.g. non-Linux hosts without /proc). Full VRAM tracking is deferred to a later hardware-aware
  scheduler phase. Signal readers are injectable — see `GovernorTests`.
- **Concurrent Director loop**: `ColonyDirector` now launches without blocking, tracks in-flight
  missions, and reaps outcomes as jobs finish. All launching/reaping stays on the single director
  thread, so `BudgetGuard` and `Strategist` calls remain sequential by construction; budgets are
  re-checked before *every individual launch* within a cycle. Kill switch / stop now *drains*:
  no new launches, in-flight missions finish and are recorded, then the thread exits.
- **Scheduling — strict priority + aging** (`SqliteMemory.NextReadyObjectives`): concurrency
  slots are filled with the highest-effective-priority distinct ready objectives; an objective
  never has two missions in flight at once (which also keeps its run-outcome bookkeeping serial).
  Effective priority = stored priority + 1 per `autonomy_aging_minutes` waited since last run
  (or creation); ties break toward the longest-queued. Aging is computed at read time — stored
  priorities never drift. `autonomy_aging_minutes = 0` disables aging (pure strict priority).
- **Mission-id integrity**: `Queen.RunMission` gained an `onMissionCreated` callback and
  `ApiJobRegistry` stamps `job.MissionId` from it the moment the mission row exists — concurrent
  workers can no longer read another mission's id off the shared `Queen.LastMissionId` (which
  remains, last-writer-wins, for the single-mission CLI path). Job workers are sized to
  `max(api_job_workers, autonomy_concurrency)` at boot so autonomous missions actually get slots.
- **Config knobs**: `autonomy_concurrency` (default 1, clamped 1–8) and `autonomy_aging_minutes`
  (default 30, clamped 0–10080). Both editable from the Settings UI.
- **Observability**: `/autonomy/status` adds `concurrency_configured` / `concurrency_effective`,
  `governor_code` / `governor_reason` / `governor_signals`, `aging_minutes`, and an `in_flight`
  list (objective, run id, mission id, job status, started_at). `autonomy_mission_started` events
  record the in-flight count, effective concurrency, and governor code; the Autonomy page shows a
  Concurrency KPI plus live In-flight / Governor rows.
- **Tests**: `GovernorTests` (all clamp paths, fail-open vs. fail-safe, tightest-constraint-wins),
  multi-slot selection + aging tests in `AutonomyTests`, and an offline two-slot Director run in
  `DirectorTests` asserting both outcomes are recorded with distinct mission ids.

## Phase 2 — what landed

- **`Strategist`** (`Anthill.Core/Autonomy/Strategist.cs`): turns an objective + recent
  `autonomy_runs` history + top pheromone trails into a concrete mission goal via the new
  `strategist` model-router role. Always computes the deterministic charter-as-goal fallback
  first; if the router is unset, the call errors, or the response isn't parseable JSON, it
  returns the fallback — never blocks or throws (`StrategistResult.Source` is `"strategist"` or
  `"fallback"` so the choice is auditable).
- **Dedup**: rejects a generated goal that's a near-duplicate of a recent completed/partial run
  for the same objective, using `TextUtil.ExtractKeywords` containment-ratio overlap against
  `ListAutonomyRuns(objectiveId, limit: 10)`, threshold `autonomy_dedupe_similarity` (default 0.8).
  A rejected goal falls back to the deterministic charter goal.
- **Follow-up objectives**: the Strategist can propose follow-up objectives in its JSON response;
  `ColonyDirector` only saves them after a *successful* mission, capped by
  `autonomy_max_followups_per_run` (default 1) and `autonomy_max_objective_depth` (default 3,
  walked via the new `SqliteMemory.ObjectiveDepth`). Follow-ups inherit `ParentObjectiveId` and
  run at `Priority - 1` so they don't outrank their parent's siblings.
- **Config knobs** (all fail-closed, safe defaults): `autonomy_dedupe_similarity` (0–1, clamped),
  `autonomy_max_followups_per_run`, `autonomy_max_objective_depth`.
- **`ColonyDirector`**: `RunObjectiveOnce` now calls `Strategist.GenerateGoal` instead of a static
  charter-as-goal builder; logs `goal_source` and `strategist_notes` on the
  `autonomy_mission_started` event, and `follow_ups_created` on `autonomy_mission_finished`. Writes
  are still queue-only — the Strategist only chooses *what mission to run next*, never applies
  patches.
- **UI**: new admin-only "Autonomy" page — Director status/start/stop/kill-switch card, an
  objectives table (add/pause-resume/reprioritize/delete), and a recent-runs table showing the
  goal source (strategist vs. fallback) per run.
- **Tests**: offline `StrategistTests` cover the no-router fallback path (never blocks/throws) and
  `ObjectiveDepth` walking/edge cases. The LLM-driven generation/dedup paths aren't testable
  without a live provider — covered by manual verification once a provider key is configured.

## Phase 1 — what landed

- **`ColonyDirector`** (`Anthill.Api/ColonyDirector.cs`): the supervisor loop — budget +
  kill-switch check → `NextReadyObjective` → submit a mission through the shared job worker →
  wait → record the outcome (`AutonomyRun` + `RecordObjectiveRunOutcome`) → idle backoff. One
  mission at a time (concurrency is Phase 3). Charter is used directly as the goal.
- **Queue-for-review writes**: the Director only launches missions; it never approves or applies
  patches. Proposals accumulate in the normal `/approvals` queue.
- **`--autonomous` flag**: starts the Director at boot (still gated by `autonomy_enabled`).
- **Control plane endpoints**:
  - `GET /autonomy/status`, `POST /autonomy/start`, `POST /autonomy/stop`, `GET /autonomy/runs`
  - `GET/POST /objectives`, `GET/PATCH/DELETE /objectives/{id}`
- **Events**: `autonomy_started/stopped/idle/mission_started/mission_finished/error` on the
  `system_api` channel, so the loop is fully visible in the existing event log/UI.
- **Tests**: 3 Director integration tests (offline loop runs an objective end-to-end, start
  refusal when disabled, halt on kill switch) + the Phase 0 suite. Verified live over HTTP.

LLM-driven goal synthesis and mission de-dup landed in Phase 2 (see above).

**Intent fidelity (v1.8.15.2).** Live testing showed the Strategist could drift — rewriting a
one-shot charter like "create docs/x.md" into an unrelated goal ("train a model on docs"). Two
guards now keep the operator's intent intact: an objective with `max_runs == 1` bypasses the LLM
and uses its charter verbatim (explicit one-shot tasks are never reinterpreted, `Source =
"charter_verbatim"`), and the Strategist prompt for standing objectives now requires the goal to
directly accomplish the charter (execute it as written on the first run; only take the next
incremental step once a prior run already accomplished it) and to almost never invent follow-ups.
A structural `autonomy_max_backlog` cap stops the Strategist enqueuing new follow-ups once the open
backlog (pending + active) is full, bounding sprawl regardless of model behavior.

## Phase 0 — what landed

- **Schema v8** (`objectives`, `autonomy_runs` tables + indexes, migration ledger entry 8).
- **Domain models**: `Objective` (+ `ObjectiveStatus` enum), `AutonomyRun`.
- **Backlog store** (`SqliteMemory.Autonomy.cs`): save/get/list objectives, `NextReadyObjective`
  (priority desc, oldest-first, skips paused/done/budget-exhausted), priority/status setters,
  `RecordObjectiveRunOutcome` (run-count, last-run stamp, consecutive-failure circuit breaker,
  Done/Paused/Active transitions), audit-trail writes + `CountAutonomyRunsSince`.
- **Kill switch** (`Autonomy/AutonomyControl.cs`): durable `.anthill/STOP` sentinel **and**
  in-process flag; `Stop`/`Resume`/`IsStopped`. No auto-clear.
- **Budget guard** (`Autonomy/BudgetGuard.cs`): denies when autonomy is disabled, the kill
  switch is engaged, or the hourly/daily mission budget is reached. Reads counts from the
  audit trail so budgets survive restarts.
- **Config knobs** (all fail-closed): `autonomy_enabled` (default false, forced off by every
  safety profile), `autonomy_poll_seconds`, `autonomy_max_missions_per_hour`,
  `autonomy_max_missions_per_day`, `autonomy_max_consecutive_failures`.
- **Tests**: 9 autonomy tests (backlog ordering, breaker, kill switch, budget denials).
- Incidental fix surfaced by fresh-DB testing: system/sentinel missions are now seeded so the
  self-test passes 15/15 on an empty database (was failing on a clean install).

There is **no execution loop yet** — nothing consumes the backlog. That is Phase 1.

## 1. Goal

Let ANTHILL run continuously (24/7), working a user-maintained backlog of objectives:
generating its own concrete missions, executing them with the existing colony, learning
from the outcomes, and queuing any file changes for human review — all under hard budgets
and an always-available kill switch.

## 2. Locked decisions

| Decision | Choice | Implication |
|----------|--------|-------------|
| **Unattended write policy** | **Queue for human review** first. Build auto-apply later as a gated phase, but keep it **disabled** until the loop is proven stable. | The autonomous Director can *propose* patches but never writes to disk on its own. Approvals accumulate in the existing approval queue for a human to `/approve` + `/apply`. |
| **Objective model** | **Backlog / priority queue.** The colony can enqueue follow-up objectives it discovers. | New persistent objective store; Director pulls the highest-priority ready objective each cycle. |
| **Deliverable** | This doc + iterative refinement. | — |

## 3. Core principle: the Director sits *above* missions

`Queen.RunMission(goal)` stays one-shot and synchronous ([Queen.cs](../src/Anthill.Core/Orchestration/Queen.cs)).
Autonomy is a new long-lived supervisor — the **Colony Director** — that loops:

```
                 ┌─────────────────────────────────────────────┐
                 │              Colony Director                 │
                 │  (long-lived loop; one per process)          │
                 │                                              │
   kill switch ──┤  1. check budgets + kill switch              │
                 │  2. pull next ready objective (backlog)      │
                 │  3. Strategist: objective + memory → goal     │
                 │  4. dedupe goal against mission history       │
                 │  5. Queen.RunMission(goal)   ◄── unchanged    │
                 │  6. record outcome, update pheromones         │
                 │  7. enqueue discovered follow-ups             │
                 │  8. idle backoff, then repeat                 │
                 └─────────────────────────────────────────────┘
```

Everything under step 5 is the existing, tested system. Per-mission caps
(`MaxMissionSeconds`, scheduler, approval gate, spec-ingestion) keep working unchanged.

## 4. New components

### 4.1 ObjectiveStore (backlog)
Persistent, priority-ordered queue of objectives.

- Fields: `id`, `title`, `charter` (the standing goal text), `priority` (int),
  `status` (`pending` / `active` / `paused` / `done` / `failed`), `created_at`,
  `last_run_at`, `run_count`, `max_runs` (0 = unlimited), `parent_objective_id`
  (for discovered follow-ups), `metadata`.
- API: `POST /objectives`, `GET /objectives`, `PATCH /objectives/{id}` (pause/resume/reprioritize),
  `DELETE /objectives/{id}`.
- Backed by a new `objectives` table (schema migration → SchemaVersion 8).

### 4.2 Strategist
Turns *one objective + pheromone memory + recent mission history* into the **next concrete
mission goal**. Reuses `ModelRouter` with a new `strategist` role route.

- Input: objective charter, last N mission summaries for this objective, top pheromone trails.
- Output: a single concrete goal string + optional list of follow-up objectives to enqueue.
- Dedup: reject a generated goal that is too similar to a recent completed mission
  (reuse `TextUtil.ExtractKeywords` overlap, same approach as source scoring).

### 4.3 Director loop
The supervisor service. One instance per process. Responsibilities:
- Budget + kill-switch checks **before every mission**.
- Pull next ready objective (respects `priority`, `status`, `max_runs`, `paused`).
- Call Strategist → `Queen.RunMission` → record an `autonomy_run` row.
- Idle backoff when the backlog is empty or budgets are exhausted (sleep, then re-check).
- Graceful shutdown on kill switch / SIGINT.

### 4.4 ResourceGovernor (Phase 3) — LANDED
Sizes concurrency to host load, memory headroom, and Ollama responsiveness (see "Phase 3 — what
landed" above). VRAM-level tracking is deferred to a later hardware-aware scheduler phase.

## 5. Safety model for unattended operation

Autonomy multiplies blast radius, so rails come **first** (Phase 0), before the loop exists.

- **Kill switch**: a sentinel file (`.anthill/STOP`) **and** an API endpoint (`POST /autonomy/stop`).
  Checked before every mission and on a timer mid-mission. Presence = immediate drain + halt.
- **Budgets** (config, hard caps): `max_missions_per_day`, `max_missions_per_hour`,
  `max_consecutive_failures` (circuit breaker → auto-pause objective), optional wall-clock
  `daily_runtime_seconds`.
- **Write policy = queue only**: Director runs with patch application gated off. Proposals
  pile up as approval requests; a human reviews via the existing `/approvals` → `/approve` →
  `/apply` flow. The Director **never** calls `/apply`.
- **Audit trail**: every autonomous decision (objective chosen, goal generated, mission id,
  outcome) is an event + an `autonomy_runs` row, fully replayable.
- **Default off**: autonomy only starts with an explicit `--autonomous` flag or
  `POST /autonomy/start`; never by default.

## 6. Data model additions

- `objectives` (see 4.1).
- `autonomy_runs`: `id`, `objective_id`, `mission_id`, `generated_goal`, `started_at`,
  `finished_at`, `mission_status`, `success_score`, `follow_ups_created`, `notes`.
- Schema migration bumps `SchemaVersion` 7 → 8 in `AnthillRuntime` + `SqliteMemory.Schema`.

## 7. Config additions (`config.json`)

```jsonc
"autonomy_enabled": false,            // master switch; CLI --autonomous also required
"autonomy_poll_seconds": 30,          // idle backoff between cycles
"autonomy_max_missions_per_hour": 6,
"autonomy_max_missions_per_day": 60,
"autonomy_max_consecutive_failures": 3, // circuit breaker per objective
"autonomy_dedupe_similarity": 0.8,    // reject near-duplicate generated goals
"autonomy_max_followups_per_run": 1,  // cap self-enqueued follow-up objectives per mission
"autonomy_max_objective_depth": 3,    // cap parent-chain depth for follow-up objectives
"autonomy_max_backlog": 40,           // stop enqueuing follow-ups when pending+active hits this; 0 = no cap
"autonomy_concurrency": 1,            // Phase 3: >1 enables concurrent missions (governor can lower it)
"autonomy_aging_minutes": 30,         // Phase 3: anti-starvation aging; 0 = pure strict priority
"autonomy_learning_enabled": true,    // Phase 4: outcome bias + retirement; false = pure Phase 3
"autonomy_priority_bias_max": 2,      // Phase 4: max ± effective-priority points from the success EMA
"autonomy_score_ema_alpha": 0.3,      // Phase 4: EMA weight of the newest run's score
"autonomy_retire_min_runs": 5,        // Phase 4: runs required before stale retirement may trigger
"autonomy_retire_score_threshold": 0.25, // Phase 4: EMA below this (with enough runs) = stale
"autonomy_loop_window": 4,            // Phase 4: near-identical goals in a row = looping; 0 = off
"autonomy_autoapply_enabled": false,  // Phase 5: apply allowlisted patches that verify green, no review
"autonomy_autoapply_paths": [],       // Phase 5: workspace globs a patch must match; [] = nothing eligible
"autonomy_autoapply_max_lines": 40,   // Phase 5: max changed lines per auto-applied patch
"autonomy_autoapply_verify_cmd": "",  // Phase 5: verify command; "" = dotnet build && dotnet test
"autonomy_autoapply_verify_timeout": 900, // Phase 5: verify hard timeout (seconds)
"autonomy_autoapply_git_commit": false    // Phase 5: also git-commit verified changes locally
```

All default to the safe/off values.

## 8. Phased plan

| Phase | Deliverable | Key files |
|-------|-------------|-----------|
| **0 — Rails** ✅ | **DONE.** Kill switch, budgets, `objectives` + `autonomy_runs` tables, config knobs, schema v8. No loop yet. | `AnthillConfig`, `AnthillRuntime`, `SqliteMemory.Schema`, `SqliteMemory.Autonomy`, `Autonomy/` namespace |
| **1 — Loop (MVP)** ✅ | **DONE.** Director runs one objective at a time, queue-for-review writes only, `--autonomous` flag, `/autonomy/{start,stop,status,runs}` + `/objectives` CRUD. **This is the milestone that must be stable before anything else.** | `Anthill.Api/ColonyDirector.cs`, `ApiHost.cs`, `Program.cs` |
| **2 — Self-generated missions** ✅ | **DONE.** Strategist role + dedup + follow-up enqueue (depth/rate capped). | `Autonomy/Strategist.cs`, `ModelRouter` route, `ColonyDirector.cs` |
| **3 — Concurrency** ✅ | **DONE.** `ResourceGovernor` (load/memory/backend-probe sizing), concurrent Director loop with drain-on-stop, strict priority + aging scheduling, `autonomy_concurrency`/`autonomy_aging_minutes` knobs. VRAM-aware scaling deferred to a later hardware-aware phase. | `Autonomy/ResourceGovernor.cs`, `ColonyDirector.cs`, `SqliteMemory.Autonomy`, `ApiHost` |
| **4 — Learning loop** ✅ | **DONE.** Success-EMA per objective biases selection (read-time, bounded); stale/looping objectives auto-pause with `objective_retired` events. | `Autonomy/ObjectiveLearning.cs`, `SqliteMemory.Autonomy`, `ColonyDirector.cs` |
| **5 — Auto-apply (gated, OFF)** ✅ | **DONE.** Strict allowlist auto-approve+apply — path globs, size cap, add/modify only, must build+test green afterward, auto-rollback on red. Fail-closed OFF, inert with an empty allowlist, forced off in every safety profile. | `Autonomy/AutoApplyPolicy.cs`, `Anthill.Api/AutoApplyRunner.cs`, `Queen.Views` (apply/rollback), `ColonyDirector.cs` |

Implementation order was strictly 0 → 1 → (stabilize) → 2 → 3 → 4 → 5 — **all phases now shipped.**

## 9. UI / observability

- New "Autonomy" panel: current objective, generated goal, live budget counters,
  next-run countdown, big STOP button (calls `/autonomy/stop`).
- Backlog editor: add/reprioritize/pause objectives.
- Reuse the existing event stream + colony canvas for the running mission.

## 10. Open questions

- **Auto-apply criteria** (Phase 5): exact allowlist, and "must build + test green" needs a
  sandboxed build runner — design separately.
- ~~**Follow-up explosion control**~~: resolved in Phase 2 — `autonomy_max_followups_per_run` and
  `autonomy_max_objective_depth` cap rate/depth of self-enqueued objectives.
- ~~**Multi-objective fairness**~~: resolved in Phase 3 — strict priority with anti-starvation
  aging (`autonomy_aging_minutes`); queued time breaks ties. Round-robin/weighted rejected as
  less predictable/auditable.
- **VRAM-aware scaling**: deferred from Phase 3 — the governor uses load/memory/backend-probe
  signals; explicit VRAM budgeting needs a configured GPU capacity (Ollama doesn't report total
  VRAM) and belongs to a later hardware-aware scheduler phase.
- **Mission de-dup window**: Phase 2 compares against the last 10 runs per objective
  (`ListAutonomyRuns(..., limit: 10)`); revisit if a time-based window is preferred instead.
