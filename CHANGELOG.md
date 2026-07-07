# ANTHILL Changelog

## v2.2.6.1 — Proxmox sync: surface the privsep "nodes-only" gotcha in the UI

Live-testing a freshly-connected Proxmox VE integration turned up a confusing dead-end: a manual
"Sync now" succeeds (HTTP 200, `proxmox sync ok (N items)`) yet the VM / Container / Storage tables
stay empty. Root cause is not in ANTHILL — it is Proxmox's read-only API-token model:

- A privilege-separated (`privsep=1`) API token's effective permissions are the **intersection** of
  the backing user's permissions and the token's own ACL. If the token holds `PVEAuditor` on `/` but
  the backing user holds nothing, the intersection is empty for `VM.Audit` / `Datastore.Audit`.
- Proxmox then returns **HTTP 200 + an empty list** (never 403) for `/nodes/{node}/qemu`, `/lxc`,
  `/storage`. Node *listing* is not gated the same way, so the sync finds the nodes and reports them
  as items while pulling zero guests underneath — exactly the "success but no data" symptom.

Fix (UI only; the sync path and every client stay read-only and unchanged):
- `hlSyncVirt()` now detects a Proxmox sync that returned nodes but left the VM/container/storage
  inventory empty, and reports the actual cause inline: grant the **backing user** the `PVEAuditor`
  role too (`effective perms = user ∩ token`). No more silent "ok" over three empty tables.
- On failure it now surfaces the error and stops rather than falling through to a generic message;
  on success it keeps the full `loadHomelab()` refresh (node graph + inventory tables).

Operator fix on the PVE host: `pveum acl modify / --roles PVEAuditor --users <user>@<realm>`.

## v2.2.6 — Cleanup + hardening pass (no new features; framework checkpoint before V2.3.0)

Full audit of the v2.2.x churn; every finding fixed at the root:

- **Resource Usage card fixed**: it read `cpu_percent`/`memory_percent`/`disk_percent` fields the
  API never provides, so it was permanently "Metrics unavailable". It now renders the real
  governor signals (CPU load/core, memory used, backend latency, concurrency) published by the
  dashboard poll — the same data the retired hidden metrics row used.
- **One System Core state machine**: pollHud (which sees autonomy, objectives, patches, and
  provider health) is the single computer of core state; the System Core card renders its
  published state, so AUTONOMY ONLINE / provider-offline can no longer be silently under-reported.
- **Hidden legacy panels deleted for real**: the display:none HUD strip and metrics row (still
  fully re-rendered every 6s) are gone — markup, writers, and their whole CSS families
  (.hud-strip/.hud-metric/.hud-dash-grid/JARVIS-orb rules). No more orphaned element lookups.
- Telemetry-bar ant count no longer goes stale (registry re-requested through the TTL cache).
- **⌂ Reset layout** button returns dragged chambers to the default map layout.
- System Core orb colors now use design tokens (raw hexes removed).
- Hardening: ids embedded in inline map handlers pass through `attrSafe()` (strips every JS-string
  breakout character; defense-in-depth over escapeHtml). Fast drag-release can no longer trigger
  an accidental chamber expand. Expanded view uses proper concentric rings (no dot overlap in
  large chambers).
- **New regression guards** (run in `dotnet test` and CI): CHANGELOG top entry must equal the
  runtime version (tag-ordering mishaps); no orphaned getElementById targets and no duplicate ids
  in the UI; registry adapter accessors must stay case-tolerant (the "Other · 25" class of bug).
- NORTH_STAR annotated with the shipped v2.2.1–v2.2.6 patch series; V2.3.0 (approval-gated
  homelab actions) is next.

## v2.2.5 — Fix: tunnels visible between ALL chambers, not just Queen ↔ Mission Control

- Delegation tunnels were drawn for every chamber but idle ones used the near-invisible border
  color at 35% opacity — only the active Queen ↔ Mission Control run could be seen. Every tunnel
  now renders in its chamber's role color (subtle when idle), curved like dug tunnels, and lights
  up with an animated glow-flow when that chamber has active ants — so pheromone traffic back to
  the Queen is followable across the whole colony. Command chain unchanged: Queen → Mission
  Control → every chamber, honoring dragged chamber positions.

## v2.2.4 — Chamber delegation lines, draggable chambers, ant duties in every inspector

- **Live delegation lines on the chamber map**: Queen → Mission Control → each chamber, mirroring
  the classic engine's structure. Lines stay faint when idle and light up in the chamber's role
  color with an animated flow when that chamber had ants active in the last 15 minutes — live
  delegation is now visible in Chamber/Expanded just like Live Colony. (Motion setting and
  prefers-reduced-motion both disable the animation.)
- **Chambers are draggable**: grab any chamber group and move it, same as classic ants; positions
  are normalized and persisted per operator (`anthill.colony.chamberPos`); a drag never triggers a
  selection; "Reset view" is unaffected (pan/zoom only).
- **Per-ant duties in the map inspector**: selecting a chamber lists every ant with its registry
  Purpose (e.g. ScribeAnt — what it does), each row click-selects that ant; selecting an ant shows
  a PURPOSE section. Real registry data only — ants without a purpose simply omit the line.
- **Ant Inspector page shows the whole colony**: below the six legacy telemetry castes (which keep
  their real task stats) a new COLONY DIRECTORY lists every registered role and worker with role
  color and duty, so any ant can be inspected — not just researcher/web/file/coder/builder/verifier.
- Chamber map adapter now carries registry Purpose fields end-to-end (case-tolerant).
- **Classic-mode view switcher fixed**: the floating map toolbar was clipped at the top-left in
  Live Colony mode. It's now hidden there entirely; "🗺 Chamber Map" / "🗺 Expanded Map" buttons
  live inside the classic canvas's own top-right viewbar (Command/Expanded/Active/Groups/Handoffs),
  which is already correctly positioned. The full map toolbar (motion/labels/pheromones/reset)
  still appears in Chamber/Expanded modes.

## v2.2.3 — Repair: Chamber/Expanded role detection ("Other · 25"), Colony dead space, Overview grid balance

- **"Other · 25" root cause fixed**: the registry serializes PascalCase (`RoleId`/`DisplayName`/
  `Workers`); the chamber adapter only tried camelCase, got '' for every role, and classified the
  whole colony as Other. The adapter now uses the same case-tolerant accessors as the classic Live
  Colony engine (which is why that view was unaffected) and falls back to the display name before
  giving up — only truly unmatchable ants land in Other. Dev-only console.debug reports total
  ants / chambers / unclassified samples.
- **Colony dead-space fixed**: the floating VIEW bar carries an inline `position:relative` (for
  cmap-mode layering) which overrode the classic-mode absolute float, leaving the bar in flow as
  an empty ~350px row column. Now `!important`-scoped; the bar floats top-right over the canvas.
- **Expanded view repaired**: with no chamber selected it shows every ant in every chamber (multi-
  ring layout, never a single placeholder circle) with a "Select a chamber to focus" hint; ant
  dots are clickable in all views (existing selection/inspector handlers), active/selected ants
  get labels and a breathing pulse (active-only animation, motion setting + reduced-motion safe).
- **Inspector**: with nothing selected it now shows a colony summary (total ants, active count,
  per-chamber breakdown with click-to-select) instead of an empty prompt.
- **Overview grid rebalanced**: Operator Attention and Mission Command moved INTO the 12-column
  grid (they were stranded below it beside retired hidden panels, leaving a huge blank region);
  Recent Events restored as the third row-3 card (reuses pollOv2 data, no extra polling); retired
  hidden System Core panel removed from the DOM (legacy writers are null-guarded); consistent card
  min/max heights with internal scroll; responsive 2-col/1-col fallbacks. Colony Vitals remains
  full-width below the grid.
- Overview System Core ant count now uses the case-tolerant worker accessor (was undercounting).

## v2.2.2 — Fix: classic live colony default + Raw blank-canvas; Overview condensed, System Core functional

Live feedback on v2.2.1: the original colony experience — every individual ant visible and
draggable, live pulses on activation, the pheromone canvas — is the heart of the page and must be
the default, and switching to it was broken (blank screen).

- **Blank "Raw Graph" root cause fixed**: the v2.2.1 `width:100%;flex-shrink:0` rule on the
  telemetry/view bars applied unconditionally — in the classic flex-ROW layout those full-width
  bars consumed the entire row and pushed the canvas and panels past the `overflow:hidden` edge.
  Rule is now scoped to `.cmap-mode`; in classic mode the telemetry bar hides and a compact VIEW
  switcher floats over the canvas instead.
- Additionally, the classic canvas now gets its full delayed boot sequence (resize → buildNodes →
  legend → pheromone poll) after being unhidden, mirroring the original page-enter path — the
  v2.2.1 toggle ran only a synchronous resize against a hidden canvas.
- **Classic view is the default** (renamed **🐜 Live Colony** and listed first): all original
  behavior — per-ant dots, drag-to-move, rename, pan/zoom, activation pulses, pheromone trails —
  untouched and primary. Chamber/Expanded remain as opt-in overview modes.
- One-time preference migration: v2.2.0/1 had persisted 'chamber' as everyone's stored default;
  reset once to the classic view, after which the operator's choice sticks.
- **System Core card fixed**: it was scraping state from the hidden legacy HUD panel's DOM and sat
  on a stale "idle" forever. It now computes state (IDLE / MISSION ACTIVE / OPERATOR ACTION /
  ALERT) from the live jobs/missions/approvals data each poll — same rules as the original core —
  and shows a state-colored pulsing orb (reduced-motion safe).
- **Overview condensed to 6 cards**: removed Tasks Today, Chamber Activity, Recent Events, and
  Mission Timeline (all visible in the Events / Colony / Missions tabs); Mission Queue folded into
  the Missions card. Overview now shows only cross-cutting status not duplicated elsewhere.
- Adopted phased colony evolution plan: classic graph is canonical (Phase 1); Living Colony map is
  an optional mode (Phase 2) fed by an adapter over live data (Phase 3); it becomes default only
  after functional parity (Phase 4).

## v2.2.1 — Fix: Colony map layout/pan-zoom + Overview de-duplication (live feedback on v2.2.0)

- **Colony map was crushed and immovable**: `#page-colony` is a flex ROW (for the original canvas
  layout), so the v2.2.0 map/toolbar/inspector were squeezed in as row items — tiny map, scattered
  controls. Chamber/Expanded views now switch the page to an immersive column layout: the map is
  big and front-and-center (`calc(100vh - 250px)`), the view toolbar is one compact row, the
  inspector is narrower and collapsible (⇤), and the original row panels hide. **Raw Graph
  restores the untouched original page exactly as before.**
- **Pan/zoom added**: scroll to zoom (cursor-anchored, clamped), drag the background to pan,
  Reset-view button; chamber clicks/double-clicks unaffected; no animation involved so
  reduced-motion is unaffected.
- **Overview had two System Cores and duplicated metrics**: the old HUD strip, HUD System Core
  panel, and HUD metric row are retired (their data lives on in the grid: telemetry bar, System
  Core card — which now also carries the live core state — Tasks Today, and Colony Health).
  Mission Command and Operator Attention remain below the grid; every handler and poller is
  untouched, only the duplicate presentation is hidden.

## v2.2.0 — 🐜 Overview + Colony living console + performance/auth/Proxmox stability pass

Four passes in one release. A/B/C deliver the Overview command center and the Living Colony Map;
D is the production-stability pass (auth floods, load times, Proxmox no-TLS, caching/polling).
Also renumbers the NORTH_STAR build order: the two unplanned insertions (v2.1.0 multi-hypervisor,
this release) shift the remaining planned phases by two minors (approval-gated actions → V2.3.0).

### Pass A — ANTHILL design system
- `:root` theme tokens (colony palette, full role-color system, status colors, glows) + glassy
  card/pill/role-badge primitives. `getRoleColor`/`getRoleLabel` are THE single role mapping for
  chambers, nodes, event dots, badges, trails, and legends.
- **TopTelemetryBar** on Overview and Colony: colony online state, task count with real
  delta-derived tasks/sec, success rate derived from the live event stream (1 − failures/events),
  active ant count from the registry, pending approvals, health pill, colony search. Every value
  is real or an em dash — nothing invented.

### Pass B — Overview command center
- 12-column responsive grid with all eleven required cards: Colony Health (real-signal scoring +
  session trend), Active Mission, Tasks Today (hourly bars from event timestamps), Pending
  Approvals (top 3 from the unified IApprovable queue, wired to the EXISTING doApproval
  handlers — approval security untouched), compact System Core (registry roles orbiting the
  Queen; click → Colony), Chamber Activity, Resource Usage ("Metrics unavailable" until real
  metrics exist), Recent Events, Quick Actions, Mission Timeline, Mission Queue.
- The existing HUD (core orb, attention panel, mission command node) is fully preserved below —
  every element id and handler intact. Clear empty states on every card.

### Pass C — Living Colony Map
- Chamber-based, Queen-centered SVG map over the deterministic normalized layout; chambers show
  role color, ant counts, active counts, representative ant dots (all ants when expanded).
- Three view modes: **Chamber / Expanded / Raw Graph** — Raw Graph is the untouched original
  canvas, so every ant remains reachable the old way too.
- Animated **pheromone trails**: width/opacity from real trail scores when present, otherwise
  derived from recent event frequency (mapping isolated in the loader, labeled as derived).
- **Inspector panel** for chambers (counts, active ants, top ants, Expand/Ant Config/Inspector),
  ants (chamber, status, recent events, Inspect/Logs), and trails (strength + recent flow).
- **ColonyMapControls** persisted in localStorage (`anthill.colony.*`): view mode, motion
  (Off/Low/Normal/High), labels, pheromone visibility, idle-ant toggle. Telemetry search finds any
  ant and jumps to its chamber. All motion honors `prefers-reduced-motion`.

### Pass D — performance, auth, Proxmox, stability
#### Fixed
- **Auth request floods / "Too many attempts; try again later"**: the first 401 now flips a
  global auth-lost gate — every poller short-circuits locally (zero network traffic) until
  re-login clears it. Text endpoints share the same gate.
- **Slow Overview / Patch Center loads + duplicate traffic**: identical in-flight GETs are
  deduplicated into one request; per-path TTL caching (events 3s, jobs 5s, summaries 10s,
  registry/pheromones 20–30s, patches 30s) with stale-while-error keeps cards rendering from
  cache while refreshing in the background.
- **Proxmox GET /nodes 401 in no-TLS/http mode**: the client hardcoded `https://`. New
  `homelab_proxmox_protocol` (http|https) is separate from TLS verification; auth headers attach
  identically in every mode; unknown protocols fall back to https.
#### Improved
- Hidden browser tabs serve cached data instead of polling; 429 responses trigger a respected
  Retry-After backoff (clamped 5–120s) instead of immediate retries; every request carries a 10s
  AbortController timeout with a structured error.
- Mutations (POST/PUT/DELETE) bust the GET cache so the UI never shows stale state after actions.
#### Added
- `POST /homelab/proxmox/test`: connection test with actionable diagnostics — distinguishes
  unreachable host / protocol mismatch / TLS-certificate issue / invalid credentials / permission
  denied / success (PVE version) — and never prints token material.
- `ProxmoxIntegrationTests`: protocol-selection tests (http BaseUrl, https default, junk-protocol
  fallback) and an explicit auth-header-over-http assertion.

## v2.1.0.1 — Allowlist + subsystem gates surfaced on the Virtualization Connections panel

Field fix: a real operator hit "Proxmox connected, credential configured, but no VMs/containers/storage."
Root cause was the **target allowlist** — a hard gate in front of every homelab request — and the v2.1.0
connection panel gave no way to see or fix it, so following the form (enable → host → credential → save
→ sync) failed silently once the sync hit the allowlist. Also the homelab **subsystem/scheduler master
gates** were "edit config.json" only.

