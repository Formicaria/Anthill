# Topology-First Dashboard Workspace

Canonical design + build order for merging the Colony topology and the Dashboard into one
customizable command workspace. Status is tracked per stage; nothing is claimed complete until its
gate passes.

Inspired by the *interaction patterns* of Homarr and professional trading/monitoring terminals.
No proprietary code, design, or branding is copied.

## The model

The live colony topology is the **persistent canvas** of the Dashboard, not a card on it.
Operational panels float above it and can be dragged, resized, collapsed, minimized, hidden,
pinned, and grouped into tabs. Topology chrome (view controls, legends, keys, inspector, hints) is
a set of independently hideable overlays.

```text
dashboard-workspace
├── topology-surface        one canonical instance: live canvas, chamber SVG, expanded, pheromones
├── topology-overlay-layer  view controls · legend/keys · map prefs · inspector · hints (all hideable)
└── dashboard-panel-layer   floating panels · docked panels · tab stacks · minimized tray · toolbar
```

## Decisions that shaped this plan

These are deliberate departures from the original brief, taken after review:

1. **Kill switch, not a leap.** Everything ships behind `dashboard_workspace_enabled`
   (default **false**). The classic Overview + Colony pages remain canonical and untouched until
   an operator opts in. Flipping the flag off is the instant rollback.
2. **Several small releases, not one 50-item gate.** An all-or-nothing acceptance list means
   nothing merges until everything works — the mega-patch failure this project has repeatedly
   avoided. Each stage ships on its own.
3. **Docking and split-panels come last, and may never come.** Free positioning + snap guides +
   tab groups deliver most of the value; six dock zones with previews and drag-out is where
   hand-rolled window managers accumulate geometry bugs. Ship without it, add only if real use
   demands it.
4. **Layout correctness lives in C#.** This repo has no browser test harness, and adding one
   contradicts the no-build-system constraint. So validation, clamping, migration, and recovery
   live in `DashboardWorkspaceState` and are unit-tested in xUnit. JavaScript keeps interaction
   only; interaction is verified by the manual walkthrough, which is stated honestly rather than
   dressed up as automated coverage.
5. **Desktop and compact are separate profiles.** One `panels` map plus "don't overwrite desktop"
   is a contradiction; visiting on a phone must not clobber the desktop arrangement.
6. **Opacity dims a scrim, never text.** Presets adjust the backdrop behind panel content so
   contrast against the animated map holds. Text layers are never made translucent.
7. **Auto-save, no "Save Layout" button.** Saving after interaction ends (debounced) plus an
   explicit **Reset Layout** is simpler and cannot lose work.
8. **Two flags, not three modes.** `locked` + `focus_mode`. "Customize mode" is simply
   `locked = false`.
9. **Pointer arbitration is a first-class design item** (see below) — the canvas already drags
   ants, drags chambers, and pans, so panel dragging above it needs explicit hit-testing rules.
10. **Performance has a number.** The topology now renders permanently instead of only on the
    Colony page; it must throttle when occluded or backgrounded.

## One renderer, chambers as a layout (v2.14.5 decision)

The original brief kept Live Colony, Chamber, and Expanded as separate *views* while also demanding
"one canonical topology instance" — those two requirements fight each other, and the repo carried
the cost: two renderers (canvas + `cmap2` SVG), two sets of map preferences, two inspectors, two
pan/zoom states, and duplicate polling.

Resolved by collapsing to **one renderer — the live colony canvas**, which is the mature, stable
implementation. Chambers are now a *layout mode* of that canvas: the same ants, same drag, same
pulses, same pheromones, same inspector, clustered into role chambers with rings and labels drawn
in world space. Map preferences (motion, labels, pheromones) and reset view / reset layout moved
onto the canvas viewbar, where they now genuinely govern rendering rather than a parallel SVG.

Consequences:

- The **Chambers** button replaces the old "Groups" view and does not route anywhere — it
  reorganizes in place.
- The chamber SVG becomes redundant and is retired in the following release, once parity has been
  confirmed in real use rather than assumed.
- Stage 6 ("extract canonical topology surface") gets much smaller and safer: there is only one
  surface left to move under the panels.

## Pointer-event arbitration

The single largest implementation risk. Rules:

