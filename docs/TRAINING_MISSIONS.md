# ANTHILL Training Missions — Fresh-Install Bootstrap (v1.8.29)

Status: Active operator guide. Part of the master roadmap — see `docs/NORTH_STAR.md` (Phase 3, V1.8.29).

A fresh ANTHILL install starts with an empty memory and no pheromone trails. This pack is a
repeatable, **read-only** curriculum the colony runs against its own repo and runtime so that later
real patch missions start with memory of the repo structure, role boundaries, validation rules, and
roadmap direction — instead of rediscovering them mid-mission.

**Hard rule: every training mission is read-only.** Each goal below deliberately contains the
phrases `read-only`, `do not modify files`, and (where applicable) `one-shot`. These are parsed by
`MissionConstraints` (v1.8.16), which strips coder patch-proposal tasks at planning time and lets
the mission end cleanly instead of looping. Training missions must never create patch proposals.
If a training mission ever produces a patch proposal, reject it and file an issue — that is a
constraint-enforcement regression (`LifecycleAndConstraintTests` covers it).

---

## How to run the pack

1. Install and start ANTHILL (see `docs/DEPLOYMENT.md`), open the console at `http://localhost:8713/ui`.
2. Submit the missions below **in order**, one at a time, from the Mission Composer on the
   Overview page. Paste each goal exactly — the constraint phrases matter.
3. Use **Preview Plan** before dispatch and confirm the plan contains **no coder patch tasks**
   (the constraint banner should show the mission as read-only/no-patch).
4. After each mission, open the mission report and skim the final result for obvious nonsense —
   a wrong lesson stored in memory is worse than no lesson.
5. When the pack is done, check the Pheromones page (Memory + Pheromone Explorer): each mission
   should have left a mission record, sources, and a positive pheromone trail.
6. Re-run the pack after major version jumps, or whenever memory has been cleared
   (Settings → Maintenance → Clear Missions wipes mission history — training restores it).

Time cost is model-dependent; each mission is a normal 3-plus-task mission (research → build →
verify). Run them during idle time.

---

## The nine training missions

### 1. Repo Orientation

```text
One-shot, read-only training mission, do not modify files: map the ANTHILL repository for future
missions. Produce a concise project map covering: the solution layout (src/Anthill.Core,
src/Anthill.Api, src/Anthill.Cli, tests/Anthill.Tests, native/anthill_kernel), the runtime flow
from CLI/API entry to Queen to Planner to ants to memory, where the HTTP API endpoints are
registered, where the embedded web UI lives (src/Anthill.Api/Ui/index.html), where tests live,
where configuration is read (config.json, AnthillRuntime, AnthillConfig), the deploy options
(Docker, LXC), and where version markers live (AnthillRuntime.Version, Directory.Build.props,
README, CHANGELOG). Summarize as a structured reference document in the final result.
```

### 2. Ant Role Training

```text
One-shot, read-only training mission, do not modify files: document the colony's ant registry and
routing rules. List every ant role and worker (researcher, builder, verifier, coder and its
workers including coder.ui_coder, file, web, and any others in the registry), what each is allowed
and forbidden to do, which roles are executable versus visible-only, how the planner assigns
workers to tasks, and which permission or constraint checks can reject a task. Note explicitly:
no ant can apply patches directly; patches always go through approval.
```

### 3. Build/Test Workflow Training

```text
One-shot, read-only training mission, do not modify files: document the ANTHILL build and
validation workflow. Cover: the required validation commands and the scripts/validate.sh and
scripts/validate.ps1 entry points, the test projects and the regression guard tests (version-marker
consistency, migration idempotence, UI glyph integrity, no-Python guard), what CI runs on every
pull request (build matrix, publish + selftest, Docker smoke test, ui-integrity, repo-guards), the
release flow (tag push triggers the Release workflow, tag must match AnthillRuntime.Version), and
the version-completion checklist in docs/NORTH_STAR.md section 7.
```

### 4. UI Structure Training

```text
One-shot, read-only training mission, do not modify files: document the embedded console UI so
future UI missions modify it safely. Cover: the single-file vanilla HTML/CSS/JS architecture of
src/Anthill.Api/Ui/index.html (no React, no Tailwind, no build step), the CSS-variable theme
system and hud-* component classes, how pages are structured and rendered, which API endpoints the
UI calls, the known encoding hazard (icon glyphs flattened to '?' when the file is saved as
non-UTF-8, guarded by CI ui-integrity), and a safe-modification checklist: additive changes only,
preserve existing pages and routes, keep UTF-8, keep vanilla JS, clean up timers, respect
reduced-motion.
```

