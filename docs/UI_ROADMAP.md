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

## Future direction (not yet built)
- **Phase 4 — Colony Live Canvas 2.0:** Queen + ant caste nodes, task dependency edges, pheromone
  trails, handoff animation, clickable worker/task detail.
- **Phase 5 — Objective Command Board:** backlog/active/paused/completed/stopped/looping/failed
  lanes; objective cards with expandable related runs/missions/tasks/patches and clear end reasons.
- **Phase 6 — Mission Timeline + Task DAG Viewer:** timeline + DAG, task detail drawer, patch links,
  failure-path visibility, final-output separation.
- **Phase 7 — Visual Patch Center 2.0:** grouping by mission/objective/file/risk/status on top of the
  existing Patch Center (shipped in v1.8.16).
- **Phase 8 — Ant Inspector + Performance Observatory:** per-ant permissions, current tasks, history,
  model routes, success/failure and verifier-rejection stats.
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