- A drag beginning on a panel header moves the panel and **never** pans the map.
- A drag beginning on empty canvas pans the map and **never** moves a panel.
- Ant/chamber drags keep priority over map panning, exactly as today.
- While `locked`, panels receive pointer events only inside their own bounds; everything else
  falls through to the topology.
- Touch: a single pointer model (Pointer Events) for both, with no synthesized double-fire.

## Persistence

Stored in the existing `ui_state.json` under `dashboard_workspace`, repaired server-side on every
load by `DashboardWorkspaceState.Sanitize`:

- versioned (`schema_version`), forward-compatible: unknown future keys survive a round trip;
- every panel and overlay validated **independently** — one bad entry never discards the layout;
- invalid coordinates/sizes clamped; off-screen panels recovered with a grabbable header edge;
- unknown panels dropped (no renderer exists); newly shipped panels merged in **without moving**
  customized ones;
- tab groups with broken references repaired; a group under two members dissolves and its survivor
  floats rather than being stranded;
- **the invariant**: a corrupt workspace resets *only* `dashboard_workspace`. Ant names, colours,
  positions, and map preferences are never touched.

## WHERE WE ARE (as of v2.14.10) — start here

**Shipped and working:**

- Server-side workspace state model with validation, clamping, off-screen recovery, and
  desktop/compact profile isolation (`DashboardWorkspaceState`, 20 xUnit tests).
- Panel shell runtime (`dashboard-workspace.js/.css`, CSP-safe, no inline JS) with collapse,
  minimize-to-tray, hide + Modules-menu restore, pin, layout lock, focus mode, reset layout.
- Pointer-event drag and resize with map arbitration, edge snapping (Alt/Cmd bypass), rAF movement,
  save-once-at-pointerup.
- **One** topology renderer: the live colony canvas. The chamber SVG is fully deleted.
- Chambers as a canvas layout: seven fixed functional chambers, draggable as a unit, renamable by
  double-click, summary-only rings, standby marking for visible-only ants.
- Canvas map preferences (motion / labels / pheromones, reset view, reset layout) with persistence.
- Per-ant truthful pheromone field.
- Seven dashboard cards registered as workspace panels reusing the existing renderers.

**The flag is still `dashboard_workspace_enabled: false` by default.** Turning it on shows the panel
workspace over the *classic dashboard page* — the topology is **not yet** the background. That is
the next milestone.

**Next three sessions, in order:**