### 5. Memory + Pheromone System Training

```text
One-shot, read-only training mission, do not modify files: document ANTHILL's memory and pheromone
system. Cover: the SQLite schema families (missions, tasks, events, sources, patches, approvals,
objectives, pheromones, users, providers), how mission history is written and searched, how
pheromone scores are produced and what they reward, how the Memory + Pheromone Explorer reads
them, the maintenance operations (backup retention, flush cache, clear missions) and what each
deletes or preserves, and how memory context is injected into future mission planning.
```

### 6. Patch Proposal Discipline

```text
One-shot, read-only training mission, do not modify files: document the patch lifecycle and its
safety rules as operating discipline for future code missions. Cover: patch proposal creation by
coder ants, risk scoring, the approve-then-apply model, verifier involvement, the audit trail,
auto-apply gating (fail-closed, allowlisted, configurable) and its git integration (standalone
branch, never main), duplicate/superseded patch handling, and the unsafe patterns to avoid:
applying without approval, patching py.old, deleting instead of editing, bypassing the Queen or
ApprovalGuard, and proposing patches on verification-only missions.
```

### 7. Failure Drill

```text
One-shot, read-only training mission, do not modify files: walk through a simulated CI-failure
incident to learn the diagnosis workflow. Scenario: the CI build fails on main after a merge.
Document, step by step, which information to collect first (failing job, failing step, error text,
recent commits), which ant roles participate in diagnosis, how to reproduce locally with
scripts/validate, how to distinguish product bugs from CI/environment flakes, what a minimal fix
proposal looks like versus overpatching, and how verification confirms the fix before the incident
is closed. Do not take action - this is a tabletop exercise producing a written runbook.
```

### 8. V2 Homelab Roadmap Training

```text
One-shot, read-only training mission, do not modify files: internalize the master roadmap in
docs/NORTH_STAR.md. Summarize: the north-star goal and the permanent
OBSERVE-DIAGNOSE-PROPOSE-RISK-APPROVAL-EXECUTE-VERIFY-LOG-LEARN rule, the non-negotiable safety
rules (no destructive infrastructure actions, no secrets in logs, kill-switch files, no Python),
the architecture rules (local-first, deterministic C# for polling, LLM ants only for judgment,
additive APIs), the consolidated build order from V1.8.x through V1.9.x homelab foundation to
V2.0 Homelab Command Center and V3.0 bounded autonomy, and which phases are marked shipped versus
future. Store this as durable direction for future mission planning.
```

### 9. Daily Memory Compression

```text
One-shot, read-only training mission, do not modify files: compress recent mission history into
durable operating lessons. Review the most recent missions, tasks, failures, and pheromone trails;
extract at most ten short, generally applicable lessons (what worked, what failed and why, which
workflows or wordings produced clean results); discard noise and one-off details; and produce the
lessons as a concise numbered list in the final result so they persist in mission memory as
searchable guidance.
```

---

## The memory-compression pattern (recurring)

Mission 9 is also the template for ongoing memory hygiene. Two ways to run it on a cadence:

- **Manual:** re-submit mission 9 daily or after every 10–20 real missions.
- **Objective:** create an autonomy objective with the same charter text minus `one-shot`
  (keep `read-only` and `do not modify files`). The objective lifecycle (v1.8.16) will run it as a
  recurring verification-style objective that ends each run cleanly instead of looping. Keep its
  priority low so it never competes with real work.

Compression keeps memory searchable and keeps pheromone context small and high-signal — prune raw
history with the Maintenance controls once its lessons have been compressed.

---

## Success criteria (mirrors NORTH_STAR Phase 3)

- A fresh install can run the whole pack without modifying a single file and without creating any
  patch proposal.
- Afterward, mission memory contains: a repo map, role boundaries, the validation workflow, UI
  safety rules, the memory model, patch discipline, an incident runbook, and roadmap direction.
- Pheromone trails reward the safe read-only workflow patterns the pack exercised.
- Future patch missions plan faster and route more accurately because that context is retrievable.
