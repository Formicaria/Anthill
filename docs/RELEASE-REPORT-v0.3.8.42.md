# v0.3.8.42 — UI Truthfulness and Cohesion: Release Report

Per the spec's required final report. Covers the truthfulness/cohesion workstream on
`feat/v0.3.8.42-ui-truth-and-cohesion`; the Tools-page deepening and Schedule workstream is a
second engineer's and lands on the same branch before merge. Nothing below is claimed that was not
either test-enforced or observed against the running console.

## 1. Base and commits

Branched from `main` at `8ef167b` (v0.3.8.40). Twenty commits at the time of this report; every
change shipped with `dotnet test` green (2,222 tests) and `node --check` clean.

## 2. What was audited

`docs/UI-CONTRACT-AUDIT.md` is the §1 working audit: 21 pages, 12 top-level IA entries, 175
`api()` call sites, 19 interval pollers, one topology renderer. The machine ledgers
(`ConsoleRouteAgreementTests`, `ConsoleRouteCoverageTests`, `StatusFieldConsumerTests`) remain the
authority on route coverage; the audit decided what they cannot: duplication, mislabeling, and
concept mismatch.

## 3. Contract mismatches found and closed

| Found | Closed by |
|---|---|
| Conversation detail omitted `cancelled`; `Doing()` answers it as prose → stopped conversations read "Working…" forever, refusal summaries overwritten | detail projects `state.Cancelled`; console consumes the field; refusal note applied after refresh |
| `buildNodes` fabricated a six-role roster on registry failure; legend padded from a hardcoded list | failure is a state: core nodes only + named error + cache-busted Retry; stale roles marked with when and why |
| UI terminal-status subsets drifted from `ApiJobRegistry.IsTerminalStatus` in three call sites | one `JOB_TERMINAL_STATUSES`, pinned across the boundary |
| `providers_configured` displayed as "connected" | reads "configured" — the only thing the field measures |
| Five of six patch mutations had no double-submit guard | one `pcMutate` rule, pending state on the card |

## 4. Retired or consolidated surfaces

All four competing mission composers (colony bar, Missions console, dashboard Mission Command with
modes + plan preview, Conversations-widget message boxes) — each replaced by a path to Chat, never
a dead end. The Monitoring domain (five second-doors). The Approvals/Changes double entry. The
Tools page's drifting summary (now the widget's renderer + read-only inventory). "Scheduled" as a
top-level concept. The chat side strip (400px, `display:none` under 900px). Dead CSS/JS deleted
with their features, not hidden. `POST /missions/plan` lost its only surface and is recorded as a
UI GAP in the coverage ledger until Chat grows a preview step.

## 5. Final information architecture

Chat · Mission Workspaces · Tools · Dashboard · Operations (Missions: Console/History/Activity/
Events · Automation: Director/Objectives/Rules · Changes & Approvals: Changes/Approvals) ·
Infrastructure (incl. Alerts, Activity — role-gated) · Colony (Topology · Roles:
Configure/Inspect/Coding Agents · Memory & Signals) · Security · Administration (Providers & Model
Routing · Users · Settings · Terminal). Every moved route resolves through `ROUTE_ALIAS`
(bookmarks survive), enforced by `MovedRoutes_StayReachable_AndConceptsHaveOneHome`.

## 6. Chat + Colony

A resizable split pane (default ~45%, divider draggable and keyboard-operable, clamped
340px–72%), not a layer: operator direction overrode the spec's layered design and the spec's own
one-canvas requirements are all kept. The canonical `#colony-canvas-area` is re-parented via
`topologyMountTo` — no second canvas, loop, or subscription; closing hands the node home and the
wake check stops the draw. The bar states mission linkage in three truthful forms. Below 900px the
pane is a full-screen switch with its own close; Escape closes unless a modal owns the key.
Two defects found by driving it (0×0 canvas from a non-flex mount; the colony page's mission bar
riding inside the canvas area as a second composer) are fixed and pinned.

## 7. Chat behaviors

