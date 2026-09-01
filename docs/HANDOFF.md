# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.108** (tagged and released). **`release/v0.3.8.109` is complete and
green**: the answer comes from somewhere, and can say where.

WHAT `.109` DELIVERS. The `research` mission class, end to end — and the finding is that research
missions were not failing, they were UNABLE to fail.

`CitationIntegrity` (`.99`) resolves cited urls against retrieved ones, so it catches a fabricated
citation. A mission asked to find something out that retrieved nothing and cited nothing left an
EMPTY STORE, and an empty store contradicts no citation — so the gate correctly read it as "nothing
to check". A fluent answer written from a model's own weights and an answer built from real sources
were indistinguishable at every layer.

`.104` recorded exactly what was missing and why the second trigger could not be faked: no `research`
class, no evidence kind meaning "a source was retrieved", no worker capability a research mission
could require. All three ship together here, because any one alone is a branch nothing reaches.

THE CLASS IS DERIVED: new intent `Research`, new target `World`, and the branch requires BOTH plus
the absence of every colony-side target. `World` vs `External` is DIRECTION — External is where
something goes (a destination a human approves), World is where knowledge comes from. A request
naming both worlds is REFUSED, not admitted: this class's gate speaks only for the retrieval half,
and admitting such a request would let the repository half go unexamined behind a pass.

THE ONE THAT WOULD HAVE BEEN MISSED: the troubleshooting branch read `targets != None`, right while
every target was checkable. With a World target it would have claimed "why is the market moving" —
a class whose premise is a reproduction, applied to something the colony cannot re-run. Narrowed to
`InspectableTargets`, which is what "any target" meant when that branch was written, so no
pre-`.109` request reclassifies.

`source_retrieval` IS ITS OWN EVIDENCE KIND, not another `inspection`, and that is the release's
sharpest small decision. `AssessmentObjective` requires inspection rows before an audit is believed;
had `web_search` written one, an audit of the operator's own repository could have been satisfied by
searching the internet. The deterministic lane still holds exactly one tool.

PER-SECTION EVIDENCE NOW HAS A READER — `.106`'s join, unread until now. It degrades honestly: a
DECLARED section must carry its own retrieval, an INFERRED one falls back to the mission's, because
under an inferred claim the builder is credited with every deliverable and leaves no evidence of its
own. Requiring per-section grounding there would grade the planner's verbosity, not the work.

DEFERRED TO `.110` WITH THE REASON, not silently: `ObjectiveVerification.Required`'s goal re-read (a
coding-lane behaviour change that does not belong in a class release), the unread `model_route`
trail (`.107`'s reasoning stands), Ollama capability discovery, the literal-only guard sweep, and
the capability-table reconciliation.

THE PROGRAM NOW RUNS TO `.111`:
- `.110` — mission RESUMPTION (an approved decision replays the refused step, not just settles the
  question), R0 enforcement tooling, the four security residuals, the `.97` Windows residual, the
  literal-only guard sweep, the capability-table reconciliation, plus the four items above.
- `.111` — typed database rows: 509 sites, 47 public methods, 89 consumer files, one slice at a time.

A GUARD THAT WILL BITE AT `.111`: `TheUniversalWorkflowProgram_IsExactlyTheRangeItDeclares` asserts
`to > from`, which cannot hold when one release remains. The program's last release has to teach it
how a program ends.

THE LIVE PACK is the operator's step. See `anthill/live-pack-runbook.md` in the project for the
procedure. `QUALIFICATION.md` §3 stays PARTIALLY RUN until the exported records exist, and the
capability-table reconciliation they feed is `.110`'s.

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
