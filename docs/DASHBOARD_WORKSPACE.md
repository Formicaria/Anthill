# Topology-First Dashboard Workspace

Canonical design + build order for merging the Colony topology and the Dashboard into one
customizable command workspace. Status is tracked per stage; nothing is claimed complete until its
gate passes.

> Cross-reference: Shadow Operations & Operator Qualification (Phase 7) is currently headless —
> v2.17.0 (recommendation engine + scoreboard) and v2.18.0 (v2.18.2 fixed the Missions conversation being rebuilt by the 3s jobs poll) (fault-injection catalog + simulation
> harness) are backend releases with **no dashboard surface yet**. A Shadow panel (diagnosis /
> prediction / rollback bundle + the qualification scoreboard + fault-simulation report) is a
> planned later stage on this workspace.

> **v3.2.0: this document describes a workspace that no longer exists.** The floating-panel
> workspace was replaced by a responsive CSS Grid; `dashboard-workspace.js` and its stylesheet are
> deleted. Panel placement, docking, tab groups, snapping and saved layouts are all gone. Kept as
> the historical record of why those mechanisms existed — several of their lessons were ported into
> the grid's guards. See `docs/DASHBOARD_GRID_MIGRATION.md` for what replaced it.
>
> *(Dashboard work concluded at v3.2.1; the shipping release is now v3.8.9. That release does
> touch the console, but only in how it is FED: `/events/stream` pushes events so panels stop
> serving data up to three seconds stale. The layout and workspace model this document
> describes are unchanged, and polling remains the fallback.)*
>
> **v3.2.1 returns direct manipulation, on the grid's terms.** Widgets are dragged to position and
> resized from their corner again — but by reordering a flow, not by placing panels on a plane.
> There are no coordinates, no z-order and no docking, so the failure modes this document catalogs
> (overlap, clipping, panels stranded off-screen) cannot recur: a widget's position is its index in
> a list and its width is a proportion of the row. The one mechanism deliberately not restored is
> free x/y placement, which is what made all three of those failures possible.
>
> v3.1.1 note: the Mission Composer is now a workspace panel (`mission-composer`). It had been
> reachable only on the classic overview grid, which this workspace hides — so the plan-preview
> review step had no control in the default console from v2.15.0 until this release.
>
> v3.1.0 note: one dashboard-visible change — the plan preview now runs the same authorization
> gate a dispatch does, so a step the runtime would refuse renders as REFUSED with its reason
> instead of looking like an ordinary planned step. Everything else in the release is internal
> composition work with no console surface.
>
> v3.0.1 note: no dashboard surface either — a backend-only mission-scoring integrity fix (a
> model-unavailable / all-fallback run can no longer show as a verified success).
> v3.0.0 note: no new dashboard surface — a baseline-lock release. `GET /runtime/inventory`
> exposes the generated declaration/consumer inventory and the call-site audit verdict.
> v2.26.0 note: no new dashboard surface — a hardening release. Config-health findings and
> introspection are API-first (`/config/health`, `/colony/introspection`); job statuses gain
> `timed_out` (mapped from the canonical mission outcome, so status can never contradict it).
> v2.25.0 note: **Homelab → Automation** runs now read as a conversation (the v2.16.0 "Next:"
> item): what the rule noticed, then what the colony did about it, in plain English — cooldown and
> cap skips read as deliberate quiet, because restraint is the engine working. The raw outcome
> token sits behind a hover. The Shadow Qualification panel gains live inputs from `/shadow/judge`
> (operator judgments) and the readiness gate reads the same stores at `/readiness/json`.
> v2.24.0 note: the Modules menu now actually collapses. `hidden` only carries `display:none`
> from the UA stylesheet, and `.ws-modules` sets `display:flex` — so two earlier fixes set the
> attribute correctly and changed nothing on screen. `.ws-modules[hidden]` and `.ws-tray[hidden]`
> rules restore it, with a guard test over every script-hidden element.
>
> v2.24.0 note: objective-verification failures appear as `objective_verification_failed` events.
> v2.24.0 note: **Homelab → Automation** gains a Shadow Qualification panel — the recommendation
> bundle shadow mode would have produced for each real incident, beside the scoreboard comparing it
> with what the operator actually did. Nothing in that table was executed; shadow mode has no action
> pathway. An empty scoreboard reads "not qualified", never as a pass. Off by default
> (`shadow_observation_enabled`).

> v2.23.0 note: no new surface — route registration is recorded as `skill_candidate_registered` events.

