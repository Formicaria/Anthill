# Handoff: Colony Live 3D

## Start here

**Do not rebuild this from a description. Port the working code.**

`reference/colony-renderer.js` and `reference/colony-topology.js` are a complete, working implementation. Every previous from-scratch attempt produced an unclickable blur of particles, because the look depends on exact shader constants, an exact particle budget, and a screen-space hit-test that no prose brief reliably conveys.

The correct task is: **move these two modules into the target codebase essentially as-is** (they are plain ES modules with no dependencies except a global `THREE`), and rebuild only the surrounding UI shell — chrome, panels, inspectors, mission clock — in the codebase's own framework. `SPEC.md` is the reference for the UI shell and for understanding *why* the renderer does what it does. It is not a substitute for the renderer source.

Suggested first prompt to Claude Code:

> Read design_handoff_colony_live_3d/README.md, then SPEC.md, then reference/colony-renderer.js and reference/colony-topology.js. Port colony-renderer.js and colony-topology.js into <path> with no behavioral changes — keep every numeric constant, both GLSL shader pairs, the screen-space hitTest, and the per-frame easing factors exactly as written. Then reimplement the UI shell described in SPEC.md §11 as <framework> components that talk to the stage only through its documented method surface. Do not rewrite the renderer's math.

---

## Overview

Colony Live is a WebGL topology view for an agent-orchestration console ("Anthill"). Seven "chambers" — the Queen's Core, four operational galleries, an Output chamber, and a child Infrastructure Micromound — are drawn entirely from particles, connected by 16 particle-stream tunnels, and populated by clickable glowing "ant" orbs representing the agent roster. An operator can orbit the colony, click a chamber to see and rename it, drill through five zoom levels down to a single record, click any ant for its authored-record stats, and recolor a chamber to repaint every particle and conduit that belongs to it.

Every particle is a real data point. There is exactly one record particle per persisted record and one orb per roster entry. Nothing is decorative.

## About the design files

The files in `reference/` are **design references created in HTML** — a working prototype of the intended look and behavior, not production code to ship. Two of them are the exception noted above: `colony-renderer.js` and `colony-topology.js` are framework-agnostic ES modules and should be **ported nearly verbatim**, because they *are* the design. The remaining files (`Colony Live 3D.dc.html`, `support.js`) are prototype scaffolding — read them for the UI shell's structure and copy, then recreate that shell using the target codebase's established patterns and libraries. If the project has no frontend environment yet, choose one and implement the shell there.

## Fidelity

**High-fidelity.** Final colors, typography, spacing, motion constants and interaction behavior. Recreate the UI pixel-perfectly. The renderer's numeric constants are not stylistic suggestions — changing them breaks the look in the specific ways catalogued below.

---

## Why previous attempts failed (read before writing code)

| Symptom seen | Cause | Fix |
|---|---|---|
| Dense fuzzy balls of hundreds of dots per chamber | Used the vestigial `points:` field (2600, 1500, …) as a particle count | The particle count is `records.length` — roughly **104 per chamber**, 46 for the Micromound, **~650 in the whole scene**. The `points:` field is unused legacy data; ignore it. |
| Everything looks out of focus | Soft sprite texture, or `PointsMaterial` | Both point shaders must keep the hard cut `if (texture2D(uMap, gl_PointCoord).a < 0.5) discard;` and use `dotTex`/`grainTex` (opaque to 0.92–0.98, transparent only at 1.0). This is what makes grains crisp. |
| Field of large soft orange/white blobs floating in empty space | Invented decorative glow sprites, or ant orbs scaled in world units | The only sprites in the scene are: one `nucleus` halo per chamber (`scale = r*4.2`), the roster orbs (**59 total**, sized in *screen pixels* via `pxScale`), two hidden travellers per conduit, one lock seal, one approval ring. Nothing else. No backdrop, no starfield, no bokeh. |
| Chambers not clickable | Tried to raycast against `THREE.Points` | There is no raycaster. Hit-testing projects candidate world positions to screen and takes the nearest within a pixel radius, in specificity order (see below). |
| Tunnels are dashed lines crossing the frame | Drew `Line`/`LineDashedMaterial`, or the curve degenerated to straight | Conduits are `THREE.Points` clouds of 24–60 grains advanced in path space along a 4-point Catmull-Rom curve. If a curve looks straight, the `bow`/`lean`/`lift`/`sag` terms were dropped or the fallback `straight()` path is being taken. |
| Chambers in the wrong places; extra chambers | Positions re-derived at runtime | The seven positions are permanent literals. There are exactly seven sectors; `HOMELAB` is not one of them. |
| Nebula-like colored wash filling a quadrant | `nucleus` opacity or scale too high, or the unused wide `glow` sprite was added to the scene | `nucleus` at `r*4.2`, opacity easing to ~1.0–1.32. The `glow` sprite is **created but never added to the group** — it exists only as a color handle for restyle. |

