# Dashboard grid migration — remaining work

**Status:** phases 1 and 2 landed on `feat/v3.3.0-dashboard-grid`. This document is the plan for
what is left, written *before* touching it because the test surface turned out to be four times
larger than it looked and mostly not about the workspace at all.

## What has landed

| Commit | What |
|---|---|
| `8fbb8d2` | Grid + widget framework (`dashboard-grid.css`, `dashboard-grid.js`), embedded and served |
| `55b19b5` | All 16 panels migrated to grid widgets; the grid is the console; floating paradigm gone from the page |
| `116c317` | Widget spans made proportionally invariant across breakpoints |

Verified in a live console: 17 widgets, 7 rows, **100% row occupancy** at 1366 and 1920, zero
overlaps, zero off-screen, no page-level horizontal scroll. The ≥2200px breakpoint was **simulated**
(span values forced onto the live grid), not exercised as a real media query — the available display
clamps at 1920. That distinction is deliberate; do not upgrade it to "verified" without a wide
display.

## The finding that stopped the next step

`DashboardWorkspaceShellTests.cs` contains **62 tests**, and the name is a lie. Only about two
thirds concern the floating workspace. The rest grew into it because it became the de-facto "UI
shell" test file, and deleting the file with the workspace would silently delete them.

### Must SURVIVE (relocate to a new `UiShellTests.cs`; nothing to do with the workspace)

- `MissionThread_*` (7), `MissionActivity_*` (2), `MissionReport_*` (2), `MissionsJson_CarriesTheAnswer`,
  `MissionThreadModule_IsShippedAndLoaded`, `MissionThreadTests_RunInCiAndValidate`,
  `Dispatch_SurfacesFailures_AndKeepsTheTypedDirective` — the mission thread reconciler.
  **Several are hard-won regression guards, not decoration:**
  - `MissionThread_WritesServerTextAsTextNotMarkup` — an XSS guard.
  - `MissionThread_RejectsStaleResponses` — an out-of-order-response guard.
  - `MissionThread_IsNeverRebuiltWholesale` — a scroll/focus-loss guard.
  - `MissionThread_IsPoliteAboutScrolling`, `MissionThread_AnnouncesOneResult_NotTheWholeThread` —
    accessibility.
- `ChamberLayout_GivesEachRoleItsOwnSector`, `ChamberRing_LeavesRoomForTheLargestChamber` — colony
  visualisation geometry, independent of layout engine.
- `CspRemains_ScriptSrcSelf_WithoutUnsafeInline` — the console carries no inline script. The grid
  work already depends on this holding (a browser refused an `eval` harness during phase 1).
- `EveryElementHiddenFromScript_HasACssRuleThatActuallyHidesIt` — general UI integrity.
- `FocusStyles_AndReducedMotion_ArePresent` — accessibility.

### Must be PORTED to grid terms (intent survives, mechanism changes)

- `EveryWorkWorkflowControl_IsReachableInTheWorkspace` → assert the composer is a registered grid
  widget. **This is the v3.1.1 fix's guard**; the plan preview was unreachable for many releases and
  must not silently become so again.
- `WorkspaceDashboard_TakesAllClassicContentOutOfFlow` → the `dg-active` equivalent, and keep the
  v2.15.3 lesson: exclude by CLASS, never by an id allow-list.
- `MissionDirective_IsAboveThePanelLayer` → in a grid there is no panel layer to be covered by, so
  the property is now "the composer widget exists and is reachable". Do not delete silently: record
  that the hazard was removed by construction rather than the guard being dropped.
- `ControlsAreRealButtons_WithLabelsAndState` → the grid's refresh control must keep this.
- `ModulesChecklist_*`, `ModulesToggle_*`, `FocusMode_*` → only if an equivalent affordance is
  built; otherwise retire WITH a note, because "hiding every overlay must stay recoverable" was a
  real lesson.

### Retire with the workspace (~35)

Tab groups, docking, snapping, dragging, resize handles, gestures, layers, chrome bands, opacity
presets, compact/locked profiles, panel placement, `DockingIsFullyRemovedFromTheClient`,
`ClientSnapRegions_MatchTheServer`, `ProfileBreakpoint_MatchesTheServerConstant`, and
`DashboardWorkspaceStateTests` (29 tests) in full.

## Remaining steps, in order

1. **Split the test file first, before deleting anything.** Move the survivors into `UiShellTests.cs`
   and confirm green. This is the step that makes the deletion safe, and doing it in the other order
   is how the XSS guard disappears.
2. Delete `dashboard-workspace.js` / `.css`, their `EmbeddedResource` entries, their `ApiHost`
   fields and routes, and the `ws-*` CSS in `index.html`.
3. Remove `registerWorkspacePanels` / `initDashboardWorkspace` / `wsMountTarget` from `app.js`.
4. Retire `DashboardWorkspaceState`, `KnownPanelIds`, the `/ui/state` workspace document and
   `dashboard_workspace_enabled`. **Check `/ui/state` first** — `app.js` also persists overlay state
   and a layout registry through it, so it is not exclusively the workspace's.
5. Update `RegressionGuardTests.Workspace_CanonicalIdsMatchTheClientRegistrations` — it asserts the
   server's `KnownPanelIds` equals the client's panel registrations. The grid equivalent is worth
   keeping: server and client must not disagree about what widgets exist.
6. Phase 3: density. The live screenshots show the targets — Colony Health, Jobs and Resource Usage
   render near-empty cards. Each widget should answer a question at a glance.

## Note on persistence

The operator chose **hard replace** with layouts **reset to grid defaults**. Old workspace rows stay
in the database unread — nothing migrates them, and because the kill switch is being removed there
is no path back. That is a coherent choice, but it means the grid must be right before this branch
merges, not recoverable afterwards.
