# ANTHILL — UI CONTRACT AUDIT (v0.3.8.42)

The §1 working audit for the *UI Truthfulness and Cohesion* release. Measured against the tree at
`9ad8eeb` (branch `feat/v0.3.8.42-ui-truth-and-cohesion`), not estimated. Where something is
unmeasured it says so.

This document does not replace the machine ledgers. Three already exist and are enforced in CI:

- `ConsoleRouteAgreementTests` — the console calls no route the API does not map.
- `ConsoleRouteCoverageTests` — every mapped route either has a console surface or a written
  reason it does not (six "UI GAP" entries remain, recorded there).
- `StatusFieldConsumerTests` — every `/status` field has a reader or an exemption.

What those cannot decide is whether a surface *should* exist, is duplicated, or misrepresents the
backend concept behind it. That is this document.

---

## 1. Inventory (measured)

| Surface | Count | Source |
|---|---|---|
| Pages (`id="page-*"`) | 21 | `index.html` |
| Top-level IA entries | 12 (5 items + 7 domains) | `IA` config, `app.js` ~439 |
| Routes in `ROUTE_TABLE` | derived from IA + tabs | `buildRoutes()` |
| `api(...)` call sites | 175 (≈120 unique paths) | `app.js` |
| Interval pollers | 19 `setInterval` sites | `app.js` (§15 of the spec owns their lifecycle) |
| Event stream | `/events/stream` + 9 documented `/events/json` polling fallbacks | PLAN.md §2 |
| Mission composers | 4 active (`mission-input`, `ms-mission-input`, `ov-mission-input`, `chat-input`) + conversations-widget `convSend` | `app.js` |
| Topology renderers | **1** — `#colony-canvas-area`, re-parented between hosts (`topologyMountTo`: colony ⇄ dashboard ⇄ chat) | `app.js` ~1892 |

The one-canvas rule already holds. The Chat + Colony work must keep it.

---

## 2. Decisions, per spec section

Legend: KEEP · CORRECT (fix in place) · CONSOLIDATE (merge into canonical) · RELABEL · REMOVE.

### Navigation (spec §2)

| Current | Backend reality | Decision |
|---|---|---|
| Chat (top item) | `/conversations` + turns; mission-mode submission via `convSend(mode:'mission')` | KEEP — canonical mission entry (§3) |
| Projects | `GET /workspaces` — detached git worktrees per mission. Read-only; no project CRUD exists | RELABEL → **Mission Workspaces**; copy must not imply project management |
| Scheduled (top item) | Routes to `objboard` (Objectives). No arbitrary-mission scheduler exists | REMOVE as top-level; lives under Operations → Automation → Objectives/Runs |
| Tools (top item) | `GET /tools` (built-in, read-only) + `/tools/user` CRUD (HTTP tools) | KEEP; CORRECT contract presentation (§5 Tools below) |
| Dashboard | aggregate polls | KEEP; simplify per spec §6 |
| Monitoring → Activity (All/Events/Results/Changes/Runs/Infra) | overlapping with Operations → Missions/Changes | CONSOLIDATE in §7 pass — one home per concept |
| Operations → Approvals + Changes (two entries, same `patches` page, different `view`) | one backend patch store | CONSOLIDATE → one **Changes & Approvals** entry |
| Colony → Model Routing (under Colony) | `/settings` stabs | RELABEL/move under Admin per spec §2; roles ≠ routing (§9/§11) |
| Infrastructure domain (vis:hl) | homelab subsystem, real | KEEP (visible only when supported — already role-gated) |

### Known mismatches (spec §5)

| Claim | Reality | Decision |
|---|---|---|
| "Projects" page copy | workspaces are per-mission isolated checkouts | RELABEL page + nav + copy; no CRUD invented |
| "Scheduled" as product concept | Objectives + homelab health schedules only | REMOVE label; keep real functionality where it lives |
| Skills management | no skills API exists anywhere in `ApiHost` | CONFIRMED ABSENT — nothing to remove; do not add |
| Desktop app claims | none found. `deployment_mode: desktop|server` is a backend wire value with a truthful label | NO ACTION (not a desktop-app claim) |
| Chat "◧ Colony" button | mounts the canonical canvas into a 400px side panel; unusable at <900px (hidden entirely) | REPLACE with layered full-page mode behind chat (spec §4) |
| Quick actions | unaudited per-widget | §5 pass: navigational labels for navigation, mutation labels only for mutations |

### Chat + Colony (spec §4) — implementation ground truth

- Canonical renderer: `#colony-canvas-area`; single render loop (`loop()`), wake gated by
  `refreshTopologyAwake()` measuring real layout, not flags.
- Re-parenting is the approved approach (the spec's preferred option): extend
  `topologyMountTo('chat')` to target a full-page layer under the chat surface instead of the
  `chat-side` panel. The `chat-side` panel and its close button are retired when the layered mode
  lands (spec: "the existing broken side-panel behavior is removed").
- Linked mission focus: conversations carry mission ids (`convSend(mode:'mission')` → job →
  `mission_id`); the topology already renders per-task activity from `/graph`. Focus = the same
  mechanism `selectJob` uses, driven by the chat's linked mission.
- Idle truthfulness: `drawPheromoneField` and ant animation already key off real data; the layered
  mode must not add decorative activity.

### Status truthfulness (spec §16)

- `JOB_TERMINAL_STATUSES` (v0.3.8.42) is the single terminal set, pinned to
  `ApiJobRegistry.IsTerminalStatus` by `UiShellTests.ConsoleTerminalStatuses_MatchTheRegistry`.
- Remaining scattered vocabularies to centralise in the §16 pass: `hudStatusClass`'s inline list,
  `MS_CHIP`, `MR_STATUS`, per-widget status colouring. Each must map from a named backend state,
  and unknown must never map to success.

---

## 3. What this release will NOT do

Owned by the alignment brief (`docs/UI-ALIGNMENT-BRIEF.md`) and unchanged:

- No frontend framework, bundler, npm dependency, or type system.
- No backend subsystems invented to justify UI. Small contract corrections only, called out in the PR.
- No rewriting shipped changelog entries; five version markers move together at release.
- The six recorded UI GAPs in `ConsoleRouteCoverageTests` remain recorded unless a surface ships
  for them in this release; any that ship move from the ledger to the page that reads them.

---

## 4. Execution order for the release

1. ~~Composer Play⇄Stop + terminal-status unification~~ — landed (`9ad8eeb`).
2. This audit (no visual change).
3. §4 Chat + Colony layered mode; retire `chat-side`.
4. §5 relabels: Mission Workspaces, Scheduled removal, Tools contract presentation.
5. §2/§3 IA consolidation: one home per concept, legacy redirects preserved.
6. §6–§11 section passes; §12–§18 cross-cutting pass.
7. §19 tests throughout (extend the existing C# text-guard convention); §20 cleanup + release
   report; version markers to v0.3.8.42.
