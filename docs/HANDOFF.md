# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.110** (`a1e2a4d`, tagged and released) plus Colony Live
(`2bf7daa`, PR #77, rebase-merged). **`release/v0.3.8.111` is the release commit for Colony Live**:
version markers, CHANGELOG heading, PLAN §2b/§2c, this file.

NOTE ON HISTORY: every commit SHA changed on 2026-09-01 when authorship was folded to three
contributors. Anything quoting a SHA from before that date is quoting a commit that no longer
exists. See the "anthill" project doc `history-rewrite-2026-09-01.md`. A stale local `main` from
before the rewrite shows as "unrelated histories" against origin — reset it, do not merge it.

WHAT `.111` DELIVERS. Colony Live — the Colony page's opt-in 3D formicarium view behind a `Live 3D`
switch; the classic canvas stays the default and the permanent fallback. Two split assets in
`src/Anthill.UI/`: `colony-topology.js` (projection — consumes the data app.js already holds, never
fetches) and `colony-live.js` (renderer — canvas-2D with a 3D projection, no framework, no CDN, CSP
stays `script-src 'self'`). Records grow only on real record-creating events; shell records are
event-derived facts, read-only; a record click opens the existing Agent Inspector; sectors with no
real records say `demo data` on hover. Mounted into the same `#colony-canvas-area` that re-parents
across Colony/Dashboard/Chat. 38 lines of wiring in app.js; it stays under the 10,000-line guard.

WHAT `.111` IS NOT: the §19 context-record index (does not exist; `verifyRecord()` is wired and never
fires), any WebGL path, Micromound telemetry beyond the existing status. The manual QA gate —
toggle on the Colony page, carry through Dashboard and Chat, Motion/Labels/Pheromones passthrough
— is the operator's and was left unticked on PR #77 deliberately.

THE PROGRAM SHIFTED BY ONE — stated, not absorbed. `.110` reserved `.111` for R0 enforcement;
Colony Live arrived as a finished UI-only package and shipped alone. Now:
- `.112` — R0 enforcement tooling (warnings-as-errors, analyzers, dependency and secret scanning,
  complexity budget, module auto-discovery, guard hierarchy written down); the S1 filesystem TOCTOU
  window (needs a HANDLE-returning path guard and P/Invoke — .NET exposes no portable `openat`); the
  system-prompt channel confirmed for the four agent CLIs that declare none; Ollama capability
  discovery; the literal-only guard sweep (TEN more instances — the right fix is one shared
  `SourceText` helper, not ten widened regexes); the `.97` Windows residual; the capability-table
  reconciliation.
- `.113` — typed database rows: 509 sites, 47 public methods, 89 consumer files, one slice at a time.

A GUARD THAT NOW BITES AT `.113`: `TheUniversalWorkflowProgram_IsExactlyTheRangeItDeclares` asserts
`to > from`, which cannot hold when one release remains.

MAIN IS PR-ONLY AND LINEAR. Branch protection refuses direct pushes AND merge commits: push a
branch, open the PR, wait for all seven checks (the Windows .NET job is the slow one, ~10 min), and
use **Rebase and merge**. A `git merge --no-ff` to main is rejected at push; this session learned it
the hard way and burned PR #76 doing so.

THE LIVE PACK is still the operator's step — `anthill/live-pack-runbook.md` in the project.
`QUALIFICATION.md` §3 stays PARTIALLY RUN. There are now FIVE recognized classes: `system_audit`,
`troubleshooting`, `system_action`, `external_action`, `research`.

RELEASE_MSG.txt is untracked in the repo root and feeds `git commit -F` / `gh release create
--notes-file`; `ReleaseNotesTests` requires its first line to equal the changelog's top heading.
Regenerate it every release — a stale one shipped `.67` under `.60`'s name.

ROSTER, quoted correctly: **25 registered roles, 34 workers, 12 executable role types** under
`activation_tier: full` + `roster_profile: full` (both shipped defaults). Only SIX are executable by
flag — researcher, file, web, coder, builder, verifier; the other six (tester, soldier, medic,
archivist, ui_cartographer, scribe) are specialists opened by canary gates; thirteen never execute
(queen, director, planner, constraint, quartermaster, and the eight homelab ants).

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
