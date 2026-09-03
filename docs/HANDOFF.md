# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.114** (`b9d4b1d0`, tagged). **v0.3.8.115 is complete and green in the
working tree and is NOT committed**: Colony Live, the console half of the last two releases.

NOTE ON HISTORY: every commit SHA changed on 2026-09-01 when authorship was folded to three
contributors. Anything quoting a SHA from before that date is dead. See the "anthill" project doc
`history-rewrite-2026-09-01.md`.

TWO PEOPLE LAND ON `main` NOW. `.111` (Colony Live) arrived from xchronusx while `.112` was being
built. Every release block must `git fetch` and verify HEAD before applying a patch.

WHAT `.115` DELIVERS, in two halves. **Colony Live**: two read-model endpoints
(`/colony/live/snapshot`, `/colony/live/records`), a vendored three.js WebGL renderer, a HUD asset
carrying breadcrumbs/controls/inspectors/timeline/descent, growth playback reconstructed from
persisted records only, a Micromound descent, and approvals decided through app.js's existing
`doApproval`. **The Micromound console**: `micromound.js`, closing all seven UI GAPs `.114` recorded
— fleet, mint, unlink, stop/resume, charters, manifests, mission dispatch, mission evidence, the
resolver and the evidence feed. `MicromoundWidgets.StatusOf` is public and the fleet listing carries
its verdict, so no client computes a status.

**THE THING TO CARRY FORWARD FROM `.115`.** The brief said "preserve the existing WebGL renderer".
There was none — `.111` shipped canvas-2D with a hand-rolled projection. Checking the premise before
building on it was the highest-value thing this release did, and the second-highest was writing the
§17 guards BEFORE believing the code: the `Math.random` rule failed on the classic fallback and
exposed `demoTopology()` inventing a mission, three named ants and a fabricated approval boundary at
every startup, plus a `setTopology` reading five fields the new projection does not emit, which would
have rendered permanently blank while looking wired up. Neither was found by reading.

FIVE FILES, ONE JOB EACH, and the boundary is guarded: `colony-topology.js` (what is true, no fetch,
no drawing), `colony-renderer.js` (WebGL, no state decisions), `colony-live.js` (renderer choice +
the canvas fallback), `colony-host.js` (the ONLY file that fetches), `colony-hud.js` (chrome and
controls). `ColonyLiveGuardTests` enforces that split and thirteen other rules.

**WHAT IS LEFT, all outside the closed program:**
- The remaining **45** untyped store methods — one slice per release, each lowering the ratchet.
  SKIPPED TWICE NOW (`.114` and `.115`). The ratchet is enforced at ≤ 45 so it cannot reverse; this
  line is the only thing stopping it quietly stopping.
- **Bus-only events.** Every Micromound event and 31 other publish sites reach `/events/stream`
  without ever being written. They cannot be replayed on reconnect, cannot appear in growth playback,
  and cannot be audited after the fact. The fix is a DECISION — persist-then-publish for the classes
  that are evidence, transient on purpose for the rest, with the split written down. `docs/PLAN.md`
  §2e carries it as the top item.
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

PROCESS FACTS WORTH KEEPING:
- NEVER run `git` through the device bridge. It takes `index.lock` and cannot unlink it.
- `git apply` is ATOMIC. It prints "Applied patch to X cleanly" per file and can still reject the
  whole patch, having applied NOTHING. Deliver files whole and verify by hashing both sides.
- Never `dotnet test --no-build`. It reports a pass from a stale assembly over a failed build.
- A namespace whose last segment matches a type or property name shadows it. `Anthill.Core.ColonyLive`
  is named that way on purpose: the records carry a `Colony` property.
- `Anthill.Core` AND `Anthill.Tests` both declare a global `using Task = …`. Adding that alias is
  CS1537. Paid twice, at `.109` and `.110`.
- A new event type must be declared in `EventTypes` or `EventVocabularyTests` refuses it — and since
  `.112` that guard resolves named constants too, so it sees far more call sites than it used to.
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
- `RELEASE_MSG.txt` is untracked, read by `ReleaseNotesTests`, and cannot travel in a `git diff`.
- `gh pr merge --delete-branch` fast-forwards main without setting an upstream; a bare
  `git pull --ff-only` then fails with "no tracking information". Set the upstream first.

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