### Scene budget — assert these numbers

```
record particles      ~650 total   (9 clusters × 6–17 records per chamber; 4 clusters for the Micromound)
conduit grains         652 total   (7 structural × 60) + (8 lateral × 24) + (1 authority × 40)
ant orb sprites         59 total   (queen 8, intelligence 13, forge 7, validation 14, memory 4, output 3, micromound 10)
chamber halo sprites      7
other sprites            33        (16 conduits × 2 travellers, 1 seal, 1 approval ring — travellers hidden by default)
LineSegments objects     14        (2 per chamber: record spokes + cluster ring)
```

If a chamber's `Points` geometry has more than ~120 vertices, the data wiring is wrong. Log the counts on boot and check them before looking at anything else.

---

## Files

```
SPEC.md                             Full implementation spec, §0–13. The UI shell is §11; the renderer math is §3–9.
reference/colony-topology.js        PORT AS-IS. Data + deterministic generation. No three.js import.
reference/colony-renderer.js        PORT AS-IS. The only file that touches three.js.
reference/three-loader.js           Loads three.js under a deadline; resolves null on failure, never rejects.
reference/Colony Live 3D.dc.html    Prototype shell — read for panel structure, copy and exact styling. Recreate, don't copy.
reference/support.js                Prototype runtime only. Not part of the design. Do not port.
```

Read order for an implementer: this README → `SPEC.md §0` and `§13` → `colony-topology.js` in full → `colony-renderer.js` in full → the HTML for the shell.

---

## Module boundary

`colony-renderer.js` exports:

```js
createStage(mount, hooks) -> stage | null      // null if WebGLRenderer construction throws
createFallback(mount)     -> stage-shaped 2D canvas stub, no motion
```

The stage's entire public surface — the only thing the UI shell may touch:

```js
stage.state                                   // live {level, focus, cluster, record, ant, follow, motion, labels, pheromones}
stage.select(id)                              // focus a chamber, fly to r*3.4
stage.reset() / stage.up() / stage.enter()    // level navigation
stage.setMission(m)                           // {lit, trails, approval, activeSeg, missionState}
stage.setChamberStyle(id, {label?, color?})   // one color repaints the whole chamber palette
stage.setAntStyle(antId, {name?, color?})     // returns a refreshed inspector payload
stage.clearAnt() / stage.setFollow(b) / stage.setPref(k, v)
stage.contextFor(id) -> {clusters}
stage.resize() / stage.resetLayout() / stage.dispose()
```

Hooks the stage calls back into the shell: `onLevel(level, focusId)`, `onSector(id)`, `onCluster(cluster)`, `onRecord(record)`, `onAnt(payload|null)`, `onHover(hit|null)`, `onStall(ms)`.

The renderer never invents activity. If `setMission` lights nothing, nothing pulses — the streams still drift. All narrative state comes from the shell's 120 ms mission clock.

---

## Making it clickable (the part that keeps getting dropped)

No raycaster. Project world positions to screen, take the nearest hit within a pixel radius, in this order:

1. **Crew orbs** — if a chamber is focused, radius **32 px**.
2. **Records** — if focused and `level >= 2`, radius **14 px**, filtered to the active cluster at `level >= 3`.
3. **Cluster centres** — only at `level === 2`, radius **26 px**.
4. **Chambers** — always. Project the centre and the point `centre + (r,0,0)`; the screen radius is `max(12, |Δx|)`. Keep the nearest in `z`.

Records and clusters must be projected through `livePos()` — the helper that blends a node's organic seat and its ordered-strata seat by the current `uOrg` — or hits will land where the particles *used* to be during the focus transition.

Click resolution happens on `pointerup` only when the pointer moved less than **6 px** (so orbiting never selects). Then: sector → `select(id)`; ant → open the ant inspector; record → `level 4` + `onRecord`; cluster → `level 3` + `onCluster`. **A click on empty space clears the entire selection and returns to L0.** Cursor feedback: `grab` idle, `grabbing` dragging, `pointer` over any hit, `move` over an alt-hovered chamber.

---

## Screens / views

There is one screen. It has two chrome shells and five zoom levels.

**Content pane.** The canvas plus a sibling `pointer-events:none` overlay div holding every label. Insets `left 240px / top 50px` in console chrome, `0/0` in minimal.

**Levels.** `L0 COLONY SURVEY`, `L1 SECTOR APPROACH`, `L2 INSIDE SPHERE`, `L3 CONTEXT CLUSTER`, `L4 RECORD`. While a chamber is focused the level is *derived from camera distance*: `< r*2.35 → 2`, `< r*4.8 → 1`, else 0 (which also clears focus). See `SPEC.md §6`.

**Chrome A — minimal (default).** 44 px bar. Ant glyph + `ANTHILL` at `600 12px/1` `.28em` `#f4e9d6`; centred `COLONY LIVE` / 1px divider / mission chip (`ACTIVE · 4/7`) in `#ff3fa4` at `500 10.5px` `.14em`; right, a 999px pill `N NEEDS YOU` at `600 10px` `.14em`, color `#ff3fa4`, background `rgba(255,63,164,.07)`, border `1px rgba(255,63,164,.45)`, padding `6px 12px`, hover background `rgba(255,63,164,.16)`. Bar background `linear-gradient(180deg, rgba(4,6,11,.92), rgba(4,6,11,.55))`, bottom border `1px rgba(38,46,68,.7)`, `backdrop-filter: blur(6px)`.

**Chrome B — console.** 240 px sidebar, `#0d1320`, right border `1px #262e44`. Header: 24px glyph + `ANTHILL` at `800 13px` `.14em` `#c9cfdc` + `v0.3.8.109` at `400 10px` `#667089`, padding `12px 14px`, bottom border. Nav group labels at `700 9px` `1px` uppercase `#667089`, padding `10px 14px 3px`. Rows: `7px 10px` padding, `1px 6px` margin, radius 6, `12px/500` text, `#8b93a8`, 15px stroke icons; the active row (Colony) is `rgba(255,63,164,.08)` / `#ff3fa4` with a 2px magenta indicator inset 25% at the left edge. Missions carries a `#ef4444` badge with white `700 9px/1.5` text, radius 10, min-width 16. Footer chip: `#161d31` on `1px #262e44`, radius 6, a 24px round avatar `linear-gradient(135deg,#ff3fa4,#e21f7b)` with `800 9px #080808` initials, `operator` at `600 11px #c9cfdc` over `admin` at `400 9px #667089`. Top bar 50 px, `#0d1320`, bottom border `1px #262e44`: breadcrumb `Colony / Topology` (`#8b93a8`, separator `#667089`, leaf `#c9cfdc` 600), centred 7px `#ff3fa4` dot with `box-shadow: 0 0 8px #ff3fa4` plus the mission title at `400 11px` mono ellipsized at 440px, and a right chip `#101625` / `1px #262e44` / radius 6 / `5px 9px` holding `AUTO` (`600 10px #c9cfdc`), `llama3.1:8b` (`400 10px #667089`) and a 6px `#10b981` dot.

