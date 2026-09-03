# Colony Live 3D — what was ported, what was refused, and why

This directory is the **design source** for `src/Anthill.UI/colony-renderer.js`. It is not shipped,
not built and not served; `Anthill.Api.csproj` embeds `src/Anthill.UI/**`, and nothing under `docs/`
reaches a browser. It is here because `docs/HANDOFF.md` tells the next session not to re-derive the
renderer's math, and a "port the working code" instruction is worthless without the working code.

Four of the seven files in the original package are here. The two omitted are the two the handoff
itself says not to port — `support.js` (prototype runtime) and `Colony Live 3D.dc.html` (prototype
shell) — and `three-loader.js`, which fetched three.js from unpkg and evaluated it from a Blob URL.
That path would break `script-src 'self'`; this console vendors three.js 0.128.0 as an
EmbeddedResource instead, and `NoConsoleAsset_LoadsCodeFromAnywhereButThisOrigin` refuses the
alternative.

---

## The port

`reference/colony-renderer.js` → `src/Anthill.UI/colony-renderer.js`.

Every numeric constant, both GLSL shader pairs, the four canvas texture stop tables, the Catmull-Rom
conduit sampling on a rotation-minimising frame, the pixel-sized crew orbs, the screen-space hit test
and the per-frame easing factors are the reference's, unchanged.

Two edits to the code itself, and no others:

- **The wrapper.** The reference is an ES module with `import`/`export`. This console loads plain
  `<script src>` under `script-src 'self'` and its assets find each other through globals, so the
  port is an IIFE that publishes `window.ColonyRenderer`. No behaviour depends on this.
- **A reduced-motion bypass on the camera easing.** The reference always lerps at 0.08/0.075. Under
  `prefers-reduced-motion: reduce` the port snaps instead, which is what that preference asks for.

`ColonyLiveGuardTests.ThePortedConstants_StillAgreeWithTheVendoredReference` reads BOTH files and
compares the extracted values. That is the guard worth having: the hand-written literal assertions
beside it can only catch a constant being deleted, while this one catches a constant being *changed*
to something plausible. If the design is ever revised, update `reference/` and the guard reports
every place the port has fallen behind.

---

## The three refusals

Each is true of the reference's generated sample data and false of this colony. They are the same
defect in three costumes: a picture that claims something the colony did not record.

A fourth refusal was tried and withdrawn — see "Motion" below. It is worth reading before adding a
fifth, because it is the one place this port was wrong in the other direction.

### 1. Generated records — so `colony-topology.js` is NOT ported

`reference/colony-topology.js` is here for its *shapes*, not to be used. `buildContext()` generates
nine named clusters per chamber and 6–17 invented records in each, plus `MISSION`, `MOUND` and a
`recordTitle()` table of plausible sentences. That is why the reference's chambers look dense.

The handoff says so itself (README, "Data fetching"): *replace `SECTORS[].clusters/leads/workers` and
`buildContext()` with live sources, preserving the record shape.* This console's
`src/Anthill.UI/colony-topology.js` already does that job against `/colony/live/snapshot` and
`/colony/live/records`. What was preserved from the reference is the geometry and the rules:

| Reference | Here |
|---|---|
| `sector.clusters` — nine invented labels | the event types this chamber's records actually have |
| `leads` / `workers` string tables | the registry's roles and their `Workers`, via the projection |
| `record.pheromone` — `mulberry32` | the author's real `TrailView.Strength` |
| `record.verification` — `VERIF[rnd()]` | the evidence table's verdict, joined server-side |
| `TYPE_TINT` by invented record type | tint by verification: verified 0.95, refused 0.35, else 0.6 |
| `depth = 1 - min(.94, (verified ? .55 : .1) + pheromone*.45)` | **identical**, over the real values |
| cluster lattice, record seat, `org` strata | **identical** |

A chamber holding nothing draws nothing. What carries an empty chamber is the core light, which is
light and not data.

### 2. The 120 ms mission clock (SPEC §9.2)

In the design the shell runs `setInterval(…, 120)`, advances `elapsed`, and hands the renderer a
`progress` the shader sweeps along the conduit as a bright Gaussian head. There is no per-task
progress anywhere in this model — `/graph` reports a status, not a fraction — so a travelling head
would be an animation of a number that does not exist. An active route brightens along its whole
length instead. The only thing that TRAVELS is a recorded transition, once per event id.

`NoColonyAsset_RunsARepeatingTimer` has forbidden the timer since `.115`.

