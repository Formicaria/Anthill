# ANTHILL UI Roadmap — Colony Command Center

Forward direction for the console HUD. **v1.8.17 ships Phases 1–2**; Phases 3–10 are documented
direction, not yet built. The console is a single embedded `Ui/index.html` (vanilla HTML/CSS/JS,
CSS variables, inline SVG) — no React/Tailwind — so "components" are reusable CSS classes + JS
render helpers, and upgrades stay additive and non-breaking.

## Delivered in v1.8.17

### Phase 1 — Design System Foundation
Shared HUD primitives: `hud-badge` (status), `hud-risk`, `hud-panel` (glass + corner brackets +
active/warn/alert glow), `hud-metric` cards, `hud-telem` lines, loading/empty/error state blocks,
`hud-act` action buttons. JS helpers `hudBadge()`, `hudRisk()`, `hudStatusClass()`. Consistent dark
command-center visual language driven by the existing theme variables.

### Phase 2 — Overview Command Dashboard
Colony status strip, central system core orb, operator attention panel, hardware/environment metric
cards (governor signals), terminal-style mission command node with mode helpers, and recent
mission/patch/objective summaries. All real-data-first with graceful fallbacks; deep-links into the
existing pages.

### Phase 3 — Mission Composer + Plan Preview (v1.8.18)
Dry-run planner endpoint (`POST /missions/plan`) that returns the task plan + constraint flags
without creating a mission, and a Mission Composer on the Overview node: compose goal + mode, Preview
Plan (ordered steps with ant badges, types, and dependencies + a constraint banner), then Approve &
Dispatch / Edit / Reject. One-shot dispatch preserved.

### Phase 4 — Colony Live Canvas 2.0 (v1.8.19)
Additive upgrade to the Colony canvas: caste legend + live pheromone-trail HUD (`/pheromones/json`),
real pheromone drift scaled by colony trail strength, and a corrected node inspector showing live
task load (running/completed/failed). Pre-existing task-dependency edges, handoff animation, worker
nodes, pan/zoom, and inspector preserved.

### Phase 5 — Objective Command Board (v1.8.20)
New admin Objectives page: seven lifecycle lanes (backlog/active/paused/completed/stopped/looping/
failed) from `/objectives` status + end_reason; cards with runs/EMA/priority/end-reason, expandable
to runs/missions/tasks/patch rollup with deep links. Reuses existing endpoints.

### Phase 6 — Mission Timeline + Task DAG Viewer (v1.8.20)
Lazy "Task Flow" section in the mission report: a dependency-layered DAG (status/ant colors, failure
paths in red) and a start-ordered timeline with duration bars, both from `/missions/{id}/graph`, plus
a click-to-open task detail drawer. Final-output separation preserved.

### Phase 8 — Ant Inspector + Performance Observatory (v1.8.22)
New admin Ant Inspector page: per-caste model route, applicable capability gates, and lifetime task
stats (totals, done/failed/skipped, success rate, avg seconds) from `GET /ants/stats`, plus a
recent-activity expander. Bundled with the ANTHILL boot/shell ASCII banner.

## Future direction (not yet built)
- **Phase 7 — Visual Patch Center 2.0:** grouping by mission/objective/file/risk/status on top of the
  existing Patch Center (shipped in v1.8.16).
- **Phase 9 — Memory + Pheromone Explorer:** success/failure and loop-pattern visualization, mission
  memory + task/source/patch search, prune/archive controls.
- **Phase 10 — Full Command Center Polish:** command palette, global search, notification center,
  saved layouts, keyboard shortcuts, onboarding tour.

## Guardrails
Additive over destructive; preserve every existing page, route, and behavior (mission submission,
autonomy, approval/apply, settings, users, security, shell, event log, navigation). Real data first;
labeled fallbacks (`—` / Unknown / empty state) when unavailable — never fabricated operational
values. CSS-only animations, reduced-motion aware, no uncleaned timers. No Python written, modified,
or treated as active code.