**Floating overlays.** Shared material: background `rgba(9,13,24,.9)` (popovers `rgba(13,19,32,.95)`, inspectors `rgba(10,14,26,.86–.88)`), border `1px rgba(38,46,68,.9–.95)`, radius 8, `backdrop-filter: blur(10–12px)`. Inset 18 px from the pane edges; inspectors at `top: 56px`, width 300–322 px. Every panel that can occlude a 3D label carries `data-chrome-avoid="1"` — the renderer reads those rects (at most every 400 ms) and re-places chamber labels around them.

- **Breadcrumb + level** (top-left, `top:56px`, only above L0): `COLONY` button / `/` / sector / optional cluster, a divider, the `L2 INSIDE SPHERE` chip at `500 9px` `.14em` `#667089`, and a `BACK` button on `rgba(255,255,255,.04)`.
- **Mission strip** (bottom-left, 404 px, `data-chrome-avoid`): `ACTIVE MISSION` at `600 9px` `.2em` `#8b93a8` with the id right-aligned; title at `400 12px/1.45 #f4e9d6`, `text-wrap: pretty`; a `gap:4px` row of 7 clickable 3px segments, radius 2 — past `rgba(255,63,164,.45)`, current `#ff3fa4`, future `rgba(38,46,68,.9)` — each jumping the mission clock; `STEP n` at `600 9.5px` `.12em` beside the step title at `500 11px #c9cfdc`; the note at `400 10.5px/1.55 #8b93a8`. When approval is pending, an inset appears: border `1px rgba(255,63,164,.5)`, background `rgba(255,63,164,.07)`, radius 6, holding `APPROVAL BOUNDARY`, the text `repo.patch.apply · destructive · high`, and a solid `#ff3fa4` `APPROVE` button with `#04060b` label.
- **Controls** (bottom-right, `data-chrome-avoid`): `SURVEY · MISSION · MEMORY · MOUNDS · FOLLOW`, a 1px divider, `VIEW ⌄`. Buttons `500 9.5px` `.12em` `#c9cfdc` on `rgba(255,255,255,.05)`, border `1px rgba(38,46,68,.9)`, radius 5, padding `7px 10px`; hover turns border and text `#ff3fa4`. FOLLOW's label goes `#ff3fa4` while active.
- **View options popover** (`right:18px; bottom:74px`, 250 px): `VIEW OPTIONS` kicker, then rows for Chrome (`MINIMAL`/`CONSOLE`, magenta button), Motion (`NORMAL`→`CALM`→`OFF`), Labels (`ALL`/`OFF`), Pheromone trails (`ALL`/`OFF`), and a `#667089` footnote: `Drag to orbit · shift-drag to pan · wheel to zoom into a sphere. Sector positions never move.`
- **Record inspector** (322 px, `max-height: calc(100vh - 160px)`, scrolls): type kicker in the type color, `✕` close, title at `400 13px/1.4 #f4e9d6`, then label/value rows (`400 10px` sans `#667089` vs `400 10px` mono, right-aligned): Record type, Sector, Cluster, Mission, Source ant, Recorded, Verification (`#10b981` verified / `#ef4444` refused / `#f59e0b` unverified), Connected records. Then a pheromone meter: label + 2-dp value in `#f59e0b`, over a 3px track `rgba(38,46,68,.9)` with an `#f59e0b` fill. Then a collapsible `▸ TECHNICAL DETAILS` revealing `record_id, task_type, side_effect_class, risk_class, required_capabilities, idempotency_key` at `400 9.5px` mono `#4f5a72`/`#8b93a8` with `word-break: break-all`.
- **Chamber inspector** (300 px) — the only place chamber identity is editable: kicker `COLONY CHAMBER` or `CHILD COLONY` in the chamber color with an 8px dot (`box-shadow: 0 0 9px`), a `NAME` text input (`rgba(5,7,12,.75)`, border `1px rgba(38,46,68,.95)`, radius 5, focus border `rgba(201,207,220,.5)`), a `COLOUR` row of 22px swatch circles, then read-only Chamber id / Records / Clusters / Registry roles / Workers.
- **Ant inspector** (300 px): kicker `RESIDENT ANT` with a dot in the ant's own color, an editable role-name input, `ant_id · sector` at `400 10px` mono `#667089`, a row of 19px swatches, then Rank (`#ffd166` when `colony authority`), Records authored, Clusters served, Verified (`#10b981`), Refused (`#ef4444` when > 0), Avg pheromone (`#f59e0b`), Home cluster, Status (`#10b981` when `laying pheromone`), Current run, Progress, Deposits this session, Protocol. Footer: `LATEST RECORD` kicker, the title at `400 11px/1.45 #c9cfdc`, the timestamp at `400 9.5px` mono.
- **Micromound panel** (322 px, border `rgba(255,63,164,.35)`): `MICROMOUND · M1 READ-ONLY`, name + `mound_id · Mound Major`, then Status / Enrollment / Hardware / Last sync / Last seq / Capabilities / Chain health / Global stop. Then `EVIDENCE BEATS` — rows of `#seq` (44px) · state (64px, `#10b981` accepted / `#ef4444` refused) · time (right). Then a full-width `STOP THIS MOUND` / `RESUME MOUND` on `rgba(239,68,68,.08)` with a `rgba(239,68,68,.5)` border. Then the standing note at `400 9.5px/1.5 #4f5a72`: "Stop always wins. Disconnection grants no additional authority. No local mission, charter, or lease is shown at M1 because none is reported."
- **Boot notice** (top-centre, only on failure): `rgba(13,19,32,.92)`, border `1px rgba(245,158,11,.5)`, `#f59e0b` mono 10.5px. Three distinct messages for assets-missing, WebGL-unavailable and render-stalled — see `SPEC.md §1`.

