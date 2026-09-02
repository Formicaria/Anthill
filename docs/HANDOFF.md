# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.111** (`c78ea7e`, Colony Live — xchronusx). **`release/v0.3.8.112` is
complete and green**: the tree enforces its own rules.

NOTE ON HISTORY: every commit SHA changed on 2026-09-01 when authorship was folded to three
contributors. Anything quoting a SHA from before that date is dead. See the "anthill" project doc
`history-rewrite-2026-09-01.md`.

WHAT `.112` DELIVERS. R0's last item — enforcement.

WARNINGS ARE ERRORS, and the ORDER matters: a census across the solution returned ZERO warnings
first, so this flips a clean tree rather than declaring bankruptcy on a dirty one.
`TreatWarningsAsErrors` was `false` EXPLICITLY, which is a decision somebody took, not an omission.

THE LITERAL-ONLY GUARD SWEEP — defect class 11 in the working-rules doc. A guard matching only a
quoted argument cannot see a call site passing a shared constant, so it stops covering the code
exactly as the code gets tidier, SILENTLY. `SourceText.CallSites` / `CallArgument` /
`ConstantsAcrossSource` is one shared reader (depth-aware, bounds a call by its own parens, resolves
against 423 declarations). SEVEN of eleven guards moved onto it.

- `EventVocabularyTests` had FOUR live escapes already open.
- `RoleContractChannelTests` — the S9 prompt-injection guard — had TWO bugs pointing the same way:
  literal-only, AND `[^;]*;`-bounded so any call with a lambda was truncated before its `system:`
  argument.
- `ContractDeclarationTests` was failing in BOTH directions at once.

THE HIERARCHY DOCUMENT FOUND THREE VIOLATIONS OF ITSELF. `docs/GUARDS.md` now states the order
(runtime black-box → typed registry → compiled inspection → source scan last) and the two rules
binding a source scan. "May never depend on a character count" has been in PLAN since `.92`; the
detector found three guards still doing it. The worst sliced 2,500 chars across `EditableConfigKeys`
— the very set it exists to watch GROW.

MODULE AUTO-DISCOVERY replaces a hand-maintained `[InlineData]` list. The composition roots KEEP
their explicit `LoadAll` calls, deliberately: reflecting over whatever assemblies are on disk is a
worse security posture. What was defective was the guard, not the explicitness.

DEFERRED WITH REASONS to `.113`: `AnalysisMode` beyond the SDK default (needs its own census — with
warnings-as-errors on, every CA diagnostic becomes a build failure); central package management (its
failure mode is "nothing builds"); four of eleven literal-only guards, each lower risk than the seven
done; the S1 TOCTOU window; the four unverified agent-CLI system-prompt channels; Ollama capability
discovery; the `.97` Windows residual; the capability-table reconciliation. `.113` also carries typed
database rows.

A GUARD THAT BITES AT `.113`: `TheUniversalWorkflowProgram_IsExactlyTheRangeItDeclares` asserts
`to > from`, which cannot hold when one release remains.

TWO PROCESS FACTS WORTH KEEPING:
- `Anthill.Core` AND `Anthill.Tests` both declare a global `using Task = …`. Adding that alias in a
  new file is CS1537. Paid twice, at `.109` and `.110`.
- A new event type must be declared in `EventTypes` or `EventVocabularyTests` refuses it.

THE LIVE PACK is still the operator's step — `anthill/live-pack-runbook.md`. `QUALIFICATION.md` §3
stays PARTIALLY RUN. FIVE recognized classes: `system_audit`, `troubleshooting`, `system_action`,
`external_action`, `research`.

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
