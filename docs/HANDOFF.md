# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.97** — execution/promotion closure. **THE v0.3.8.97 TAG IS NOT CUT
YET**: per the release brief it waits for the live qualification pack (Claude Code with objective
verification ENABLED, the exported `LiveQualificationRecord`, QUALIFICATION.md §3 updated from
the store, and the operator-machine `dotnet_test` failure diagnosed). Do not tag until those
gates pass.

WHAT v0.3.8.97 SHIPPED (full account in the CHANGELOG entry): the target tree is resolved from
`PatchSet.WorkspaceId → workspace.SourceRoot` by `PatchTargetResolver` and used by the gate
(new `TargetUnresolvable` refusal, fail-closed), the set applier, the intent hashes,
reconciliation, recovery, and the auto-commit — no project patch consults the configured live
root as its target; the Apply button applies multi-file sets transactionally
(`ApplyApprovedSetAsAUnit`); bypass application is DEFERRED until the inserted tester/soldier
reviews complete (`patch_bypass_deferred` + `MaybeApplyBypassAfterReviews` at review-task
completion); a writable agent CLI with no usable worktree never starts
(`Context.MissionWorktreeMissing` → `AgentCliProvider.Confinement` refusal; the acting branch
fails closed instead of falling through to propose-only); `WorkspaceChangeSet` captures delete
and rename as first-class proposals and refuses unrepresentable changes loudly
(`CaptureResult.Problems`, both callers refuse an unfaithful capture whole); acting Claude Code
may run the repository's DECLARED build/test commands in its worktree (stems from
`WorkspaceCapabilityManifest` → bounded `Bash(stem:*)` in both CLI channels; tester stays the
evidence source); and `MissionReport` reports per-set files, approval state, application state,
and target root. Acceptance: `ExecutionPromotionClosureTests` (two disposable repositories —
B changes wholly, A byte-for-byte never).

AND THE SAME RELEASE CARRIES THE QUALIFICATION DAY'S REMAINING LEDGER, which is what makes the
pack runnable at all: `workspace_checks` and `objective_verification_enabled` are editable
settings now (declaring a check took a file edit and a restart per attempt; the evaluation layer
could not be switched on, which is why every record so far reads `deliverable: not_checked`),
and a failed check KEEPS ITS OUTPUT — head-and-tail truncation, the transcript recorded on the
failure branch, an evidence cap that fits. So two of the .96 STILL-OPEN items below are closed,
and the third — the Windows `dotnet_test` failure inside a materialized revision — is now
diagnosable rather than mysterious: the next failing run is the first one that can be READ.

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

Repo: `formicaria/anthill` in the operator's VSCode folder; remote
`https://github.com/formicaria/anthill.git`. Build with `-c Release`. The operator's shell is
PowerShell 5.1 — `&&` is a parse error; separate statements. A Linux sandbox CAN build and test
everything except `Anthill.Desktop` (net9.0-windows ref packs): .NET 9 SDK tarball + the
operator's NuGet cache as `NUGET_PACKAGES`, restore fully offline.

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
