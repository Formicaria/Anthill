# ANTHILL Console — Information Architecture & UX Redesign Proposal

Status: Product/UX architecture proposal (not an implementation task). Grounded in an audit of the
current `src/Anthill.Api/Ui/index.html` and the subsystem docs (NORTH_STAR, HOMELAB, AUTONOMY,
APPROVALS). Goal: turn a single organically-grown HTML page with ~16 equal-weight tabs into a
cohesive, routable, enterprise operations platform — **preserving every existing capability** and
reorganizing around user workflows rather than build history.

Reference products for the target feel: Azure Portal, Microsoft Defender, CrowdStrike Falcon,
vCenter, Proxmox VE, Grafana, GitHub Enterprise, Kubernetes Dashboard, Datadog, Rancher.

---

## Implementation status (living)

**Live-verified** on a deployed build (browser walkthrough): the grouped sidebar, hash routing +
deep links (`#/dashboard`, `#/monitoring/activity`, `#/infrastructure/compute`, `#/colony/agents`),
clickable breadcrumbs, contextual sub-nav, the unified Activity page + facets, the Infrastructure
rename with **bidirectional** sidebar↔in-page sub-nav sync, and the Agents Configure/Inspect tabs
all work with no errors. The walkthrough surfaced stale in-page `<h1>` titles on renamed pages
(e.g. the Infrastructure page still read "Homelab"); those were aligned to the new names
(Infrastructure, Changes & Approvals, Mission Results, Objectives, Events, Signals & Memory
Explorer, Agent Configuration, Agent Inspector, Automation, Terminal).

**Phase 1 — IA / routing spine: IMPLEMENTED** (front-end only; `src/Anthill.Api/Ui/index.html`).
Internal page ids are unchanged, so every existing caller of `showPage(id)` keeps working; the new
IA is a presentation + routing layer on top.

- Config-driven grouped sidebar (`IA` config → `buildNav()`): 7 domains + Dashboard, role-aware
  visibility (`all` / `admin` / `hl`), collapsible domain groups. Replaces the 16 flat items.
- Real hash routing: 35 deep-linkable routes (`ROUTE_TABLE`), `go(route)`, `router()` + `popstate`
  (back/forward), and a `LEGACY_REDIRECT` table mapping every old `#page` id to its new route.
- Breadcrumbs (`#hdr-crumbs`) replace the single page title; `updateChrome()` drives them.
- Contextual sub-navigation (`#domain-subnav`) for the grouped domains — Activity
  (Events / Mission Results / Changes / Autonomous Runs / Infra Changes), Missions
  (Console / History), Automation (Director / Objectives / Rules), Agents (Configure / Inspect).
- Enterprise renaming applied in nav + breadcrumbs + command palette: Homelab → Infrastructure,
  Overview → Dashboard, Pheromones → Signals, Autonomy → Automation, Ant Config/Inspector →
  Agents, Patch Center → Changes & Approvals, Event Log/Results → Activity, Shell → Terminal.
- All 11 Homelab sub-pages are reachable through the new domains (Infrastructure + Monitoring);
  live nav badges (jobs / patches / autonomy) are preserved on the new nav children.
- Validated: `node --check` on the embedded JS is clean; the UI-integrity guards (no duplicate
  ids, no U+FFFD, no flattened `?` glyphs) all pass.

**Phase 2 — navigation cohesion: IMPLEMENTED** (front-end only).

- The Homelab in-page sub-nav (`#hl-subnav`) now drives the router: clicking a sub-page (or the
  `1`–`-` keyboard shortcuts) updates the breadcrumb, sidebar highlight, and URL via `HLSUB_ROUTE`
  + `hlSubShow(name, fromRoute)`. Entry points no longer desync.
- Breadcrumb segments are clickable — the domain crumb jumps to its first section, the section
  crumb to the section route (`DOMAIN_HOME` + per-crumb targets, role-gated).
- Collapsed rail shows each domain's children as a hover fly-out, so navigation stays usable when
  the sidebar is minimized.
- Validated: `node --check` clean; duplicate-id and glyph guards pass.

**Phase 3 — content consolidation: IN PROGRESS.**