Fingerprinted 4s self-refresh (only while the page is on screen; unchanged polls rebuild nothing),
reading position preserved unless following the bottom, fenced code blocks escape-first with only
`pre/code` added, Up-arrow recall into an empty composer only, per-message copy fed from JS state,
header Stop wired to the conversation cancel shown exactly during real work, "New conversation" no
longer undone by the rail's auto-open.

## 8. Tests

Nine new/rewritten facts in `UiShellTests` plus the ledger entry in `ConsoleRouteCoverageTests`:
terminal statuses × registry, chat-as-entry + stop, pane one-canvas rules, §5 label guards, moved
routes, chat thread safety, registry-failure states, dispatch failure surfacing, dashboard
reachability. Suite: 2,222 passing. Two guard self-trips during the release (a comment quoting a
banned literal; a template-branch duplicate id) were themselves caught by the convention — noted
because that is the argument for the convention.

## 9. Verified against the running console

Composer Play⇄Stop; pane open/close/hand-off in both directions (canvas parent verified);
`/monitoring/*` alias resolution; fenced-code rendering; Up-arrow recall; Stopped-conversation
state and rail badge; all three registry-failure states (absent / stale / recovered) simulated
against the live page; v0.3.8.42 in the header.

## 9b. The live walkthrough (spec §19), performed — not mocked

Run against the composed runtime with the `conversation` route on `agent:claude-code` and the
full twelve-role roster enabled (v0.3.8.41 default):

1. **Chat answers through a routed agent.** A conversational turn was answered by Claude Code,
   recorded as an assistant turn with provider/model attribution, rendered by the live poll, and
   the state line settled to idle. The same mechanism accepts any provider in Providers & Model
   Routing — the point of routing it.
2. **Mission entry through Chat.** The ⚒ request produced the in-thread escalation gate ("It
   wants to start_mission — nothing happens until you say so"); the operator's Allow started the
   real mission, linked to the conversation.
3. **Watched live.** `doing: running mission 49a47921…`; `/graph` showed the real tasks advancing
   (Research → Build → Verify), the Colony pane open beside the conversation throughout.
4. **Canonical result.** Mission history reported `complete` with the agent-authored answer.
5. **Full Colony round trip.** Open full Colony handed the one canvas home; returning to Chat
   restored the same conversation with the typed draft intact.
6. **Memory.** The archivist recorded memory candidates for the mission (server log:
   "Archived 2 memory candidate(s)"); 72 pheromone trails present via `/pheromones/json`.

The walkthrough found and closed three defects the tests had not: the pane's mission note read
`/jobs`, which cannot see conversation-escalated missions; `Doing()` claimed "running mission"
forever after settlement (fixed against the canonical evaluation — the chat state line, rail badge
and pane note all read it, so all three stopped lying together); and "New conversation" had been
silently undone by the rail's auto-open.

## 9c. The merged workstream

The parallel branch (v0.3.8.41 base — full roster default, agent workspace confinement, fitness
graded against the resolved model — plus four console fixes) merged with each overlapping fix
resolved to its union: Objectives keeps its top-row promotion under its truthful label on the
canonical route; the Tools page renders through the one shared renderer carrying the
unresolved-route distinction; registry failure keeps the stale/absent split plus the loading
state. History was consolidated to one release commit per the one-version-one-commit guard, which
this branch briefly violated and the guard caught.

## 10. Known limitations and open work

- The remote reasoning endpoint (`http://10.10.10.57:11434`) refuses connections in the test
  environment, so conversation replies hang in a truthful "Working…" — the spec's live
  end-to-end mission walkthrough is blocked on that endpoint, not on the console.
- The manual browser matrix (1440×900 / 1024×768 / 390×844) is partially covered (desktop driven
  extensively; narrow widths verified by CSS rule, not by hand).
- Six pre-existing UI GAPs remain recorded in the coverage ledger, plus `/missions/plan` (new,
  deliberate). Reduced-motion/idle throttling of the render loop is deferred and noted in the
  audit. Dashboard §6 trimming beyond the composer/conversations work was judged already-curated
  and left alone.
- The Tools/Schedule workstream and the final tag land after the second engineer's work merges;
  the changelog entry may be amended (it is unshipped until tagged).