**Panel routing.** Exactly one panel is open at a time, in priority order: record inspector → micromound panel (child colonies) → ant inspector → chamber inspector.

**Swatch set** (both inspectors): `#e21f7b #ff3fa4 #35aadf #22d3ee #fb923c #f59e0b #ef4444 #8b5cf6 #10b981 #94a3b8`. Circles, `box-shadow: 0 0 9–10px <color>`, 2px border — `#f4e9d6` when selected, `transparent` otherwise.

---

## Interactions & behavior

**Pointer.** Drag orbits (`theta -= dx*0.005`, `phi -= dy*0.004`). Shift-drag or middle-drag pans (`k = cam.dist*0.0016`). Alt- or meta-drag on a chamber repositions it in camera space and re-solves every conduit that touches it; hold shift during that drag to push it in depth. Wheel zooms (`dist *= exp(deltaY*0.0014)`, floored at `focus ? r*1.45 : 18`, capped at 130). `L` restores the stored home layout. Click semantics as in "Making it clickable" above.

**Camera easing.** Never snap. Per frame, after a `sane()` gate that resets any non-finite value to HOME and clamps phi/dist/target: theta at 0.08 (through a shortest-path ±π wrap), phi 0.08, dist 0.075, target `lerp 0.075`. `HOME = {target: centroid + (0,2.6,0), dist: 96, theta: 0.42, phi: 1.36}`; `resize()` recomputes `HOME.dist = 62 * max(1, 1.75/aspect)`.

**Focus transition.** `select(id)` flies to `r*3.4` and drives that chamber's `uOrg` uniform 0→1 at 0.07/frame; the point shader `mix()`es every record from its organic seat to a horizontal cluster stratum (one stratum per cluster, golden-angle spiral within it), and the link geometry is rewritten each step so spokes follow. Leaving lets it relax back. Simultaneously: `uAlpha` of other chambers eases to 0.42, `uRec` to 1.7 (records grow), `uScale` to 0.8, crew labels fade in, and cluster labels appear at `level >= 2`.

**Chamber emphasis** (all eased, never set): `glow.opacity → focused ? .34 : lit ? .60 : .44` at 0.05; `nucleus.opacity → (queen ? 1.1 : 1) + (lit ? .22 : 0) + .06*sin(ms*.0012 + r)` at 0.06.

