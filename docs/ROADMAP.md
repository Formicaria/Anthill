# ANTHILL Roadmap — Objective Lifecycle & Visual Patch Review

> **Status:** Retained as subsystem history. Phases 1–2 (and the direction that followed) are shipped.
> For the current ordered build order, see **[docs/NORTH_STAR.md](NORTH_STAR.md)** — the canonical roadmap.

This is a forward-direction roadmap for the revision line that begins with **v1.8.16**. Phase 1 and
Phase 2 are implemented in v1.8.16; Phases 3–10 are documented direction, **not yet built**. The
intent is to keep the colony's autonomous work correct (objectives that end cleanly, planners that
respect constraints) and to make everything the colony does to the codebase visible and reviewable.

## Delivered in v1.8.16

### Phase 1 — Objective Lifecycle Hardening
- One-shot and verification-only objectives complete cleanly (`Completed` / `Stopped`) instead of
  regenerating near-identical missions until loop detection retires them.
- Planner constraint enforcement: a `verification-only` / `read-only` / `do not modify files`
  mission never has coder patch-proposal tasks planned for it (prompt directive **and** a
  deterministic post-plan strip).
- Explicit end reasons on every ended objective: `completed_successfully`,
  `stopped_no_followup_required`, `retired_looping`, `failed`, `manually_paused`, `manually_stopped`.
- Loop detection is preserved strictly for true repeated autonomy loops — it is no longer the
  normal ending path for successful maintenance work.

### Phase 2 — Visual Patch Review Center
- New **Patch Center** page: every patch proposal in a filterable list (status, risk, mission,
  objective, file path) with status/risk badges.
- Expandable unified **diff viewer** (removed / added / context) per patch.
- Actions: View Diff, Approve, Reject, Apply (when approved), View Mission — reusing the existing
  approval/apply safety model. Apply shows a confirmation with operator safety checks (risk level,
  missing old content, no backup yet).
- Patch links wired into mission Results, the Autonomy runs table (patch summary per run), and the
  Completed Objectives detail (patch activity per objective).

## Future direction (not yet built)

### Phase 3 — Mission Timeline
A single expandable history per mission: objective → run → mission → tasks → events → patches, so an
operator can reconstruct exactly what happened without cross-referencing pages.

### Phase 4 — Approval Center v2
Batch approval, explicit duplicate/superseded patch handling, risk grouping, and visible build/test
requirement state before an apply is allowed.

### Phase 5 — Memory / RAG Foundation
Searchable mission memory, a record of approved decisions, a repo/docs index, and source-linked
mission context the ants can retrieve.

### Phase 6 — Ant Capability Profiles
Per-ant permissions: patch/no-patch, web/no-web, and max task/context limits, configured per ant.

### Phase 7 — Hardware-Aware Scheduler
Effective concurrency that accounts for CPU/RAM, Ollama latency, and VRAM/model-load state, with a
human-readable explanation of why concurrency was scaled.

### Phase 8 — Model Routing Intelligence
Per-ant model performance statistics, a model test harness, and a model-assignment UI.

### Phase 9 — Self-Modification Safety Lanes
Distinct lanes with their own gates: docs-only, config-only, test-only, source-patch, and an
auto-apply-eligible lane (extending the Phase 5 gated auto-apply allowlist).

### Phase 10 — Colony Command Center
A live colony map: pheromone trails, objective lanes, a mission timeline, a patch stream, and an
operator command console in one operational view.

## Implementation guardrails (unchanged)
- C#/.NET first; optional native C++. **No Python** is written, modified, or treated as active code
  (`py.old/` is archived historical material only).
- Prefer additive API/UI and DTOs over destructive rewrites; reuse existing approval, mission, task,
  event, and objective models.
- Preserve authentication/permission checks and the approve-then-apply write safety model.