- **Unified Activity center: IMPLEMENTED.** A new `page-activity` renders one filtered timeline
  over the same `/events/json` stream the Event Log uses, with category facets (All / Missions /
  Changes / Autonomy / Infrastructure / System) mapping event-type prefixes to operator workflows,
  plus free-text search. It is the Activity domain landing ("All" tab); the Event Log, Mission
  Results, Changes, Autonomous Runs, and Infra Changes pages are **untouched** and remain reachable
  as tabs — so the change is purely additive and degrades gracefully. Reuses the proven
  `evTypeClass` / `ANT_MAP` / `setEl` render helpers. `node --check` clean; id/glyph guards pass.

- **Patch Center → Approvals + Changes split: IMPLEMENTED.** Operations now carries two distinct
  sections/routes — **Approvals** (`/operations/approvals`, the list pre-filtered to `pending`, the
  pending-approvals badge lives here) and **Changes** (`/operations/changes`, the full history +
  apply/rollback). Done as two route-driven *views* over the one patches page: `showPage('patches',
  {view})` sets the `pcFilters.status` filter and relabels the header (`pc-title`/`pc-sub`) before
  `PAGE_ENTER` reloads — no DOM moved, so it's non-destructive.

**Still deferred (Phase 3 remainder — deeper page-content surgery; land one at a time with browser
verification on a deployed build):** a single Agents *blade* with View/Edit/Live modes (today
Configure/Inspect are sub-nav tabs over the two existing pages); extracting Model Routing as its
own Colony page. The version bump to **v2.6.0** should accompany completion of this phase.

---

## 0. Executive summary

The console today is one page (`/ui`) with no routing, sixteen first-level nav items in four
inconsistently-themed groups ("Operations" contains Homelab; "Configuration" contains Autonomy,
Security, and Shell), and one item — Homelab — that secretly holds eleven sub-pages while
everything else is flat. The same information (activity/history) is exposed in six places; the same
object (agents/roles) is split across two pages that only differ by view-vs-edit; and the same
workflow (mission execution) appears as three separate destinations (Missions, Autonomy, and the
Overview command box).

The proposal collapses sixteen flat items into **seven workflow domains** with real parent/child
routing, a unified Activity/history center, a single Agents experience, a single Missions &
Automation surface, a properly structured Settings area, and enterprise naming (most importantly,
**Homelab → Infrastructure**). It is designed to absorb 3–5× more features without adding
first-level nav items, and it ships behind a phased, backwards-compatible migration that keeps the
current page working while routes and modules are introduced one domain at a time.

---

## 1. Current-state audit (what exists today)

### 1.1 Top-level navigation (as built)

| Group (as-is) | Items |
|---|---|
| Main | Overview, Colony, Missions |
| Operations | Event Log, Results, Patch Center, Objectives, Pheromones, Homelab |
| Configuration (admin) | Ant Config, Ant Inspector, Autonomy, Security, Shell, Settings, Users |

Global chrome (persistent header): page title, active-mission indicator, system status chip
(mode/model/connectivity popover with shortcuts to "Change models" → Ant Config and "Providers" →
Settings), notifications bell, pending-approvals bell, sign-out. A `HOMELAB_STOP` kill switch lives
inside the Homelab Actions panel.

### 1.2 Every page and its real contents

- **Overview** (`page-overview`) — telemetry bar; 6 cards (Colony Health, System Core → Colony,
  Missions, Pending Approvals → Patch Center, Resource Usage, Quick Actions); Operator Attention;
  **Mission Command** (directive input + Inspect/Verify/Patch/Build modes + Preview Plan); Recent
  Events. *This page already re-hosts Missions, Approvals, Events, and Resources.*
