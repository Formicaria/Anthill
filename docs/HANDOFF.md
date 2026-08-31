# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.102** (`34bc529`, tagged and released). **`release/v0.3.8.103` is
complete and green**: approval-gated external actions, and the authority ceiling read for the
first time.

WHAT `.103` DELIVERS, in one paragraph: something can LEAVE the colony, and only to a destination a
human approved. The adapter resolves the operator's alias to a concrete target BEFORE approval is
offered (an operator cannot consent to a name), the resolution is recorded, and what the adapter
reports it actually hit is recorded beside it — so an approval of one destination and a send to
another is refused BY NAME, which no absence check could ever catch because every field is
populated. Delivery happens only under the recorded escalation decision. A refused send writes its
own `external_action` record with the reason, and `ResultAssembler` leads the answer with that
record ahead of every prose path — because a builder whose tool was refused several steps upstream
still writes "I've posted the summary to the team", and absence of a record IS the condition under
which that prose becomes the answer.

AND THE CEILING IS FINALLY READ. `MissionAuthority` has carried a doc comment since `.98` calling
it "the ceiling on what the mission may DO, agreed across specification, operator policy, worker
contract and adapter before dispatch" — and no dispatch ever consulted it. `MissionAuthorityGate` is
one table from action to required authority plus one comparison, swept against
`EscalationGate.SideEffecting` so no side-effecting action can omit an entry. It is NOT a second
escalation gate: that lane asks "did a human decide", per action, at dispatch; this asks "is this the
KIND of mission that may do this at all", once, from intake. Both must pass.

**THE FIRST THING TO KNOW BEFORE TOUCHING THIS: no production adapter ships.**
`ExternalActionTools` has exactly one composition site and it is the test harness. On a real install
an external-action mission classifies, plans, dispatches and refuses with `NOT SENT — Tool not found
or not registered`. Fail-closed and more honest than the pre-`.103` behaviour (that request used to
resolve `general` and the colony wrote prose about a send that never happened) — and not a feature
anyone can use. Wiring an adapter plus its tool-inventory surfaces (`ToolInventory.Implemented`, the
SDK `SafetyPolicy` mirror, `CallSiteAuditTests.implementedBy`, `ToolInventoryTests`' third
composition site) is `PLAN.md` §2c's first open item.

WHAT `.103` FOUND IN ITS OWN PROCESS, and it cost most of a session: the exit gate was written first
as always, but its ROUTING was put in a later increment than the composed missions that needed it. A
task type no contract admits does not fail an assertion — it goes wrong at dispatch, which is a
statement about the harness rather than about the release. A red gate has to be red for the reason
it claims. `.98` and `.102` both put the routing in the gate's own commit; do that.

Three times in the same session a missing or stale input produced a confident-looking pass: a
checkout three releases behind (built `.100`–`.102` on another machine and never fetched), a
PowerShell block with no `&&` where every line ran regardless of the previous one failing, and a
patch that was never downloaded. Every one would have been invisible if the green had been taken at
face value. **Verify the base before trusting a run**: `git log --oneline -1` must show the expected
commit before the patch, and the filtered test count must be non-zero.

NEXT RELEASE IS `.104`, AND IT IS A PREREQUISITE rather than the next feature — the recovery and
answer-coverage work moved to `.105` and `.106`. The mission specification is NOT persisted:
`MissionContext.Create` re-resolves it from the goal on every context build, and three more sites
re-derive meaning from `mission.Goal` independently (`AdaptiveMissionController:137`,
`SpecialistAnts:1039`, and `Ants:819` for constraints). `.103` changed intake, so a `.102` mission
replayed today reclassifies — and `.98`'s own rule is that a grade must be reproducible from the
persisted record. Objective verification is also OFF by default, so every gate `.98`–`.103` built is
inert on a default install. `PLAN.md` §2b's `.104` row carries the full gate.

ROSTER, stated the way it should be quoted: **25 registered roles, 34 workers, 12 executable role
types** under `activation_tier: full` + `roster_profile: full` (both shipped defaults). Only SIX are
executable by flag — researcher, file, web, coder, builder, verifier; the other six (tester, soldier,
medic, archivist, ui_cartographer, scribe) are specialists opened by canary gates, and thirteen are
never executable (queen, director, planner, constraint, quartermaster, and the eight homelab ants).
Drop the tier to `adaptive` and it is nine; to `core` and it is six.

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
