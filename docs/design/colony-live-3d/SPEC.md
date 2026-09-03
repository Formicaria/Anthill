# Implementation spec — Colony Live 3D

Build a three.js visualization called **Colony Live** for an agent-orchestration console ("Anthill"). A colony of seven chambers is drawn entirely from particles, connected by particle-stream tunnels, populated by clickable glowing ant orbs, and drillable down to a single record. This document is an implementation spec, not a brief: the constants, shaders and formulas below are the design. Reproduce them literally. Where a number appears, use that number.

---

## 0. What goes wrong if you improvise

A previous attempt failed in five specific ways. Guard against each:

1. **Chambers came out as loose blobs.** They are not `random point in sphere`. Records are placed by the deterministic cluster lattice in §3, and their radius from centre encodes durability. Point size and alpha are per-point attributes derived from pheromone and edge falloff, so the cloud has a dense lit centre and a thinning shell with no rim.
2. **Positions drifted.** The seven positions in §2 are permanent. Queen at the origin, four galleries on the equator at (±16.5, 0, ±16.5), Output at (0, 17, 0), Micromound at (0, −17, 0). Nothing re-solves at runtime.
3. **Tunnels were drawn as lines or dashed lines.** They are `THREE.Points` clouds of 24–60 grains travelling in *path space* along a Catmull-Rom curve with a rotation-minimising frame (§4). No `Line`, no `TubeGeometry`, no dashes.
4. **No ant orbs.** Every chamber has its full roster present as sprite orbs on two concentric rings, sized in *screen pixels* (not world units), each individually pickable (§5).
5. **No core light and no ordered-strata transition.** Each chamber has a single halo sprite scaled to `r * 4.2` in a deepened chamber hue, and clicking a chamber cross-fades the whole cloud into cluster strata via a shader `mix()` between two position attributes (§3.4).

The look is: near-black volume, seven saturated point clouds each sitting in its own soft coloured light, thin bright dotted streams running between them, cream monospace labels floating outside each cloud. Nothing else in the scene. No backdrop, no starfield, no grid, no wireframe spheres, no fog.

---

## 1. Modules, contract, boot

```
colony-topology.js   pure data + deterministic generation. No three.js import.
colony-renderer.js   the only file that touches three.js. Imports topology.
three-loader.js      loads three.js; resolves null on failure, never rejects.
<view component>      chrome, panels, mission clock. Owns UI state.
```

`colony-renderer.js` exports:

```js
export function createStage(mount, hooks) -> stage | null   // null if WebGL construction throws
export function createFallback(mount) -> stage-shaped 2D canvas stub
```

`stage` surface:

```js
{
  state,                    // live: {level, focus, cluster, record, ant, follow, motion, labels, pheromones}
  setMission(m), select(id), reset(), up(), enter(), resize(), resetLayout(),
  setPref(key, value), setFollow(bool), clearAnt(),
  setChamberStyle(id, {label?, color?}),
  setAntStyle(antId, {name?, color?}) -> refreshed ant payload,
  contextFor(id) -> {clusters}, dispose()
}
```

`hooks`: `onLevel(level, focusId)`, `onSector(id)`, `onCluster(cluster)`, `onRecord(record)`, `onAnt(payload|null)`, `onHover(hit|null)`, `onStall(ms)`.

Boot sequence in the view component: import topology + loader under an 8 s deadline (`Promise.race` with a rejecting timer), call `loader.loadThree()`, then import the renderer under its own 8 s deadline. If `window.THREE` is absent, mount `createFallback` and report the reason. Distinguish three failure truths: **assets missing** vs **WebGL unavailable** vs **render stalled**. Wrap the whole chain in a catch that still mounts `createFallback` so the stage is never blank. Log every phase as `[colony-live] boot:<phase>`.

Renderer setup:

```js
new th.WebGLRenderer({antialias:true, alpha:false, powerPreference:'high-performance'})  // in try/catch → return null
new th.PerspectiveCamera(42, 1, 0.5, 400)
renderer.setPixelRatio(Math.min(2, devicePixelRatio||1))
renderer.setClearColor(0x04060b, 1)
canvas.style = 'position:absolute;inset:0;width:100%;height:100%;display:block;'
```

Append a second absolutely-positioned `div` over the canvas with `pointer-events:none; overflow:hidden` — all labels are DOM, not sprites.

---

## 2. Topology data (`colony-topology.js`)

Deterministic throughout. Two PRNGs: `mulberry32(seed)` for content generation, and a small LCG `s = (s*1664525 + 1013904223) & 0x7fffffff` for renderer-side jitter. Seed strings via FNV-1a: `h = 2166136261; h ^= char; h = Math.imul(h, 16777619)`.

### 2.1 TOKENS

```
bg #05070c  ink #0a0e1a  panel #0d1320  card #101625  border #262e44
text #c9cfdc  muted #8b93a8  dim #667089  cream #f4e9d6
queen #ff3fa4  queenDeep #e21f7b  gold #f5b23c  goldHot #ffd98a
cyan #35aadf  cyanHot #57c7f0  orange #fb923c  rose #ef4444
amber #f59e0b  purple #8b5cf6  root #232a3a  rootLit #ff3fa4
```

Green (success) `#10b981`. Renderer clear `0x04060b`. No color outside this set.

### 2.2 SECTORS (order matters — array index is used)

| id | label | pos | r | mass | shell | core | nucleus | points | labelSide |
|---|---|---|---|---|---|---|---|---|---|
| `queen` | `QUEEN'S CORE` | `[0,0,0]` | 7.7 | 1.55 | `#e21f7b` | `#f5b23c` | `#c2247e` | 2600 | left |
| `intelligence` | `INTELLIGENCE` | `[-16.5,0,16.5]` | 5.0 | 1 | `#35aadf` | `#57c7f0` | `#0d7f9c` | 1500 | right |
| `forge` | `FORGE` | `[16.5,0,16.5]` | 5.3 | 1.06 | `#fb923c` | `#ffb26b` | `#c05f18` | 1600 | right |
| `validation` | `VALIDATION` | `[16.5,0,-16.5]` | 5.0 | 1 | `#ef4444` | `#ff7a7a` | `#b02f3e` | 1450 | right |
| `memory` | `MEMORY` | `[-16.5,0,-16.5]` | 5.5 | 1.1 | `#f59e0b` | `#ffcf6b` | `#b8811c` | 1750 | right |
| `output` | `OUTPUT` | `[0,17,0]` | 4.7 | 0.95 | `#8b5cf6` | `#b79bff` | `#6247c0` | 1250 | left |
| `micromound` | `INFRASTRUCTURE\nMICROMOUND` | `[0,-17,0]` | 3.1 | 0.6 | `#ff3fa4` | `#ff3fa4` | `#bc2872` | 240 | left |