### 3. The ant work timer (SPEC §5.3)

`a.work -= dt`; on expiry, pick a cluster (72% home, else random), set `beamDur = 1.3 + rnd()*1.4`,
report `status: 'laying pheromone'` and a `dwell` of `"NN% of run"`. An operator looking at a quiet
colony would see every ant working. An ant is `working` here only when a real running task is
assigned to it, and the pheromone number it shows is the trail the colony recorded.

### And one narrowing, not a refusal

`setChamberStyle(id, {label, color})` becomes colour-only. Colour is presentation, like the chamber
layout that already persists to `/ui/state`. A name is identity: it is the registry's `Colony` value
projected by `ColonyLiveProjection`, and a console where one page can disagree with the registry
about what a colony is called is wrong on every other page at the same time.

---

## Motion — the refusal that was withdrawn

An earlier pass also froze the conduit grains, reasoning that flow along a permanent structural link
claims work is passing through it. That was the rule applied one step too far, and what it bought was
a console that looked dead. Recorded here because the correction is more useful than the rule was.

**The line is not motion versus stillness, it is AMBIENT versus ASSERTED.** Grains drifting along a
passage say the passage exists and the view is live, the way a cursor blinks; they carry no claim
about any task. What would be a lie is a bright wave with no event behind it, or an ant that looks
busy while idle. Those two are still refused, and the guard was rewritten to forbid exactly them
rather than to forbid movement.

So `driftConduit(co, k ? dtSec * 3 : 0)` runs every third frame, at the reference's speed — a grain
crosses in roughly 9–14 seconds — and freezes with the motion preference and with
`prefers-reduced-motion`. Beyond that ambient drift, `conduitState()` admits exactly two things that
may brighten a conduit, and `AConduitBrightens_OnlyForSomethingTheColonyRecorded` holds them:

- **A recorded transition travels it.** One wave per unique event id, ever, from `task_started`,
  `handoff_admitted`, `task_rerouted`, `adaptive_escalated` or `micromound_mission_dispatched`. The
  event names the ant, so the route is known rather than inferred. This is the colony visibly
  lighting up as work moves through it.
- **A running task sits at one end of a persisted mission edge.** That raises the conduit's *resting*
  brightness. It never sweeps a head, because a task status is not a position along a line — and
  drawing a position from a status is the invented-progress defect `.115` shipped and `.116` removed.

## No authority seal

The reference parks a lock sprite at 0.42 along the Queen→Micromound conduit. It is a badge on a
line, not a control, and it sat in the middle of the frame implying the mound was locked to
interaction — the opposite of true, since clicking that chamber is now how an operator stops the
device. The authority relationship is already said by the conduit itself, which is the only edge in
the colony drawn as its own kind, so the badge carried no fact the picture did not and was actively
misread. `TheAuthorityConduit_CarriesNoLockBadge` keeps it out.

## Clicking the Micromound opens the Micromound

Every other chamber is a group of registry roles and the chamber inspector answers the questions
worth asking about one. The mound is a physical device with a stop, a charter, a lease and an
enrollment; a card reading "registry roles: 0, workers: 0" answers none of them.

The panel carries per-mound **stop and resume** — the one control that has to be reachable from
wherever the operator happens to be looking, because the reason to reach for it is that something is
going wrong right now — and hands everything else to the Micromound console rather than growing a
second copy of forms whose vocabulary is a closed PROTOCOL set. The **global** stop is absent here as
it is there: it is a file on disk precisely so no API flow can clear it, and a button that appeared
to would teach an operator the opposite.

The post goes through `colony-host.js`, the only file in the feature allowed to reach the network,
and it re-reads the fleet listing afterwards on both the success and the failure path. A view that
flipped its own `stopped` flag on a 200 would disagree with the colony the first time an order was
accepted and then superseded, and would look right while doing it.

---

## The pheromone layer, drawn from both of its rows

`pheromone_trails` keys strength to `worker:{id}`. An EDGE therefore has no row of its own, and a
conduit that displayed a "trail strength" would be quoting a number for a thing the table does not
describe. So the layer is drawn twice, each at the level it actually exists at:

