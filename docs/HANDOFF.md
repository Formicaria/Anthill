# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

State: main carries **v0.3.8.98** (`4a4ed7f`, tagged and released — the first universal vertical
slice). **`release/v0.3.8.99` is complete and green**: sourced research with claim→source
traceability, both from the web and from the colony's own memory.

WHAT `.99` DELIVERS, in one paragraph: a research answer is a typed CLAIM RECORD (`sourced_answer`)
rather than prose — each claim with the url that supports it, or an explicit `[UNSOURCED]` marker —
and `CitationIntegrity` resolves every cited url against what the mission ACTUALLY retrieved, from
`source_set` (the web) and `recall_set` (prior missions, citable as `mission:<id>`). A citation that
resolves to nothing refuses the mission and the url is named. An unsourced claim is marked and
counted and is NEVER fatal, because refusing a mission for admitting what it could not attribute
would teach that deleting the unsupported parts is how an answer passes. The rendering comes from
the record and is composed AHEAD of answer synthesis, so a paraphrase cannot drop a caveat while
keeping the claim it qualified.

WHAT IT DELIBERATELY DOES NOT DO: judge whether a source SUPPORTS the claim. Traceability is
answerable from a record; support is semantic, and a model asserting it is the evidence v2.19.0
stopped accepting.

THREE DEFECTS `.99` FOUND IN ITSELF, all found the same way and worth carrying forward:
(1) `Json.Dumps` sets no naming policy, so `new { src.Title, src.Url }` serialises PascalCase while
both readers asked `TryGetProperty("url")` — case-SENSITIVE — and silently resolved nothing; the
unit tests passed because their fixtures wrote the payload the way the READERS expected rather than
the way the PRODUCER writes it, so a green suite proved only that two things written together
matched each other. (2) The fabrication test asserted `IsPositive == false`, which ANY failure
satisfies. (3) Strengthening it to demand the explanation NAME the gate immediately exposed that
`evaluation_explanation` had never been persisted — since v2.26.0 every reader after the process
exited saw the placeholder "loaded from persisted evaluation".

**The lesson, now written at the call sites:** three consecutive defects were caught not by the
feature's own tests but by making an assertion demand WHY rather than WHETHER — and by entering at
the CONVERSATION rather than at the unit being built. `.98` learned the same thing by a different
road (a capability branch that compiled, read correctly and never executed once).

NOT CLAIMED: any of it live. No search provider has been exercised and no model has written a claim
record. `PLAN.md` §2c records `.99`'s three divergences (no `research` mission class at intake — the
gate keys on what a mission DID, not on a label; no retrieval TIME in the mapping; an unsourced
claim marks the claim and does not demote the mission) and carries `.98`'s unmet items forward:
`blocked_missing_capability`, the call-site mutation property, and the `.97` live pack.

STILL THE OPERATOR'S STEP, unchanged across three releases: objective verification ENABLED in a real
run, an exported `LiveQualificationRecord`, and a live non-code mission (audit or research) through
the composed application. `QUALIFICATION.md` §3 is the authority and says PARTIAL.

WHAT `.98` SHIPPED (full account in its CHANGELOG entry): an audit request classified at intake into
declared dimensions and carried on `MissionContext` as a `MissionSpecification`; workers resolved by
DECLARED CAPABILITY rather than by substring, with a named-but-incompatible worker repaired and the
repair announced; repository AND live colony state inspected (`colony_state` +
`researcher.runtime_researcher`) leaving `inspection` evidence — a NON-DETERMINISTIC lane that
records that an inspection happened and can promote nothing; the answer assembled BEFORE the grade;
and `AssessmentObjective` plus a deliverable ledger refusing an audit that inspected nothing, a
verifier that consumed nothing, or a requested deliverable nothing produced.

One `.97` residual is explicitly carried and is NOT a gate for either release: the Windows
materialized-revision `dotnet_test` failure. It is real, unresolved, and a CODING-lane defect, so it
must not pull a universal-workflow release back into a coding one. A failed check now keeps its
output tail (.97), so the next occurrence is the first one that can be read rather than guessed at.

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