`micromound` also carries `child: true`.

Each sector carries `leads[]`, `workers[]`, `clusters[]`:

- **queen** — leads `Queen, Director, PlannerAnt, ConstraintAnt`; workers `MissionPlanner, DependencyMapper, ScopeGuard, ToolGuard`; clusters `Mission intake, Authority state, Recorded plans, Constraint rulings, Directives, Objective ledger, Approval boundaries, Delegation grants, Mission roots`.
- **intelligence** — leads `ResearcherAnt, FileAnt, WebAnt, UICartographerAnt`; workers `RepoResearcher, MissionResearcher, RuntimeResearcher, FileScout, FileReader, SourceFinder, SourceVerifier, RouteMapper, ComponentMapper`; clusters `Source scans, Web findings, Repo reads, Prior research, Open questions, Contradictions, Citations, Discarded leads, Handoff briefs`.
- **forge** — leads `CoderAnt, ScribeAnt`; workers `BackendCoder, UICoder, DocsCoder, ChangelogScribe, OperatorScribe`; clusters `Proposed patches, Build artifacts, Workspace reads, Test scaffolds, Rejected diffs, Dependency notes, Idempotency keys, Compensations, Handoff briefs`.
- **validation** — leads `VerifierAnt, TesterAnt, SoldierAnt, MedicAnt`; workers `ResultVerifier, SafetyVerifier, DotnetTester, FrontendTester, ActionProposer, ExternalActionProposer, RuntimeSentinel, PatchSentinel, FailureDiagnoser, FixRouter`; clusters `Verification runs, Failure taxonomy, Evidence bundles, Risk rulings, Approval requests, Regression guards, Refusals, Signed outcomes, Handoff briefs`.
- **memory** — leads `ArchivistAnt, ChangeArchivistAnt`; workers `MemoryArchivist, RuleArchivist`; clusters `Durable memories, Pheromone trails, Verified outcomes, Mission histories, Evidence archive, Decayed signals, Reinforced routes, Failure lessons, Retrievals`.
- **output** — leads `BuilderAnt`; workers `ResponseBuilder, ResultCompiler`; clusters `Mission results, Operator briefs, Change summaries, Open decisions, Delivered reports, Rendered diffs, Read receipts, Follow-ups, Retrievals`.
- **micromound** — leads `QuartermasterAnt, InventoryAnt, ProxmoxAnt, StorageAnt, BackupAnt, NetworkScoutAnt, HealthAnt, SecurityScoutAnt`; workers `ResourceMonitor, ConcurrencyAdvisor`; clusters `Evidence beats, Capabilities, Hardware profile, Sync chain`.

Export `SECTOR_BY_ID` as an id→sector map. Sector objects are **mutable** at runtime (`pos`, `label`, `shell`, `core`, `nucleus` are rewritten by drag and by restyle), so read through the map, never from a copy.

### 2.3 ROOTS (16 conduits)

`{id, from, to, kind, bow:[x,y,z]}`; `bow` is a hand-tuned control-point offset so no two tunnels overlay.

```
structural: q-i  queen→intelligence   bow [ 1.6,  0.6, -3.4]
            q-f  queen→forge          bow [ 0.4,  2.2,  3.2]
            q-m  queen→memory         bow [ 1.2, -1.4,  4.0]
            q-o  queen→output         bow [-1.0, -2.4, -2.6]
            i-f  intelligence→forge   bow [ 0.2,  2.8, -1.8]
            f-v  forge→validation     bow [ 2.6,  0.4,  2.2]
            v-m  validation→memory    bow [ 2.4, -1.0, -2.0]
authority:  q-mm queen→micromound     bow [ 0.6, -1.2,  3.0]
lateral:    q-v  queen→validation     bow [ 0.5,  4.2, -1.6]
            i-v  intelligence→validation bow [2.0, 2.6, 2.4]
            i-m  intelligence→memory   bow [ 3.2,  1.0, -2.8]
            i-o  intelligence→output   bow [-2.8,  1.4,  2.0]
            f-m  forge→memory          bow [ 2.8, -0.6,  2.6]
            f-o  forge→output          bow [-1.2,  3.4, -2.2]
            v-o  validation→output     bow [ 0.8, -3.6, -2.4]
            m-o  memory→output         bow [-0.6, -3.8,  2.2]
```

`CIRCUIT = ['q-i','i-f','f-v','v-m','q-o']` — mission dispatch order.

### 2.4 Type tints and per-sector type pools

```
TYPE_TINT = {context .62, plan .9, task .7, artifact .8, evidence .95,
             memory 1.0, decision .9, result .75, failure .35, pheromone .85}
TYPES_FOR = {queen:[plan,decision,task,context], intelligence:[context,evidence,memory,failure],
             forge:[artifact,task,context,failure], validation:[evidence,decision,artifact,failure],
             memory:[memory,pheromone,evidence,decision], output:[result,context,decision],
             micromound:[evidence,context]}
VERIF = ['verified','verified','unverified','verified','refused']   // sampled uniformly
```

---

## 3. `buildContext(sector)` — the placement math (this is what makes it read as a colony)

Seed: `mulberry32(fnv1a(sector.id))`. For each cluster `i` of `n`:

```js
const golden = Math.PI * (3 - Math.sqrt(5));          // 2.399963
const y   = 1 - (i / Math.max(1, n-1)) * 1.55;        // 1 → -0.55, so clusters lean upper-hemisphere
const rad = Math.sqrt(Math.max(0.05, 1 - y*y));
const th  = golden * i;
const shellFrac = 0.42 + rnd() * 0.36;                // 0.42–0.78 of the radius
center = [cos(th)*rad*r*shellFrac, y*r*shellFrac*0.85, sin(th)*rad*r*shellFrac];
```

Then `count = 6 + floor(rnd()*12)` records per cluster. For each record `k`:

```js
type = types[floor(rnd()*types.length)];
pher = round(((type==='memory' ? .62 : .18)*100 + rnd()*38)) / 100;   // clamp to .99
verification = type==='failure' ? 'refused' : VERIF[floor(rnd()*5)];
durable = (verification==='verified' ? .55 : .1) + pher*.45;
depth   = 1 - Math.min(.94, durable);          // small depth = deep/durable
dir     = normalize([rnd()*2-1, rnd()*2-1, rnd()*2-1]);
spread  = r * 0.16;
pos     = center*(0.5 + depth*0.75) + dir*spread;
id      = sectorId.slice(0,3) + '_' + clusterLabel.toLowerCase().replace(/[^a-z]/g,'').slice(0,4) + '_' + k;
links   = 1 + floor(rnd()*5);
ts      = '2026-09-01 HH:MM'  with H = 8+floor(rnd()*4), M = floor(rnd()*60)
ant     = leads.concat(workers)[floor(rnd()*rosterLen)]
mission = rnd() > .55 ? MISSION.id : 'msn_' + (0x1000+floor(rnd()*0xefff)).toString(16)
tint    = TYPE_TINT[type]
```

Record titles come from a per-type table of 2–4 plausible strings, e.g. `evidence: ['verification run 214/214 passed', 'evidence bundle ev_9c2 signed', 'beat 4179 refused — chain digest', 'guard: UiIntegrity holds']`; `memory: ['Nightly verify fails under NVMe contention', 'Retry window 15m holds across 6 runs', 'Proxmox 8.2 backup lock behaviour']`. Never `Lorem`, never `Record 4`.

`CONTEXT = SECTORS.reduce(...)` — built once at module load, shared by renderer and UI.

Note the consequence of `pos = center*(0.5 + depth*0.75) + dir*spread`: durable records land nearer the origin, weak/refused ones sit further out. Depth *is* the encoding. Do not add extra scatter.

### 3.1 Chamber geometry construction

One `THREE.Points` per chamber, `total = number of records` (typically 60–110; queen most). **One particle per persisted record — nothing else.** Five attributes:

```
position (3)  aOrg (3)  acolor (3)  size (1)  alpha (1)
```

Per record:

```js
rad  = min(1, hypot(x,y,z) / r);          // normalised distance from chamber centre
depth = 1 - rad;
edge  = 1 - 0.72 * pow(rad, 2.6);         // soft outer falloff, kills the rim
alpha = min(1, 0.82 + pher*0.2) * (0.86 + 0.14*edge);
size  = (1.15 + pher*1.7) * (0.72 + 0.28*edge);
color = Color(shell).lerp(Color(core), min(1, pow(depth,1.5)*1.15))
                    .multiplyScalar(0.5 + tint*0.45);
```

So the centre of each cloud is the bright `core` hue and the shell is the darker `shell` hue, and record type modulates overall luminance. Store `r._i = i` on each record so restyle can find its slot.

### 3.2 Chamber material and shaders

```glsl
// vertex
attribute vec3 acolor; attribute float size; attribute float alpha; attribute vec3 aOrg;
uniform float uScale; uniform float uAlpha; uniform float uRec; uniform float uOrg;
varying vec3 vC; varying float vA;
void main(){
  vC = acolor; vA = alpha * uAlpha;
  vec4 mv = modelViewMatrix * vec4(mix(position, aOrg, uOrg), 1.0);
  gl_PointSize = clamp(size * uRec * uScale * (300.0 / max(1.0, -mv.z)), 2.0, 12.0);
  gl_Position = projectionMatrix * mv;
}
// fragment
uniform sampler2D uMap; varying vec3 vC; varying float vA;
void main(){ if(vA < 0.02) discard; if(texture2D(uMap, gl_PointCoord).a < 0.5) discard;
  gl_FragColor = vec4(vC, clamp(vA, 0.0, 1.0)); }
```

`ShaderMaterial({transparent:true, depthWrite:false, blending: th.AdditiveBlending})`. Uniforms start `uScale:1, uAlpha:1, uRec:1, uOrg:0`.

The **hard alpha cut at 0.5 on the sprite mask** is what makes points read as crisp grains instead of a fog. Do not use `PointsMaterial` with a soft sprite.

### 3.3 Textures (generated on a canvas, no external assets)

`radialTex(stops, size)` = a canvas radial gradient from centre to edge, wrapped in `CanvasTexture`.

```js
dotTex   = radialTex([[0,'#fff'],[0.90,'#fff'],[0.98,'#fff'],[1,'transparent']], 128)  // solid disc, hairline edge
grainTex = radialTex([[0,'#fff'],[0.92,'#fff'],[0.99,'#fff'],[1,'transparent']], 128)  // conduit grain
glowTex  = radialTex([[0,'rgba(255,255,255,1)'],[0.3,'rgba(255,255,255,.92)'],
                      [0.46,'rgba(255,255,255,.34)'],[0.72,'rgba(255,255,255,.06)'],
                      [1,'rgba(255,255,255,0)']], 256)                                  // ant orb halo
haloTex  = 25 stops at t=i/24 with alpha = pow(1-t, 2.2) * 0.5, size 256                // chamber core light
```

`haloTex`'s power falloff is essential: it has no defined rim, so the chamber light dissolves into the volume rather than ending on a visible circle.

Also draw two small canvas glyphs procedurally: `antTex()` — three cream ellipses (head r5 at y15, thorax 6×9 at y26, abdomen 8×11 at y40) plus four 2.4px round-cap legs, on 64px; and `lockTex()` — a magenta 30px ring, a 24×20 body rect and a 10px shackle arc, on 96px.

### 3.4 Core light and the ordered-strata alternate layout

Two sprites per chamber, both `AdditiveBlending`, `depthWrite:false`, `fog:false`:

```js
glow    = Sprite(haloTex, color = shell,   opacity 0.17, scale = r * 2.8)   // not added to the group; opacity is animated but it stays a handle for restyle
nucleus = Sprite(haloTex, color = nucleus, opacity 0.80, scale = r * 4.2)   // added to the group — THE core light
```

The `4.2 × r` diameter puts the gradient's zero exactly on the outermost record of the chamber. Deep and saturated, never white-hot.

**`aOrg` — the ordered formation.** Precompute, for every record, a second seat: one horizontal stratum per cluster, records on an even spiral within it.

```js
const C = clusters.length;
clusters.forEach((cl, ci) => {
  const m = max(1, cl.records.length);
  const y = ((ci + 0.5)/C - 0.5) * r * 1.55;
  const band = sqrt(max(0.12, 1 - pow(y/(r*1.05), 2)));
  cl.org = [0, y, 0];
  cl.records.forEach((rec, k) => {
    const ang = k * 2.399963;                        // golden angle
    const rad = r * 0.86 * band * sqrt((k+0.55)/m);
    rec.org = [cos(ang)*rad, y, sin(ang)*rad];
  });
});
```

