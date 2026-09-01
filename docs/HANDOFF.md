# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.104** (`3484e33`, tagged and released). **`release/v0.3.8.105` is
complete and green**: a mission that stops says what it is waiting for.

WHAT `.105` DELIVERS — three ways a mission stops short, told apart, because the words decide what
the operator does next. All three used to arrive at `failed_permanent` and invite the same useless
response, a retry.

1. THE DISPATCH-TIME REROUTE, and the gap it closes is one `.104` created. `MissionPreflight` has
exactly ONE call site — `Queen.RunMission`, over the COMPILED plan, before execution — and the plan
does not stop changing there. Handoff tasks, delta-plan tasks, the medic's repair tasks, inserted
policy reviews and added verification steps all reached dispatch unexamined, and those are the tasks
created BECAUSE something already went wrong. `TaskReroute.Evaluate` runs FIRST in
`ExecutionService.RunSingleTask`, before the durable claim and before `AntRuntime.Resolve`, and asks
the one question answerable per task: does the worker about to run this declare the capability it
requires? Rerouted within the role (never across it — a wrong ROLE is a planning error the admission
gate answers for); ambiguity is not a block; nothing-serves-it refuses as `capability_unserved` with
a deterministic block.

2. AN UNANSWERED QUESTION PAUSES. Under `Ask` a side-effecting action with no recorded answer is
refused — untouched, absence is not consent — but the refusal used to be the WHOLE response. The
seam already existed and nothing read it: `EscalationDecision.DecidedBy` is "nobody" when `Ask` got
no answer and names a person on every other path, so a REJECTION (an answer) and an ABSENT decision
(a question) were indistinguishable. Now only the second files a pending `ApprovalRequest` and the
mission grades `waiting_for_approval`. Two constants got their first producer, both the house
defect: that outcome code has been in the vocabulary since v2.19.0 unused, and
`ApprovalActionType.ToolUse` has never had a producer — every approval this colony ever raised is a
`PatchProposal`. NARROWED TO `ToolUse` DELIBERATELY: a pending PATCH approval is the healthy end
state of every coding mission, and reading those as "waiting" would stop every one of them reaching
`completed_verified`, which auto-apply consumes. That is `.74`'s defect, nearly recommitted here.

3. A RECURRENCE NAMES THE STOP IT NEVER CAUSES. An exhausted repair loop stopped with
`adaptive_stop` ("the bound is spent, not the problem") while saying nothing about the failure being
reproducible; it now stops as `repeated_failure` when the store holds a recurrence. THE TRAJECTORY IS
UNCHANGED, and a draft of this release changed it: reading the recurrence above the repair budget
deleted the loop's second generation and the medic's only route in, which `CodePatchLifecycleTests`
refused. A repair generation changes the artifact, so one signature across two generations is the
loop working. `FailureRecurrence` is the one shared reading. The detector returns NULL when the
store cannot be read, because the two consumers need OPPOSITE defaults from the same rows: the
controller treats unknown as no-recurrence (inventing one refuses a repair the mission is owed), the
medic falls back to its narrative scan (losing the bound is how a bounded loop becomes unbounded).

AND RECOVERY FINALLY CONSULTS `FailureClass`. `RecoveryOrchestrator` decided from four booleans and
knew nothing of the taxonomy's twenty-three members and three predicates; a policy denial and a rate
limit reached the same `Retryable` bool. EXTENDED, NEVER REPLACED: `FailureClass.None` means "this
caller has no typed class" and every existing caller (ShadowOperator, the homelab bridge) is
untouched. A supplied class narrows BY CONJUNCTION and never widens — a caller that said no is never
overruled into a retry.

CARRIED DEBT CLOSED: `blocked_missing_capability` was charged `-0.08` against every ant, worker and
task-type path in a plan that never ran, while its own `.104` documentation says nothing reinforces
or retires on the strength of it. It fell through the pheromone switch's default.
`waiting_for_approval` would have been the identical bug on day one. Both score zero now.

NOT CLOSED, AND NAMED: a paused mission does NOT resume itself — approving settles the question and
the outcome stops saying "waiting", but re-running the refused step is `.106`'s continuity work and
is recorded in `PLAN.md` §2c rather than approximated. The citation gate's second trigger still has
nothing to read: no `research` class, no evidence kind for a retrieved source, no capability to
require, and adding one as an addendum is how a request gets silently rerouted. The `.97` Windows
`dotnet_test` residual is still undiagnosed — the sandbox's silent 5000-file truncation fit every
symptom and is NOT the cause (792 eligible files); the truncation is loud now and verification
refuses inside an incomplete sandbox anyway.

THE ONE FROM `.104` STILL WORTH REPEATING: objective verification is no longer optional for a
recognized class and FAILS CLOSED. Expect missions that used to pass to stop passing. That is the
release working.

THE LIVE PACK IS ONE COMMAND AWAY AND STILL THE OPERATOR'S STEP. Run a real mission per class with a
provider attached, then `anthill --live-qualification <mission-id> --json <path>` for each.
`QUALIFICATION.md` §3 is the authority and still says PARTIAL.

ROSTER, quoted correctly: **25 registered roles, 34 workers, 12 executable role types** under
`activation_tier: full` + `roster_profile: full` (both shipped defaults). Only SIX are executable by
flag — researcher, file, web, coder, builder, verifier; the other six (tester, soldier, medic,
archivist, ui_cartographer, scribe) are specialists opened by canary gates; thirteen never execute
(queen, director, planner, constraint, quartermaster, and the eight homelab ants). Drop the tier to
`adaptive` and it is nine; to `core`, six.

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