- Each connection card now shows **host allowlist status** ("allowlisted" / "NOT allowlisted — requests
  are blocked") with a one-click **"Allow this host"** button (`POST /homelab/allowlist`). `VirtStatus`
  gained `host_allowlisted`.
- A **subsystem bar** at the top of the panel shows `homelab_enabled` / `scheduler_enabled` and lets an
  admin flip them (with a restart note, since scheduled syncs (de)register at startup). The unified
  status endpoint now returns those two gates.
- Result: hooking up a hypervisor is now type host → save cred → **Allow this host** → Sync — entirely in
  the panel, no config.json or curl. (The integrations themselves are unchanged and still read-only.)

## v2.1.0 — Multi-hypervisor read-only inventory + Virtualization Connections UI

Extends the read-only virtualization layer beyond Proxmox to **VMware ESXi/vCenter, Docker, and
Hyper-V**, and makes every integration configurable from the console (previously Proxmox could only be
set up by hand-editing `config.json`). Enterprise-geared and read-only end-to-end — every client is
read-only *by construction*, exactly like Proxmox.

- **New read-only clients + inventory providers** (each disabled by default, credential in the store,
  host gated by the target allowlist, `AllowAutoRedirect = false` SSRF hardening):
  - **Docker** — Engine API over TLS (or a read-only socket proxy). GET-only: no
    start/stop/kill/remove/exec exists in the client. Syncs the engine as a container-host node plus
    its containers and volumes.
  - **VMware ESXi / vCenter** — vSphere REST. The only non-GET is a single `POST /api/session` (auth
    only — mints a session token, changes nothing); all inventory reads are GET. A built-in Read-only
    role is enough. Syncs hosts → hypervisor nodes, VMs, and datastores.
  - **Hyper-V** — WinRM / WS-Management, restricted to the read-only WMI `Enumerate` of
    `Msvm_ComputerSystem` (no `Invoke`/`Put`/`Create`/`Delete`, no command shell). Syncs the host node
    and its VMs.
  - All four project into the same inventory tables through one `IInventoryProvider` shape and a
    unified `GET /homelab/virtualization/status` + `POST /homelab/virtualization/{kind}/sync`.
    Providers are built **on demand from current config**, so a connection edited in the UI works on
    the next sync without a restart.
- **Virtualization Connections panel** in the console: one card per integration (enable, host, port,
  credential id, skip-TLS, plus an inline write-only "Save cred"), Save + Sync-now, and live status
  (credential configured / active). Wire-level tests prove each client stays read-only (every request
  a GET / Enumerate; the vSphere session is the only POST) and that the allowlist blocks unlisted hosts.
- **Dependency graph** now renders hosts as **boxes** and services as **pills**, coloured by kind with
  a status-coloured border and a legend, so "host vs service" reads at a glance. (Delete-dependency and
  the full host/dependency tables already shipped in v2.0 — the delete `✕` and Actions column are live.)

## v2.0.0 — 🐜 Homelab Command Center launch (NORTH_STAR Phase 11)

The V2 era begins: everything the V1.9–V1.14 line taught ANTHILL to know, in one living console
view. Built in two deliberate passes (functional data layer first, identity layer second), still
read-mostly: visibility, not control. Answers the eight NORTH_STAR questions at a glance — what is
broken, where it runs, what it depends on, what changed, what to do next, what is not backed up,
what is exposed, what is unknown.

### Pass 1 — functional data layout & routing
- **One aggregation endpoint** `GET /homelab/dashboard`, assembled by the pure, testable
  `CommandCenter` builder: entity counts, latest-per-target health rollup, active incidents, open
  risk errors/warnings + top findings, storage used/total + backup-capable pool count, last
  health/proxmox/risk job stamps, pending-approvals count, failed checks, recent changes, the full
  dependency graph, and deterministic **"What Should I Do Next"** recommendations (derived only
  from real signals: failing targets, error incidents/findings, pending approvals, missing checks).
- **Dependency graph as a first-class feature**: nodes for every host and service (status from
  health data — `unknown` when unchecked, never assumed healthy), edges from implicit `runs_on`
  placement plus the mapped dependency table, **failure impact propagation** (a failed service
  marks its host worst-of and every touching edge impacted), exposure and open-incident flags per
  node, click-to-select highlighting connected paths and listing **transitive dependents** —
  "what depends on this?", answered visually and via `GET /homelab/graph/dependents/{id}`.
- **Host & service detail drawers**: facts, status, uses/depended-on-by, related active incidents
  and recent changes — opened by clicking any Hosts/Services row.
- Tests (`CommandCenterTests`): empty state fabricates nothing (stamps stay empty, approvals stay
  -1), aggregation faithfulness, graph edge construction, impact propagation through hosts and
  dependent paths, node flags, transitive dependents, recommendation determinism.

### Pass 2 — the ANTHILL identity layer
- **Centralized semantic tokens** (`#hl-theme` CSS variables): health `--hl-health`, compute
  `--hl-compute`, storage `--hl-storage`, security `--hl-security`, incidents `--hl-incident`,
  memory/history `--hl-memory` — applied as card spines + section-head dots via one decoration
  helper, consistent across chips, cards, and graph nodes.
- **Colony-mesh background**: a pure-CSS low-contrast node/tunnel lattice behind the dashboard
  (opacity ≤ .05, pointer-events none) — colony identity without touching readability.
- **Command summary strip**: KPI chips (hosts, services, healthy/degraded/failed, incidents,
  risks, VMs/CTs, storage+backup, pending approvals) with a **colony-link dot** derived strictly
  from real job stamps (green pulse = a scheduler job ran in the last 15 minutes; amber = idle;
  gray = never — labeled, never fabricated).
- **Purposeful motion only**: pulse on failed/incident graph nodes and the live dot, row-flash
  **connection cues** (click a failed check → related incidents flash; click a risk finding → the
  services it names flash), hover emphasis on graph nodes — all disabled under
  `prefers-reduced-motion`.
- Every new visual degrades to labeled empty states ("no data yet", "not configured", "no graph
  yet") — no value is ever invented for visual completeness.
- Page renamed **Homelab Command Center**; no framework migration, single embedded vanilla
  HTML/CSS/JS preserved, all existing routes/pages stable.

## v1.14.0.1 — Unified approvals dedupe: collapse older pendings even when the newest is resolved

Bug-finder/tester pass over the v1.14.0 code (last stop before V2.0). The incident/change-memory and
`IApprovable` design hold up well — deterministic, repo-only, correct SQL, well-tested. One real
logic bug in the unified-queue dedupe:

- **`ApprovableProjections.DedupePending`** (behind `GET /homelab/approvals/unified`) only superseded
  older pending duplicates when the *absolute newest* item in a dedupe group was itself pending
  (`ordered[0].State == "pending"`). If the newest item was already approved/rejected/executed while
  two older duplicates were still pending, **both** older items stayed pending — the unified queue
  would show two live pending approvals for the same target, violating the stated "at most one pending
  per key" invariant. Now it keeps the newest still-pending item and supersedes every older pending
  one regardless of the newest item's state. Added a regression test for the newest-non-pending case
  (the existing test only covered the newest-is-pending happy path).

Nothing else found: structural sweep clean (version consistency, all `.cs` balance, `node --check`,
ui-integrity), security sweep clean (TLS-bypass is Proxmox-only and config-gated, no secret logging,
SQL interpolation is table-names/constants only, all 116 endpoints auth-gated).

## v1.14.0 — Incident + change memory + the IApprovable design (NORTH_STAR Phase 10)

Phase 10 of the master roadmap — the final phase of the V1.x line. ANTHILL now connects failures
to recent changes and past fixes, and the unified approval abstraction that V2.1's actions build
on is designed, shipped, and test-reviewed. Incident tracking, timelines, and recommendations
only — nothing here can remediate anything.

### Incident + change memory
- **Auto-opened incidents**: the `incident-sweep` scheduler job turns the health system's
  `incident_candidate` events (3 consecutive failures) into incidents, deduped per subject —
  one open incident per failing thing, re-sweeps never duplicate. Manual opening via API/UI too.
- **Incident timeline**: reconstructs everything around an incident — `change_log` entries from
  the 24h lookback window before it opened are flagged **SUSPECT** ("what changed right before it
  broke"), plus correlated homelab events and per-target health results through resolution,
  chronologically ordered.
- **Similar incidents + fix memory**: deterministic scoring (token overlap + same-subject/kind
  bonuses) over past incidents; resolved matches carry their root cause verbatim as
  *"this fixed it last time"*. Resolving with a root cause writes an `incident_fix_recorded`
  event — the durable memory future incidents draw on.
- **Repeated-failure patterns**: a subject producing 3+ incidents in 14 days is pattern-flagged
  (`incident_pattern` event) and its new incidents open at error severity.
- **API**: `GET|POST /homelab/incidents`, `GET /homelab/incidents/{id}/timeline`,
  `GET /homelab/incidents/{id}/similar`, `POST /homelab/incidents/{id}/status`
  (open|investigating|resolved + root cause).
- **UI**: Incidents panel on the Homelab page — severity/status tables, a detail drawer with the
  suspect-flagged timeline and similar-incident fix suggestions, resolve-with-root-cause flow,
  and manual incident opening.

### IApprovable (designed before V2.1, per the roadmap)
- **`IApprovable`** interface + `ApprovableView` projection: ONE pending queue, ONE lifecycle
  (pending → approved → executed / rejected / superseded; execution never from pending), ONE
  dedupe rule (equal DedupeKeys can't both be pending; newer supersedes), per-kind renderers
  (`patch_diff` today; `action_proposal` V2.1; `network_preview` V2.4).
- **`GET /homelab/approvals/unified`**: today's patch approvals projected into the unified queue
  via a read adapter over `approval_requests` — no new table, no migration, existing decision
  endpoints untouched.
- **`ActionProposal` skeleton** (deliberately inert: no executor exists, nothing constructs one,
  risk defaults `high`): carries the Phase 12 blast-radius rubric inputs (dependency fan-out,
  criticality, backup coverage, exposure, rollback note, dry-run availability) so V2.1 implements
  against reviewed fields.
- **`docs/APPROVALS.md`**: the canonical design doc — lifecycle, dedupe, renderer table, and the
  five execution requirements V2.1 is bound to (separate approve/execute permissions, state
  re-checks, HOMELAB_STOP, audit events, forbidden-actions enforcement in the executor).

### Tests
- `IncidentMemoryTests`: per-subject dedupe across resolve cycles, idempotent candidate sweep,
  repeat-offender severity upgrade + pattern event, timeline suspect flagging + chronological
  order + subject correlation, similar-incident ranking with verbatim fix surfacing, resolve
  validation + fix-memory event, and the IApprovable design review (faithful patch projection,
  supersede-on-dedupe, inert fail-safe ActionProposal).

## v1.13.0 — Network + security awareness (NORTH_STAR Phase 9)

Phase 9 of the master roadmap: understand the network shape and the obvious risks. Awareness and
reporting only — no firewall/DNS/DHCP writes, and stronger: **zero network I/O**. Active scanning
does not exist in this phase; if it ever arrives it ships disabled-by-default behind the target
allowlist like every other prober.

- **`RiskAnalyzer`** — deterministic rules over inventory ANTHILL already knows, producing all nine
  NORTH_STAR findings: `risky_open_port` (legacy/cleartext ports; severity upgrades to error when
  internet-exposed), `unknown_device`, `ownerless_service`, `un_backed_up_host` (workloads with no
  backup-capable storage anywhere), `exposed_dashboard` (admin surfaces reachable from the
  internet), `duplicate_ip` (across hosts AND network devices), `missing_dns_name`,
  `service_without_health_check`, and `credential_never_verified`.
- **Stable-id reconciliation**: findings upsert by `risk:{kind}:{subject}`, so re-analysis never
  duplicates, **fixed problems auto-resolve**, and operator **acknowledgements survive re-runs**
  (and still auto-resolve when the underlying issue is actually fixed).
- **Network-device registry** (manual/import only): name/kind/MAC/IP/VLAN/known-flag/notes with
  first/last-seen stamps; unknown devices become findings; devices ride the inventory
  import/export bundle.
- **Scheduler**: `risk-analysis` job on the shared scheduler (`homelab_risk_interval_seconds`,
  default hourly) — repo-only work, safe at any cadence.
- **API**: `GET|POST|DELETE /homelab/devices`, `GET /homelab/risks`,
  `POST /homelab/risks/analyze` (run now), `POST /homelab/risks/{id}/ack`.
- **UI**: Network & Risk section on the Homelab page — device registration + table with unknown
  flagging, findings table with severity coloring/KPI counts, Analyze Now, and per-finding Ack.
- **Tests** (`RiskAwarenessTests`, socket-free by construction): every finding rule, exposure
  classification, duplicate-IP detection, watched-service suppression, un-backed-up-host
  resolve-on-fix, stable reconciliation with sticky acks, the scheduler adapter, and device
  import/export round-trip.

## v1.12.0.1 — Proxmox client: don't follow redirects (SSRF hardening)

Bug-finder/tester pass over the v1.12.0 Proxmox integration. The rest of it holds up well —
GET-only by construction, target-allowlist gate before every request, token pulled from the
credential store per call and never logged, defensive JSON parsing, `INSERT OR IGNORE` event dedup.
One defense-in-depth gap:

- **`ProxmoxApiClient` followed HTTP redirects** (`AllowAutoRedirect` left at the .NET default of
  `true` on both the verified and insecure handlers). The allowlist gate validates the configured
  host, but a `3xx` from a compromised or misconfigured node would bounce the authenticated GET to a
  `Location` that was never allowlist-checked — an SSRF hole straight through the integration's
  "safety by construction" premise. Both handlers now set `AllowAutoRedirect = false`; the PVE API
  never legitimately redirects, so a redirect surfaces as a clean non-success status instead of being
  chased off-allowlist. Added a wire-level regression test (mock 302 → `Location` to a dead off-host
  port; asserts the client fails clean with `HTTP 302` and never requests the redirect target).

## v1.12.0 — Proxmox read-only integration (NORTH_STAR Phase 8)

Phase 8 of the master roadmap: ANTHILL connects to Proxmox safely, in read-only mode. There is no
start/stop/reboot/migrate/delete/clone/resize/config-write path anywhere in this integration.

- **GET-only `ProxmoxApiClient`** — write operations are *structurally impossible*: the class has
  no POST/PUT/DELETE code at all. Proven twice in tests: the public type surface exposes only
  `Get*` methods, and a mock PVE server asserts every wire request is a GET. Allowlist check (D1)
  and credential lookup happen before any request; strict per-request timeout; TLS verification is
  config-controlled (`homelab_proxmox_insecure_tls` for self-signed homelab certs, default verify).
- **`ProxmoxInventoryProvider`** (riding the shared scheduler as `proxmox-sync`): syncs nodes (as
  `hypervisor` hosts tagged `proxmox` with status/CPU/RAM/uptime), QEMU VMs (vmid, status, vCPU,
  RAM, uptime), LXC containers, storage pools (with backup-capable flagging + used/total bytes),
  and failed Proxmox tasks — recorded as `proxmox_task_failed` events with stable UPID ids so
  re-syncs never duplicate (RecordEvent is now INSERT OR IGNORE). All upserts use stable ids —
  re-sync is idempotent.
- **`ProxmoxHealthProvider`**: GET /version reachability check for the health system.
- **Credentials**: the API token lives in the homelab credential store
  (`homelab_proxmox_credential_id`, default `proxmox-main`; save as `user@realm!tokenid=SECRET`),
  is fetched per sync with an audited use, flows only into the PVE Authorization header, and is
  proven absent from events, changes, inventory, statuses, and export bundles. A read-only
  PVEAuditor token is all it needs — matching the integration's own permissions.
- **Repository**: `UpsertVm/ListVms`, `UpsertContainer/ListContainers`,
  `UpsertStoragePool/ListStoragePools` fill the v1.9.0 `vm_inventory`, `container_inventory`, and
  `storage_inventory` tables for the first time.
- **API**: `GET /homelab/vms`, `GET /homelab/containers`, `GET /homelab/storage`,
  `GET /homelab/proxmox/status` (secret-free), `POST /homelab/proxmox/sync` (manage-gated run-now).
- **UI**: Virtualization section on the Homelab page — Proxmox status card with setup hints and
  Sync Now, VM/container tables with running-state coloring, storage pools with usage-percent
  coloring (green/amber/red at 75/90%).
- **Config**: `homelab_proxmox_enabled` (off), `homelab_proxmox_host`, `homelab_proxmox_port`,
  `homelab_proxmox_credential_id`, `homelab_proxmox_insecure_tls`,
  `homelab_proxmox_sync_interval_seconds` — all operator-editable and in the settings snapshot.
- **Tests** (`ProxmoxIntegrationTests`, mock PVE API on loopback): no-write type-surface + wire
  proofs, allowlist blocks with zero requests, missing-credential clean failure, full sync
  population, idempotent re-sync, HTTP-500 soft failure, hung-server timeout bound, credential
  redaction sweep, and health-provider healthy/failed paths.
- Deferred to V2.2 (backup intelligence): per-VM snapshot detail and deep backup inspection.

## v1.11.0.2 — Replace blocking native dialogs with an in-app modal

Fix: the console used native `window.confirm()`/`prompt()` for every destructive action (Stop the
Director, reject/apply patches, flush cache, reset settings, delete objectives/users, restart the
service, prune pheromones, etc.). Native dialogs **block the renderer's main thread** until
dismissed — which is what hung the Autonomy **Stop** button (the click froze the page until the
modal was cleared), and they also look out of place in the custom HUD and break automated testing.

- New promise-based `uiConfirm()` / `uiPrompt()` — themed, non-blocking, keyboard-navigable
  (Enter = confirm, Esc / backdrop = cancel), with an optional danger style for destructive actions.
- All 18 native `confirm()`/`prompt()` call sites migrated (handlers made `async` where needed).
  Behavior is unchanged (default is still cancel); only the blocking + styling changed.

## v1.11.0.1 — Auto-apply observability + auth-redirect hardening

Two fixes surfaced while live-verifying the autonomous auto-apply → git loop end-to-end on the LXC
(the loop itself works: a verified patch applied, committed to the standalone `<username>-anthill`
branch, synced origin/main into it, and pushed — never touching main).

- **Auto-apply git step is now logged.** Previously only the *failure* path emitted an event, so a
  successful commit/push was invisible in the Event Log — from the UI it looked like the loop applied
  and verified but never committed (it had). `AutoApplyRunner` now emits
  `autonomy_autoapply_committed` on success, naming the commit sha, branch, files, and push result
  (`pushed to <remote>/<branch>` / `push failed …` / `push disabled`), so the git step is visible and
  searchable.
- **UI reliably bounces to login on a 401.** `onUnauthorized` early-returned after the first 401, so
  if a session went invalid mid-flight (e.g. the server rotating its session secret during a
  redeploy) the console could stay stuck half-loaded behind failing background polls instead of
  redirecting. It now re-asserts the login screen on any 401 while the app shell is still visible,
  without re-running once already on login.

## v1.11.0 — Health checks + notifications (NORTH_STAR Phase 7)

Phase 7 of the master roadmap: ANTHILL can tell what is alive, degraded, or broken. Awareness and
reporting only — there is no auto-remediation anywhere in this subsystem.

- **`HealthCheckRunner`** (deterministic C#, never routed through the model router): ping, HTTP
  status (200s healthy / 4xx degraded / 5xx failed), TCP port, service-URL checks, plus disk and
  uptime placeholders that report `unknown` until agent support lands. Every check must pass the
  Homelab Target Allowlist (D1) **before any I/O**, runs under a strict per-check timeout
  (`homelab_health_timeout_ms`, per-schedule override) so a hung host can never hang the app, and
  persists a `HealthCheckResult` with latency + detail.
- **Failure alerting**: each failed check writes a `health_check_failed` event; 3 consecutive
  failures of one target promote it to a single **`incident_candidate`** event (fires once per
  streak) — groundwork for V1.14's incident memory.
- **`NotificationService`** (config-gated, OFF by default): Slack, Discord, and generic JSON
  webhooks; fires on health-check failures, incident candidates, and operator tests. Strict
  timeouts, soft failure, and every send attempt audited as a homelab event that never contains a
  webhook URL or any secret.
- **Scheduler wiring**: one `health-checks` job on the shared `HomelabScheduler`
  (`homelab_health_interval_seconds`, default 60s) — no per-subsystem timers. Mock providers now
  register only when their own gate is on; the scheduler starts whenever it has jobs.
- **Operator-managed schedules**: new `health_check_schedules` table with CRUD + ChangeRecords.
- **API**: `GET /homelab/health/summary` (latest-per-target rollup), `GET /homelab/health/results`,
  `GET|POST|DELETE /homelab/health/schedules`, `POST /homelab/health/run` (run everything now),
  `POST /homelab/notifications/test`. Reads = `read_homelab`, writes = `manage_homelab_integrations`.
- **UI**: Health panel on the Homelab page — add/run/delete checks, healthy/degraded/failed/unknown
  KPI line, last status/latency/detail per check, and a Test Notify button.
- **Config**: `homelab_health_interval_seconds`, `homelab_health_timeout_ms`,
  `homelab_notifications_enabled`, `homelab_slack_webhook`, `homelab_discord_webhook`,
  `homelab_generic_webhook` — all operator-editable, all conservative/off by default.
- **Tests** (`HealthAndNotificationTests`, all on loopback sockets — zero external network):
  host extraction, allowlist-blocks-before-I/O, HTTP 200/404/500 classification, TCP open/closed/
  malformed, hung-server timeout bound, placeholder kinds, incident-candidate streak, notifications
  disabled-by-default / delivery + URL-free audit / unreachable-webhook soft-fail, latest-per-target
  summary, and schedule CRUD persistence across reopen.

## v1.10.0 — Inventory + service registry with Homelab console page (NORTH_STAR Phase 6)

Phase 6 of the master roadmap: ANTHILL knows what exists. Manual/import-based only — no active
scanning. Plus two operator-facing fixes found in live testing.

### Inventory + service registry
- **Dependency mapping**: `dependencies` CRUD in `HomelabRepository` with ChangeRecords, answering
  "what runs where?" and "what depends on this?" (service→host `runs_on`, `needs`, `stores_on`).
- **Import/export**: `GET /homelab/export` / `POST /homelab/import` round-trip nodes + services +
  dependencies as one JSON bundle. Import is upsert-by-id, so re-importing an export is idempotent;
  invalid records are skipped; credentials and allowlist entries are never part of the bundle.
- **API completion** (per NORTH_STAR): `PUT /homelab/hosts/{id}`, `PUT /homelab/services/{id}`,
  `GET|POST|DELETE /homelab/dependencies`. Reads = `read_homelab`; writes = `manage_homelab_integrations`.
- **New console page: Homelab Inventory** (visible to admins and homelab operators; write forms
  admin-only): Subsystem Status, Hosts, Services, Open Ports (derived from services), Dependencies,
  Recent Changes panels, host/service/dependency registration forms, and JSON export/import buttons.
- Homelab gates (`homelab_enabled`, `homelab_scheduler_enabled`, `homelab_mock_providers_enabled`,
  `homelab_max_concurrent_checks`) are now operator-editable settings and appear in the settings
  snapshot — no more hand-editing config.json.

### Fixes
- **LXC deployments silently froze on old versions (the "header says v1.8.26" bug).** The
  `setup.sh` upgrade path ran `git pull --ff-only` on whatever branch the build checkout was on;
  since the auto-apply git integration (v1.8.26) that checkout can end up parked on the standalone
  `<username>-anthill` branch, so every upgrade re-run rebuilt stale code while releases moved on.
  The upgrade path now forces the build checkout to `origin/main`
  (`git fetch` + `git checkout -B main origin/main`), logs exactly which version+commit it is
  building, and after the service restarts it polls `/health` and **fails loudly on a
  built-vs-running version mismatch** — a stale deployment can never look healthy again. (The UI
  header renders the `/health` version since v1.9.1.1, so header == running binary, always.)
- **Patch Center "Apply" always returned 403.** The API capability gate `apply_patch` shipped as a
  static `false` and was never projected from `patch_application_enabled`, so `POST /apply/{id}`
  answered `permission_denied` even after the operator enabled patch application in Settings. The
  gate now follows the setting at boot and on live settings updates (`PatchApplyGateTests`), and the
  Patch Center error toast now surfaces the server's actual reason plus the fix
  ("enable Patch application in Settings") instead of a bare HTTP code.
- The `homelab_operator` role now renders correctly in the nav footer and sees the Homelab page.

### Tests
- `PatchApplyGateTests` (gate follows setting, homelab keys editable/snapshotted),
  `InventoryRegistryTests` (dependency CRUD + change records, export/import round-trip into an
  empty DB, idempotent re-import, invalid-record skipping, exports never contain credential or
  allowlist material).

## v1.9.1.1 — Fix: UI header/title version drift (hardcoded markup)

The console title, login logo, and nav header displayed a hardcoded version (`v1.8.29.1`) that had
silently drifted from the runtime version — release bumps only covered the four canonical markers
(runtime const, Directory.Build.props, README, CHANGELOG), not markup literals.

- The UI now fetches the version from the public `/health` endpoint at boot (`bootVersion()`) and
  renders it into the title, login logo, and nav header — `AnthillRuntime.Version` is the single
  source of truth; the markup carries no literal version anywhere.
- New regression guard (`UiIntegrity_NoHardcodedVersionInMarkup`): fails `dotnet test`/CI if any
  `>vX.Y.Z<` literal or versioned `<title>` ever reappears in `index.html`.

## v1.9.1 — Homelab scheduler + mock-provider harness (NORTH_STAR Phase 5)

Phase 5 of the master roadmap: one shared execution/testing pattern for every future homelab
provider. Still read-only, still zero real network calls, still disabled by default.

- **Five mock providers** (`FakeProxmoxProvider`, `FakeDnsProvider`, `FakeDhcpProvider`,
  `FakeFirewallProvider`, `FakeHealthProvider`) built on a shared `FakeHomelabProvider` base:
  deterministic item counts, simulated latency, scriptable failure injection, thread-safe
  secret-free `HomelabProviderStatus`, and an audit `provider_run` event per run.
- **Target-allowlist discipline baked into the base class**: a provider with a target host
  consults `IHomelabTargetGuard` before doing anything and fails cleanly when the host is not
  allowlisted — the exact D1 wiring real providers inherit.
- **Scheduler wiring**: the five mocks register as `HomelabScheduler` jobs at boot but only run
  when BOTH `homelab_scheduler_enabled` AND the new `homelab_mock_providers_enabled` gate are true
  (both default false). Jitter, per-failure exponential backoff, the global concurrency cap, and
  restart-surviving job state all exercised end-to-end.
- **API**: new `GET /homelab/providers` (secret-free statuses, `read_homelab`); `/homelab/summary`
  now includes the provider list.
- **Shared mock-provider test harness** (`MockProviderHarnessTests`): one `[MemberData]` fixture
  runs every provider through identical assertions — success/status consistency, failure streak +
  recovery, allowlist gating, disabled-provider behavior — plus scheduler proofs for the Phase 5
  validation list: run-all, backoff growth/reset, concurrency cap (no stampede), background
  start/stop, and job-state persistence. Real providers from v1.10+ join by adding a factory line.

## v1.9.0 — Homelab foundation (NORTH_STAR Phase 4)

Phase 4 of the master roadmap and the start of the V1.9.x homelab line: the read-only backend
foundation. Nothing in this release can control infrastructure — no Proxmox control, no firewall
changes, no SSH execution, no destructive actions. Everything ships disabled by default.

- **Models + persistence.** 16 homelab record types and 15 new SQLite tables (`homelab_nodes`,
  `network_devices`, `services`, `vm_inventory`, `container_inventory`, `storage_inventory`,
  `backup_inventory`, `health_checks`, `homelab_events`, `change_log`, `incidents`, `dependencies`,
  `risk_records`, `homelab_credentials`, `homelab_target_allowlist`) in the existing colony DB via
  the new `HomelabRepository` (idempotent schema init; every inventory write logs a `ChangeRecord`).
- **Interfaces** for all future integrations: `IInventoryProvider`, `IHealthCheckProvider`,
  `IHomelabEventSink`, `IHomelabRepository`, `IIntegrationStatusProvider`, `IHomelabTargetGuard`,
  `ICredentialProvider`.
- **Homelab Target Allowlist (D1).** `HomelabTargetGuard`: deterministic providers may only reach
  operator-allowlisted targets (exact hostname / exact IP / IPv4 CIDR, no DNS resolution). Fully
  isolated from the general SSRF guard — `UrlSafety` still blocks private/loopback for LLM-directed
  tools, proven by tests in both directions.
- **Credential store (D2).** `HomelabCredentialStore` on the existing `FieldCipher`: secrets are
  write-only via the API, statuses expose only configured/last_verified, and every secret use
  writes an audit `homelab_events` row.
- **Homelab permission tier (D3).** New permissions `read_homelab`, `manage_homelab_integrations`,
  `approve_homelab_actions`, `execute_homelab_actions` (the two action gates ship capability-OFF
  until V2.1) and a new `homelab_operator` role: view + approve, never manage/execute/admin.
- **Scheduler skeleton (D4).** `HomelabScheduler`: jittered intervals (no check stampede),
  exponential backoff on consecutive failures, global concurrency cap, last-run/last-result
  persisted (survives restart). Disabled by default; registers no jobs in v1.9.0.
- **Read-only homelab ants** (visible-only, never executable, never patch-capable): InventoryAnt,
  NetworkScoutAnt, HealthAnt, ProxmoxAnt, StorageAnt, BackupAnt, SecurityScoutAnt,
  ChangeArchivistAnt.
- **API** (permission-scoped, secrets never returned): `GET /homelab/summary`, `GET|POST
  /homelab/hosts`, `GET|POST /homelab/services`, `GET /homelab/events`, `GET /homelab/changes`,
  `GET|POST|DELETE /homelab/allowlist`, `GET|POST|DELETE /homelab/credentials`.
- **Config**: `homelab_enabled`, `homelab_scheduler_enabled`, `homelab_max_concurrent_checks`
  (all off/conservative by default) + config.example.json documentation.
- **Docs**: new `docs/HOMELAB.md` (canonical homelab design doc, D10) with phase status at top;
  reserved backend folders carry phase-pointer READMEs.
- **Tests**: new `tests/Anthill.Tests.Homelab` project — migration idempotence (fresh/existing/
  re-run + coexistence with colony memory), allowlist matching + SSRF isolation, credential
  save/use/verify/remove with audit and redaction, scheduler run/backoff/persistence, ant-registry
  shape, and the D3 permission matrix.

## v1.8.29.1 — Auto-apply: coder add-vs-modify, default paths, and LXC provisioning

Makes the autonomous auto-apply → git loop work end-to-end on a fresh LXC install, removing the
manual steps and the last blockers hit during live testing.

- **Coder add-vs-modify** (`Ants.cs` + `Tools.cs`): the loop stalled whenever the coder proposed
  `change_type: add` for a file that already exists (a common LLM slip) — `ApplyPatchTool`
  hard-refused, so the patch never applied. The coder prompt now chooses `add`/`modify` by whether
  the target already exists, and an `add` to an existing path is applied as a backed-up full-file
  overwrite (`add_overwrite`) instead of failing. Fully reversible: the pre-apply backup, verify +
  rollback, and standalone-branch-never-main gate all still apply.
- **Default auto-apply paths** (`AnthillRuntime.cs` + UI): enabling auto-apply with an empty path
  allowlist was a silent no-op (empty allowlist = nothing eligible). Turning it on now seeds a
  starter allowlist of `docs/**` and `src/**`, persisted to config so it shows up pre-filled in
  Settings → Security and can be edited or removed like any operator entry. Never overrides paths
  the operator already set; never seeded while auto-apply is off. The UI also pre-fills the box the
  moment the toggle is switched on.
- **LXC provisioning** (`deploy/lxc/setup.sh` + service template): setup.sh now provisions the
  agent workspace as a git checkout under `.anthill/workspace` (already writable via the unit's
  `ReadWritePaths=.anthill`), sets the service user's git identity + `safe.directory`, checks out
  the standalone `<username>-anthill` branch on re-runs where a username is configured, and creates
  a private `.ssh` deploy-key slot (700; the key is provided by the operator and referenced by path,
  never generated or stored). Idempotent, so it doubles as the upgrade path. End users no longer do
  any of this by hand.

## v1.8.29 — Fresh-install training + pheromone bootstrap missions (NORTH_STAR Phase 3)

Phase 3 of the master roadmap: give fresh installs a repeatable, read-only way to learn the repo,
roles, workflow, UI, memory system, and V2 roadmap before doing real patch missions. Docs only —
no runtime behavior change.

- New **`docs/TRAINING_MISSIONS.md`** — a nine-mission training pack (Repo Orientation, Ant Role
  Training, Build/Test Workflow, UI Structure, Memory + Pheromone System, Patch Proposal
  Discipline, Failure Drill, V2 Homelab Roadmap, Daily Memory Compression) with copy-paste goal
  text for each.
- Every goal embeds the exact `MissionConstraints` phrases (`read-only`, `do not modify files`,
  `one-shot`) so the v1.8.16 constraint enforcement strips coder patch tasks at planning time —
  training can never produce patch proposals.
- Operator instructions: run order, Preview Plan verification, memory/pheromone checks afterward,
  and when to re-run the pack (fresh install, major version jump, after Clear Missions).
- Documents the recurring **memory-compression pattern**: mission 9 doubles as a daily/periodic
  compression template, runnable manually or as a low-priority recurring objective.

## v1.8.28 — Validation / regression harness hardening (NORTH_STAR Phase 2)

Phase 2 of the master roadmap: lock in regression protection for every bug class that has already
shipped once, before homelab complexity lands. Validation/CI/test changes only — no product
behavior change.

- **Centralized validation commands**: new `scripts/validate.sh` and `scripts/validate.ps1` run the
  full required validation set (restore → Release build → Release test, `--full`/`-Full` adds
  self-contained publish + `--selftest`, plus `node --check` on the embedded UI JS when node is
  available). CI runs the same steps.
- **New `RegressionGuardTests`** (run in plain `dotnet test`, so local work and CI gate identically):
  - *Version-marker consistency*: `AnthillRuntime.Version` must match `Directory.Build.props`
    `<AnthillVersion>`, the README "Current version" line, and a matching `## vX.Y.Z` CHANGELOG
    entry. (Directory.Build.props had silently drifted to 1.8.15.6 since v1.8.15.6 — fixed.)
  - *Migration idempotence*: fresh DB, reopen of an existing DB, and repeated re-runs of schema
    init all pass with an identical table set.
  - *UI glyph/encoding integrity*: the CI-only corruption checks (U+FFFD, flattened `?` icons,
    `'?':'?'` caret ternaries) now also run as unit tests.
  - *No-Python guard*: no `.py` file may exist outside archived `py.old/`.
- **CI hardening**: `Docs + version consistency` step extended to cover Directory.Build.props and
  the CHANGELOG entry; new `repo-guards` job fails any PR that touches `py.old/` and any commit
  that adds Python outside it.
- Assembly/package version now correctly stamps as the real release version (was 1.8.15.6).

## v1.8.27 — Roadmap / documentation consolidation (NORTH_STAR)

Phase 1 of the master roadmap: stop roadmap drift by making one canonical direction document.

- New **`docs/NORTH_STAR.md`** — the single, ordered build order from the current baseline (v1.8.26)
  through the V2 Homelab Command Center and V3 bounded autonomous operator, plus the non-negotiable
  safety/architecture rules, the global bug-prevention gates, and the version-completion template.
- `docs/ROADMAP.md`, `docs/UI_ROADMAP.md`, and `docs/AUTONOMY.md` now carry a status block marking them
  as retained subsystem history and pointing to `NORTH_STAR.md`.
- README links `NORTH_STAR.md` from the version notes and adds a v1.8.27 changelog row.
- Docs only; no runtime behavior change.

## v1.8.26.1 — Harden auto-apply git for the systemd sandbox

Two fixes found while bringing the v1.8.26 loop up on a hardened LXC (`ProtectSystem=strict`):

- **Commit identity inline.** The service user (`anthill`) has no global git identity, so `git commit`
  failed with "Please tell me who you are." The commit now sets it inline —
  `git -c user.name="ANTHILL Auto-Apply" -c user.email="anthill@localhost" commit` — so it never
  depends on host git config.
- **Writable `known_hosts`.** `ssh` records the remote host key on first connect, but the service
  user's `~/.ssh` is read-only under `ProtectSystem=strict`. `GIT_SSH_COMMAND` now points
  `UserKnownHostsFile` at `/tmp/anthill_known_hosts` (writable via `PrivateTmp`, per-service), so the
  push succeeds without adding `.ssh` to `ReadWritePaths`.

Note: a non-`.anthill` auto-apply workspace still needs a systemd drop-in adding it to
`ReadWritePaths` (the sandbox mounts everything else read-only), and the workspace must be a clone
owned by the service user, checked out on the `<username>-anthill` branch.

## v1.8.26 — Auto-apply git integration (standalone branch, never main)

Expands the "Git-commit verified changes" toggle into a real, safety-gated git workflow for the
Director's auto-apply. After a green verify, ANTHILL commits the applied files to a standalone branch
and can push it for review — **without ever touching main**.

- New config: `autonomy_autoapply_git_username` (→ branch `<username>-anthill`),
  `autonomy_autoapply_git_remote` (default `origin`), `autonomy_autoapply_git_ssh_key_path`,
  `autonomy_autoapply_git_push`. Surfaced in **Security → Autonomous Auto-Apply** (username field shows
  the resulting branch; remote; SSH key path; "Push branch to origin" toggle).
- **SSH deploy key by reference:** the key is used via `GIT_SSH_COMMAND="ssh -i <path> …"`. Only the
  *path* is stored/shown; no key material is ever read into config, DB, UI, logs, or events.
- **Flow (per kept auto-apply):** verify the workspace is on `<username>-anthill` (create/checkout is
  a one-time operator step) → `git add`/`commit` the applied files → if push is on, `git fetch` +
  merge `origin/main` **into** the branch (one-way sync) → `git push <remote> <branch>` via the key.
- **Hard main-safety:** refuses to commit if the workspace is on `main`/`master`; only ever commits
  and pushes the standalone branch; never merges the branch into main; never force-pushes;
  fail-closed (a git error keeps the change on disk and logs `autonomy_autoapply_git_failed`).
- Open PRs from the pushed branch on GitHub; filing PRs/issues from ANTHILL needs the GitHub API
  (a token) and is a separate follow-up, out of scope for an SSH deploy key.

**Operator setup (one-time, on the host clone):** create the deploy key, add its public half to the
repo (Settings → Deploy keys, allow write), then `cd <workspace> && git checkout -b <username>-anthill
origin/main`. Point the SSH key path setting at the private key.

## v1.8.25.4 — Auto-Apply Security toggles never saved

The two Autonomous Auto-Apply toggles — "Enable auto-apply" (`autonomy_autoapply_enabled`) and
"Git-commit verified changes" (`autonomy_autoapply_git_commit`) — render in their own containers
(`#sec-autoapply-toggle`, `#sec-autoapply-git`), but `saveSecurity()` only harvested toggle state
from `#sec-toggles` and `#sec-shell-toggle`. So both toggles flipped visually and then silently
dropped out of the save payload. Added their containers to the collector; both persist now.

## v1.8.25.3 — Approved patches were un-appliable

Found during live V&V of the Patch Center. Approving a patch flipped only the *approval record* to
`approved` — nothing ever set the patch itself to `PatchStatus.Approved`. The Patch Center gates its
Apply action on the patch status being `approved`, so **Apply never appeared after approval** and
approved patches could not be applied through the UI (true for both the normal flow and the operator
approve-by-patch-id path).

- `ApproveRequest` now flips the patch to `approved` for a `patch_proposal` approval, mirroring the
  reject path (which already set the patch to `rejected`).
- The Patch Center's `canApply` also honors `approval_status === 'approved'`, so patches approved
  before this fix (approval approved but patch still `proposed`) are appliable too.

Apply still respects the write gates (`patch_application_enabled` / `file_writing_enabled`).

## v1.8.25.2 — CI guard against UI glyph corruption

The console UI has been re-saved as non-UTF-8 several times, flattening icon glyphs to `?` and other
glyphs to the U+FFFD replacement char (`�`). Adds a **`ui-integrity` CI job** that fails the build on
any `�`, bare `>?<` icon, `>? Label` button, or `'?':'?'` caret in `index.html` (the legitimate
`<kbd>?</kbd>` help key is allowlisted), plus a `node --check` of the embedded JavaScript — so this
recurring corruption can never merge again. CI-only; no runtime change.

## v1.8.25.1 — Console glyph-corruption repair

A follow-on to the v1.8.23.1 encoding repair, which only caught labeled buttons (`>? Label`) and
U+FFFD (`�`) characters. This pass fixes the icon-only glyphs that were also flattened to `?` and had
survived into the mainline:

- 19 `>?<` markup icons restored from the last clean revision: collapse buttons and expand carets
  (`▾`), mission-dispatch buttons (`▶`), the results-close button (`✕`), the full-event-log button
  (`⛶`), and the pheromone-table success/failure headers (`✓` / `✕`).
- 4 JS-literal expand/collapse carets (`det.open?'?':'?'` / `hidden?'?':'?'`) → `▾` / `▸`.
- The apply-warning prefix (`⚠`) and the nav "autonomy running" badge (`●`).
- The legitimate `?` help-shortcut key (`<kbd>?</kbd>`, from the v1.8.25 Command Center) is preserved.

No behavior change; embedded UI JavaScript still parses cleanly.

## v1.8.25 — UI Phase 10: Full Command Center Polish

Finishes the UI roadmap (all 10 phases now shipped). Everything is additive vanilla JS/CSS inside
the embedded console; no backend changes.

- **Command palette (Ctrl+K):** fuzzy-matched pages and actions (new mission, toggle nav, pending
  approvals, shortcuts, tour), recents boosted, arrow-key navigation. Ctrl+K previously jumped to
  the mission input — "New mission" is now the top palette action, one Enter away.
- **Global search:** typing 2+ characters in the palette also searches mission memory
  (`/memory/explorer`) — missions, tasks, patches, and sources deep-link to Results / Patch Center.
- **Notification center:** a header bell collects notable colony activity (mission complete/failed,
  patch applied/verified/failed, approvals, auto-apply outcomes) from the existing event feed, with
  an unread badge and per-item deep links. No new polling.
- **Keyboard shortcuts:** `g` then a letter jumps between pages (g o / g c / g m / g r / g e, plus
  admin g p / g b / g s / g u / g a), `?` opens a shortcuts reference, Esc closes overlays.
- **Saved layouts:** the console reopens on the page you left, alongside the existing persisted nav
  collapse, card collapse, and Patch Center grouping state.
- **Onboarding tour:** a five-step first-login walkthrough (dispatch → patch review → memory →
  shortcuts); skippable, never auto-shows again, restartable from the palette.
- Reduced-motion aware; role-gated (coordinators see no admin pages in palette, search, or g-nav).

## v1.8.24 — UI Phase 7: Visual Patch Center 2.0

Finishes UI Roadmap Phase 7 — grouping — and closes the operator gaps around pending patches.

**Grouping**
- New "Group by" control (status / risk / file / mission / objective); choice persists.
- Collapsible group sections with patch counts and per-status mini-chips; status/risk groups sort
  logically, the rest by size. Pure client-side re-render — filters, diffs, and actions unchanged.

**Operator approval for orphaned pending patches**
- Some pending patches had no approval record (deduped duplicates, pre-v1.8.16 history), so they
  were visible but impossible to act on. New `POST /patches/{id}/approve` and
  `POST /patches/{id}/reject` create the missing approval record first, then run the exact same
  Queen approve/reject transition — never a direct status write. Approve/Reject buttons now appear
  for these patches in the Patch Center.

**Operator-edited alternative patches**
- "✎ Edit as alternative" opens the proposal's content in an editor; submitting creates a NEW
  proposal (same file, same base content) behind the standard approval gate via
  `POST /patches/{id}/alternative`. Nothing is written to disk by editing. The original is marked
  superseded (optional) and its pending approval resolved.

**Unbiased verification with auto-approve**
- "⚖ Verify & Auto-approve" (`POST /patches/{id}/verify`): the patch is applied with a backup, the
  verify command runs (`autonomy_autoapply_verify_cmd` or built-in `dotnet build && dotnet test`),
  and the workspace is ALWAYS restored — green or red. The toolchain judges the change, not the
  ant that proposed it. Green ⇒ the patch is auto-APPROVED through the normal Queen/approval path;
  applying to disk still requires the operator. Red ⇒ stays pending with the failure tail recorded.
  Requires the same write gates as Apply (the temporary staging honors them).
- Tests: `PatchOperatorActionTests` covers orphan approve/reject, alternative creation/supersede,
  and edge cases against a real SQLite database.

## v1.8.23.3 — CI linux-x64 artifact packaging

Roadmap item "CI release packaging foundation": every successful CI run now produces a
release-ready, downloadable package — not just tagged releases.

- `publish-and-selftest` job now packages `./publish/linux-x64` (binary + `config.example.json`,
  README, CHANGELOG) as `anthill-linux-x64-v<version>.tar.gz` and uploads it as a CI artifact.
- Artifact name is read from `AnthillRuntime.Version` at build time, so it always matches the code.
- Packaging steps run strictly after publish + `--selftest` succeed; a broken build can never
  produce a downloadable package (`if-no-files-found: error` guards the upload).
- No runtime behavior changes; existing build/test/selftest/Docker/shellcheck jobs untouched.
- Documented where CI artifacts appear in `docs/DEPLOYMENT.md` §4.

## v1.8.23 - Phase 9: Memory + Pheromone Explorer

- Adds a Memory + Pheromone Explorer on the existing Pheromones page.
- Visualizes success/failure/loop-pattern signals from mission history and pheromone trails.
- Adds mission memory search across missions, tasks, patches, and source summaries using existing read endpoints.
- Keeps prune controls on the same surface so weak/failure-dominant trails can be cleaned up without leaving the explorer.
- Delivered through issue #22, branch `feat/22-memory-pheromone-explorer`, and pull request workflow.

> Versioning convention: each autonomy phase or notable feature ships as a patch bump.
> Phase 1 = **v1.8.1**, live console + operator accounts = **v1.8.2**, enterprise shell UI = **v1.8.3**,
> model provider connections = **v1.8.4**, Phase 2 autonomy (Strategist) = **v1.8.5**, container-style
> deployment (Docker) = **v1.8.6**, LXC deployment = **v1.8.7**, provider base-URL fix = **v1.8.8**,
> LXC upgrade-in-place fix (ETXTBSY) = **v1.8.9**, LXC upgrade-in-place fix (stale native asset
> cache) = **v1.8.10**, Autonomy page recursion fix = **v1.8.11**, Phase 3 autonomy
> (concurrency + ResourceGovernor) = **v1.8.12**, coder Python-bias fix = **v1.8.13**, Phase 4
> autonomy (learning loop) = **v1.8.14**, mission reports (readable observability) = **v1.8.14.1**,
> UI cache + approval dedupe fixes = **v1.8.14.2**, Security + Shell config tabs = **v1.8.14.3**,
> header status + update check = **v1.8.14.4**, auto-publish releases + hardening = **v1.8.14.5**,
> Phase 5 autonomy (gated auto-apply) = **v1.8.15**, live-test fixes = **v1.8.15.1**, Strategist
> intent + shell service control = **v1.8.15.2**, native polkit install = **v1.8.15.3**, disk
> hygiene + maintenance controls = **v1.8.15.4**, completed-objectives box = **v1.8.15.5**, coder
> JSON parse hardening = **v1.8.15.6**, Overview System Health panel = **v1.8.15.7**, objective
lifecycle hardening + visual Patch Center = **v1.8.16**, Patch Center robustness = **v1.8.16.1**,
Colony Command Center HUD (design system + Overview dashboard) = **v1.8.17**, Mission Composer +
plan preview = **v1.8.18**, Patch Center invalid-UTF-16 500 fix = **v1.8.18.1**, Colony Live Canvas 2.0 = **v1.8.19**, Objective Command Board +
Mission Timeline/DAG = **v1.8.20**, autonomous auto-apply persistence fix = **v1.8.21**, Phase 8
Ant Inspector/Performance Observatory + Ant Capability Profiles & Worker Runtime = **v1.8.22**,
ASCII banner tweak = **v1.8.22.1**, Memory + Pheromone Explorer = **v1.8.23**, console UTF-8 repair
+ API serialization hardening = **v1.8.23.1**, Patch Center duplicate-route fix = **v1.8.23.2**,
CI linux-x64 artifact packaging = **v1.8.23.3**, Visual Patch Center 2.0 grouping (UI Phase 7)
= **v1.8.24**, Full Command Center Polish (UI Phase 10) = **v1.8.25**, console glyph-corruption
repair = **v1.8.25.1**, and so on.

## v1.8.23.2 — Patch Center duplicate-route fix

**Root cause of the recurring Patch Center empty HTTP 500.** `GET /patches` was registered twice: a
legacy `ProtectedText("/patches")` (the old `Queen.FormatPatchList()` text list) collided with the
structured `app.MapGet("/patches")` that the Patch Center UI uses. Two endpoints with an identical
method+template make ASP.NET throw `AmbiguousMatchException` during routing — *before* any handler or
middleware runs — so it surfaced as an uncatchable empty-body 500 that neither the v1.8.18.1 UTF-16
sanitizer nor the v1.8.23.1 serialization guard could touch (they run after routing).

- Removed the duplicate legacy `ProtectedText("/patches")` registration; the structured list remains.
- Added `AssertNoDuplicateRoutes()` at startup: enumerates every registered endpoint and throws a
  clear error at boot if any method+template is registered more than once, so this class of bug fails
  loudly at startup instead of silently 500ing at request time.

## v1.8.23.1 — Console UTF-8 repair + API serialization hardening

Two fixes bundled on top of Phase 9.

**Console encoding repair.** The v1.8.23 save round-tripped `index.html` through a non-UTF-8 encoding,
flattening 28 button icon glyphs (`↺`, `✂`, `▶`, `⌕`, `✓`, `✕`, `◈`) to `?` and leaving 354 U+FFFD
replacement characters (`�`) where em-dashes, ellipses, middot separators, and password-field bullets
used to be. All glyphs are restored; the file is clean UTF-8 again and the embedded JS still parses.

**Permanent Patch Center fix (empty HTTP 500).** `ApiJson.Ok`/`Error` previously handed the object
graph to `Results.Json`, which serializes during result execution — *after* the endpoint's own
try/catch has returned — so any serialization failure surfaced as an uncatchable empty-body 500 (the
`/patches` list was failing this way again). Responses are now serialized up front inside a guarded
`Envelope` helper (returning `Results.Content`), non-finite numbers are neutralized in the sanitizer,
and an outermost middleware converts any remaining unhandled exception into a valid JSON 500. No
endpoint can emit a silent empty 500 anymore — a failure now returns the real error message.

## v1.8.22.1 — ASCII banner tweak

Trim the boot/shell ANTHILL banner to the single large ant: removed the row of small ant figures
and the empty gap beneath the art so the banner butts directly against the following output line.

## v1.8.22 — Phase 8 + Ant Capability Profiles & Worker Runtime

Phase 8 UI (Ant Inspector + Performance Observatory) and the ASCII banner ship alongside the
capability layer incorporated from the codex branch:

- **Ant Capability Profiles** (`Agents/AntRegistry.cs`): 17 role definitions (6 executable —
  researcher, web, file, coder, builder, verifier) each with an `AntPermissionContract`
  (read/write workspace, read/write memory, web, shell, allowlisted checks, propose/apply patches)
  and named sub-workers. Forbidden paths (`py.old/`, `.git/`, `data/`, `.venv/`) and no-apply task
  types are enforced. `ValidateTask` gates each task against the mission constraints.
- **Worker Runtime** (`Agents/AntRuntime.cs`): resolves the role+worker for a task, injects worker
  context into the task snapshot, and emits audit warnings + metadata.
- Planner assigns a default worker per task and drops capability-rejected tasks; the Queen validates
  and resolves each task at run time (permission-denied tasks fail with a clear reason).
- Persistence: `tasks.assigned_worker` column (+ schema auto-migration), worker carried through
  task DeepCopy, graph nodes, and scheduler views; `SummarizeWorkerTelemetry()` aggregates worker
  performance.
- API: `GET /colony/registry` (roles + validation + telemetry) and `GET /colony/workers/telemetry`;
  `/missions/plan` now returns each step's `worker`/`display`, a `selected_path`, and
  `constraint_warnings`.
- UI: caste inspector shows a worker sub-caste breakdown, the DAG task drawer shows the resolved
  worker, and the plan preview shows the worker per step plus capability notes.

## v1.8.21 — Fix: autonomous auto-apply changes not persisting

Auto-apply is *apply → verify → keep-or-rollback*: a patch is kept only if verify exits 0, else every
applied patch is reverted. On a deployment with no build toolchain (a published-binary LXC, no dotnet
SDK, or `agent_workspace_dir` that isn't a buildable checkout), the built-in
`dotnet build && dotnet test` verify always failed — so auto-applied changes were silently rolled
back and never persisted ("not saving").

- **New opt-in gate `autonomy_autoapply_keep_without_verify`** (default false = keep verifying, safe).
  When true **and** no `autonomy_autoapply_verify_cmd` is configured, auto-apply keeps the applied
  patches instead of running (and failing) the built-in verify. If a verify command *is* set, it
  always runs and gates keep/rollback as before.
- **Clearer outcome logging.** The `autonomy_autoapply_started` / `_reverted` events now record the
  workspace path and the verify command; the reverted event's message spells out the fix options.
  A new `autonomy_autoapply_kept_unverified` event marks the keep-without-verify path, and
  `autonomy_autoapply_git_failed` surfaces a failed local git commit (kept on disk regardless).
- **Mission report surfacing.** `/missions/{id}/report` now includes an `auto_apply` outcome
  (kept / kept-unverified / reverted / apply-failed / git-failed / skipped) and the console shows an
  "Autonomous auto-apply" section — so "did the change actually stick?" is answerable at a glance.
  Auto-apply failures are also added to the report's Problems list.

Config default stays fail-safe: auto-apply is still OFF unless enabled with a path allowlist, and it
still verifies unless the operator explicitly opts out.



## v1.8.20 — Objective Command Board + Mission Timeline & Task DAG (UI Phases 5–6)

Two additive UI views over existing endpoints — no backend/API changes.

**Phase 5 — Objective Command Board** (new admin **Objectives** page). Every autonomous objective
laid out in seven lifecycle lanes — Backlog, Active, Paused, Completed, Stopped, Looping, Failed —
derived from `/objectives` status + `end_reason`/`retired_code`. Each card shows title, runs, success
EMA, priority, and end reason; expanding a card loads `/objectives/{id}/detail` (runs, missions,
tasks, patch rollup) with deep links to Results and the Patch Center. Admin-gated.

**Phase 6 — Mission Timeline + Task DAG viewer** (in the mission report / Results). A lazy-loaded
"Task Flow" section renders the mission's task graph two ways from `/missions/{id}/graph`:

- **DAG** — layered by dependency depth, nodes colored by status and ant, dependency edges drawn with
  **failure paths highlighted in red**.
- **Timeline** — tasks ordered by start time with duration bars.

Clicking a node/row opens a task detail drawer (ant, type, status, elapsed, attempts, failure
reason). Final output stays separated in its own report section as before. Rendered on demand so the
report stays light.


## v1.8.19 — Colony Live Canvas 2.0 (UI Phase 4)

Additive upgrade to the existing Colony canvas — the working node graph, task-dependency edges
(`dataFlowEdges`), handoff animation, pan/zoom, and node inspector are all preserved. New:

- **Caste legend + pheromone HUD overlay** on the canvas: the six ant castes with live-activity dots,
  and a "Colony Learning · Pheromones" panel showing the top real pheromone trails (`/pheromones/json`)
  with strength bars. Polls only while the Colony page is visible; glass overlay, `pointer-events:none`.
- **Real pheromone drift** on the canvas: motes drift from the castes toward the Queen with density and
  opacity scaled by actual colony trail strength (a global `pheromoneIntensity`). One additive CSS-cheap
  draw pass, guarded so it's invisible until the colony has learned something; reduced-motion aware.
- **Corrected node inspector**: the previously mislabeled "Pheromone Trail" bar (which just showed
  activity %) is now a **Live Task Load** breakdown — running / completed / failed tasks for that caste
  from the current mission graph. Real data.

No backend or API changes; reuses `/pheromones/json` and the existing `/graph` feed. The render loop
gains a single guarded pass; existing colony interaction and behavior are unchanged.



## v1.8.18.1 — Fix: Patch Center empty HTTP 500 (invalid UTF-16 in JSON)

Live testing surfaced `GET /patches` returning an empty HTTP 500 ("Error loading patches: Empty
response (HTTP 500)"). Root cause: `ApiJson.Ok` returns `Results.Json`, which serializes the payload
during response execution — **after** the endpoint's own try/catch has returned — so the failure was
uncatchable and produced an empty 500. `System.Text.Json` throws *"Cannot transcode invalid UTF-16"*
on a string containing a lone/unpaired surrogate, which LLM-generated patch `reason` / `summary` /
`mission_goal` text occasionally contains (clean test data never did).

Fix — scrub invalid UTF-16 at the JSON boundary so no endpoint can 500 on it:

- `TextUtil.SanitizeUtf16` replaces lone surrogates with U+FFFD (fast path: strings without
  surrogates are returned unchanged, no allocation).
- `ApiJson.Ok` / `Error` now recursively sanitize every string reachable from the payload
  (`ApiJson.SanitizeJson` walks dictionaries and lists; `byte[]` and scalars pass through so base64 /
  number serialization is preserved). This makes **all** JSON endpoints fail-safe against bad
  Unicode, not just the Patch Center.
- Tests (`JsonSafetyTests`) cover lone high/low surrogates, valid emoji preservation, deep nested
  scrubbing that then serializes cleanly, and `byte[]`/scalar passthrough.



## v1.8.18 — Mission Composer + Plan Preview (UI Phase 3)

Lets an operator review the generated task plan — and see how a mode/constraint reshapes it — before
a mission runs. Additive; existing one-shot dispatch is unchanged.

**Backend — dry-run planner.** New `POST /missions/plan` (permission `run_mission`, rate-limited)
runs the real planner + v1.8.16 constraint enforcement for a goal and returns the task list
**without creating, persisting, executing, or logging a mission** (`Queen.PlanPreview`). The response
includes each step's title, assigned ant, task type, and dependency edges (as human step numbers),
plus the parsed constraint flags (`verification_only` / `read_only` / `no_patches` / `one_shot` /
`blocks_patches`) and whether the plan contains a coder patch step. No fake capability — the preview
is exactly what a dispatch would plan.

**UI — Mission Composer.** The Overview mission node gains a **Preview Plan** action. It composes the
goal (raw directive + any selected mode's safe wording), calls `/missions/plan`, and renders the plan:
a constraint banner (e.g. "verification-only — no file changes"), the ordered steps with per-ant
badges, task types, and "after step N" dependencies, then **Approve & Dispatch** / **Edit** / **Reject**.
Approve submits the exact previewed goal via the existing `/missions` path; the raw ▶ / Enter dispatch
still works unchanged for one-shot use. Direct dispatch and Approve share one `submitMissionGoal()`
so the goal string is identical either way.

**Tests.** `PlanPreviewTests` (Ollama forced off → deterministic fallback planner) assert the preview
drops coder steps for verification-only goals, keeps them for code goals, always ends with a verifier,
and creates no mission row.



## v1.8.17 — Colony Command Center HUD Upgrade (Phases 1–2)

Turns the Overview into a live swarm command-center HUD, built additively on the existing
single-file console — no new dependencies, all animations CSS-only and reduced-motion aware.

**Phase 1 — HUD design system.** Reusable vanilla primitives + CSS: a canonical `hud-badge`
(running/idle/active/paused/completed/stopped/looping/failed/pending/approved/applied/rejected/
warning/unknown), `hud-risk` (low/medium/high/unknown), glass `hud-panel` with corner brackets and
optional active/warn/alert glow, `hud-metric` cards, `hud-telem` lines, loading/empty/error state
blocks, and a `hud-act` action-button group. JS helpers `hudBadge()`, `hudRisk()`, `hudStatusClass()`.

**Phase 2 — Overview command dashboard.** All panels use real API data with graceful `—`/empty/error
fallbacks (no fabricated values):

- **Colony status strip** — API link, provider/model, autonomy state, active missions, active
  objectives, pending approvals, warnings, and governor resource pressure; each deep-links to the
  relevant page and highlights warn/alert states.
- **Central system core** — a J.A.R.V.I.S.-style orb whose state (IDLE / MISSION ACTIVE / AUTONOMY
  ONLINE / OPERATOR ACTION / ALERT) is derived from real counts (active jobs, autonomy, pending/high-
  risk patches, failed missions, retired objectives, provider health). CSS rings/pulse only.
- **Operator attention** — real action items (pending/high-risk patches, failed missions, retired/
  failed objectives, backend-unreachable) with severity, reason, and deep link; "No operator action
  required" when clear.
- **Hardware/environment cards** — CPU load/core, memory-available %, backend latency, and effective/
  configured concurrency from the ResourceGovernor signals (`/autonomy/status`); shown as `—` with a
  "runs during autonomy" note when the governor hasn't sampled yet. Percentages clamped 0–100.
- **Mission command node** — terminal-style `ANTHILL_CORE >` input (existing submission preserved)
  with Inspect / Verify / Patch Proposal / Full Build-Test mode buttons that prepend visible, safe
  wording read by the v1.8.16 planner constraints (verification-only / no-patch). Selected mode is
  shown as a badge; nothing is changed silently.
- **Summaries** — recent missions, patch-status rollup (+high-risk), and objective-status rollup,
  each linking to Results / Patch Center / Autonomy. Live telemetry + recent jobs reuse the existing
  event/job feeds.

Data polling reuses the existing gated cadence (only fetches while Overview is visible); no new
uncleaned timers. Existing pages, navigation, mission submission, autonomy, and approval/apply
behavior are unchanged. UI-only — no API or backend changes.



## v1.8.16.1 — Patch Center robustness + validation

Stabilization pass on the v1.8.16 Patch Center after live testing surfaced an opaque
"Unexpected end of JSON input" error in the console:

- **`api()` never throws on an empty/non-JSON body.** The shared client helper now reads the body as
  text and returns a structured `{success:false, message}` instead of letting `Response.json()`
  throw a raw parse error. A 404 (e.g. a stale server missing a newly added endpoint) now reports a
  clear "Empty response (HTTP 404) — this build may be missing the endpoint; redeploy?" message.
- **`GET /patches` and `/patches/{id}/detail` are wrapped in try/catch**, returning a JSON error
  payload instead of a bare 500 if anything unexpected happens while assembling the list/detail.
- **Fixed one-shot phrase detection** so "run this once" / "do this once" are recognized (also adds
  "just once" / "only once").
- **New DB-backed tests** (`PatchCenterTests`) exercise `ListPatchesForCenter`, the per-mission and
  per-objective patch rollups, and `ListEndedObjectives` against a real SQLite database, so a query
  dialect/column error is caught in CI rather than as a runtime 500.



## v1.8.16 — Objective Lifecycle Hardening + Visual Patch Review Center

Two focused improvements to how the colony ends autonomous work and how the operator reviews the
changes it proposes. See `docs/ROADMAP.md` for the 10-phase direction; Phases 1–2 ship here.

**Objective lifecycle (Phase 1).** One-shot and verification-only objectives now end cleanly instead
of regenerating near-identical missions until loop detection retires them:

- New clean-completion path (`ObjectiveLifecycle.EvaluateCompletion`) runs *before* loop detection.
  A successful one-shot objective ends `completed_successfully`; a successful verification-only /
  read-only / no-patch objective that discovered no new work ends `stopped_no_followup_required`.
  Broad standing objectives (no one-shot/verify wording, `max_runs` 0/>1) keep running as before.
- Loop detection is preserved strictly for true repeated loops — it is no longer the normal ending
  path for successful maintenance work.
- Unified end reasons stamped on every ended objective: `completed_successfully`,
  `stopped_no_followup_required`, `retired_looping`, `failed`, `manually_paused`, `manually_stopped`.
- New config `autonomy_oneshot_completion` (default on) gates the behaviour.

**Planner constraint enforcement (Phase 1).** The planner now reads explicit mission constraints
(`MissionConstraints`): a `verification-only` / `read-only` / `do not modify files` mission gets a
hard prompt directive *and* a deterministic post-plan strip of every coder patch-proposal task, with
a read-only file-inspection task substituted so verification missions still actually inspect files.
Normal code-change missions keep the full coder/builder/verifier workflow.

**Visual Patch Center (Phase 2).** A new admin page lists every patch proposal with status and risk
badges, filterable by status, risk, mission, objective, and file path. Each patch expands to a
unified diff (removed/added/context) and offers Approve / Reject / Apply / View Mission — reusing the
existing approve-then-apply safety model, with an Apply confirmation that surfaces operator safety
checks (risk level, missing old content, no pre-apply backup). Patch links are wired into mission
Results (per-mission counts + deep link), the Autonomy runs table (a patch summary per run), and the
Completed Objectives detail (patch activity per objective). Additive API only: `GET /patches`,
`GET /patches/{id}/detail`, plus patch rollups on the report/runs/objective endpoints. Storage is
unchanged; new `PatchStatus.Superseded` completes the status model. No Python touched.



## v1.8.15.7 — System Health panel on the Overview

Added an enterprise-style **System Health** card to the Overview, giving an at-a-glance read on the
three things that actually go wrong in a long-running colony, each with a green/amber/red status dot:

- **Autonomy** — RUNNING / IDLE / HALTED / OFF state, missions-today vs the daily cap, live backlog
  (pending + active objectives), and effective/configured concurrency. Sourced from `/autonomy/status`.
- **Storage** — free disk with a usage bar that turns amber at 85% and red at 93%, plus the SQLite DB
  size and backup count/size. Sourced from `/maintenance/stats`. When disk is tight *and* backups are
  prunable, an alert line points straight to **Settings → Maintenance → Flush Cache** to reclaim it.
- **Coder Patches** — recent parse success rate (`patch_set_created` vs `patch_proposal_parse_failed`)
  with applied count, so the v1.8.15.6 parse-hardening win stays visible. Sourced from `/events/json`.

The panel polls every 8s but only fetches while the Overview is the visible page (and refreshes
immediately on navigation to it), so it adds no load elsewhere. UI-only change — no API surface added.

## v1.8.15.6 — Coder patches actually parse now (fewer patch_proposal_parse_failed)

Live diagnostics showed a steady stream of `patch_proposal_parse_failed` — coder output that never
reached the approval queue or auto-apply. Root causes and fixes:

- **Raw control chars in JSON (the big one).** Small local models emit patches with a literal
  newline inside string values (`"new_content": "line1<newline>line2"`), which strict JSON rejects
  with `'0x0A' is invalid within a JSON string`. `Json.ExtractJsonObject` now retries every parse on
  a copy where control chars inside string literals are escaped, and tolerates trailing commas and
  comments. This recovers the most common failure class.
- **Placeholder file paths.** The v1.8.13 neutral example (`file.ext`) was being copied literally,
  producing `file_path: .ext` — rejected as an unsupported type. The coder prompt now uses an
  obvious `<...>` placeholder with real examples, forbids placeholder paths outright, and tells the
  model to escape newlines as `\n` (single-line JSON).
- **One bad proposal no longer discards the set.** `PatchProposalParser` parses each proposal in its
  own try/catch — a malformed entry (bad path, missing reason) is skipped and the valid proposals in
  the same set survive, instead of the whole patch set being thrown away.
- Tests: `JsonRepairTests` (raw newline/tab/CR recovery, trailing commas, code fences, prose
  stripping, valid-escape round-trip, control-chars-outside-strings untouched).

## v1.8.15.5 — Completed Objectives box for loop-retired objectives

Objectives the Director retires for looping (Phase 4 loop detection) previously stayed in the
paused backlog, mixed in with normal paused objectives. They now move to a dedicated
**Completed Objectives** box under Configuration → Autonomy.

- The Director stamps a retirement marker (`retired_code`, `retired_reason`, `retired_at`) onto
  the objective's metadata when it retires it — reusing the existing objective model, no schema
  change and no change to the loop-detection logic itself.
- The active/paused backlog table now filters out `retired_code == "looping_goals"`; normal
  paused objectives (circuit-breaker / stale) are unaffected.
- New **Completed Objectives** card: each loop-retired objective is one collapsed expandable row —
  title, a **Stopped** badge, a **Looping** badge, and the short stop reason. Expanding lazy-loads
  the compiled detail (objective ID, title, stop/loop reason, related runs, missions, tasks, and
  the stopped timestamp).
- New: `SqliteMemory.ListRetiredObjectives`, `ApiHost.CompletedObjectiveDetail`; endpoints
  `GET /objectives/completed` and `GET /objectives/{id}/detail` (both `read_objectives`); retirement
  markers added to the `/objectives` response.
- Tests: `ListRetiredObjectives_FindsLoopingRetired_ByMetadata` (looping-retired found; plain-paused
  and stale-retired excluded).

## v1.8.15.4 — Disk hygiene: backup retention + maintenance controls

Live diagnosis of a filling 51 GB disk found the cause: **1,032 pre-mission DB backups = 34 GB**.
ANTHILL copies the whole 68 MB database before every mission and never pruned the copies. Fixed at
the root, plus operator controls for cleanup.

- **Backup retention (the fix).** After each pre-mission backup the Queen now prunes the backup
  directory to the newest `max_db_backups` (default 10) via `FileSecurity.PruneBackups`. The backup
  dir is now bounded (~10 × DB size) instead of growing one full copy per mission forever. Existing
  bloat is reclaimed by the first Flush (or the next mission's auto-prune).
- **Flush Cache** (Settings → System Info → Maintenance): prunes old backups, deletes events older
  than `event_retention_days` (0 = keep all), and `VACUUM`s the database — reports the bytes freed.
  The panel shows disk free, DB size, and backup count/size.
- **Clear Missions** (Missions page): deletes all mission-execution history (missions, tasks,
  events, patches, approvals, sources, agent messages) and compacts — keeps objectives, pheromones,
  users, providers, config.
- **Cancel All** (Missions page): drops all queued jobs (a running mission finishes on its own,
  bounded by its timeout). Also adds `POST /jobs/{id}/cancel` and `POST /jobs/cancel-all`, with a
  `cancelled` job status the worker honors.
- **Dump Directives** (Autonomy page): clears the entire objective backlog + its run history.
- **Reset Config** (Maintenance): resets all tunable settings to safe defaults while **preserving
  connection settings** (Ollama host/model/routes, API bind, workspace) so a reset never strands the
  colony.
- New: `SqliteMemory.Maintenance` (FlushCache / ClearMissionHistory / ClearObjectives / TableCounts
  / DatabaseFileBytes), `FileSecurity.PruneBackups`/`BackupStats`, `AnthillRuntime.ResetConfig`,
  `ApiJobRegistry.Cancel`/`CancelAll`; endpoints `GET /maintenance/stats`, `POST /maintenance/{flush,
  clear-missions,reset-config}`, `POST /objectives/clear`. Config: `max_db_backups`,
  `event_retention_days`.
- Tests: `MaintenanceTests` (retention keeps newest N, reports freed, edge cases).

## v1.8.15.3 — Install polkit natively in the LXC setup

v1.8.15.2 shipped the scoped polkit rule but only installed it *if* polkit was already present —
on a fresh Debian LXC it isn't, so the installer skipped it ("polkit not present"). `setup.sh`
now **installs polkit itself** (like it installs the .NET SDK): `apt-get install polkitd` with
fallbacks to `polkit` / `policykit-1` across distros, enables the daemon, writes the scoped JS
rule (modern polkit, Debian 12+) *and* a `.pkla` fallback (legacy polkit 0.105, Ubuntu 22.04),
and restarts polkit. After a `git pull && bash deploy/lxc/setup.sh` the operator Shell's
Restart/Status/Logs buttons work with no extra steps. No application-code change — same binary,
version bumped for deploy traceability.

## v1.8.15.2 — Strategist intent fidelity, backlog-sprawl cap, operator-shell service control

The v1.8.15.1 live test proved the planner now routes file goals to the coder, but exposed that
the **Strategist drifts** — it rewrote a one-shot charter ("create docs/x.md") into an unrelated
goal ("train a model on docs") before the planner ever saw it — and that autonomous runs **spawn
follow-up objectives aggressively** (13 accumulated in a short test). Both fixed, plus the
operator-shell service-control item from the roadmap.

Autonomy — intent fidelity:

- **One-shot objectives are never reinterpreted.** An objective with `max_runs == 1` is an
  explicit do-this-once task; `Strategist.GenerateGoal` now uses its charter verbatim (bypassing
  the LLM entirely, `Source = "charter_verbatim"`) so the operator's intent reaches the planner
  unchanged. Standing objectives (`max_runs` 0/>1) still go through the Strategist.
- **The Strategist prompt now preserves the charter.** It must produce a goal that directly
  accomplishes the charter (execute it as written on the first run; only take the next incremental
  step once a prior run already accomplished it) — never substitute a different or broader task —
  and follow-ups should "almost always be empty" (add one only for a genuinely distinct new
  objective, never to seem productive).

Autonomy — sprawl guard:

- **`autonomy_max_backlog` (default 40).** The Strategist stops enqueuing self-generated follow-up
  objectives once the open backlog (pending + active) reaches the cap — a structural bound on
  sprawl regardless of model behavior, on top of the existing per-run rate and depth caps. 0 = no
  cap. Clamped, settings-whitelisted, in `config.example.json`.

Operator-shell service control:

- The systemd unit's `NoNewPrivileges=true` blocks `sudo`, so `setup.sh` now installs a **scoped
  polkit rule** (`deploy/lxc/anthill-polkit.rules.template` → `/etc/polkit-1/rules.d/49-anthill.rules`)
  that lets the admin-only operator Shell manage **only** the `anthill.service` unit
  (restart/stop/start/status) over D-Bus — no privilege escalation, hardening untouched. The Shell
  tab gains quick buttons: Service status, Recent logs, Restart service (with a confirm), Host
  health. Best-effort: skipped with a message if polkit isn't installed. Docs in DEPLOYMENT.md.
- Tests: `OneShotObjective_UsesCharterVerbatim_EvenWithNoRouter`.

## v1.8.15.1 — Fixes from the live Phase 5 test

A live test of Phase 5 on the LXC confirmed both the keep and rollback branches work end to end
(patch applied → verify → kept + approval consumed; and applied → verify failed → rolled back,
workspace clean). It also surfaced three issues, all fixed here:

- **`DELETE /objectives/{id}` returned 500 for any objective that had run.** `autonomy_runs` has a
  foreign key to `objectives(id)` with `foreign_keys=ON`, so deleting an objective with run history
  threw — meaning the Delete button in the backlog was broken for anything that had executed.
  `DeleteObjective` now cascades the dependent runs and detaches follow-up children in one
  transaction; the endpoint returns a clean error instead of a 500 on any other failure.
- **The planner rarely routed file-creation goals to the coder ant** — the root cause of "work
  happens but nothing lands." Two prompt bugs: `docs` was listed as a *web-search* trigger (so
  "create a file in docs/" went to web research), and nothing told the planner that creating or
  editing a file requires a coder patch. The planner prompt now states plainly that any goal which
  creates/adds/writes/edits/patches a file (including `.md`/config) **must** include a
  `patch_proposal` coder task, clarifies that proposing a patch is expected (not a "don't write
  files" violation), and stops treating a documentation path as a web trigger. The offline fallback
  planner now checks code/file keywords **before** the web branch and recognizes create/add/write/
  edit/`.md`/`.cs` goals.
- **Auto-apply on a read-only workspace failed one patch at a time with no explanation.** On a
  hardened LXC (`systemd ProtectSystem=strict`) the source tree is read-only to the service, so
  every apply failed individually. The runner now does a one-shot writability preflight and, if the
  workspace root can't be written, logs a single clear `autonomy_autoapply_skipped`
  (`reason: workspace_readonly`) pointing at `agent_workspace_dir` — instead of a stream of
  `apply_failed`. Docs add the writable-checkout deployment pattern for real self-modification.
- Tests: `DeleteObjective_CascadesRunsAndDetachesChildren`.

## v1.8.15 — Phase 5 autonomy: gated auto-apply (the autonomy roadmap is complete)

The Director can now **ship low-risk fixes on its own** instead of queueing every patch for a
human forever — the direct answer to the approval pile-up. It's the highest-risk capability in the
system (autonomous writes to disk), so it's fail-closed and multiply gated, and the entire safety
model is *apply → verify → keep-or-rollback*.

- **Strict eligibility gate** (`Autonomy/AutoApplyPolicy.cs`): a patch is auto-appliable only when
  every condition holds — the master switch is on; the change is `add`/`modify` (never
  delete/rename); the file path matches an operator glob in `autonomy_autoapply_paths` (an **empty
  allowlist means nothing is eligible**, so it's inert until you widen it); and the change is
  within `autonomy_autoapply_max_lines`. Glob supports `**`/`*`/`?`.
- **Apply → verify → rollback** (`Anthill.Api/AutoApplyRunner.cs`, runs on the Director thread
  after a *successful* mission): applies eligible patches with per-file backups, then runs
  `dotnet build && dotnet test` (or your `autonomy_autoapply_verify_cmd`) in the workspace,
  timeout-bounded. **Green** ⇒ changes stay, the matching approval requests are marked `consumed`
  (they leave the queue), optional local `git` commit (never pushed). **Red/timeout** ⇒ every
  applied patch is rolled back (modify → restore backup, add → delete) and marked failed.
- **Depends on the write gates** (`patch_application_enabled` + `file_writing_enabled`); logs
  `autonomy_autoapply_skipped` and does nothing if they're off. **Forced off in every safety
  profile** and off by default.
- **Full audit trail**: `autonomy_autoapply_started`/`_applied`/`_verified`/`_reverted`/
  `_rolled_back`/`_ineligible`/`_skipped` events; applied/reverted patches appear in the mission
  report's tangible-changes with their final status.
- **Config** (clamped, settings-whitelisted, editable in **Configuration → Security → Autonomous
  Auto-Apply**): `autonomy_autoapply_enabled`, `autonomy_autoapply_paths`,
  `autonomy_autoapply_max_lines`, `autonomy_autoapply_verify_cmd`,
  `autonomy_autoapply_verify_timeout`, `autonomy_autoapply_git_commit`.
- New `Queen.ApplyPatchForAutomation` / `RollbackAutoApplied` (structured apply with backup path
  for rollback). `/autonomy/status` gains `autoapply_enabled` / `autoapply_paths`.
- Tests: `AutoApplyPolicyTests` — eligibility matrix, glob semantics, size cap, change-type,
  disabled and empty-allowlist denial. (`InternalsVisibleTo("Anthill.Tests")` added to
  Anthill.Core so the suite can exercise the internal `GlobMatches` helper — same pattern as
  Anthill.Api.)

Also fixed: **`UpdateChecker.Compare` didn't tolerate a leading `v`** — `Compare("v1.8.15", …)`
parsed the `v1` segment as `0`, so the version read as older (a CI test caught it; production was
unaffected because `Fetch()` stripped the `v` before calling `Compare`). `Compare` now strips a
leading `v`/`V` on both sides itself.

With Phase 5 in, **the autonomy roadmap (Phases 0–5) is complete.**

## v1.8.14.5 — Auto-publish releases + hardening pass (audit)

Wraps the release-automation change with a thorough audit of everything shipped in the 1.8.14.x
line — resource leaks, security boundaries, and correctness. All findings fixed.

Release automation:

- The release workflow now **publishes** the GitHub Release and pushes the GHCR container package
  automatically on every tag push (was: created as a draft for manual publishing). `make_latest`,
  and four-part maintenance tags (`vX.Y.Z.W`) matched explicitly. README/DEPLOYMENT.md updated.

Security:

- **Fixed a privilege leak in the mission report.** `GET /missions/{id}/report` is served under
  `read_status` (which Mission Coordinators hold) but surfaced patch proposals, approval state,
  and autonomy objectives — all admin-only reads (`read_patches`/`read_approvals`/`read_objectives`
  are never in the coordinator set). The report now includes those sections only for callers who
  could read them directly (`CallerHas`), so it can't be used as a side channel around the
  permission model. Non-admins still get goal, status, final output, per-task results, and
  problems (all things they can already read).
- **Bounded two unbounded `?limit` query params** (`/events/json`, `/pheromones/json`) — a huge
  value could sweep the entire log/trail table in one request; now clamped.

Resource leaks:

- **Removed two per-request `new HttpClient` allocations** (`/system/summary`'s Ollama probe and
  the `/ollama/models` proxy). Under the header's periodic polling these leaked sockets over time;
  both now share one static client with per-call `CancellationToken` timeouts.
- **Session registry no longer grows unbounded**: abandoned session tokens (user logs in, never
  returns) were only evicted when that exact token was next resolved. Login now opportunistically
  prunes expired sessions.

Correctness:

- **Operator shell could truncate output.** `Process.WaitForExit(timeout)` can return before the
  async stdout/stderr handlers finish draining; the executor now calls the parameterless
  `WaitForExit()` afterward to guarantee a full flush, and locks the output builders against the
  threadpool callbacks that append to them.
- Tests: `PermissionBoundaryTests` (admin-only vs coordinator permission matrix).

## v1.8.14.4 — Live header status: update check, model/provider popover, local-vs-cloud icon

The top-right header was static — a fixed model string and an "Online" badge that didn't say what
was online. It's now a live, clickable status chip.

- **Update check** (`GET /update/check`): compares the running version against the latest release
  tag on the public GitHub repo and flags when a newer one exists. Result is cached server-side
  (30 min) so the header poll never hammers GitHub, and every failure (offline, rate-limited, no
  releases) degrades to "unknown" rather than erroring. When an update is available the chip shows
  a pulsing dot and the popover gives the new version, a release-notes link, and the exact LXC
  upgrade command. A "Re-check" button forces a fresh look (`?force=1`).
- **Local vs Providers icon**: the chip carries a monitor icon (green **LOCAL**) when every model
  role runs on local Ollama, or a cloud icon (purple **PROVIDERS** / **MIXED**) when any role is
  routed to OpenAI/Anthropic/Perplexity/OpenRouter — so the colony's cost/privacy posture is
  visible at a glance.
- **What's actually online**: the chip's dot now reflects backend health, not just the API — it
  goes red if Ollama is unreachable even while the API answers. The popover breaks it down: API
  server, Ollama backend reachability (with a live 3s probe), and Ollama host.
- **Model visibility + quick actions** (`GET /system/summary`): the popover lists every role's
  provider + model (each tagged local/cloud), the default model, and how many providers are
  connected, with one-click buttons to Ant Config (change models) and Settings → Providers.
- New: `src/Anthill.Api/UpdateChecker.cs`; `GET /update/check` + `GET /system/summary`.
- Tests: `UpdateCheckerTests` (dotted four-part version ordering, leading-v tolerance).

Release automation:

- The release workflow now **publishes** the GitHub Release and the GHCR container package
  automatically on every tag push (was: created as a draft for manual publishing). Each
  `git push origin vX.Y.Z[.W]` builds the self-contained linux-x64/win-x64 archives, pushes
  `ghcr.io/<repo>:<version>` + `:latest`, and publishes the Release (marked "latest") with the
  matching CHANGELOG section as notes. Four-part maintenance tags (`vX.Y.Z.W`) are matched
  explicitly. Docs (README, DEPLOYMENT.md) updated to match.

## v1.8.14.3 — Configuration: Security tab + admin-only Shell console

> **Pre-commit checklist** — the docs must be true before every commit:
> 1. Bump the version in all markers: `Directory.Build.props`, `AnthillRuntime.Version`,
>    `src/Anthill.Api/Ui/index.html` (title + auth logo + nav badge), `src/Anthill.Api/Program.cs`,
>    `src/Anthill.Cli/Program.cs`, `build.sh`, `build.ps1`, and the README banner.
>    Verify with: `grep -rn "<old version>" --exclude-dir=.git --exclude-dir=obj --exclude-dir=bin .`
> 2. Add the CHANGELOG entry (this file) — it becomes the GitHub release notes.
> 3. **Update README.md sections touched by the change** (Colony UI Guide, API Reference,
>    Configuration Reference, deployment sections) and `config.example.json` for new knobs.
> 4. Update `docs/AUTONOMY.md` / `docs/DEPLOYMENT.md` when behavior in their scope changes.
> 5. Sweep for leftovers: stale comments, dead config keys, outdated status claims, debug code.
> 6. `dotnet test Anthill.sln -c Release` green, then commit + tag + push.

## v1.8.14.3 — Configuration: Security tab + admin-only Shell console

Two new admin-only pages under Configuration.

- **Security tab**: a single place for the app's security posture — auth mode, safety profile,
  network bind exposure, and encryption-at-rest at a glance — plus live toggles for every
  capability gate (web search, file read/write, patch application, the AI ants' shell tool), the
  **workspace boundary** (`agent_workspace_dir`, the only path the file/coder ants may touch), and
  the operator-shell controls. All persist through the existing `/settings` path.
- **Shell tab**: a direct interactive terminal into the host ANTHILL runs on (the LXC/VM/box) —
  command input with history (↑/↓), streamed stdout/stderr, exit code, elapsed time, and a
  settable working directory. Built for host maintenance the AI ants must never do (restart the
  service, pull updates, edit config).

Because the Shell console is host remote-code-execution, it is gated four independent ways:
(1) authenticated, (2) **admin role only** — the new `operator_shell` permission is never in the
coordinator set, so a Mission Coordinator cannot see or use it; (3) the `operator_shell_enabled`
config gate (toggleable from the Security tab); and (4) **every command is written to the audit
event log** (`operator_shell_command` before it runs, `operator_shell_result` after) with the
operator's username, so there's a durable record of who ran what. Each command is bounded by a
60-second timeout and its output capped. Per operator request it ships **enabled for admins** by
default; set `operator_shell_enabled: false` (or toggle it off in Security) on any install you
don't fully trust on the network. Distinct from `shell_tool_enabled`, which gates the AI ants'
*allowlisted* tool and stays off by default.

- New: `GET /shell/info`, `POST /shell/exec` (both `operator_shell`, admin-only);
  `src/Anthill.Api/OperatorShell.cs`; config keys `operator_shell_enabled` / `operator_shell_dir`;
  `api_auth_enabled` + `agent_workspace_dir` added to the settings snapshot.
- Tests: `OperatorShellTests` — admin-only permission, command execution, non-zero exit,
  working-directory handling.

## v1.8.14.2 — Results page; stale-UI cache fix; approval-queue dedupe

New — **Results page** (operator request: mission results shouldn't take over the whole screen):

- A dedicated **Results** nav page lists every mission (newest first, filterable by
  completed / partial / failed) as compact collapsible rows — status in plain English, goal,
  score, and finish time. Expanding a row lazily loads the full Mission Report inline: final
  output, per-task readable results, tangible changes with approval states, problems, and — new —
  the **autonomous-run context** (which objective drove the mission) and the **objectives this
  mission created** (Strategist follow-ups, now stamped with `created_by_mission_id` /
  `created_by_run_id` metadata when the Director saves them, so the lineage is queryable).
- Every **View Result** button routes here and auto-expands that mission (jobs lists, Missions
  page, and the Autonomy runs table's View). Running jobs keep a compact "View Status" quick
  view; the old full-screen overlay remains only as a fallback for legacy jobs without a
  mission id.
- New API: `GET /missions/json?limit=` (mission history as JSON) and mission reports now include
  `autonomy_run` + `created_objectives`. New `SqliteMemory.GetAutonomyRunForMission` /
  `ListObjectivesCreatedByMission`.

Verified live against the running LXC instance (v1.8.14.1) before shipping: the backend live
task feed and the new canvas logic were confirmed working in a fresh browser session — particles
flowing, per-ant activity tracking real task states, correct idle when nothing runs. The
"still broken" console was the *browser's cached copy of the previous UI*.

- **`/ui` is now served with `Cache-Control: no-store`.** The console is embedded in the binary,
  and without cache headers a browser can silently pin an operator to the previous version's
  UI after every upgrade (stale canvas logic, missing panels) until a manual hard-refresh. Now
  every page load fetches the UI the running binary actually ships. (One last hard-refresh is
  needed to pick this version up; after that, never again.)
- **Approval-queue flooding fixed with dedupe.** High-frequency autonomous testing (observed
  live: 62 missions/hour, 1000/day until the daily budget tripped) re-proposes the same change
  run after run while the first request sits unreviewed — every rerun stacked another identical
  approval request. `Queen.ProcessPatchProposals` now checks
  `SqliteMemory.HasDuplicatePendingApproval` (same file, change type, and old/new content,
  compared after decryption) and skips creating a duplicate, logging an
  `approval_request_deduped` event instead. Decided (approved/rejected) requests never block a
  fresh proposal. Reminder that stacking ≠ malfunction: the Director *never* auto-approves —
  clearing the queue is the operator's half of the workflow until gated auto-apply (Phase 5)
  lands.
- **Tests**: `ApprovalDedupeTests` — identical pending change detected; different content/file
  not deduped; decided approvals don't block; null-content comparisons.

## v1.8.14.1 — Mission Reports: see exactly what the colony did, in plain English

Operator feedback from live use: work was "seemingly being done" (autonomous runs completing,
follow-up objectives appearing) but nothing readable in the UI showed what actually happened or
changed. Two root causes: (1) the only result view was the raw CLI dump — final output, debug
trace, and task JSON in one wall of jargon; (2) the tangible outputs of missions (patch
proposals waiting in the approval queue) were never connected to the mission/run that produced
them. Note the design constraint that makes visibility essential: **the colony cannot change
files or its own UI by itself** — every file change is a patch proposal that waits for human
approval + apply. If nothing is approved, nothing changes; the console now says so explicitly.

New:

- **`GET /missions/{id}/report`**: structured, human-readable report per mission — goal, status,
  score; the mission-level **final output** (kept separate from per-task outputs, since tasks are
  the steps and the mission is the deliverable); a per-task breakdown (title, ant, status,
  elapsed, readable output, and the *why* for failed/skipped/blocked tasks); **tangible changes**
  (every patch proposal the mission created, its file, reason, and current state — awaiting
  approval / approved / applied to disk / rejected, with apply errors); pending-approval count;
  sources saved; and **problems** — including `patch_proposal_parse_failed`, the silent killer
  where the coder did work but its proposal never reached the approval queue.
- **Plain-English task outputs**: coder results (raw JSON patch sets) are translated to
  "Proposed modify to src/... : reason" lines server-side (`ApiHost.ReadableTaskOutput`);
  other ants' prose passes through; malformed output falls back to raw text.
- **Mission Report modal**: "View Result" on any completed job now renders the structured report
  — status in words, final output, tangible-changes list with a pointer into Approvals,
  problems, and an expandable per-task list — instead of the raw CLI text (which remains the
  fallback for legacy jobs without a mission id).
- **Autonomy runs are inspectable**: each row in Recent Autonomous Runs gains a **View** button
  opening the same mission report, so every unattended run answers "what did it actually do,
  and did anything tangible come out of it?" in one click.
- **`SqliteMemory`**: `ListPatchProposalsForMission` / `ListApprovalRequestsForMission`
  (secret-free, per-mission).
- **Tests**: `ReportTests` — coder-JSON translation, empty-proposal wording, malformed-output
  fallback, prose passthrough. `InternalsVisibleTo("Anthill.Tests")` added to Anthill.Api.

Fixed (Colony canvas + Autonomy page housekeeping):

- **Ant/Queen hover tooltips showed activity over 100% and not live data.** Three bugs, one of
  them structural: (1) an operator-precedence error in the animation loop —
  `(colonyActivity[ant]||0-n.activity)` parses as `colonyActivity || (0-activity)`, accumulating
  activity unboundedly every frame; (2) the activity source was "this ant's share of all tasks"
  (including finished ones), not a live reading; and (3) — the structural one — **task rows were
  only persisted at mission start (before tasks exist) and mission end**, so `/graph` had no
  nodes at all while a mission ran; every mid-run number the canvas ever showed was stale data
  from the previous completed mission. Fixed end to end: the Queen now persists the planned task
  DAG before execution and upserts each task on every status transition (started → live
  "running"; complete/failed/skipped on finalize — new `SqliteMemory.SaveTask`), and the canvas
  computes activity from those current task states each poll (running = 100%, queued work = 35%,
  idle = 0%), clamped to [0,1] everywhere. Signal particles, node glow, the hover panel, and the
  task-DAG dataflow arrows now reflect what the colony is doing *right now* — the graph poll
  tightened from 5s to 2.5s to match.
- **Colony canvas sharpened**: the canvas now renders at the display's real pixel density
  (devicePixelRatio-scaled backing store, logical-coordinate drawing) — crisp nodes, edges, and
  labels on HiDPI screens instead of the previous blurry 1x upscale.
- **Autonomy tables no longer grow the page unboundedly**: the Objectives and Recent Autonomous
  Runs boxes are collapsible (click the header) and cap at ~20 rows with their own scrollbar and
  sticky column headers.
- **Docs housekeeping**: README brought up to date with everything shipped since v1.8.12
  (Concurrency/Governor status card, Score column, Mission Report views, `/missions/{id}/report`
  in the API reference, autonomy-knob pointers), and a pre-commit checklist added at the top of
  this file so the docs stay true on every release.

No schema change, no config change.

## v1.8.14 — Phase 4 autonomy: the learning loop

Mission outcomes now feed back into what the Director chooses to work on. Design per operator
review: read-time bias (stored priorities never drift — same philosophy as Phase 3 aging) and
auto-pause retirement with explicit events (never delete; a human reviews and resumes).

New:

- **Per-objective success EMA** (`objectives.success_ema`, schema v10 → v11, additive migration):
  every recorded run folds its mission success score into an exponential moving average
  (`autonomy_score_ema_alpha`, default 0.3; an unscored/failed run counts as 0). Always recorded
  — even with learning disabled — so history exists the moment it's turned on.
- **Selection bias** (`Autonomy/ObjectiveLearning.cs`, new): at selection time an objective's
  EMA adds a bounded, linear bias to its effective priority — EMA 1.0 → +`autonomy_priority_bias_max`
  (default 2), EMA 0.5 → 0, EMA 0.0 → −max. Computed read-time in
  `SqliteMemory.EffectivePriority` alongside Phase 3 aging; new objectives (null EMA) are
  unbiased. Operator numbers in the backlog stay authoritative.
- **Stale retirement**: after `autonomy_retire_min_runs` (default 5) runs, an objective whose EMA
  is below `autonomy_retire_score_threshold` (default 0.25) is auto-paused — it keeps running
  without producing value.
- **Loop retirement**: if the last `autonomy_loop_window` (default 4, 0 = off) generated goals
  are all near-identical (≥ `autonomy_dedupe_similarity` keyword overlap — the exact metric the
  Strategist's dedup uses), the objective is auto-paused. Catches the charter-fallback spiral:
  dedup already replaces repeat goals with the charter, so a true loop shows up as the same goal
  run after run.
- **Retirement = pause + event, never delete**: the Director emits an `objective_retired` event
  (code `stale_low_success` or `looping_goals`, with reason, EMA, and run count) and sets the
  objective to Paused, exactly like the existing failure circuit breaker. Resume from the
  Autonomy page after review. Retirement checks run on the director thread after each outcome is
  recorded, so nothing races the objective's own bookkeeping.
- **Config**: `autonomy_learning_enabled` (default true; false = exact Phase 3 behavior),
  `autonomy_priority_bias_max`, `autonomy_score_ema_alpha`, `autonomy_retire_min_runs`,
  `autonomy_retire_score_threshold`, `autonomy_loop_window` — all clamped, all in the settings
  whitelist; toggle + integer knobs editable from Settings → Colony.
- **Observability**: `/objectives` and the Autonomy page's backlog table gain a **Score** column
  (the EMA, color-coded); `autonomy_mission_finished` events and `/autonomy/status` include
  `success_ema` / `learning_enabled`.
- **Tests**: `LearningTests` — EMA seeding/smoothing/persistence, bias linearity and bounds,
  EMA-driven selection ordering (and its disappearance when learning is off), stale/loop/never
  retirement decisions.

## v1.8.13 — Fix: coder ant proposed Python patches regardless of the project's language

Reported from live use: send the colony an objective against this (C#) repo and the coder ant
comes back proposing Python. Root cause was leftover DNA from ANTHILL's original Python build
(`py.old/`) — three compounding biases, no model misbehavior:

- **CoderAnt's JSON format example showed a `.py` path** (`"file_path":
  "relative/path/to/file.py"`). Small local models imitate format examples very literally, so
  the example language became the answer language. Now a neutral `file.ext`, plus a new
  first-position rule: match the language/conventions of the files visible in context, and if no
  existing code is visible and the goal names no language, return an empty proposals list rather
  than guess.
- **FileAnt injected `anthill.py` as a candidate path** whenever a mission mentioned "anthill",
  "this script", or "main script" — a relic of the Python-era entry point that fed Python-flavored
  context to every downstream ant. Removed; candidate paths now come only from what the mission
  text actually names.
- **FileAnt's path-extraction regex couldn't see .NET paths**: its suffix list (`py|txt|md|...`)
  predated the port and omitted `cs|csproj|sln|props|targets` (and other patchable types:
  `sh|bat|ps1|cmd|go|rs|java|kt|rb|php|tf|hcl|sql`). A mission saying "fix
  src/Anthill.Api/ApiHost.cs" never surfaced that path as a read candidate. The list now matches
  `AnthillRuntime.PatchAllowedSuffixes`, so every file type the coder may patch is also one the
  file ant can spot and read — including the colony's own sources for self-modification missions.

No schema change, no API change, no config change.

## v1.8.12 — Phase 3 autonomy: concurrent missions + ResourceGovernor

The Director can now run up to `autonomy_concurrency` missions side by side (default 1 —
behavior is unchanged until the operator raises it; clamped 1–8). Design decisions per operator
review: strict-priority scheduling with anti-starvation aging, and a load/probe governor with
full VRAM tracking deferred to a later hardware-aware scheduler phase.

New:

- **`ResourceGovernor`** (`src/Anthill.Core/Autonomy/ResourceGovernor.cs`): sizes effective
  concurrency each cycle from the configured cap — and can only ever lower it. Signals:
  normalized CPU load per core (≥1.25 halves, ≥2.0 clamps to 1), available-memory fraction
  (≤20% halves, ≤10% clamps to 1), and an Ollama probe (`GET /api/version`, 15s cache —
  unreachable clamps to 1, ≥2.5s latency halves). Unreadable *host* signals fail open (skip);
  a dead *backend* fails safe (clamp to 1 — missions would fail anyway, don't multiply them).
  Skipped entirely when `use_ollama` is false, so offline installs are never clamped by it.
- **Concurrent Director loop** (`src/Anthill.Api/ColonyDirector.cs`): non-blocking launches with
  an in-flight table, reaped as jobs finish. Everything still happens on the one director thread
  (Strategist/BudgetGuard stay sequential by construction); the hard rails are re-checked before
  every individual launch. Stop/kill-switch now *drains*: no new launches, in-flight missions
  finish and are recorded, then the thread exits — nothing is ever left unrecorded.
- **Strict priority + aging** (`SqliteMemory.NextReadyObjectives`): slots fill with the
  highest-effective-priority distinct ready objectives; an objective never runs two missions at
  once. Effective priority = priority + 1 per `autonomy_aging_minutes` waited (default 30;
  0 = pure strict priority); longest-queued wins ties. Computed at read time — stored priorities
  never drift.
- **Config**: `autonomy_concurrency`, `autonomy_aging_minutes` — in `config.example.json`, the
  settings whitelist, `/settings`, and the Settings → Autonomy panel.
- **Observability**: `/autonomy/status` gains `concurrency_configured`/`concurrency_effective`,
  `governor_code`/`governor_reason`/`governor_signals`, `aging_minutes`, and `in_flight`;
  `autonomy_mission_started` events carry the governor verdict. Autonomy page: Concurrency KPI +
  In-flight and Governor rows.

Fixed (latent, pre-existing):

- **`Queen.LastMissionId` race**: with >1 job worker, a finishing worker could stamp its job with
  *another* worker's mission id. `Queen.RunMission` now reports the mission id through an
  `onMissionCreated` callback the moment the row is persisted, and `ApiJobRegistry` uses it —
  also making the mission id visible on the job while it's still running. `LastMissionId` remains
  for the single-mission CLI path.
- Job worker pool is sized `max(api_job_workers, autonomy_concurrency)` at boot so concurrent
  autonomous missions actually get worker slots instead of queueing behind each other.

Validation: `GovernorTests` (every clamp path, fail-open vs. fail-safe, tightest-constraint-wins,
throwing readers), multi-slot selection/aging tests in `AutonomyTests`, and an offline two-slot
Director run in `DirectorTests` asserting both objectives complete with distinct mission ids and
per-objective run records. Existing Phase 0–2 suites unchanged and still green.

## v1.8.11 — Fix: Autonomy page's Start/Stop (kill switch) froze the UI via infinite recursion

No schema change, no API change — pure front-end JS bug in `src/Anthill.Api/Ui/index.html`.
Reported live as "the web app and service crashes when you hit the kill switch in the autonomy
page." Reproduced by driving the real running instance directly: clicking the "■ Stop" kill
switch caused the browser tab to stop responding to input.

Root cause: `openAutonomy()` called `showPage('autonomy')` at its top, but `showPage()` itself
calls `PAGE_ENTER['autonomy']()` right after switching pages — and `PAGE_ENTER['autonomy']` was
wired to call `openAutonomy()` again. That's unbounded mutual recursion:
`openAutonomy → showPage → PAGE_ENTER.autonomy → openAutonomy → showPage → ...`. It fired on
*every* visit to the Autonomy page — including the periodic status refresh the page runs while
open — and threw `RangeError: Maximum call stack size exceeded` hundreds of times per trigger
(confirmed live via the browser console). Each occurrence briefly pegs the JS main thread as it
unwinds thousands of stack frames, which is what made the tab appear to hang or "crash" right as
a click (like Stop) landed. `openAntConfig()` (the Ant Config page) had the exact same bug
pattern, not yet reported but fixed here too. The .NET backend was never actually affected —
`/health` and the Director's own stop/start logic kept working the entire time this was
happening; confirmed via `/autonomy/status` and the `autonomy_stopped`/`autonomy_started` event
log entries recording correctly through repeated live reproduction.

Fixed:

- **`openAutonomy()`**: no longer calls `showPage('autonomy')` — it's now a pure data-loader,
  correct since its only caller is `PAGE_ENTER['autonomy']`, which `showPage()` already invokes
  *after* switching to the page.
- **`openAntConfig()`**: same fix, same reasoning (its `showPage('antconfig')` call is gone; its
  second caller, the Ant Config "Reset" button, doesn't need a page-switch either since the user
  is already on that page when clicking Reset).

Validation:

- Reproduced and fixed live against the user's running LXC instance via direct browser
  automation: captured the exact `RangeError` stack trace from the browser console, hot-patched
  the corrected function into the live page, then repeated the same Start → Stop sequence with
  the patch active — no errors, no hang, instant response both times. `bash -n`/syntax not
  applicable (HTML/JS); confirmed by the live before/after test described above. Ship this build
  to make the fix permanent (the hot-patch only lived in that one browser tab's memory).

## v1.8.10 — Fix: LXC upgrade republish silently dropped the SQLite native library

No schema change. Bug found live re-running `deploy/lxc/setup.sh` on the user's LXC instance
immediately after the v1.8.9 ETXTBSY fix — the very first time a republish onto that install
directory ever ran to full completion. The service came up, then immediately crashed in a
restart loop:

```
Unhandled exception. System.TypeInitializationException: The type initializer for
'Microsoft.Data.Sqlite.SqliteConnection' threw an exception.
 ---> System.DllNotFoundException: Unable to load shared library 'e_sqlite3' or one of its
dependencies.
/opt/anthill/bin/e_sqlite3.so: cannot open shared object file: No such file or directory
```

Root cause: that same install directory had been publish-targeted several times across this
session's earlier v1.8.7/v1.8.8/v1.8.9 attempts, including at least one run that was killed
mid-bundle by the ETXTBSY bug itself. `dotnet publish` reused the leftover `obj/`/`bin`
incremental state from those prior (partially-failed) runs and decided the RID-specific SQLite
native asset (`e_sqlite3.so`) was already up to date — so it skipped copying it into the output
directory, even though it wasn't actually there. The resulting single-file binary builds, starts,
and immediately SIGABRTs the moment it touches the database.

Fixed:

- **`deploy/lxc/setup.sh`**: wipes `obj/`/`bin` for `Anthill.Cli`, `Anthill.Core`, and
  `Anthill.Api` immediately before every publish, so install/upgrade is always a from-scratch
  build rather than trusting incremental state that a prior interrupted run may have left
  inconsistent. Adds a post-publish check that fails loudly (with a clear error) if no
  `e_sqlite3` native library made it into the output directory, instead of letting it surface
  later as a silent SIGABRT crash loop under systemd.

Validation:

- Found via the user's real upgrade attempt on their LXC instance — full stack trace confirmed
  root cause precisely (`SqliteConnection..cctor` → `SQLitePCL.Batteries_V2.Init` →
  `DllNotFoundException`). Fix itself has **not been re-verified live** — no LXC/Proxmox host or
  dotnet SDK available in the environment this was authored in. `bash -n` syntax check passes.
  Confirm by re-running `bash deploy/lxc/setup.sh` and checking `ls /opt/anthill/bin/*e_sqlite3*`
  finds the native library, then that the service stays up (`systemctl status anthill`).

## v1.8.9 — Fix: LXC upgrade-in-place failed with "Text file busy"

No schema change. Bug found live re-running `deploy/lxc/setup.sh` to upgrade an already-running
LXC install to v1.8.8: `dotnet publish` failed with
`System.IO.IOException: Text file busy : '/opt/anthill/bin/anthill'` inside the `GenerateBundle`
MSBuild task.

Root cause: `setup.sh` republishes directly into `$INSTALL_DIR/bin`, which is exactly where the
systemd unit's `ExecStart` runs the binary from. .NET's single-file bundler does an in-place file
copy rather than write-to-temp-then-atomic-rename, and Linux refuses to open a currently-executing
binary for direct write access (`ETXTBSY`) — replacing a running program's file via `rename()` is
fine, overwriting it in place while it's executing is not. First-time installs never hit this
(nothing running yet); every subsequent upgrade-in-place did, 100% of the time.

Fixed:

- **`deploy/lxc/setup.sh`**: stops the `anthill` systemd unit immediately before the publish step
  (`systemctl stop anthill 2>/dev/null || true` — safe no-op on a first install, since the unit
  doesn't exist yet). The existing `systemctl restart anthill` at the end of the script already
  starts it back up regardless of whether it was freshly installed or stopped for an upgrade, so
  this was a one-line, symmetrical fix.

Validation:

- Found via the user's real upgrade attempt on their LXC instance — full MSBuild stack trace
  confirmed root cause precisely (`Microsoft.NET.HostModel.Bundle.Bundler.GenerateBundle` →
  `SafeFileHandle.Open` → `ETXTBSY`). Fix itself has **not been re-verified live** — no LXC/Proxmox
  host or dotnet SDK available in the environment this was authored in. `bash -n` syntax check
  passes. Confirm the fix by re-running `bash deploy/lxc/setup.sh` on the same instance and
  checking it completes without stopping mid-publish this time.

## v1.8.8 — Fix: provider Base URL override sent as a bare prefix, not a real endpoint

No schema change. Bug found live on a real LXC deployment: testing the OpenAI connection in
Settings → Providers failed every time with `ERROR: OpenAI request failed (404): `.

Root cause: the stored `base_url` override (`https://api.openai.com/v1`) was used as the literal
request URL in `OpenAiCompatibleClient`, with no path appended — so the request actually hit
`https://api.openai.com/v1`, not a real API route, and OpenAI correctly 404'd it. The value that
was stored is exactly how OpenAI's own SDKs define `base_url` (host + version prefix only, path
appended internally), so typing it that way into the override field is a completely reasonable
thing to do even though the field's placeholder shows the full path — the code should tolerate
both forms rather than silently breaking on one of them.

Fixed:

- **`OpenAiCompatibleClient.NormalizeEndpoint`** (covers OpenAI, Perplexity, and OpenRouter, which
  all share this client): if a configured endpoint doesn't already end with `/chat/completions`,
  it's appended automatically. Handles a trailing slash either way. Applied in the constructor, so
  it self-corrects for any already-stored override without needing a database fix or the user to
  re-save anything.
- **`AnthropicClient`**: previously didn't accept a `base_url` override at all —
  `ModelRouter.BuildKeyedClient`'s `"anthropic"` branch built the client with only the API key and
  model, silently discarding whatever was stored in `provider_credentials.base_url`. Now accepts
  an optional endpoint, normalized the same way (`/messages` appended if missing), wired through
  from `ModelRouter`.
- **`tests/Anthill.Tests/ProviderTests.cs`**: added `NormalizeEndpoint` coverage for both
  providers — bare prefix, full path, and either with/without a trailing slash — plus confirms
  `AnthropicClient` falls back to its documented default when no override is stored. Made both
  `NormalizeEndpoint` methods `public` (were `private`) specifically so this is directly
  unit-testable without a network call.

Validation:

- Found via live testing against a real running LXC instance (`10.10.10.60:8713`, connected via
  browser automation) — reproduced the exact failing request, read the actual response body
  (`ERROR: OpenAI request failed (404): `) and the stored `base_url` from `GET /providers`,
  confirmed the root cause by reading `OpenAiCompatibleClient`/`ModelRouter` against that data.
  The fix itself has **not yet been re-verified live** — no `dotnet` SDK available in the
  environment this was authored in. Brace/paren balance checked manually. Once deployed
  (`git pull && bash deploy/lxc/setup.sh` on the LXC box, or a new tagged release), re-run Test
  Connection on OpenAI and confirm it now succeeds.

## v1.8.7 — LXC deployment

No schema change. Second step of the container/LXC/Windows-Service deployment push (see
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)) — a one-shot installer for a fresh Debian/Ubuntu-family
LXC container, Proxmox or otherwise.

Added:

- **`deploy/lxc/setup.sh`** — unattended installer/upgrader for a fresh Debian 12+/Ubuntu 22.04+
  LXC container. Installs the .NET 9 SDK if missing (Microsoft's apt repo, resolved dynamically
  from `/etc/os-release` rather than hardcoded to one distro/version, with a `dotnet-install.sh`
  fallback for distros/versions Microsoft's repo doesn't have an entry for), clones/updates the
  repo, publishes a self-contained `linux-x64` binary, creates a dedicated unprivileged system
  user, installs + enables the systemd unit, and starts it. Idempotent — re-running it is the
  upgrade path (pulls latest, republishes, restarts). Configurable via `ANTHILL_REPO_URL`,
  `ANTHILL_INSTALL_DIR`, `ANTHILL_SERVICE_USER` env vars.
- **`deploy/lxc/anthill.service.template`** — the systemd unit `setup.sh` installs, with the same
  hardening as the manual systemd install already documented in the README (`NoNewPrivileges`,
  `PrivateTmp`, `ProtectSystem=strict`, scoped `ReadWritePaths`), plus `Environment=ANTHILL_HOME`
  for unambiguous workspace resolution and a generated `/etc/anthill/token.env` for an optional
  static API token.
- No special LXC features 