1. **v2.14.11 — Editable Ant Inspector** (spec below under "Queued: editable Ant Inspector side
   panel"). Self-contained; does not block the topology work.
2. **v2.14.12 — Topology as the dashboard canvas (Stage 6).** The big one. Move the canvas mount so
   the dashboard renders it full-bleed behind `#ws-root`, with panels above. Watch for: the canvas
   sizes from its container (`resize()`), so it must be measured after the workspace mounts; keep
   ONE render loop and one `PAGE_ENTER` polling path; verify pointer arbitration end-to-end (panel
   drag must not pan the map, and empty canvas must still pan).
3. **v2.14.13 — Topology overlays (Stage 7).** Make the canvas viewbar, legend/keys, hints, and the
   inspector independently hideable and anchor-positioned, with a Topology Overlays menu.

Then: tab groups (Stage 4), route consolidation with the legacy Colony redirect (Stage 9),
responsive/a11y pass (Stage 10). Docking and split-panels remain deferred and optional.

## Build order and status

| Stage | Scope | Release | Status |
|---|---|---|---|
| 0 | Audit: routes, page ids, topology DOM, polling, `app.js`, UI-state API | — | done |
| 1 | Workspace state model (C#, tested), kill switch, this document | v2.14.2 | **done** |
| 2 | Panel shell: register/render, header controls, collapse · minimize · hide · pin, Modules menu, layout lock | v2.14.3 | **done** |
| 3 | Drag, resize, snap guides, z-order, clamping, debounced save | v2.14.4 | **done** |
| 3b | **Topology consolidation**: chambers become a LAYOUT of the live canvas (not a second renderer); map preferences (motion, labels, pheromones) and reset view/layout move onto the canvas viewbar | v2.14.5 | **done** |
| 3b2 | Pheromone field tells per-ant truth (emission and brightness from each ant's own trail) | v2.14.6 | **done** |
| 3b3 | Chambers draggable as a unit (centre + member ants, persisted) | v2.14.7 | **done** |
| 3c | Retire the chamber SVG: markup, control bar, inspector, page plumbing removed; search repointed at the canvas | v2.14.8 | **done** |
| 3c2 | Sweep the now-unreachable `cmap*` functions and orphaned `#cmap2` CSS (dead code, no behaviour) | v2.14.9 | planned |
| 3d | Chamber renaming (double-click, mirroring ant rename; canonical keys unchanged) | v2.14.10 | **done** |
| 3e | Editable Ant Inspector side panel — see the spec above | v2.14.11 | planned |
| 4 | Tab groups: create by drag, reorder, detach, active-tab persistence | v2.14.12 | planned |
| 5 | Migrate existing dashboard cards to registered panels — renderers reused verbatim by re-parenting their own body elements, so there is one implementation per card | v2.14.9 | **done** |
| 6 | **Topology as the dashboard canvas** — mount the live canvas full-bleed behind `#ws-root`; measure after mount, one render loop, one polling path, verify arbitration | v2.14.12 | **next after 3e** |
| 7 | Topology overlays: view controls, legends/keys, inspector, prefs, hints — all hideable + anchored, with a Topology Overlays menu | v2.14.13 | planned |
| 8 | Unified workspace + polished default layout + lifecycle audit (no duplicate timers/listeners) | v2.15.0 | planned |
| 9 | Route consolidation + legacy Colony redirect (flag still respected) | v2.15.x | planned |
| 10 | Responsive (compact profile) + accessibility pass | v2.15.x | planned |
| 11 | Documentation sync + final verification | — | per release |

Docking/split-panel layouts are explicitly **deferred** past stage 10 and are not required for the
feature to be considered delivered.

## Queued: editable Ant Inspector side panel (operator request, next release)

Clicking an ant on the live colony canvas must open a right-side inspector that both **shows** and
**edits** that ant. Specified here so it is built once, correctly:

**Shows (read-only):** role id and display name, chamber, runtime kind, implemented / enabled /
planner-eligible / runtime-available with the unavailability reason, execution contract (supported
task types, required capabilities, allowed vs forbidden tools, side-effect and risk class,
compensation), permission contract, workers with their purposes, recent activity/events for that
ant, and its pheromone trail strength.

**Edits (each with an existing persistence path — do not invent new ones):**

- **Name** → `uiState.castes[role].name` (same key the dblclick rename writes; the two must not
  diverge).
- **Colour** → `uiState.castes[role].color`, via `casteColor`/`applyUiState`.
- **Model** → per-role routing. This is *not* UI state: it belongs to model routing config, so the
  control must write through the existing settings/provider-route endpoint with its normal auth,
  never directly. If that endpoint cannot set a per-role route, the field is read-only with a link
  to Settings rather than a control that silently does nothing.

**Rules:** the inspector never grants capabilities, never edits permissions or tool allowlists
(display only — those are contract-owned), and shows "standby / gate closed" for visible-only ants
instead of offering controls that cannot work. Reuse `#colony-right` (the existing Agent Inspector
region) rather than adding a second panel, and keep it CSP-safe (delegated `data-*` handlers).

## Performance budget

- One topology polling lifecycle; panels sharing an endpoint share the request.
- The topology render loop throttles when substantially occluded, on `document.hidden`, and under
  `prefers-reduced-motion`.
- Minimized/hidden/collapsed panels and inactive tabs pause expensive rendering and polling.
- Panel drag must not re-render the topology; panel resize reflows only that panel; overlay
  movement never reconstructs the map.
- State saves are debounced and written after interaction ends, not continuously.

## Accessibility

Beyond the usual (keyboard focus, real buttons, `aria-expanded`, `role="tablist"` with
`aria-selected`, reduced motion, touch targets): every drag-only capability must have a
non-drag equivalent in a menu, focus-mode exit is always reachable by keyboard, and panel content
must maintain contrast against a *moving* background — which is why opacity dims a backdrop scrim
and never the text.

## Security

UI and layout only. No change to authentication, authorization, patch application, auto-apply,
capability gates, ant permissions, mission execution, autonomy budgets, homelab action
permissions, credentials, API protection, model routing, or tool permissions. Panel actions call
the same protected endpoints they call today; no direct write paths are introduced.