> v2.22.0 note: the Modules toggle now reports its real state (▸ / ▾ and a truthful
> `aria-expanded`, previously hardcoded to `'false'`), and Focus mode closes the module list and
> keeps it closed — enforced in the setter as well as at render. Skill credit is recorded as
> `skill_outcome_recorded` events; no other new surface.

> v2.21.0 note: handoff-created tasks appear in the mission task graph like any other task —
> they carry a `Handoff: source -> destination` title and a depth marker. No new surface.

> v2.19.0 note: the workspace Modules checklist is now collapsible (it was persistent and in the
> way), and mission results are unchanged on this surface — the adaptive-runtime work in v2.19.0
> deliberately preserved every operator-facing narrative. v2.20.0 adds the learning-reset boundary
> to memory surfaces: `/memory/explorer` carries `learning_reset`, and pre-reset pheromone trails
> appear with a `legacy` flag. See `docs/ADAPTIVE_RUNTIME_STATUS.md`.

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

## WHERE WE ARE (as of v2.16.0) — TRACK COMPLETE

**Shipped and working:**

- Server-side workspace state model with validation, clamping, off-screen recovery, and
  desktop/compact profile isolation (`DashboardWorkspaceState`, 20 xUnit tests).
- Panel shell runtime (`dashboard-workspace.js/.css`, CSP-safe) with collapse, minimize-to-tray,
  hide + Modules-menu restore, pin, layout lock, focus mode, reset layout.
- Pointer-event drag and resize with map arbitration, edge snapping, rAF movement, save-at-pointerup.
- **One** topology renderer: the live colony canvas. The chamber SVG is fully deleted.
- Chambers as a canvas layout: seven fixed chambers, draggable as a unit, renamable by double-click.
- Canvas map preferences (motion / labels / pheromones, reset view, reset layout).
- Per-ant truthful pheromone field.
- Seven dashboard cards registered as workspace panels reusing the existing renderers.
- **Editable Ant Inspector** (Stage 3e) — the existing `#colony-right` card now edits name, accent
  colour, and model route, and shows chamber, runtime status, planner eligibility, pheromone
  strength, and workspace path allowlists.
- **Topology as the dashboard canvas** (Stage 6) — `#colony-canvas-area` is re-parented between the
  Colony page and a `#ws-topology` layer behind `#ws-root`. One canvas, one loop, one polling path.
- **Topology overlays** (Stage 7) — view controls, caste legend, learning signals, and interaction
  hints are independently hideable and re-anchorable across six slots, with an always-available
  Overlays menu. State is validated server-side.
- **The workspace sanitizer is actually wired in** — `/ui/state` GET and PUT now run
  `DashboardWorkspaceState`. Until v2.14.14 it was called only from unit tests.
- **Nine panels, including the Colony page's own** — the Agent Inspector and Jobs list are now
  workspace panels, so the dashboard hosts everything the Colony page did.
- **The topology is persistent** — with the workspace live, `/colony/topology` resolves to the
  dashboard and the canvas stays mounted there for the whole session instead of being re-parented
  on every navigation. Keyed off the topology layer existing, not off the flag, so a workspace that
  fails to initialise leaves the Colony route working exactly as before.

**v2.15.0: `dashboard_workspace_enabled` now defaults to ON — the workspace is the console.** It
remains a kill switch: set it false to restore the classic Overview grid and standalone Colony page
instantly, with no migration and no data loss. The config property is `bool?` so an explicit false
survives the default flip; only an unset value takes the new default.

Also shipped in v2.15.0: tab groups, the single-writer fix, the default layout, and the
responsive/accessibility pass.

**v2.15.1** made the topology fill the entire page (all classic sections taken out of flow by one
rule), promoted the last six cards to panels for fifteen total, fixed the clipped colony view bar
and the mid-page status bar, and **replaced docking with edge/corner snapping** — halves and
quadrants. Saved docked layouts migrate to the equivalent snap rather than breaking.

**v2.15.2** fixed the workspace chrome positioning: `#page-overview.ws-active` needed
`position: relative`, without which every absolute layer resolved against the viewport and rendered
under the nav sidebar. The fixed chrome now has an explicit vertical budget (status bar 0-52,
toolbar 58-90, overlay slots from 96, mission bar pinned at the bottom above the panel layer).

**v2.15.3** fixed a hotfix regression from that change: the classic-page hide rule was an id
allow-list, so the newly added status bar and mission bar were both `display:none`. It excludes by
`.ws-layer` class now, which is additive-safe.