**Ant behavior.** Each orb bobs on three axes at chamber-relative amplitudes and runs a work timer: on expiry it picks a cluster (72% its home cluster, else random), sets `beamDur = 1.3 + rnd()*1.4` and `work = 4 + rnd()*7`. `status` reads `laying pheromone` while beaming, `holding station` otherwise; `dwell` reads `"NN% of run"` or `"N.Ns to next run"`. While an ant inspector is open the payload is re-emitted at most every 0.45 s so the panel stays live. Orb pixel size `(detail ? 54 : 30) * (queen ? 1.55 : lead ? 1 : 0.62) * (selected ? 1.35 : 1)`; opacity from `near = clamp((78 - cam.dist)/46, 0, 1)`.

**Conduit motion.** Grains drift continuously whether or not a mission is running — a crossing takes 9–14 s. Drift is applied every third frame with `dt*3`. A lit conduit adds a travelling Gaussian wave (`wave = uActive * exp(-d²*uSharp)`) that brightens and *enlarges* grains **in their own gradient color** — it must never recolor them to magenta. Completed segments accumulate a persistent pheromone trail that raises the conduit's resting brightness by `trail * 0.22`.

**Approval halt.** When the mission clock passes 55% of a step flagged `approval: true`, the shell raises the approval flag; the wave clamps to `halt = 0.62` and a magenta ring sprite pulses at that point on the curve (`opacity 0.55 + 0.25*sin(ms*0.004)`, `scale 3.4 + sin(ms*0.004)*0.5`). Nothing advances until `APPROVE`.

**Recolor propagation.** `setChamberStyle(id, {color})` derives `core = shell.lerp(white, .42)` and `nucleus = shell * .6`, then updates, in order: the nucleus sprite, the glow handle, the DOM label color, **every record's `acolor` attribute** recomputed with the build-time formula, the spoke and cluster-ring line materials, every non-Queen crew orb (leads take `core`, workers `shell`), and the `uFrom`/`uTo`/`uDest` uniforms of every conduit touching the chamber (each tinted 0.3 toward `#dfe8f5`). The Queen's gold orb is deliberately exempt.

**Motion preference.** `NORMAL` / `CALM` (clock ×0.5) / `OFF` (clock 0 — all drift, bobbing and pulsing freeze, but every value on screen stays readable and truthful; a halted wave parks at `halt ?? 0.5`). Boot with `OFF` when `prefers-reduced-motion` matches.

**Follow mode.** While a segment is active, `want.target.lerp(curve.getPoint(progress), 0.08)` and `want.dist += (16 - want.dist)*0.02`.

**Frame pacing and degradation.** Throttle to ~30 fps (skip if `ms - lastDraw < (slack ? 90 : 31)`). Time only the `renderer.render` call. `rms > 1000` trips the circuit breaker: stop the loop, hold the last frame, call `onStall(ms)`. `rms > 500 → quality 2`; `> 120 → quality 1`; `avg > 45` on every 20th frame steps down one level. Quality is monotonic, capped at 2, drops `pixelRatio` to 1, and sets draw ranges (conduits keep 50% then 20% of grains; chambers keep 40% of records at level 2). `rms > 60` sets a one-frame 90 ms slack.

---

## State management

**Renderer-owned** (`stage.state`): `level, focus, cluster, record, ant, follow, motion, labels, pheromones, reduced`. The shell reads these back after `up()` but must never write them directly except through the documented methods.

**Shell-owned**: `ready, noWebgl, bootFail, stallMs, level, focus, cluster, record, ant, chamberEdits{}, chamberPanel, stepIndex, approvalPending, missionState, optionsOpen, techOpen, chrome, motion, labels, pher, follow, moundOpen, attention`. Plus non-state instance fields for the mission clock: `_elapsed`, `_trails{}`, `_approvedAt`, `_tick`.

