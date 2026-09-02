# Anthill.UI

The console's assets. Phase 6 of `docs/REFACTOR-PLAN.md`, v3.8.17.

```
index.html          the console shell
app.js              the console                       (~503 KB)
mission-thread.js   the mission conversation view
dashboard-grid.js   the responsive grid               (v3.3.0)
dashboard-grid.css
colony-topology.js  Colony Live: projection layer      (design doc §14 — owns state, never fetches)
colony-live.js      Colony Live: 3D formicarium renderer (design doc §17 — renders, never decides)
```

Colony Live is opt-in (`Live 3D` in the Colony viewbar); the classic canvas is the default and the
permanent fallback. The topology is fed from the handlers app.js already runs — `pollGraph`,
`loadColonyRegistry`, `pollApprovals`, the event stream — so the feature adds no request of its own.

## Why this is a folder and not a project

There is no `.csproj` here. These are static assets with no compilation unit, and a project
containing only content would produce an assembly that exists solely to be a name.

`Anthill.Api` embeds them — see the `EmbeddedResource` block in `Anthill.Api.csproj`, which pins
each `LogicalName`. That pinning is load-bearing rather than tidy: `ApiHost.LoadUiAsset` finds
assets by name SUFFIX, so a move that changed the generated resource names would serve a blank
console with no build error and nothing failing. `UiAbsenceTests` asserts each one is still found.

Still embedded rather than served from disk because a self-contained binary is the deployment
model. What phase 6 asks for is not loose files; it is that the API **serves when these are
absent** — which `LoadUiAsset` does by returning a fallback, and `UiAbsenceTests` proves.

## The rule

The console reads the SSE stream (`GET /events/stream`) and REST. It does not hold orchestration
logic: deciding what a mission does next, when a patch may apply, or whether a role may dispatch a
tool belongs to `Anthill.Core`, and an endpoint that decides one of those on the console's behalf is
a defect to fix in the core rather than a convenience to keep here.

Nine `/events/json` poll sites remain in `app.js` as a fallback behind the stream. They were kept
deliberately when the stream shipped in v3.8.3 and are still the safety net; replacing them is a
console change rather than a boundary one.