- **Per conduit** — how many recorded transitions have crossed that route this session, normalised
  against the busiest one, raising resting brightness by `trail * 0.22` (the reference's factor).
  Reinforcement-by-use is what a trail *is*, and a route the colony keeps using glows without
  anything needing to be running on it now. Bounded by the retained transition window
  (`MAX_TRANSITIONS = 200`), and the stream only — so it is "this session", not "ever".
- **Per ant** — the role's own summed `TrailView.Strength`, as size (±12%) and brightness (±15%) on
  its orb, so a chamber's most-reinforced workers read first. A role whose workers have never run has
  **no** trail, which is not a strength of zero: it draws at the floor, and the inspector prints the
  two differently rather than showing "0.000" as a verdict nobody reached.

Both are gated by the operator's `trails` preference. A control that turns off a display while the
thing it names keeps driving the picture is a lie about the control.

---

## Intra-chamber linkage — five families, not two

The reference draws two. The other three are relationships this colony records and the reference's
sample data has no equivalent of, which also makes them the easiest place to quietly add a generated
link to make a sparse chamber look busy.

| Family | The row behind it | Reads at |
|---|---|---|
| cluster → its records | the record's event type | always, brightening with proximity |
| cluster → next cluster | the chamber's context ring | always, at 0.7× |
| **worker → its role** | `AntWorkerDefinition.ParentRoleId`, carried by the projection | always; brightest when focused |
| **ant → its records** | `record.ant` — whichever unit actually ran | approach (L1) |
| **record → record** | consecutive records sharing `mission_id`, in recorded order | inside (L2) |

The roster chain is the brightest of the five, because it is the one that answers "what is this
chamber MADE of" and, unlike the other four, it is true of a chamber that has never recorded
anything. Its endpoints are fixed seats, so it is written once and never reflowed.

A chamber whose records name no ant and no mission draws the cluster families and the roster chain
and nothing else. A record whose author is not hosted here contributes no segment, and a mission with
a single record here contributes none either — a thread of one is not a thread. `reflowLinks`
rewrites the four record-bearing families during the ordered-strata cross-fade, so a link never
trails the particle it points at.

### And a worker sits under its own role

The reference spreads workers evenly around the outer ring by their index in a flat roster. That is
fine for a list of names and wrong here: `scope_guard` reports to `constraint`, the registry says so,
and roster order puts it on the far side of the chamber from the ant it belongs to. Each worker is
seated in the arc directly outside its parent, spanning 80% of that parent's share of the ring so two
roles' workers never interleave — the shape of the chamber is the shape of the roster before a single
link is drawn.

### A worker has two names and the view needs both

`AntWorkerDefinition.WorkerId` is `{parent}.{id}` — `constraint.scope_guard` — and it is the identity
an event carries in `ant_name`, so it is the only thing a record can be matched on. `DisplayName` is
`ScopeGuard`, which is what the registry calls the ant and what the 2D colony view has always shown.

Until `.116` the projection carried the id ALONE, so Colony Live labelled an ant
`constraint.scope_guard` while every other page in the console called the same ant `ScopeGuard` — one
ant, two names, in one product. `ColonyWorker` now carries both plus `ParentRoleId`; orbs and labels
use the name, record matching uses the id, and the ant inspector prints both because someone holding
a log line needs the one and someone reading the roster needs the other. A parallel array of display
names would have been the same defect with an extra way to fall out of step.

---

## Additions the reference does not have

- **Two more chambers.** The reference has exactly seven and its README says "`HOMELAB` is not one of
  them". This colony's registry has nine sectors, including `unassigned` — where an unrecognised role
  goes, and the whole point of routing it there rather than to the Queen is that an operator can SEE
  it. Both take free equatorial axes at 33, not at the diagonals' own radius of 23.33: measured, at
  23.33 each sat 17.9 units from its nearest neighbour and `unassigned` smeared into `intelligence`.
- **Two more conduits**, `q-h` and `q-u`, so those chambers hang off the Queen like every other one.
- **Layout schema 2.** `.115` persisted chamber seats in a world fourteen times this one. Replayed
  here every chamber lands far outside the 130-unit dolly limit. A schema-1 payload is refused and
  resets to home; the ×14 factor was never written down, so back-solving it would be a fiction
  dressed as a migration.
- **Label placement that also avoids other labels and the frame edge.** The reference avoids the
  chrome only, so two chambers projecting near each other print over one another, and a chamber near
  the right edge gets a label past the frame that the overlay clips away — drawn, lit and unnamed.
  Homelab was exactly that case.

---

## Provenance

Delivered by the operator as `UI mockups.zip` → `design_handoff_colony_live_3d/`, 2026-09-03.
`README.md` and `SPEC.md` are reproduced verbatim; the two `reference/` modules are byte-identical to
the package. They are design inputs to this repository, not third-party dependencies: nothing here is
compiled, packaged or served.