- **Colony** (`page-colony`) — the swarm topology / "System Core" visualization.
- **Missions** (`page-missions`) — "New Mission" builder/dispatch.
- **Event Log** (`page-events`) — colony event stream.
- **Results** (`page-results`) — mission/run outputs.
- **Patch Center** (`page-patches`) — patch approvals + apply/rollback + execution history.
- **Objectives** (`page-objboard`) — "Objective Command Board".
- **Pheromones** (`page-pheromones`) — coordination-signal view.
- **Homelab** (`page-homelab`, "Homelab Command Center") — 11 sub-pages (Overview, Services,
  Virtualization, Containers, Storage, Networking, Monitoring, Automation, Apps, Alerts, Activity)
  spanning: Service Deck, Widgets, Apps, "What Should I Do Next", Backup Intelligence, Automation
  Rules, Dependency Graph, Health, Incidents, Actions, VMs, Containers, Storage Pools, Targets
  (allow/block), Network Devices, Security & Risk Findings, Inventory (hosts/services/ports/
  dependencies), Recent Changes, register host/service/device/dependency/*arr app, Subsystem
  Status, Virtualization Connections.
- **Ant Config** (`page-antconfig`) — per-role agent configuration (incl. model selection).
- **Ant Inspector** (`page-antobs`) — agent search / inspection (read).
- **Autonomy** (`page-autonomy`) — Director Status, Add Objective, Objectives, Recent Autonomous
  Runs, Completed Objectives.
- **Settings** (`page-settings`) — Operator Account, API Endpoint, Ollama Status, Model Provider
  Connections, Ollama Connection, Feature Toggles, Limits, Autonomy (settings), Active Routes,
  Available Models, System Information, Diagnostics, Maintenance.
- **Security** (`page-security`) — Posture, Capability Gates, Workspace Boundary, Operator Shell,
  Autonomous Auto-Apply.
- **Shell** (`page-shell`) — operator terminal.
- **Users** (`page-users`) — operator accounts.

### 1.3 The five structural problems (evidenced)

1. **No routing / one page.** Every "page" is a `<div class="page">` toggled by `showPage()`.
   Nothing is linkable, bookmarkable, or back-button-able. State lives in one DOM.
2. **No hierarchy.** Fifteen destinations sit at depth 1; Homelab alone sits at depth 2 with 11
   children. Importance is not encoded in the structure.
3. **Redundancy.** Activity/history is shown in ≥6 places (Event Log, Results, Patch Center
   history, Homelab → Activity, Homelab → Recent Changes, Autonomy → Recent Autonomous Runs, plus
   the Overview "Recent Events" card). Objectives appear both as a top-level page *and* inside
   Autonomy. Model/provider settings are split across Ant Config, Settings, and the status chip.
4. **Naming.** "Homelab" (hobbyist), "Pheromones" and "Ant Config/Inspector" (opaque/branded
   without an enterprise anchor), "Objective Command Board" (grandiose), "Shell" (ambiguous vs
   Security → Operator Shell).
5. **Grouping.** The three sidebar sections don't map to anything a user reasons about: Homelab is
   under "Operations," while Autonomy/Security/Shell are under "Configuration."

---

## 2. Design principles

1. **Workflow first, not implementation history.** Group by what an operator is trying to do.
2. **Three personas drive the top level** (see §7): SOC analyst (monitor), systems administrator
   (infrastructure), security engineer (policy/automation).
3. **One home for every datum.** Each piece of information has exactly one authoritative page;
   everywhere else *links* to it rather than re-rendering it.
4. **Progressive disclosure.** Landing pages summarize; detail lives one click down. No page dumps
   everything at once.
5. **Real routing.** Deep-linkable URLs, breadcrumbs, back/forward, shareable views.
6. **Scale by depth, not by width.** New features slot into an existing domain as children; the
   first-level count stays fixed (~7).
7. **Preserve capability.** Nothing is deleted; a few things merge or move. Reorganization over
   removal.

---

## 3. Target information architecture (sitemap)

Seven persistent domains plus a standalone Dashboard. Every leaf maps to an existing capability
(the "from" is shown where it moves).

```
Dashboard                                   (from: Overview)

Monitoring
├── Activity                                (UNIFIED history center)
│   ├── Events                              (from: Event Log)
│   ├── Mission Results                     (from: Results)
│   ├── Changes & Patches                   (from: Patch Center → history tab)
│   ├── Autonomous Runs                     (from: Autonomy → Recent/Completed Runs)
│   └── Infrastructure Changes              (from: Homelab → Recent Changes / Activity)
├── Alerts                                  (from: Homelab → Alerts/Incidents + Risk Findings)
└── Analytics                               (future; learning-loop / outcome metrics)

Operations
├── Missions
│   ├── Console                             (from: Overview Mission Command + Missions "New")
│   ├── Library                             (mission templates/charters; future-forward)
│   └── History                             (from: Results — cross-linked, canonical in Monitoring)
├── Automation                              (from: Autonomy)
│   ├── Director                            (from: Autonomy → Director Status)
│   ├── Objectives                          (from: Objectives page + Autonomy objectives — MERGED)
│   ├── Schedules                           (existing scheduled tasks)
│   └── Policies                            (autonomy budgets/concurrency/auto-enqueue)
├── Approvals                               (unified queue: patches + homelab actions)
└── Changes                                 (from: Patch Center — proposals, apply, rollback)

Infrastructure                              (RENAMED from: Homelab)
├── Overview                                (from: Homelab → Overview / Service Deck)
├── Compute                                 (from: Homelab → Virtualization + Containers)
├── Storage                                 (from: Homelab → Storage)
├── Network                                 (from: Homelab → Networking + Network Devices)
├── Services                                (from: Homelab → Services + Apps + Inventory + Deps)
├── Health & Incidents                      (from: Homelab → Monitoring + Alerts + Incidents)
├── Backups                                 (from: Homelab → Backup Intelligence)
└── Automation Rules                        (from: Homelab → Automation; cross-linked to Ops)

Colony                                      (the swarm runtime / brand identity)
├── Topology                                (from: Colony / System Core)
├── Agents                                  (from: Ant Config + Ant Inspector — MERGED)
├── Signals                                 (from: Pheromones)
└── Model Routing                           (from: Ant Config models + Settings providers/Ollama)

Security
├── Posture                                 (from: Security → Posture)
├── Capability Gates                        (from: Security → Capability Gates)
├── Access Policy                           (from: Homelab → Targets allow/block + Workspace Boundary)
├── Auto-Apply Policy                       (from: Security → Auto-Apply + Settings → Autonomy safety)
├── Credentials                             (from: homelab credential store)
└── Risk Findings                           (from: Homelab → Security & Risk; cross-linked to Alerts)

Administration
├── Users                                   (from: Users)
├── Roles & Permissions                     (from: capability tiers / permissions)
├── Settings                                (RESTRUCTURED — see §6)
└── Tools
    └── Terminal                            (from: Shell + Security → Operator Shell — MERGED)
```

Depth is capped at three (`Domain → Section → Detail`); entity drawers (an agent, a VM, an
incident) open in place over any list rather than becoming new pages.

---

## 4. Navigation model

### 4.1 Primary sidebar (grouped, collapsible)

Seven domain headers, each expanding to its children. The current flat rail becomes a two-level
rail where the domain is always visible and the active domain's children are revealed inline
(Azure/Defender pattern). Collapsed rail shows domain icons only with fly-out children.

```
▸ Dashboard
▾ Monitoring        Activity · Alerts · Analytics
▸ Operations        Missions · Automation · Approvals · Changes
▸ Infrastructure    Overview · Compute · Storage · Network · Services · Health · Backups
▸ Colony            Topology · Agents · Signals · Model Routing
▸ Security          Posture · Capability Gates · Access Policy · Auto-Apply · Credentials · Risk
▸ Administration     Users · Roles · Settings · Tools
```

Role-aware: SOC analysts default to Monitoring expanded; sysadmins to Infrastructure; security
engineers to Security/Operations. Admin-only domains (Administration, parts of Security) hide for
non-admins exactly as the current `nav-section-admin` gating already does.

### 4.2 Top bar (global, persistent)

Keeps today's chrome and formalizes it: **breadcrumb** (replaces the single page title), **global
command/search** (⌘K — jump to any page, run a mission, find an agent/VM/incident; this subsumes
the Overview Mission Command box so dispatch is possible from anywhere), **active-mission
indicator**, **system status chip**, **notifications**, **approvals**, **kill switch** (promote
`HOMELAB_STOP` from inside Homelab Actions to a global control), **account menu**.

### 4.3 Breadcrumbs

`Domain / Section / Detail`, e.g. `Infrastructure / Compute / media-vm` or
`Operations / Automation / Objectives`. Breadcrumb segments are links; the last is the current
context. This replaces the flat `hdr-page-title`.

### 4.4 Contextual sub-navigation

Within a section that has tabs (e.g. Activity's five streams, Missions' Console/Library/History),
use a horizontal sub-nav under the breadcrumb — the pattern Homelab already pioneered with its 11
sub-pages, now applied consistently. Keyboard: keep the `g`-key jumps (extend to
`g m` Monitoring, `g o` Operations, `g i` Infrastructure, `g c` Colony, `g s` Security, `g a`
Administration) plus number keys for sub-nav, mirroring the existing homelab behavior.

---

## 5. Page consolidation decisions

Every existing destination, with disposition and rationale.

| Existing page | Action | Target | Why |
|---|---|---|---|
| Overview | **Rename → Keep** | `Dashboard` | Enterprise term; stays the persona-aware landing. Its embedded Mission Command graduates into the global ⌘K/command bar. |
| Colony | **Keep + Move** | `Colony → Topology` | Core brand/runtime view; becomes a child of the Colony domain rather than a peer of everything. |
| Missions | **Merge** | `Operations → Missions → Console` | Dispatch/builder unifies with the Overview command box; one place to launch work. |
| Event Log | **Move** | `Monitoring → Activity → Events` | Redundant as a top-level; it is one stream of the unified Activity center. |
| Results | **Merge/Move** | `Monitoring → Activity → Mission Results` (linked from `Operations → Missions → History`) | "Results" is mission run history — belongs in Activity; surfaced contextually under Missions. |
| Patch Center | **Split** | `Operations → Changes` (proposals/apply/rollback) + `Operations → Approvals` (queue) + `Monitoring → Activity → Changes & Patches` (history) | One page conflates a work surface, an approval queue, and a history log — three different jobs for three personas. |
| Objectives | **Merge** | `Operations → Automation → Objectives` | Duplicates the Objectives inside Autonomy; the Director works this exact backlog. One home. |
| Pheromones | **Rename + Move** | `Colony → Signals` | Keep the concept; "Signals" reads as enterprise, "Pheromones" can be the in-page subtitle to preserve identity. |
| Homelab | **Rename + Restructure** | `Infrastructure` (+ children) | Hobbyist name; its 11 sub-pages become proper sections (see §3). |
| Ant Config | **Merge** | `Colony → Agents` (Edit/Configuration) | Same object as Ant Inspector; edit and view should be modes of one page. Model selection moves to `Colony → Model Routing`. |
| Ant Inspector | **Merge** | `Colony → Agents` (View/Live State) | See above; eliminates a whole page and a navigation hop. |
| Autonomy | **Rename + Restructure** | `Operations → Automation` (Director/Objectives/Schedules/Policies) | Autonomy *is* mission execution with orchestration; it belongs beside Missions, not under "Configuration." |
| Security | **Restructure** | `Security` (Posture/Gates/Access Policy/Auto-Apply/Credentials/Risk) | Promote to a first-class domain; absorb scattered policy (Targets, Workspace Boundary, Auto-Apply, Credentials, Risk Findings). |
| Shell | **Merge** | `Administration → Tools → Terminal` | Ambiguous top-level; unify with Security's "Operator Shell" (same capability) under Tools. |
| Settings | **Restructure** | `Administration → Settings` (see §6) | Flat grab-bag; split into conventional sub-pages; model/provider config moves to `Colony → Model Routing`. |
| Users | **Move** | `Administration → Users` | Belongs with Roles under Administration, not as a peer of Homelab. |

Net: **16 first-level items → 7 domains + Dashboard**; two full pages eliminated by merges (Ant
Inspector into Agents, Shell into Tools); zero capabilities removed.

---

## 6. Settings restructure

Today Settings mixes account, connectivity, model routing, feature toggles, limits, autonomy,
diagnostics, and maintenance in one flat list. Split along enterprise lines, and **move model
routing out** to `Colony → Model Routing` (where Ant Config's model selection also lands):

```
Administration → Settings
├── General            App/API endpoint, workspace, appearance/theme, reduced-motion
├── System             System Information, version/update, Diagnostics, Maintenance
├── Feature Toggles    Capability flags + Limits (budgets, concurrency ceilings)
├── Notifications      Slack/Discord/webhook channels (from homelab notifications)
├── Integrations       Providers & connectors registry (external model providers live in Colony → Model Routing; this lists non-model integrations)
├── Appearance         Theme, density, motion (if not folded into General)
└── About              License, build, links
```

Users, Roles & Permissions, and Tools are peers of Settings under Administration (not inside it),
matching Azure/GitHub where identity and settings are distinct.

Cross-domain policy that is *security*, not preference — Capability Gates, Auto-Apply Policy,
Access Policy (Targets/Workspace Boundary), Credentials — lives under **Security**, with
deep-links from Settings so operators can still find it from either mental model.

---

## 7. User-journey analysis

### 7.1 Personas → default surfaces

- **SOC analyst** → lands on **Dashboard**, lives in **Monitoring** (Activity, Alerts) and watches
  **Operations → Approvals**. Wants glanceable status, recent activity, mission state.
- **Systems administrator** → lives in **Infrastructure** (Compute/Storage/Network/Services/
  Health) and **Operations → Changes**. Wants inventory, health, patching.
- **Security engineer** → lives in **Security** (Policies, Credentials, Risk) and
  **Operations → Automation**. Wants gates, detections, credentials, automation policy.

### 7.2 Common workflows, before → after

- **Viewing infrastructure.** *Before:* click "Homelab" (buried in Operations), then hunt among 11
  sub-pages with no URL. *After:* `Infrastructure` domain with named sections and deep links
  (`/infrastructure/compute`); breadcrumb shows exactly where you are; a bookmark returns you there.
- **Running a mission.** *Before:* three entry points (Overview box, Missions page, Autonomy) with
  unclear differences. *After:* dispatch from the global ⌘K bar anywhere, or
  `Operations → Missions → Console`; autonomous runs are the same workflow under
  `Operations → Automation`. One mental model.
- **Viewing results.** *Before:* "Results" and "Event Log" and Patch history are separate tabs you
  cross-check manually. *After:* `Monitoring → Activity` unifies all streams with a type filter;
  Missions → History links straight to the mission-results stream.
- **Managing users.** *Before:* "Users" sits beside "Homelab"; permissions are elsewhere. *After:*
  `Administration → Users` next to `Roles & Permissions` — one identity area.
- **Applying patches.** *Before:* Patch Center conflates queue + apply + history. *After:* approve
  in `Operations → Approvals`, act in `Operations → Changes`, audit in `Monitoring → Activity`.
  Each persona touches the surface they need.
- **Inspecting agents.** *Before:* Ant Inspector to look, Ant Config to change — two pages, a
  round-trip per edit. *After:* `Colony → Agents` with View / Edit / Live State modes on one page,
  like a cloud resource blade (Properties vs Configuration vs Live metrics).
- **Viewing event history.** *Before:* scattered across ≥6 places. *After:* one Activity center;
  everything else links into it.

---

## 8. Future scalability (3–5× growth without clutter)

The seven-domain frame is a fixed spine; growth happens as **new sections inside a domain**, never
as new first-level items.

- **New integrations** (the R5 waves — media servers, monitoring, networking, notify/auth/dev)
  land as entries under `Infrastructure → Services` and `Colony → Model Routing`/`Integrations`,
  driven by the existing catalog — no nav change.
- **New detections / analytics** slot under `Monitoring → Analytics` and `Security → Risk Findings`.
- **New automation** (more triggers, playbooks) extends `Operations → Automation` sections.
- **New agent types / roles** extend `Colony → Agents` filters, not the nav.
- **Multi-environment / multi-tenant** (a likely future) fits as a top-bar **environment switcher**
  (vCenter/Azure subscription pattern) without touching domain structure.
- **Guardrails:** cap first-level domains at ~8; a domain that exceeds ~7 sections is a signal to
  introduce a sub-hub, not a new top-level item; every new feature must declare its one home
  (mirroring the R3 "one datum, one home" audit rule already in the codebase).

---

## 9. Implementation plan (phased, backwards-compatible)

The current console is a single `index.html` with a `showPage()` switch and per-page `<div>`s. The
migration introduces routing and modular structure **without a big-bang rewrite**, one domain at a
time, keeping the old page reachable throughout.

### Phase A — Routing foundation (no visual change)
- Introduce a hash or history router (`/dashboard`, `/monitoring/activity`, …) mapping 1:1 to the
  current `showPage()` targets. Every existing page gets a canonical URL; `showPage()` becomes the
  router's render step.
- Add a redirect table from legacy in-app links to new routes (see URL migration below).
- **Risk/mitigation:** deep-link regressions — mitigate by generating routes from the existing page
  registry so no page is missed; keep `showPage(id)` working as an alias.

### Phase B — Sidebar reshape + breadcrumbs
- Replace the 4-group flat rail with the 7-domain grouped rail; add breadcrumbs bound to the router.
- No pages move yet — domains simply *point* at existing pages. This is reversible and low-risk.

### Phase C — Unify Activity (highest redundancy payoff)
- Build `Monitoring → Activity` as tabs that reuse the existing Event Log, Results, Patch history,
  autonomous-runs, and homelab-changes renderers (they already exist — this is composition, not
  rewrite). Point old nav items at the new tabs; leave the old pages as redirects.

### Phase D — Merge Agents and Missions/Automation
- Combine Ant Config + Ant Inspector into `Colony → Agents` (View/Edit/Live modes reusing both
  renderers). Merge Objectives into `Operations → Automation`; fold the Overview command box into
  the global command bar.

### Phase E — Rename + restructure Infrastructure, Security, Settings
- Homelab → Infrastructure (the 11 sub-pages already exist as `data-hlsub` sections — this is a
  relabel + regroup, not new UI). Promote Security to a domain and pull in Targets/Workspace/
  Auto-Apply/Credentials/Risk. Split Settings per §6; move model routing to Colony.

### Phase F — Cleanup
- Remove dead redirects after a deprecation window; delete the emptied Ant Inspector/Shell pages;
  finalize keyboard map and ⌘K.

### Cross-cutting engineering notes
- **Component reuse:** the widget runtime (R2), the `collectionManager` (R4), entity drawers, and
  the homelab sub-page engine (R3) are the reusable primitives — the redesign is mostly *routing +
  regrouping + relabeling around them*, which is why it can be incremental.
- **State management:** page state currently lives in one DOM/global scope. Introduce per-route
  view state (and lazy render on route enter, teardown on exit — the widget runtime already stops
  polling off-DOM, so extend that discipline to all sections). Persisted per-operator layout
  (`/ui/state`) generalizes to per-route preferences.
- **Backward compatibility:** every legacy route/anchor 301-style redirects to its new home for at
  least one minor version; `g`-key shortcuts keep working and gain the new domain jumps; the API is
  untouched (this is a front-end IA change only).
- **URL migration table (excerpt):** `#overview → /dashboard`, `#events → /monitoring/activity/events`,
  `#results → /monitoring/activity/mission-results`, `#patches → /operations/changes`,
  `#objboard → /operations/automation/objectives`, `#homelab → /infrastructure/overview`,
  `#antconfig`/`#antobs → /colony/agents`, `#autonomy → /operations/automation`,
  `#pheromones → /colony/signals`, `#shell → /administration/tools/terminal`,
  `#users → /administration/users`, `#settings → /administration/settings/general`.
- **Risks & mitigations:** (1) *scope creep into a rewrite* → enforce "regroup, don't rebuild" per
  phase; (2) *muscle-memory disruption* → redirects + keep old shortcuts + a one-time "things
  moved" tour; (3) *permissions drift* → reuse the existing admin-gating on the new domains;
  (4) *SEO/bookmarks* → ship the redirect table before renaming; (5) *partial migration confusion*
  → each phase leaves the app fully coherent (domains can point at old pages until moved).

---

## 10. Naming reference (hobbyist → enterprise)

| Current | Proposed | Note |
|---|---|---|
| Homelab / Homelab Command Center | **Infrastructure** | Primary fix. "Infrastructure" is the enterprise standard (vCenter/Rancher). |
| Overview | **Dashboard** | Conventional landing term. |
| Pheromones | **Signals** (subtitle: "pheromone coordination") | Keep identity, lead with the enterprise word. |
| Ant Config + Ant Inspector | **Agents** (View / Edit / Live State modes) | One resource blade. |
| Autonomy | **Automation** (Director inside) | Names the capability, not the adjective. |
| Objective Command Board | **Objectives** (under Automation) | Drop the grandiosity; one home. |
| Patch Center | **Changes** (+ Approvals + Activity history) | Splits the three jobs it conflates. |
| Event Log / Results | **Activity** (Events / Mission Results streams) | Unified history center. |
| Shell | **Terminal** (under Tools) | Unambiguous; merges with Security's Operator Shell. |
| Colony | **Colony** (kept) | Core brand + the runtime domain; now has clear children. |
| Missions | **Missions** (kept) | Already enterprise-appropriate. |

Colony and Missions are intentionally *kept* — they are strong, non-hobbyist brand terms that also
read as enterprise ("fleet"-like and "operations"-like respectively). The renames target only the
names that read as personal-project or opaque.

---

## 11. Open questions for the operator

1. **Colony as a domain vs. Infrastructure child** — Colony (the agent swarm) and Infrastructure
   (the managed homelab) are distinct in this design. Confirm that separation matches your mental
   model, or fold Colony under a broader "Platform/Runtime" umbrella.
2. **Analytics now or later** — reserved under Monitoring; say whether the learning-loop metrics
   warrant a section in phase C.
3. **Environment switcher** — do you foresee multi-environment/multi-host management that would
   justify the top-bar environment selector in the near term?
4. **Terminology sign-off** — especially Infrastructure, Signals, Agents, and Automation before
   any relabeling ships (renames are the cheapest thing to get wrong and the most visible).