Focusing a chamber drives `uOrg` 0→1 (`+= (want-value)*0.07` per frame); the shader `mix()`es each point from its organic seat to its stratum seat. Leaving lets it relax back. Whenever `uOrg` moves by more than 0.0008, also rewrite the link geometry (§3.5) so spokes follow.

### 3.5 Intra-chamber link geometry

Two `LineSegments` per chamber, both additive, `depthWrite:false`, starting at `opacity 0`:

- **spokes**: cluster centre → each of its records, color = `core`.
- **cluster ring**: each cluster centre → the next one (wrapping), color = `shell`.

Per-frame opacity target, where `near = clamp((58 - cam.dist)/30, 0, 1)`:

```
focused && level>=2  → 0.36
another chamber focused → 0.03
otherwise            → 0.06 + near*0.16
```
Cluster-ring opacity is 0.7× the spoke target. Both ease at 0.06/frame.

`reflowLinks(id)` rewrites both position arrays by lerping each endpoint between `pos` and `org` by the current `uOrg`. A shared helper `livePos(so, node, out)` returns the on-screen world position of a record or cluster (`pos → org` blend + the sector group's position) and is the single source of truth for links, labels and hit-testing.

---

## 4. Conduits — particle streams in path space

### 4.1 Curve

`curveFor(root)`: a `CatmullRomCurve3` of four points, `curveType 'catmullrom'`, `tension 0.5`.

```js
A = from.pos, B = to.pos, dir = B-A, len = |dir|
p0 = A + dir*(from.r/len*0.35)          // start just inside the source cloud
p3 = B - dir*(to.r/len*0.35)
axis = p3-p0, span = |axis|, u = normalize(axis)
n1 = u × (|u·(0,1,0)|>0.9 ? (1,0,0) : (0,1,0)), normalized
n2 = u × n1, normalized
rnd = LCG(fnv1a(root.id) + 7)
lean = (rnd()*2-1) * span * 0.035
lift = (rnd()*2-1) * span * 0.025
bow  = Vector3(...root.bow) * 0.18
for i in 1..2:  t = i/3;  env = sin(PI*t)
  p = lerp(p0,p3,t) + n1*(env*lean) + n2*(env*lift) + bow*env
  p.y -= env * span * 0.012                       // sag under its own span
points = [p0, p1, p2, p3]
```

Every stage is guarded: non-finite endpoints, `len < 1e-3`, `span < 0.25`, or a non-finite midpoint all fall back to a straight three-point curve. Never hand three.js a curve whose arc-length search can't converge.

### 4.2 Per-kind spec

```js
conduitSpec(r) => {
  n:      authority 40 | lateral 24 | structural 60,     // grains
  streams: authority 2 | lateral 1 | structural 2,
  rad:    authority .44 | lateral .5 | structural .8,    // corridor radius
  rest:   authority .3  | lateral .14 | structural .32,  // idle brightness
  sharp:  authority 150 | else 120                       // mission wave tightness
}
```

### 4.3 Path sampling with a rotation-minimising frame

`fillConduit(ro)` samples the curve at `N = 64` uniform-parameter steps (no arc-length cache, no binary search) and stores three Float32Arrays: `samp`, `nrm`, `bnm`. Per step: tangent from the forward difference (fall back to the backward difference, then `(0,0,1)`); if `|T·N| > 0.98` re-seed `N`; `B = T×N`, `N = B×T`, both normalized, with a zero-length guard on each. Any non-finite triple resets to `(0,0,0)/(0,1,0)/(0,0,1)`.

Set a fixed generous bounding sphere so per-frame drift never needs bounds recomputation, and keep `frustumCulled = true`:

```js
geo.boundingSphere = new Sphere(lerp(cA,cB,0.5), |cA-cB|*0.6 + spec.rad*6 + 12)
```

### 4.4 Grain state and drift

Each grain owns `t, base, jit, a0, tw, sp, b0` (per-grain radius, angle, twist, speed, brightness), seeded from `LCG(fnv1a(id)+77)`. With `per = ceil(n/streams)`, for grain `i`: `stream = i % streams`, `step = floor(i/streams)`, `primary = stream===0`, `syn = primary && step%9===4` (a periodic brighter "synapse" grain).

```js
t    = clamp((step + 0.15 + rnd()*0.7)/per, 0.002, 0.998)
base = primary ? rad*(0.04 + rnd()*0.26) : rad*(0.5 + stream*0.26 + rnd()*0.26)
a0   = stream*2.2 + rnd()*0.6
tw   = primary ? 1.1 : 3.2
sp   = (0.072 + rnd()*0.038) * (primary ? 1 : 0.9)     // ⇒ a crossing takes ~9–14 s
b0   = (primary ? 0.75+rnd()*0.5 : 0.4+rnd()*0.3) * (syn ? 1.9 : 1)
aS   = (primary ? 1.1+rnd()*0.8 : 0.65+rnd()*0.6) * (syn ? 1.7 : 1)
```

`driftConduit(ro, dt)` advances `t` (wrapping at 1), interpolates the sampled position/normal/binormal at `t*N`, and offsets radially:

```js
env = sin(PI*t)
rr  = base * (0.06 + 0.94 * pow(env, 0.5))    // converge to the axis at both ends
ang = a0 + t*tw
pos = P + N*(cos(ang)*rr) + B*(sin(ang)*rr)
aT  = t
aB  = b0 * (0.32 + 0.68 * pow(env, 0.3))      // dim near the chamber mouths
```

The convergence at both ends is why the streams appear to *enter* the chambers rather than splay across their shells. Drift is called every third frame with `dt*3` (visually identical, a third of the cost).

### 4.5 Conduit shaders

```glsl
// vertex
attribute float aT; attribute float aS; attribute float aB;
uniform float uHead, uActive, uRest, uScale, uSharp;
uniform vec3 uFrom, uTo, uMag, uDest;
varying float vA; varying vec3 vC;
void main(){
  float d = aT - uHead;
  float wave = uActive * exp(-d*d*uSharp);
  vec3 rest = mix(uFrom, uTo, smoothstep(0.05, 0.95, aT));
  vC = mix(rest, min(rest*1.9, vec3(1.0)), clamp(wave*1.3, 0.0, 1.0));
  vA = aB * (uRest + 2.1*wave);
  vec4 mv = modelViewMatrix * vec4(position, 1.0);
  gl_PointSize = clamp(aS * (1.0 + 1.5*wave) * uScale * (300.0/max(1.0,-mv.z)), 2.4, 8.5);
  gl_Position = projectionMatrix * mv;
}
// fragment: same discard pair as the chamber points
```

`uFrom`/`uTo` are the two chambers' shells each lerped 0.3 toward `#dfe8f5`, so a grain reads as leaving one chamber and arriving at the other. **The mission wave brightens and enlarges a grain in its own gradient color — it never recolors to magenta.** (`uMag` and `uDest` exist for state-specific tinting; keep them wired but unused by default.)

### 4.6 Conduit ants, the seal, the approval ring

Every conduit carries two hidden traveller sprites: a `glowTex` halo in `Color(to.shell).lerp(white,0.25).lerp(white,0.4)` plus a `dotTex` core at scale 0.32 in `0xfff4e0`. They become visible only when `mission.lit[rootId].ants > i`, ride `curve.getPoint(t)`, wobble laterally by `cos(ms*0.0007 + i*2.1) * spec.rad * 0.3` along a tangent-perpendicular, and are pixel-sized at 11 px (floored at `spec.rad*0.9`).

A `lockTex` seal sprite sits at `rootObjs['q-mm'].curve.getPoint(0.42)`, scale 2.1, opacity 0.7 (1.0 and `#ef4444` when the mound is stopped, magenta otherwise). A `glowTex` boundary sprite marks the approval point: position `curve.getPoint(mission.approval.at ?? 0.6)`, opacity pulsing `0.55 + 0.25*sin(ms*0.004)`, scale `3.4 + sin(ms*0.004)*0.5`.

---

## 5. Ants — resident crew

Per chamber, build the roster as `leads.map(lead:true)` then `workers.map(lead:false)`; if `roster[0].name === 'Queen'` flag `authority: true`. Attach records by author name (`byAnt[record.ant]`), then derive per-ant truth: home cluster = the cluster where it filed the most records; `verified` / `refused` counts; `pheromone_avg`; `clusters_served` = distinct clusters; `last_record` = highest `ts`.

### 5.1 Ring placement

```js
ringN = max(1, isLead ? (isQueen ? nLead-1 : nLead) : nWork);
ringI = isLead ? (queenPresent ? i-1 : i) : i - nLead;
ang   = (ringI/ringN)*2PI + (isLead ? 0 : PI/ringN);       // workers offset half a step
rr    = r * (isLead ? (child ? .40 : .44) : (child ? .82 : .86));
slot  = isQueen ? (0,0,0)
                : (cos(ang)*rr, (ringI%2 ? 1 : -1) * r * (isLead ? .08 : .14), sin(ang)*rr);
```

Registry roles inside, their workers outside, alternating slightly above/below the equator. The Queen holds the dead centre of her own chamber.

### 5.2 Orb construction

```js
orbColor = isQueen ? TOKENS.gold : isLead ? sector.core : sector.shell;
sp   = Sprite(glowTex, orbColor, opacity 0, additive, fog:false)   // added to the sector group at slot
core = Sprite(dotTex, orbColor.lerp(white, 0.55), opacity 0)       // child of sp, z +0.001, scale 0.34
ring = isQueen ? Sprite(glowTex, TOKENS.queen, opacity 0, scale 2.1, z -0.001) : null
```

### 5.3 Behaviour (`colonyLife(ms, k)`, `k` = 0 when motion is off, 0.5 calm, 1 normal)

Each ant holds station with a small triple-axis bob — `sin(bob*.55)*r*.008`, `sin(bob*.7+1.3)*r*.012`, `cos(bob*.6)*r*.008` — and runs a work timer: when `work` expires it picks a cluster (72% its home, else random), sets `beamDur = 1.3 + rnd()*1.4`, and resets `work = 4 + rnd()*7`. While `beam > 0` its status is `laying pheromone`, otherwise `holding station`.

**Orbs are sized in screen pixels, not world units.** With `TANF = tan(42°/2)` and

```js
pxScale = (dist, px) => 2*TANF*dist/(canvas.clientHeight||700) * px;
```

the target pixel size is `(detail ? 54 : 30) * (isQueen ? 1.55 : isLead ? 1 : 0.62) * (selected ? 1.35 : 1)`, where `detail = focused && level>=1`. Opacity target:

```js
near = clamp((78 - cam.dist)/46, 0, 1);
antA = min(0.9, 0.14 + near*0.8) * (focusElsewhere ? 0.35 : 1) * (disconnectedChild ? 0.25 : 1);
base = antA * (selected ? 1.3 : detail ? 1 : 0.85) * (isQueen ? 1.2 : 1);
```

Ease halo and core at 0.08/frame; the Queen's ring eases toward `min(0.85, base*(0.35+0.35*puls))` with `puls = 0.5+0.5*sin(ms*0.0016)`.

While an ant inspector is open, re-emit `hooks.onAnt(antPayload(ant))` at most every 0.45 s so the panel stays live.

### 5.4 `antPayload(a)`

Static `info` — `ant_id` (`queen_01`, else `ant_<sec3>_<role6>`), `role`, `rank` (`colony authority` / `registry role` / `worker`), `sector`, `sectorLabel`, `home_cluster`, `records`, `clusters_served`, `verified`, `refused`, `pheromone_avg`, `last_record`, `last_ts`, `protocol` (`edge_queen` for the child, else `colony_native`), `color` — merged with live fields: `status`, `current_target`, `tasks_completed`, `deposits`, and `dwell` = `"NN% of run"` while beaming or `"N.Ns to next run"` otherwise.

---

## 6. Camera rig

```js
HOME = { target: colonyCentroid + (0,2.6,0), dist: 96, theta: 0.42, phi: 1.36 }
LIMITS = { phi: [0.06, PI-0.06], dist: [2.2, 130] }
```

Keep two structs, `cam` (current) and `want` (target). Every frame: shortest-path wrap the theta delta into ±π, then `cam.theta += d*0.08`, `phi *0.08`, `dist *0.075`, `target.lerp(want.target, 0.075)`; place the camera spherically and `lookAt(cam.target)`.

A `sane()` gate runs first and is not optional: any non-finite `theta/phi/dist/target` is reset to HOME, phi and dist are clamped to LIMITS, and the target is clamped to ±80/±60/±80. One bad frame otherwise runs away forever.

`resize()` sets `HOME.dist = 62 * max(1, 1.75/aspect)` so the spread stays framed on narrow viewports; when nothing is focused it also snaps `want.dist` to that fit.

### 6.1 Levels

```
L0 COLONY SURVEY · L1 SECTOR APPROACH · L2 INSIDE SPHERE · L3 CONTEXT CLUSTER · L4 RECORD
```

`select(id)` sets focus, clears cluster/record/ant, `level = 1`, `want.target = sector.pos`, `want.dist = r*3.4`, then fires `onLevel(1,id)` and `onSector(id)`.

Wheel: `want.dist *= exp(deltaY*0.0014)`, floored at `focus ? r*1.45 : 18`. While focused the level is *derived from distance*: `dist < r*2.35 → 2`, `< r*4.8 → 1`, else 0 (which also clears focus and returns the target home). Never let the level jump from 3/4 back down except through `up()`.

`up()` walks 4→3→2→1 (restoring `dist = r*3.4` at 1) and calls `reset()` below that. `enter()` jumps to level 2 at `dist = r*1.8`.

### 6.2 Pointer

- **drag** = orbit: `want.theta -= dx*0.005`, `want.phi = clamp(want.phi - dy*0.004, ...)`.
- **shift-drag or middle-drag** = pan: `k = cam.dist*0.0016`, move `want.target` along the camera-right axis and world-up, clamped to ±40/−32..28/±34.
- **alt- or meta-drag on a chamber** = reposition it. `moveSector` converts pixels to world units via `wpp = 2*tan(21°)*dist/clientHeight`, moves along camera right/up (or camera-forward when shift is held, for depth), clamps to ±60/±46/±60, then re-solves every conduit touching that chamber and re-places the seal. Cursor becomes `move`.
- **wheel** = zoom (see above), `passive:false`, `preventDefault`.
- **`L` key** resets all chamber positions to their stored home positions and reflows.
- **pointerup with < 6px of movement** = a click. On a hit: sector → `select`; ant → open the ant inspector; record → `level 4` + `onRecord`; cluster → `level 3` + `onCluster`. **On empty space**: drop the entire selection and return to the survey (focus null, level 0, camera home).
- Cursor states: `grab` idle, `grabbing` dragging, `pointer` over a hit, `move` over an alt-hovered chamber.

### 6.3 Hit-testing (screen-space, ordered by specificity)

No raycaster. Project candidate world positions to screen and take the nearest within a pixel radius, in this order:

1. If focused: crew orbs, radius 32 px.
2. If focused and `level >= 2`: records, radius 14 px (restricted to the active cluster at `level >= 3`).
3. If `level === 2`: cluster centres, radius 26 px.
4. Always: chambers — project the centre and the point `centre + (r,0,0)`, take `max(12, |Δx|)` as the screen radius, keep the nearest in `z`.

Records and clusters must be projected through `livePos` so hit-testing tracks the cloud→strata blend.

---

## 7. Restyle (chamber and ant customization)

`setChamberStyle(id, {label, color})`:

- `label` — write to `sector.label` and straight into the DOM label's `textContent`.
- `color` — one color drives the whole chamber palette:

```js
shell = Color(color);  core = shell.lerp(white, 0.42);  nuc = shell * 0.6;
```

Then, in this order: `nucleus.material.color = nuc`; `glow.material.color = shell`; DOM label color = the new hex; **every record's `acolor` recomputed with the same build-time formula** (`shell→core` by `pow(1-rad,1.5)*1.15`, scaled by `0.5 + tint*0.45`) with `needsUpdate = true`; spoke material = `core` and cluster-ring material = `shell`; every non-Queen crew orb recolored (`isLead ? core : shell`, core sprite lerped 0.55 to white, `info.color` updated); and finally both `uFrom`/`uTo`/`uDest` uniforms of **every conduit touching this chamber**, re-tinted 0.3 toward `#dfe8f5`. The Queen's gold orb is deliberately exempt.

`setAntStyle(antId, {name, color})` searches all crews for `info.ant_id`, updates `role`/`info.role` and the halo + core colors, and returns a fresh `antPayload` so the panel stays controlled.

Swatch set offered in both inspectors (10 colors):

```
#e21f7b  #ff3fa4  #35aadf  #22d3ee  #fb923c  #f59e0b  #ef4444  #8b5cf6  #10b981  #94a3b8
```

Chamber swatches are 22 px circles, ant swatches 19 px; both get `box-shadow: 0 0 10px <color>` and a 2px `#f4e9d6` ring when selected, `transparent` otherwise. The chamber name field is the **only** place chamber identity is editable.

---

## 8. DOM label layer

All labels are absolutely-positioned divs in the overlay, IBM Plex Mono, with heavy text-shadow, and CSS opacity transitions. Pools are fixed-size, reused by index.

- **Sector labels** (7): `600 11px/1.35`, `letter-spacing .16em`, `white-space: pre` (the Micromound label has a newline), color = the sector shell (Queen uses `#ff3fa4` rather than `#e21f7b`), `text-shadow: 0 0 18px rgba(0,0,0,.9)`, `transition: opacity .35s`. Placement is chrome-aware: project the centre, compute the screen radius, then try four candidate slots in order — preferred side at `rad+34`, opposite side, preferred side above (`-rad-26`), opposite above — and take the first that doesn't intersect any element marked `[data-chrome-avoid]` (rects re-read at most every 400 ms). If all four collide, park the label just above the blocking panel. Anchor via `transform: translate(0|-100%, -50%)` and matching `text-align`. Hidden when labels are off, when off-screen, or when `level >= 2` and this isn't the focused chamber; dimmed to 0.35 when another chamber is focused.
- **Cluster labels** (pool of 12): `500 9.5px/1.3`, `.1em`, `rgba(201,207,220,.82)`, uppercase, shown only when focused and `level >= 2`, and filtered to the active cluster once one is selected.
- **Record labels** (pool of 16): `400 9.5px/1.3`, `rgba(244,233,214,.9)`, `padding-left:6px`, `border-left:1px solid rgba(255,63,164,.45)`, `transform: translate(10px,-50%)`. Shown for the selected cluster's first 16 records.
- **Crew labels** (pool of 24): `500 9px/1.3`, `.08em`, `transform: translate(12px,-50%)`, color = the ant's own hex; opacity 1 selected / 0.95 Queen / 0.78 lead / 0.5 worker. Only for the focused chamber at `level >= 1`.
- **Move hint**, bottom-left, `500 8.5px/1`, `.14em`, `rgba(139,147,168,.55)`: `ALT-DRAG A CHAMBER TO REPOSITION · SHIFT TO PUSH IN DEPTH · L RESETS LAYOUT`.

---

## 9. Mission projection and the frame loop

### 9.1 `setMission(m)` payload

```js
{ lit: { [rootId]: {ants, progress, halt|null, reverse}, [sectorId]: true },
  trails: { [rootId]: 0..1 },
  approval: { root, at } | null,
  activeSeg: rootId | null,
  missionState: 'idle|active|waiting|blocked|approval|complete|failed|degraded|disconnected|stopped|incompatible' }
```

The renderer never invents activity. If nothing is lit, nothing pulses; the streams still drift.

### 9.2 The clock (host side, `setInterval` 120 ms)

Advance `elapsed += 0.12 * missionSpeed` unless an approval is pending or motion is off. Walk the step durations to find the current step and its local 0–1 progress. Light `{[step.segment]: {ants:1, progress: min(0.995, local), halt: step.approval ? 0.62 : null, reverse: step.n===6}}`, plus `lit[step.sector] = true` and `lit.queen = true`. Keep `q-mm` on a slow reverse heartbeat, `progress = (elapsed % 4)/4`, unless the mound is stopped. Accumulate `trails[segment] += 0.02` (clamp 1) for every completed step. When a step with `approval: true` passes local 0.55, raise the approval flag — it halts the wave at 62% of the conduit until the operator decides. Six seconds past the end, reset `elapsed` and clear trails.

### 9.3 Frame

Throttle to ~30 fps: return early if `ms - lastDraw < (slack ? 90 : 31)`. Then, in order: `applyCam()`; chamber emphasis; link opacity; `colonyLife`; refresh the chrome-avoid rects; conduits; approval boundary and seal; labels; follow; render.

Chamber emphasis per frame (all eased, never snapped):

```
glow.opacity   → focused ? .34 : lit ? .60 : .44                    (×.05)
nucleus.opacity→ (queen ? 1.1 : 1) + (lit ? .22 : 0) + .06*sin(ms*.0012 + r)   (×.06)
uAlpha         → (focusElsewhere && level>=2) ? .42 : 1             (×.05)
uScale         → inside ? .8  : 1                                    (×.06)
uRec           → inside ? 1.7 : 1                                    (×.06)
uOrg           → (focused && level>=1) ? 1 : 0                       (×.07, reflow links when it moves)
```

Conduit per frame: derive `head` from `lit.progress` (or `(ms*0.00016)%1` as a fallback), clamp it to `halt`, mirror it when `reverse`; freeze at `halt ?? 0.5` when motion is off. Ease `uActive` toward 0/1 at 0.12, and `uRest` toward `spec.rest*(level>=2 ? .6 : 1) + trail*0.22 + (lit ? 0.2 : 0)` at 0.06. Drift every third frame.

Follow mode: when `state.follow` and a segment is active, `want.target.lerp(curve.getPoint(progress), 0.08)` and `want.dist += (16 - want.dist)*0.02`.

### 9.4 Quality ladder and circuit breaker

Measure only the `renderer.render` call. `perf.avg = avg*0.85 + rms*0.15`.

```
rms > 1000 → breaker(): dead = true, cancelAnimationFrame, hooks.onStall(ms). Hold the last frame.
rms > 500  → setQuality(2)
rms > 120  → setQuality(1)
avg > 45 and every 20th frame → setQuality(level+1)
rms > 60   → perf.slack = true (next frame gets a 90 ms gap)
```

`setQuality(level)` is monotonic (never restores), caps at 2, drops `pixelRatio` to 1, and sets draw ranges: conduits keep 50% of grains at level 1 and 20% at level 2; at level 2 chambers keep 40% of their records. Log every transition.

Also: respect `prefers-reduced-motion` by booting with motion off. Dispose the renderer, the `ResizeObserver` and the interval on unmount, and empty the mount node.

---

## 10. `createFallback(mount)` — no WebGL

A flat 2D canvas of the same topology, redrawn on resize, no motion: `#05070c` fill; `sc = min(w/62, h/42)`; origin at `(w/2 - 2sc, h/2 + 2sc)`; project `(x, y)` orthographically as `(cx + x*sc, cy - y*sc)`. Draw roots first — 5px `rgba(58,68,92,.9)`, or 2px `rgba(255,63,164,.45)` for the authority root — then each chamber as a 1.5px shell-colored circle of radius `r*sc` with a 12%-alpha fill and its label in `600 10px "IBM Plex Mono"` centred 16px below. Return an object with the same method names as the real stage, all no-ops except `resize`.

---

## 11. Chrome and panels (host component)

Two shells, toggled from View Options; the content pane insets by `left 240px / top 50px` in console mode and `0/0` in minimal.

**Minimal (default).** 44 px bar: an ant glyph + `ANTHILL` (600/12 px, `.28em`, `#f4e9d6`); centred `COLONY LIVE`, a 1px divider, and the live mission chip (`ACTIVE · 4/7`) in magenta; right, a pill `N NEEDS YOU` — magenta on `rgba(255,63,164,.07)`, border `rgba(255,63,164,.45)`, hover `rgba(255,63,164,.16)`. Background `linear-gradient(180deg, rgba(4,6,11,.92), rgba(4,6,11,.55))`, bottom border `rgba(38,46,68,.7)`, `backdrop-filter: blur(6px)`.

**Console.** 240 px sidebar on `#0d1320`, borders `#262e44`: logo block with `ANTHILL` + `v0.3.8.109`; group `Main` — Overview, **Colony** (active: `rgba(255,63,164,.08)` fill, magenta text, 2px magenta left indicator inset 25%), Missions (red `1` badge); group `Operations` — Event Log, Results, Homelab; footer chip `operator / admin` with a `linear-gradient(135deg,#ff3fa4,#e21f7b)` avatar. Plus a 50 px top bar: breadcrumb `Colony / Topology`, a centred pulsing magenta dot with the mission title (ellipsis at 440 px), and a right chip `AUTO · llama3.1:8b` with a `#10b981` dot.

Floating overlays share the same material: `rgba(9,13,24,.9)`–`rgba(13,19,32,.95)`, 1px `rgba(38,46,68,.9)`, radius 8, `backdrop-filter: blur(10px)`. Every panel that can occlude a label carries `data-chrome-avoid="1"`.

- **Breadcrumb + level** (top-left, above L0): `COLONY / SECTOR / CLUSTER`, the `L2 INSIDE SPHERE` chip, and a `BACK` button.
- **Mission strip** (bottom-left, 404 px): `ACTIVE MISSION` kicker + id; the title in cream 12 px; a row of 7 clickable 3 px segments (past `rgba(255,63,164,.45)`, current `#ff3fa4`, future `rgba(38,46,68,.9)`) that jump the clock; `STEP n` + step title + note. When approval is pending, an inset appears: `APPROVAL BOUNDARY`, `repo.patch.apply · destructive · high`, and a solid magenta `APPROVE`.
- **Controls** (bottom-right): `SURVEY · MISSION · MEMORY · MOUNDS · FOLLOW`, a divider, `VIEW ⌄`. Hover turns border and text magenta; FOLLOW turns magenta while active.
- **View options** popover: Chrome (MINIMAL/CONSOLE), Motion (NORMAL→CALM→OFF), Labels (ALL/OFF), Pheromone trails (ALL/OFF), plus the hint `Drag to orbit · shift-drag to pan · wheel to zoom into a sphere. Sector positions never move.`
- **Record inspector** (322 px, right, top 56): type kicker in the type color, title, then Record type / Sector / Cluster / Mission / Source ant / Recorded / Verification (green–amber–red) / Connected records; an amber 3 px pheromone meter; and a collapsible `▸ TECHNICAL DETAILS` with `record_id, task_type, side_effect_class, risk_class, required_capabilities, idempotency_key`.
- **Chamber inspector** (300 px): kicker `COLONY CHAMBER` / `CHILD COLONY` with a glowing dot, the editable `NAME` field, the `COLOUR` swatch row, then read-only Chamber id / Records / Clusters / Registry roles / Workers.
- **Ant inspector** (300 px): kicker `RESIDENT ANT` with a dot in the ant's color, an editable role-name input, `ant_id · sector`, the swatch row, then Rank (gold when `colony authority`) / Records authored / Clusters served / Verified / Refused / Avg pheromone / Home cluster / Status / Current run / Progress / Deposits this session / Protocol, and a `LATEST RECORD` block with its timestamp.
- **Micromound panel** (322 px, border `rgba(255,63,164,.35)`): `MICROMOUND · M1 READ-ONLY`, name + `Mound Major`, then Status / Enrollment / Hardware / Last sync / Last seq / Capabilities / Chain health / Global stop; an `EVIDENCE BEATS` list (seq · state · time, green accepted, red refused); a red-outlined `STOP THIS MOUND` / `RESUME MOUND`; and the standing note "Stop always wins. Disconnection grants no additional authority. No local mission, charter, or lease is shown at M1 because none is reported."

Panel routing: the record inspector wins over everything; then the mound panel for child colonies; then the ant inspector; then the chamber inspector. Only one is ever open.

Type colors for record kickers and fields: `memory #f59e0b, evidence #ef4444, artifact #fb923c, context #35aadf, plan/decision #ff3fa4, result #8b5cf6, pheromone #f59e0b, failure #8b93a8, task #c9cfdc`.

---

## 12. Sample data (replace with live sources; keep the shapes)

**Mission** `msn_7f31c4`, "Nightly Proxmox backup verification has been failing since 08-27", 7 steps, each `{n, sector, segment, ant, dur, task, title, task_type, caps[], side_effect, risk, note, creates[]}`:

1. queen · no segment · 4.5 s · `diagnose` · `mission.plan` · none/low — admit the mission, record a five-task plan.
2. intelligence · `q-i` · 8 s · `research` · `repo.read, network.http.public, micromound.read` · none/low — read backup job history and mound beats.
3. forge · `i-f` · 9 s · `change` · `repo.read, repo.patch.propose` · reversible/medium — propose a retry-window patch.
4. validation · `f-v` · 9 s · `verify` · `repo.read, build.run, micromound.read` · none/medium — verify against evidence beats.
5. validation · `v-m` · 7 s · **approval: true** · `change` · `repo.patch.apply` · destructive/high — halts at the boundary.
6. memory · `v-m` **reverse** · 8 s · `change` · `memory.write` · reversible/low — settle evidence into durable memory.
7. output · `q-o` · 6 s · `change` · `ui.render` · none/low — deliver the operator-facing result.

**Mound** `mm-rack-01` "Rack Micromound", controller `Mound Major`, `edge_queen`, standard tier, online, enrolled, `last_seen 2026-09-01T11:22:07Z`, `last_seq 4182`, 30 s sync, capabilities `proxmox.vm.read, storage.pool.read, backup.job.read, net.probe, health.report`, hardware `x86_64 · 8c/16t · 64 GB · 2×2 TB NVMe`, beats `4182/4181/4180 accepted` and `4179 refused — chain digest did not continue`, `chain_health: continuous since seq 4180`, both stops clear.

**STATES** — one entry per runtime state with `{label, color, note}`: idle (dim), active (queen), waiting (amber), blocked (rose), approval → label `NEEDS YOU` (queen), complete (`#10b981`), failed (rose), degraded (orange), disconnected (dim), stopped (rose), incompatible (muted). One table drives chip text, ring color and inspector copy.

---

## 13. Acceptance checks

1. At rest, seven point clouds sit in an octahedral arrangement, each inside its own soft coloured light, with 16 thin dotted streams flowing between them and cream monospace labels beside each cloud. Nothing else is visible.
2. Grains visibly *travel* chamber to chamber with no mission running; a crossing takes 9–14 s.
3. Clicking a chamber flies the camera to `r*3.4`, cross-fades the cloud into horizontal cluster strata, reveals crew labels, and opens the chamber inspector.
4. Wheeling in past `r*2.35` reaches L2: cluster labels appear, records become individually clickable, and the rest of the colony dims to 0.42.
5. Clicking an ant orb opens the ant inspector; its `Progress`/`Status` values change while the panel is open.
6. Recoloring a chamber instantly changes its records, core light, spokes, non-Queen orbs, label, and both ends of all of its conduits.
7. Alt-dragging a chamber moves it and every attached conduit re-solves to follow; `L` restores the layout.
8. Clicking empty space returns to L0 and clears every panel.
9. During step 5 the active stream stops dead at 62% with a pulsing magenta ring at that point, and nothing advances until `APPROVE`.
10. Motion OFF freezes all drift and bobbing but leaves every value on screen readable and truthful.
