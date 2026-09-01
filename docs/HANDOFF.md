# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.109** (`e65a6aa`, tagged and released). **`release/v0.3.8.110` is
complete and green**: an approved decision replays the refused step.

NOTE ON HISTORY: every commit SHA changed on 2026-09-01 when authorship was folded to three
contributors. Anything quoting a SHA from before that date is quoting a commit that no longer
exists. See the "anthill" project doc `history-rewrite-2026-09-01.md`.

WHAT `.110` DELIVERS. Mission resumption — the item deferred from `.105` to `.106` to `.109` to here.

WHY IT KEPT SLIPPING, and it was not want of will: there is NO TYPED MISSION LOADER in this tree.
`GetMission` returns a `Dictionary<string, object?>`, `GetTasksForMission` returns a list of them, and
`new Mission` appears in exactly four places — every one CREATING a mission. The object graph died
with `RunMission`, so there was nothing to re-enter execution with. `MissionRehydration` is that
loader, and `ParseTaskStatus` (declared since the enum was written, called by nothing) has its first
caller.

THE ONE THAT WOULD HAVE BEEN MISSED: approving wrote to `approval_requests`; the mission-lane gate
`OperatorDecisions.ForMission` read `escalation_decisions`. TWO DISJOINT TABLES. An operator's
approval was recorded, shown in the UI, counted in the badge — and invisible to the runtime. A replay
built on the loader alone would have refused identically and re-filed the same question, and the
feature would have LOOKED implemented while changing nothing. `ForMission` now reads both under
last-answer-wins. `OperatorDecisions.Decided` is the new read.

A COMMENT THAT WAS WRONG IS FIXED: `RunMission` claimed since v3.1.0 that a resumed run keeps the
original deadline. Written about a resumption that did not exist, and wrong — a mission waiting on a
person is not running, and charging human latency to its budget would make every slow approval resume
straight into a timeout. `ResumeMission` anchors its own window.

SCOPE OF THE REPLAY IS NARROW: only tasks the mission's own `escalation_refused` events name for the
approved action, never a COMPLETED task (its effects landed), never "every failed task". A rejection
replays nothing.

ALSO IN `.110`: `dotnet` subcommands allowlisted to reporting verbs only (it was the one entry on a
nine-command READ allowlist that could execute workspace-supplied code); a Windows JUNCTION test (the
one reparse point an unprivileged writer can create — every existing link test needs Developer Mode
and silently `return`s without it, so seven facts pass green asserting nothing on a bare Windows
agent); `ObjectiveVerification` reads the contract's `OriginalRequest` instead of the composed goal
with its transcript; and a guard making the unread `model_route` trail permanent.

THE PROGRAM GREW A RELEASE — stated, not absorbed:
- `.111` — R0 enforcement tooling (warnings-as-errors, analyzers, dependency and secret scanning,
  complexity budget, module auto-discovery, guard hierarchy written down); the S1 filesystem TOCTOU
  window (needs a HANDLE-returning path guard and P/Invoke — .NET exposes no portable `openat`); the
  system-prompt channel confirmed for the four agent CLIs that declare none; Ollama capability
  discovery; the literal-only guard sweep (TEN more instances found — the right fix is one shared
  `SourceText` helper returning literal-or-resolved-constant, not ten widened regexes); the `.97`
  Windows residual; the capability-table reconciliation.
- `.112` — typed database rows: 509 sites, 47 public methods, 89 consumer files, one slice at a time.

A GUARD THAT NOW BITES AT `.112`: `TheUniversalWorkflowProgram_IsExactlyTheRangeItDeclares` asserts
`to > from`, which cannot hold when one release remains.

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
