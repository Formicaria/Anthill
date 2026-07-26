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

## Build order and status

| Stage | Scope | Release | Status |
|---|---|---|---|
| 0 | Audit: routes, page ids, topology DOM, polling, `app.js`, UI-state API | — | done |
| 1 | Workspace state model (C#, tested), kill switch, this document | v2.14.2 | **done** |
| 2 | Panel shell: register/render, header controls, collapse · minimize · hide · pin, Modules menu, layout lock | v2.14.3 | **done** |
| 3 | Drag, resize, snap guides, z-order, clamping, debounced save | v2.14.4 | **done** |
| 4 | Tab groups: create by drag, reorder, detach, active-tab persistence | v2.14.5 | planned |
| 5 | Migrate existing dashboard cards to registered panels (incremental) | v2.15.0 | planned |
| 6 | Extract the canonical topology surface as the workspace canvas | v2.15.x | planned |
| 7 | Topology overlays: view controls, legends/keys, inspector, prefs, hints — all hideable + anchored | v2.15.x | planned |
| 8 | Unified workspace + polished default layout + lifecycle audit | v2.16.0 | planned |
| 9 | Route consolidation + legacy Colony redirect (flag still respected) | v2.16.x | planned |
| 10 | Responsive (compact profile) + accessibility pass | v2.16.x | planned |
| 11 | Documentation sync + final verification | — | per release |

Docking/split-panel layouts are explicitly **deferred** past stage 10 and are not required for the
feature to be considered delivered.

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
