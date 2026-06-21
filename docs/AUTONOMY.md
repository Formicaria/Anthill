# ANTHILL — 24/7 Autonomy Design

> Status: **Phase 0 (Rails) IMPLEMENTED. Loop NOT yet implemented.** The colony is still
> one-shot; the Director loop arrives in Phase 1. Phase 0 shipped the durable foundations:
> objective backlog, audit trail, budgets, kill switch, config knobs, schema v8.
> Target: ANTHILL v1.9.x.

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

### 4.4 ResourceGovernor (Phase 3)
Sizes concurrency to load and Ollama VRAM headroom. Until Phase 3, concurrency is fixed at 1.

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
"autonomy_concurrency": 1             // Phase 3: >1 enables concurrent missions
```

All default to the safe/off values. Auto-apply gets **no** config key until Phase 5.

## 8. Phased plan

| Phase | Deliverable | Key files |
|-------|-------------|-----------|
| **0 — Rails** ✅ | **DONE.** Kill switch, budgets, `objectives` + `autonomy_runs` tables, config knobs, schema v8. No loop yet. | `AnthillConfig`, `AnthillRuntime`, `SqliteMemory.Schema`, `SqliteMemory.Autonomy`, `Autonomy/` namespace |
| **1 — Loop (MVP)** | Director runs one objective at a time, queue-for-review writes only, `--autonomous` flag, `/autonomy/{start,stop,status}`. **This is the milestone that must be stable before anything else.** | new `Autonomy/ColonyDirector.cs`, `Program.cs`, `ApiHost.cs` |
| **2 — Self-generated missions** | Strategist role + dedup + follow-up enqueue. | new `Autonomy/Strategist.cs`, `ModelRouter` route |
| **3 — Concurrency** | `ResourceGovernor`, unlock `ApiJobWorkers`/`autonomy_concurrency`, VRAM-aware scaling. | `ApiHost`, governor |
| **4 — Learning loop** | Outcomes bias objective priority; retire stale/looping objectives. | `PheromoneEngine`, ObjectiveStore |
| **5 — Auto-apply (gated, OFF)** | Strict allowlist auto-approve+apply (path allowlist, size cap, must build+test green, auto-rollback). Enabled only after Phase 1 is proven. | new policy module + `/apply` automation |

Implementation order is strictly 0 → 1 → (stabilize) → 2 → 3 → 4 → 5.

## 9. UI / observability

- New "Autonomy" panel: current objective, generated goal, live budget counters,
  next-run countdown, big STOP button (calls `/autonomy/stop`).
- Backlog editor: add/reprioritize/pause objectives.
- Reuse the existing event stream + colony canvas for the running mission.

## 10. Open questions (revisit before Phase 2)

- **Auto-apply criteria** (Phase 5): exact allowlist, and "must build + test green" needs a
  sandboxed build runner — design separately.
- **Follow-up explosion control**: cap depth/rate of self-enqueued objectives so the backlog
  can't grow unbounded.
- **Multi-objective fairness** (Phase 3): strict priority vs. round-robin/weighted.
- **Mission de-dup window**: how many days of history to compare against.
