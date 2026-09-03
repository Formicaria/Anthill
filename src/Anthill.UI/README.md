# Anthill.UI

The console's assets. Phase 6 of `docs/REFACTOR-PLAN.md`, v3.8.17.

```
index.html          the console shell
app.js              the console                       (~503 KB)
mission-thread.js   the mission conversation view
dashboard-grid.js   the responsive grid               (v3.3.0)
dashboard-grid.css
colony-topology.js  Colony Live: the reducer over the read model (/colony/live/snapshot, /records,
                    the stream watermark, /graph, approvals, the fleet) — owns state, never fetches
colony-live.js      Colony Live: the formicarium renderer, canvas-2D with a 3D projection — renders
                    the reducer's scene, never decides, never fetches
colony-host.js      Colony Live: the wiring — the ONLY file that reaches the network (hydration,
                    /ui/state layout persistence, per-mound stop); toggle; default on
colony-home.js      Colony Live: the page — focus mode, the live bar, sector/record panels, and the
                    composer that hands every message to Chat (a doorway, not a pipeline)
micromound.js       the Micromound console (Tools › Micromound)
```

Colony Live is the default view and the landing page (`/colony/live`, full-screen "focus" until the
operator opens the console around it); `Classic 2D` in the live bar opts out, and the classic canvas
is the permanent fallback. Everything the renderer draws comes from the read model: a chamber's
grains are its persisted records (placed by a hash of their id, `verified` ones in the core), its
orbs are the registry's residents, its label is the projection's unless the operator renamed it.
There is no vendored runtime — no three.js — so the console stays first-party and the CSP stays
`script-src 'self'`.

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