**v2.16.0** changed the default panel arrangement (two rails, clear centre, six panels hidden) and
rewrote the chamber layout so each role owns an angular sector — zero overlapping nodes, verified.
Missions moved to a conversation view; the same treatment for Automation is queued.

### Release numbering correction

v2.14.11 and v2.14.12 were both consumed by hotfixes (colony layout, then the missing colony/chamber
definitions), so the feature stages shifted. The table below is the corrected mapping.

**The build order below is complete.** Remaining work on this surface is ordinary maintenance, not
a staged track. Known follow-ups:

- Occlusion-based render throttling is still not implemented (the loop suppresses only on a
  backgrounded tab or a zero-sized canvas). A wrong "it's hidden" freezes the map, so it is not
  being guessed at.
- Two debounced writers became one in v2.15.0, but the single writer is still last-write-wins
  against a concurrent second browser tab. Nobody has hit that; it is recorded, not fixed.

**Superseded plan (kept for provenance):**

1. **v2.14.16 — Tab groups (Stage 4).** Now lands on a dashboard that already hosts all nine
   panels, so grouping is the last piece of "modular and customizable".
2. **v2.15.0 — Unified workspace, default layout, and the Stage 8 lifecycle audit.** That audit now
   has a specific job: `saveUiState` (app.js) and `save()` (dashboard-workspace.js) are two
   independent debounced writers doing read-modify-write against the same document. Both preserve
   each other's keys as of v2.14.14, but last-PUT-wins remains. Collapsing them into one owner is
   Stage 8's work.
3. **Then** route consolidation with the legacy Colony redirect (Stage 9) and the responsive/a11y
   pass (Stage 10).

**Deferred deliberately:** the inspector is not an overlay. On the Colony page it is a sidebar card
rather than canvas chrome, so anchoring it belongs with Stage 9, when that sidebar layout goes away.

**Known gap, stated honestly:** the render loop currently suppresses drawing only when the tab is
backgrounded or the canvas measures zero (i.e. its page is `display:none`). Occlusion-based
throttling — "mostly covered by panels" — is **not** implemented. A wrong "it's hidden" silently
freezes the map, and this repo has paid for that twice, so it is not being guessed at.

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
| 3c2 | Sweep the now-unreachable `cmap*` functions and orphaned `#cmap2` CSS (dead code, no behaviour) | v2.14.9 | **done** |
| 3d | Chamber renaming (double-click, mirroring ant rename; canonical keys unchanged) | v2.14.10 | **done** |
| 3e | Editable Ant Inspector side panel — see the spec above | v2.14.13 | **done** |
| 4 | Tab groups: create by drag, reorder, detach, active-tab persistence | v2.15.0 | **done** |
| 5 | Migrate existing dashboard cards to registered panels — renderers reused verbatim by re-parenting their own body elements, so there is one implementation per card | v2.14.9 | **done** |
| 6 | **Topology as the dashboard canvas** — mount the live canvas full-bleed behind `#ws-root`; measure after mount, one render loop, one polling path, verify arbitration | v2.14.13 | **done** |
| 7 | Topology overlays: view controls, legend, signals, hints — hideable + anchored across six slots, with an Overlays menu (inspector deferred to Stage 9) | v2.14.14 | **done** |
| 8 | Unified workspace + polished default layout + lifecycle audit (no duplicate timers/listeners) | v2.15.0 | planned |
| 9 | (groundwork done in v2.14.15) Route consolidation + legacy Colony redirect (flag still respected) | v2.15.x | planned |
| 10 | Responsive (compact profile) + accessibility pass | v2.15.x | planned |
| 11 | Documentation sync + final verification | — | per release |

Docking/split-panel layouts are explicitly **deferred** past stage 10 and are not required for the
feature to be considered delivered.

## Delivered in v2.14.13: editable Ant Inspector side panel (operator request)

Clicking an ant on the live colony canvas opens the right-side inspector, which both **shows** and
**edits** that ant. Built into the existing `#colony-right` Agent Inspector card — no second panel.

Two deviations from the spec below, both deliberate:

- **Execution contract detail** (supported task types, required capabilities, side-effect and risk
  class, compensation) is *not* shown, because `/colony/registry` does not expose it. What the
  endpoint does expose — runtime kind, implemented, enabled, planner-eligible, runtime-available,
  and the unavailability reason — is shown in full. Inventing the rest was not an option.
- **Name and colour are caste-level.** Worker nodes derive their label and colour from their caste
  in `applyUiState`, so a worker's inspector edits the caste and says so, rather than pretending to
  offer a per-worker name that nothing would read.

Original specification:

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
