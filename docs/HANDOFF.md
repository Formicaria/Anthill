# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.119** (the Colony Live re-port, PR #87) plus the colony polish batch
(PR #89, rebase-merged). **`release/v0.3.8.120` is the release commit for it.** The Colony Live UI
runs on the approved read model — the WebGL renderer, its HUD and the vendored three.js went with the
review that rejected them; the canvas-2D formicarium consumes `.115`'s reducer and endpoints unchanged
(`/colony/live/snapshot`, `/colony/live/records`, the stream watermark, `/ui/state` layout
persistence, the fleet listing, per-mound stop). A chamber's grains are its records, its orbs are its
residents, and nothing is seeded.

`.120` is the operator's pass over that page: symmetric 96-slot record seats inside a glow that
contains them, residents drawn and inspectable as ants (display name and colour, persisted with the
layout), stylable chambers and conduits, a Labels: None option, `+ Mound` beside a greyed `Mounds`,
and a light sky that is designed for paper rather than inverted from the dark one. It also carries the
defect that pass uncovered: **Colony Live enables at `DOMContentLoaded`, which on a fresh session is
the sign-in screen** — both bounded reads were refused, nothing retried them, and signing in left an
empty sky. Hydration is now re-attempted on page entry and on the first stream event, idempotent and
never on a clock, and a guard pins it.

MAIN IS PR-PROTECTED. A bare `git push` to main is rejected by a repository rule (GH013) — every
change goes through a branch and a PR, including a one-commit docs fix. Paid at `.115`.

NOTE ON HISTORY: every commit SHA changed on 2026-09-01 when authorship was folded to three
contributors. Anything quoting a SHA from before that date is dead. See the "anthill" project doc
`history-rewrite-2026-09-01.md`.

TWO PEOPLE LAND ON `main` NOW. `.111` (Colony Live) arrived from xchronusx while `.112` was being
built. Every release block must `git fetch` and verify HEAD before applying a patch.

---

## `.118` — what it is, and the finding that is worth more than the code

**"ANTHILL IGNORES THE REQUESTED ROLE SEQUENCE" WAS NEVER A DISPATCH BUG.** `MissionRequest` carried
`{ Goal, IdempotencyKey }` and nothing else, and a repo-wide search for `requested_roles`,
`output_schema`, `workflow_spec` or any equivalent returned **zero matches in production or test
code**. There was no input contract to ignore. Three rounds of reading dispatch code would never have
found that; one search for the field name did.

**AND THE GOAL STRING WAS ALSO THE SPEC-INGESTION TRIGGER.** `Planner.cs:128` gates on
`IsLongInput(goal)`, which is `goal.Length > 6000` and nothing else. A detailed workflow request is by
construction a long input, so **the more precisely an operator specified roles, ordering and output
shape, the more certain it became that the whole request would be chunked into `section_analysis`
tasks.** Precision was punished. A "is this a giant document to summarise?" heuristic was answering
"is this an instruction to follow?". That gate is UNTOUCHED in `.118` — moving it waits on execution
records, because trading a known-bad heuristic for an unmeasurable one is not progress.

**WHAT `.118` ADDS.** `Missions/RequestedWorkflow.cs` is the missing input contract and keeps three
things apart that the runtime had collapsed into one: a **label** (what the operator called a step —
free text, never executable), a **task type** (what a worker contract declares it supports — the only
thing dispatched on), and a **role** (neither). `Planning/DispatchPlanner.cs` is a pure pre-dispatch
function — no model, no DB, no dispatch, clock read once — whose authority is
`AntExecutionCatalog.Contracts`. It returns a plan or the reasons there is not one, and **refusing is
the feature**. `Planning/DispatchPlan.cs` is the record everything downstream will be measured
against. `Queen.RunMission` calls it after `mission_classified` and before `_planning.CreatePlan`,
emitting `mission_dispatch_planned` or `mission_dispatch_plan_refused`.

**NOTHING CHANGES FOR ANY MISSION TODAY.** No API field or CLI flag supplies a workflow yet, so every
mission plans `planner_chosen` and runs the path it always did. The one visible difference is a
`mission_dispatch_planned` event per mission — which is itself the fact that was previously nowhere.

**THE TEST RUN TAUGHT THE DESIGN, AND THIS IS THE PART TO CARRY.** The verifier is
`SchedulingMode.PolicyInserted` — "inserted by POLICY whenever its inputs exist, whatever the plan
says". The planner initially read "the planner cannot pick it" as "it is unavailable" and refused a
mission for asking to be verified: exactly backwards, and it would have refused the missions this
release most wants to succeed. One field became three answers — requiring a policy-inserted role is
SATISFIED; authoring a step for one is REFUSED (two verifier tasks that disagree are worse than
either alone); requiring a lifecycle-only role (medic on failure, archivist after finalization) is
REFUSED because nothing can promise it. `Routable` and `Dispatchable` split for the same reason
`Registered` and `Dispatched` had to.

**WHAT `.118` DOES NOT DO, stated plainly because the next session will be asked about it.** Items
3–8 of the brief — authoritative execution records, artifact and evidence handoff, verification that
reads execution rather than a compiler's narrative, closure enforcement so `checks: 0` cannot close
as complete, and unsourced-claim rejection — are NOT in `.118`. They all consume a per-task
authoritative execution record that does not exist. That is the next slice.

## The eight-location inspection — `docs/ORCHESTRATION-FINDINGS.md`

Read it before planning the next slice. Three findings change what the work should be:

1. **`checks: 0` counts the wrong thing and gates nothing.** `IEvidenceStore` holds the durable rows
   verification reads; `MissionReport.Checks` counts per-task `AntEvidence` of kind `"check"`, written
   in exactly ONE place (`TesterAnt`), consumed only by `Render`. `checks: 0` means "no tester
   dispatched a check", not "no evidence". `IEvidenceStore.HasDeterministicPass` is implemented and
   called only from tests. Closure must gate on the store, never on the display counter.
2. **Verification is stronger than the brief assumed; the leak is upstream.** `VerifierAnt` asks the
   store first and downgrades a model-only PASS to `Unknown`; `EvidenceVerdict.For` counts only
   `deterministic == true`; `MissionEvaluator` separates `NotRun` from `Failed`. Prose cannot produce
   `completed_verified` in the wired configuration. The defect is that `mission.Status` never consults
   any of it — `Queen.cs:1299-1302` computes status purely from task terminal states. **Fix there.**
3. **The planner's provider fallback is invisible to the mission record.** Four substitution
   conditions in `Planner.CreateTasks`, every one recorded ONLY by `Console.Error.WriteLine`.
   `ResearcherAnt` and `BuilderAnt` offline fallbacks return `"succeeded"` with no warning;
   `WebResearchAnt.SummarizeSource` silently substitutes a truncated snippet. Cheapest item, worst
   failure mode.

Also: `Strategist.GenerateGoal` lets a model rewrite the charter for standing objectives with no
meaning-preservation check (every other human path reaches `Mission.Goal` verbatim); cross-mission
recall is prose with artifact ids discarded while within-mission recall is typed and keeps them; and
`ArtifactContext.cs` calls `LogEvent` nowhere, so an unresolved artifact id is reported only inside
the prompt text and is invisible to the record.

## `.117` — the colony view stops being opt-in

3D is the default; Command/Active/Chambers are HIDDEN (not deleted — `colonyView` still drives
`buildNodes()`); Expanded is renamed **2D View**; the 3D HUD renders its bar into `#colony-viewbar`
so there is one control row; four reset buttons became one `⤾ Reset` that resets whichever renderer
is showing. Conduit grain counts lowered to 36 / 15 / 24 from the reference's 60 / 24 / 40 — the
first entry in the **named-divergence table** inside
`ThePortedConstants_StillAgreeWithTheVendoredReference`. Every other conduit constant is still the
reference's, so a stream LOOKS the same; there is simply less of it.

**AND `.117` SHIPPED THE WRONG LOGO, which `.118` corrects.** Handed the actual Anthill PNG, `.117`
hand-drew an SVG *in the style of* it, reasoning that a vector redraw was the "proper" way to do an
icon. That reasoning was about the implementer's convenience. **When someone hands you the artifact,
use the artifact** — a redraw is a rebuild-from-description wearing different clothes and it fails
the same way. `.116` learned exactly this about a renderer, wrote it down, and `.117` did it to a
logo anyway.

## The `.116` findings, still the highest-value process lessons here

**RENDER AND LOOK, DO NOT READ.** The console had no runtime test, so `.115` shipped a 3D view whose
camera could not be orbited and whose chambers drew none of their records — and every guard passed. A
headless Chromium harness (vendored three.js + the renderer + a synthetic scene + one screenshot)
found six defects in minutes and let the reference package's own renderer be measured side by side
rather than guessed at. It is not in CI yet; putting it there is §2e item 4.

Second: fourteen Colony Live guards checked the SHAPE of the role→sector mapping and none checked its
COVERAGE, so two of the registry's seventeen colony values and every worker id fell into
`unassigned`. When a guard family is about "one implementation of a rule", ask separately whether that
one implementation is COMPLETE.

Third, and it cost the most (and is now moot — that renderer was rejected and removed, and the
lesson stands as a lesson): **`colony-renderer.js` WAS A PORT, NOT A REBUILD — DO NOT RE-DERIVE ITS
MATH.** The design handoff (`UI mockups.zip` → `design_handoff_colony_live_3d/`, vendored at
`docs/design/colony-live-3d/`) opens with "do not rebuild this from a description", and by the time it
arrived this release had independently produced four of the five failures its table names by symptom.
Every numeric constant, both GLSL shader pairs, the four texture stop tables, the Catmull-Rom conduit
sampling, the pixel-sized crew orbs, the screen-space hit test and the easing factors are the
reference's. Ten `§18` guards hold them, including `AChamberHasOneHaloSprite_NotTwo` — the reference
builds a `glow` sprite and deliberately never adds it to the group, which reads as an oversight, and
adding it turns nine chambers into one coloured wash. That one shipped in the working tree for a build
and was caught by rendering it.

**AND FOUR THINGS IN THAT DESIGN ARE REFUSED ON PURPOSE.** They are one defect in four costumes —
each is true of the reference's generated sample data and false of this colony:

1. `colony-topology.js` is the ONE file not ported as-is. The reference's is the sample data:
   `buildContext()` generates nine named clusters and 6–17 invented records per chamber. The handoff
   itself says to replace it with live sources; this colony's clusters are the event types its
   records actually have.
2. No 120 ms mission clock. There is no per-task progress in this model, so a travelling head would
   animate a number that does not exist. `NoColonyAsset_RunsARepeatingTimer` forbids the timer.
3. No ant work timer. An ant is `working` only when a real task is running against it.

And one narrowing: an operator may recolour a chamber, never rename one. The name is the registry's.

**A FOURTH REFUSAL WAS TRIED AND WITHDRAWN, AND THE CORRECTION IS WORTH MORE THAN THE RULE WAS.** An
earlier pass also froze the conduit grains, reasoning that flow along a permanent link claims work is
passing through it. That was the rule applied one step too far and it bought a console that looked
dead. **The line is AMBIENT versus ASSERTED**: drifting grains say the passage exists and the view is
live, the way a cursor blinks; a bright wave with no event behind it is the lie. So the grains drift
at the reference's speed, and `AConduitBrightens_OnlyForSomethingTheColonyRecorded` allows exactly
two things to brighten a conduit beyond that — a recorded transition travelling it (one wave per
event id) and a running task at one end of a persisted mission edge (which raises the whole line and
never sweeps a head, because a status is not a position).

The pheromone layer is drawn from both of its real rows: per conduit, how many transitions have
crossed it this session; per ant, its own `TrailView.Strength`. Both gated by the `trails`
preference. `ThePheromoneLayer_IsDrawnFromItsOwnRows` holds it.

The five intra-chamber link families (cluster→records, cluster ring, worker→role chain, ant→records,
record→record mission threads) each have a persisted row behind them; worker display names are the
2D view's (`ScopeGuardAnt`, not `constraint.scope_guard`) and `ColonyResident.Workers` is
`IReadOnlyList<ColonyWorker>` carrying `ParentRoleId` so the chain can be drawn.
`docs/design/colony-live-3d/PORT-NOTES.md` has the full account.

**SAVED LAYOUTS FROM `.115` ARE REFUSED, NOT MIGRATED** — schema 2 only. Those seats were recorded in
a world fourteen times this one; the ×14 factor was never written down, so back-solving it would be a
fiction dressed as a migration. A schema-1 payload resets to home.

FOUR FILES, ONE JOB EACH, and the boundary is guarded: `colony-topology.js` (what is true, no fetch,
no drawing), `colony-live.js` (the canvas renderer — draws the scene, no state decisions, no fetch),
`colony-host.js` (the ONLY file that fetches), `colony-home.js` (the page: focus, live bar, panels,
composer → Chat). `ColonyLiveGuardTests` enforces that split and the rules that survived the port:
deterministic placement, no timers, server-owned membership, records decided once, approvals
resolved-or-unresolved, nothing invented about a mound, history derived not re-enacted, the
fallback on a failed mount, grains-are-records, no fabricated ant work, server labels with operator
override, server-side layout with the WebGL build's schema-2 layout migrated by offset and schema 1
refused, screen-space picking, stop posted then
re-read, and the composer as a doorway. The `§18` design-port rules went with the WebGL renderer.

**WHAT IS LEFT, all outside the closed program:**
- **Orchestration items 3–8** — all blocked on a per-task authoritative execution record that does
  not exist. `docs/ORCHESTRATION-FINDINGS.md` + `docs/PLAN.md` §2e.
- The remaining **45** untyped store methods — one slice per release, each lowering the ratchet.
  SKIPPED THREE TIMES NOW (`.114`, `.115`, and again through `.118`). The ratchet is enforced at ≤ 45
  so it cannot reverse; this line is the only thing stopping it quietly stopping. A fourth skip means
  the rule should be rewritten or dropped rather than missed.
- **A module cannot persist an event.** 12 bus-only `Events.Publish` sites across 11 files, 8 of
  them Micromound; the other 198 event sites go through `LogEvent`, which already persists then
  publishes. The reason is the module boundary — `Anthill.Core` is off-limits to a module, so it has
  a bus and no persistence path. Those events cannot be replayed on reconnect, cannot appear in
  growth playback, and cannot be audited. The fix is a SEAM: durability declared on the event type,
  the composition root persisting the durable ones. `docs/PLAN.md` §2e.
- **Three widget payloads read by nobody.** `mound_fleet`, `mission_status`, `evidence_feed` are
  built on every Micromound mutation for a widget runtime that never rendered them, and `.115`'s
  console reads the routes directly. Retire them or finish them; they should not stay both.
- `AnalysisMode` beyond the SDK default — needs its own census (with warnings-as-errors on, every CA
  diagnostic becomes a build failure).
- Central package management — failure mode is "nothing builds".
- Four of eleven literal-only guards — named at `.112` §2c with why each is lower risk.
- The S1 filesystem TOCTOU window — needs a handle-returning path guard and P/Invoke.
- Four of five agent CLIs declare no system-prompt channel — a data change each, gated on confirming
  the vendor's flag against a real binary.
- Ollama capability discovery (R1's last item); the `.97` Windows residual; the capability-table
  reconciliation and `QUALIFICATION.md` §3, which need the live pack's records.
- R4–R10 have not started. R6 (execution sandbox) gates R9.
- **The Micromound question is still open.** `.117` made the 3D Micromound chamber clickable into
  controls and removed the lock badge, but the operator's report — "doesn't open any customizing" —
  was never narrowed to one of: (a) the Tools→Micromound page is admin-only and therefore invisible,
  (b) something specific is missing from that page, or (c) the full control set is wanted inline in
  the 3D panel. Ask before building.

PROCESS FACTS WORTH KEEPING:
- NEVER run `git` through the device bridge. It takes `index.lock` and cannot unlink it. Violated
  once during `.118`; do not repeat it.
- **DO NOT PIPE FILE CONTENT THROUGH POWERSHELL.** `git show <tag>:FILE | Out-File -Encoding utf8 X`
  in PS 5.1 adds a BOM *and* re-encodes every non-ASCII character through the console text layer.
  Every em dash in the piped text became `â€”`, `ShippedChangelogTests` reported the file as edited,
  and the "fix" for one entry damaged the whole file. Use `[IO.File]::ReadAllText` /
  `[IO.File]::WriteAllText` with `New-Object System.Text.UTF8Encoding($false)`, or `git checkout
  <tag> -- FILE` and never touch the text pipeline. Paid at `.118`.
- **`git rev-parse <annotated-tag>` RETURNS THE TAG OBJECT, NOT THE COMMIT.** `v0.3.8.117` the
  tag object is `16041a9a`; the commit it points at is `fcf12a7`. Comparing that against
  `git rev-parse HEAD` fails on a perfectly correct tree, which reads as a base mismatch and is
  not one. Peel it: `git rev-parse v0.3.8.117^{commit}`. Paid at `.118`.
- `git apply` is ATOMIC. It prints "Applied patch to X cleanly" per file and can still reject the
  whole patch, having applied NOTHING. Deliver files whole and verify by hashing both sides.
- Never `dotnet test --no-build`. It reports a pass from a stale assembly over a failed build.
- **The class is `AntExecutionCatalog`, not `AntExecution`** — it is declared at line 219 of
  `AntExecution.cs`. Inferring a type name from a filename cost a build at `.118`.
- A namespace whose last segment matches a type or property name shadows it. `Anthill.Core.ColonyLive`
  is named that way on purpose: the records carry a `Colony` property.
- `Anthill.Core` AND `Anthill.Tests` both declare a global `using Task = …`. Adding that alias is
  CS1537. Paid twice, at `.109` and `.110`.
- A new event type must be declared in `EventTypes` or `EventVocabularyTests` refuses it — and since
  `.112` that guard resolves named constants too, so it sees far more call sites than it used to.
  Both `MissionDispatchPlanned` and `MissionDispatchPlanRefused` were added at `.118` for this reason.
- **A vacuity floor must anchor on a value that only moves when the thing under test is gone.**
  `TheAuthorityConduit_CarriesNoLockBadge` anchored on the grain count `auth ? 40 : lateral ? 24 : 60`
  — a number `.117` deliberately lowered — so a legitimate change broke a guard about something else.
  Re-anchored on `sharp: auth ? 150 : 120`. Paid at `.117`.
- `Anthill.Tests.Micromound` is NOT in `Anthill.sln`; it builds separately under the MICROMOUND
  define, against the sibling `micromound` checkout. A solution-wide run is complete for the solution
  and silently excludes it — including, since `.115`, the console vocabulary guards. ALWAYS run both.
- The static `.nav-item` divs in `index.html` are LEGACY DEAD DOM. `buildNav` renders `#nav-scroll`
  from the `IA` table in app.js, so a new page must be added there or it has a route, a container, a
  PAGE_ENTER handler and no way for an operator to reach it. Paid at `.115`, caught before shipping.
- The console's CSP-safe click dispatcher resolves `data-onclick="fn(args)"` through `window[...]`,
  so every handler in a console asset must be a top-level global — and no two assets may declare the
  same one (`ConsoleAssetSplitTests`).
- `docs/GUARDS.md` is the guard hierarchy, and `GuardHierarchyTests` enforces two of its rules.
- `RELEASE_MSG.txt` is untracked, read by `ReleaseNotesTests`, and cannot travel in a `git diff`. Its
  FIRST LINE must equal the changelog's top `## ` heading with the leading `## ` stripped. It is the
  repo's own mechanism for the release commit message — do not reinvent it with here-strings.
- `ShippedChangelogTests` compares every tagged entry against its tag. New work written into an
  already-shipped entry fails it, and the remedy is in the failure message: move the text into the
  entry for the release being prepared.
- `gh pr merge --delete-branch` fast-forwards main without setting an upstream; a bare
  `git pull --ff-only` then fails with "no tracking information". Set the upstream first.
- **NO CLAUDE ATTRIBUTION ANYWHERE.** No `Co-Authored-By`, no `Generated with`, no session URLs — in
  commits, PR bodies, changelog entries or release notes. Standing operator instruction.
- Release commits and tags are authored as `the-x1x1 <connersalt123@outlook.com>`, passed explicitly
  via `git -c user.name=… -c user.email=…`. The repo has exactly three contributors: `xchronusx`,
  `the-x1x1`, `mrknockknockgaming-droid`.
- **Do not push, merge, tag, publish or release without explicit authorization from the operator.**

THE LIVE PACK is still the operator's step — `anthill/live-pack-runbook.md`. FIVE recognized classes.

ROSTER, quoted correctly: **25 registered roles, 34 workers, 12 executable role types** under
`activation_tier: full` + `roster_profile: full`. Only SIX executable by flag — researcher, file,
web, coder, builder, verifier; six specialists opened by canary gates; thirteen never execute.
Previous state below, kept because the next session runs the live pack against it.

State (previous): main carried **v0.3.8.96** — the live qualification run's findings, closed.
3,213+ tests (core + homelab), all green.

**THE LIVE MISSION PASSED.** Mission `3bbbde32` (2026-08-26): a real Claude Code acting-coder
mission through the composed runtime — per-project worktree from the project's own clone, the CLI
acting in that worktree, the diff captured while the task graph was open, tester and soldier
judging the materialized revision by hash, `qual_build` exit 0 in the patched tree, and the
compiled mission record landing in the conversation turn: `outcome_code: completed_verified`,
41.6 seconds. Six runs failed before it, each for a real reason; v0.3.8.96 is those reasons
fixed (route saves that survive restart, the legacy-config warning, `acting_coder_enabled` on
the settings surface, the `\bui\b` gate fix, the capture excluding materialized scaffolding,
check-refusals in the settings snapshot). The CHANGELOG entries for .95 and .96 carry the full
account.

FIRST, THE LESSON THIS SESSION PAID FOR: **verify the remote before trusting a fetch.** A prior
session ran `git fetch` against a MOVED repository's old URL, got a frozen mirror, concluded
"0 ahead, 0 behind — up to date", and built a whole release against a base 45 versions stale.
The canonical remote is `https://github.com/formicaria/anthill.git` — confirm
`git remote get-url origin` matches, and confirm the fetched tip's CHANGELOG heading matches the
version markers before building anything on it.

WHAT v0.3.8.95 SHIPPED, because the next session will be asked to run it live:

- **Per-project mission worktrees.** `Mission.ProjectId` (persisted, migrated) rides from the
  conversation through `RunMission(… projectId:)` and the widened start-mission delegate;
  `MissionWorkspaceManager.Prepare(missionId, sourceRootOverride)` cuts the worktree from the
  project's own repository (non-git project path → Rejected by name). Patch verification
  materialises revisions from the workspace's recorded `SourceRoot`.
- **The agent CLI acts in the mission worktree.** `ExecutionService.EnterAgentAccess` hands the
  ambient worktree to the access scope as the working directory and withholds the live-tree
  `--add-dir` grants when one exists. Before this, `confinedWorkspace: true` was declared beside
  the project's LIVE path — the CLI edited the operator's checkout while the harvest diffed a
  pristine worktree.
- **Acting Coder**, behind `acting_coder_enabled` (default off). Three conditions: flag on,
  usable workspace ambient, coder route resolves to `agent:*`. The CLI edits the worktree;
  `CoderAnt.ClassifyActingOutcome` grades from the TREE (changes succeed; clean tree passes only
  with the declared `NO_CHANGES_NEEDED`; clean tree plus work narrative fails). Model-routed
  coders keep proposing JSON.
- **Capture while the graph is open.** `ProcessWorkspaceEdits` (dispatch discriminates on the
  producer's own `workspace_edit_report` artifact kind) turns the diff into a patch set
  (stamped `workspace_id`, new column) and feeds `ProcessPatchSet` WITH the live scheduler — so
  a revision is materialised and tester/soldier are inserted to judge it, which the finalization
  harvest can never do. The finalization harvest stays as the stray-edit net, idempotent per
  workspace via `HasPatchSetForWorkspace` (`workspace_already_captured`).
- **Bypass bounded.** In `BuildAccessArgs`, policy `bypass` inside a CONFINED workspace maps to
  the autoapprove posture (acceptEdits + bounded tools); `--dangerously-skip-permissions` is
  emitted only for an unconfined context — a road no mission takes. Role clamp unchanged and
  still first. CliBoundaryCharacterizationTests carries the matrix;
  ActingMissionPipelineTests pins both roads.
- **The mission record reaches chat.** `RecordMissionAnswer` now leads with `final_result` (the
  inversion is fixed) and appends `MissionReport.Render(Compile(...))` — v0.3.8.73's compiled
  record finally has its reader. Honest about absence ("outcome_code: none persisted").
- **`manage_models` exists.** It was required by `POST /routes/{role}` and absent from
  `ApiPermissions`, so every route write 403'd for everyone. Found live, first minutes of the
  qualification attempt. Ships granted, like its read twin.

STILL OPEN, and honestly: `dotnet_test` run INSIDE a materialized revision of the qualification
clone exited 1 on the operator's Windows machine while the same suite is green in CI and in a
Linux revision-simulation — undiagnosed; the check evidence now captures output, so the next
failing run can be read instead of guessed at. The deliverable evaluation layer ran
`not_checked` during the qualification (objective verification disabled in the operator's
config) — honest, but a passing run with that layer ON has not happened. And the operator-check
declaration (`workspace_checks`) still is not on the editable-settings surface.

THE FINDINGS THAT MUST KEEP TRAVELLING: (1) ask "does this have a call site on the path that
matters", not "is it tested" — the operator report compiler had writers and no reader for
sixteen releases; (2) a cross-boundary value is obtained FROM THE PRODUCER, never constructed to
match (`CrossBoundaryAgreementTests`); (3) checks that answer a question ADJACENT to the one
asked are the house defect — the live qualification attempt added `manage_models` to the list.

Repo: `formicaria/anthill`; remote `https://github.com/formicaria/anthill.git`. Build with
`-c Release`, and stop `Anthill.Api` first — a running instance locks `Anthill.Core.dll`. The
operator now works on a **Linux laptop** (bash); the earlier PowerShell 5.1 machine is still in use
occasionally, where `&&` is a parse error and statements must be separated. A Linux sandbox CAN
build and test everything except `Anthill.Desktop` (net9.0-windows ref packs): .NET 9 SDK tarball +
the operator's NuGet cache as `NUGET_PACKAGES`, restore fully offline.

Version bumps touch FIVE markers (RegressionGuardTests + the PLAN mention):

1. `<AnthillVersion>` in `Directory.Build.props`
2. `AnthillRuntime.Version`
3. `**Current version:** vX.Y.Z` in README.md
4. The TOP `## vX.Y.Z` entry in CHANGELOG.md (headings unique, DESCENDING; no file header line)
5. The literal `vX.Y.Z` in docs/PLAN.md ("Shipping release")

Release recipe — commit with explicit paths (`data/anthill.json` is live local config, never
commit it), push, PR, watch checks, squash, pull main, `git log --oneline -1` MUST show the
version's commit before tagging, then tag. PR #215 and the v3.8.13-tag-on-v3.8.12 incident are
both that check skipped.

If a session proposes something that contradicts docs/archive/v3/REFACTOR-PLAN.md, the plan is
probably right — it is the record of what was measured rather than assumed.