**Mission clock** — a single 120 ms interval, the only thing that moves narrative state. `elapsed += 0.12 * missionSpeed` unless approval is pending or motion is off. Walk step durations to find the current step and local progress, then call `setMission({lit, trails, approval, activeSeg, missionState})`. Always light `step.sector` and `queen`; keep `q-mm` on a slow reverse heartbeat (`progress = (elapsed % 4)/4`) unless the mound is stopped; add `trails[segment] += 0.02` (clamp 1) per completed step; reset 6 s past the end.

**Chamber and ant edits** are pushed straight into the stage; the shell keeps them in state only so the inputs stay controlled. On stage (re)creation, replay `chamberEdits` via `applyChambers()`.

**Data fetching.** None in the prototype — `colony-topology.js` generates deterministically at module load. In production, replace `SECTORS[].clusters/leads/workers` and `buildContext()` with live sources, preserving the record shape (`{id, title, type, cluster, sector, ant, mission, ts, verification, pheromone, tint, depth, pos, links}`) and the depth rule `depth = 1 - min(.94, (verified ? .55 : .1) + pheromone*.45)`. Record shapes follow the repo's `TaskContract` (`docs/CONTRACTS.md`), `ToolResult` status/`FailureClass`, and `MoundRecord`/`FleetItem` (`Anthill.Modules.Micromound`).

---

## Design tokens

```
bg      #05070c     ink     #0a0e1a     panel  #0d1320     card   #101625
border  #262e44     text    #c9cfdc     muted  #8b93a8     dim    #667089
cream   #f4e9d6     queen   #ff3fa4     queenDeep #e21f7b
gold    #f5b23c     goldHot #ffd98a     cyan   #35aadf     cyanHot #57c7f0
orange  #fb923c     rose    #ef4444     amber  #f59e0b     purple #8b5cf6
root    #232a3a     rootLit #ff3fa4     success #10b981    clear  0x04060b
```

Record type colors: `memory/pheromone #f59e0b`, `evidence #ef4444`, `artifact #fb923c`, `context #35aadf`, `plan/decision #ff3fa4`, `result #8b5cf6`, `failure #8b93a8`, `task #c9cfdc`.

Type tint multipliers (luminance): `memory 1.0, evidence .95, plan .9, decision .9, pheromone .85, artifact .8, result .75, task .7, context .62, failure .35`.

Typography: `IBM Plex Mono` 400/500/600 for all ids, numerals, kickers and chips; `Segoe UI`/system sans for prose. Kickers `600 9px` `.2em`; field labels `400 10px` sans; field values `400 10px` mono; titles `400 12–13px/1.4` `#f4e9d6` with `text-wrap: pretty`. 3D labels: sector `600 11px/1.35` `.16em` `white-space: pre` with `text-shadow: 0 0 18px rgba(0,0,0,.9)`; cluster `500 9.5px` `.1em` `rgba(201,207,220,.82)`; record `400 9.5px` `rgba(244,233,214,.9)` with a `1px rgba(255,63,164,.45)` left rule; crew `500 9px` `.08em` in the ant's color.

Radii: 4 (small buttons), 5 (buttons, inputs), 6 (sidebar rows, chips, insets), 8 (panels), 999 (pills). Panel padding `12–13px 14px`. Overlay inset 18 px. Field row gap 7 px.

Motion: opacity transitions `.25–.35s` on DOM labels; all 3D easing is per-frame lerp at the factors given above (0.05–0.15), never CSS.

## Assets

None external. Every texture is generated on a `<canvas>` at boot — `dotTex`, `grainTex`, `glowTex`, `haloTex` (a 25-stop `pow(1-t, 2.2) * 0.5` falloff), plus two procedural glyphs, `antTex()` (three cream ellipses + four round-cap legs) and `lockTex()` (a magenta ring, body rect and shackle arc). All UI icons are inline 15px stroke SVGs. Fonts come from Google Fonts (`IBM Plex Mono` 400/500/600). If the target codebase has a brand or icon system, use it for the chrome icons; keep the generated textures as-is.
