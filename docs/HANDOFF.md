# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

Continuing the ANTHILL Core/Modules refactor. Read docs/REFACTOR-PLAN.md first — it now carries every
phase's survey, the decisions taken and why, the measurements, and the five rules the execution
produced. Don't re-survey what's already recorded there.

Repo: C:\Users\jconn\OneDrive\Documents\vscode\anthill\operation-anthill. Origin is
`thexonexone/operation-anthill` and is the ONLY remote — there is no `upstream`. Build with
`-c Release` (that's what CI uses). I run the builds; you don't have a .NET SDK, and you have no
GitHub credentials either — you can read from origin but not push.

State: main is at v3.8.16, tagged and green. Core is 24,973 lines, down from the 34,247 baseline
(27%, nothing deleted). PHASE 5 IS COMPLETE; phase 7 has begun. `Anthill.Core/Common` is down to FileSecurity,
MissionConstraints and NetworkUtil, none of them on the tool path.

Recent releases, for the patterns they establish:
- v3.8.12 — `UrlSafety` + `Validation` to `Anthill.SDK.Common`, behind an optional options argument
  whose `null` resolves through `SafetyPolicy`, installed by a `[ModuleInitializer]` in Core rather
  than a composition-root call — because SelfTest, PheromoneEngine, Queen.Views and most of the test
  suite reach these helpers without ever building a colony.
- v3.8.14 — `TextUtil` to the SDK. 119 of 121 references needed no edit.
- v3.8.15 — `ToolDefinition` + `IToolKindExecutor` to the SDK. The plan called the record "entangled
  with `ToolAuthorization` and `ToolInventory`"; measured, that was three lines inside `Validate()`,
  which now reach the core through `IToolDefinitionPolicy` on the same `SafetyPolicy` mechanism.
  All 60 references were bare, so nothing needed an edit.
- v3.8.16 — six tool implementations to `Anthill.Modules.Tools`, ending phase 5. Three findings
  worth more than the move, all of the same shape (compiles, boots, wrong answer): `Queen.Profile`
  is resolved at construction so module tools would not have appeared in its grants — closed by
  `Queen.AdoptModuleTools`, which registers and re-resolves in one call; the CLI had never drained
  `ContributedTools`; and the SDK cannot name `HttpRequestException` without acquiring a
  `System.Net.Http` reference every module inherits. Also ADR-007, recording the boundary.

NEXT: **phase 6 (UI decoupling) — ASK BEFORE STARTING IT.** It is the largest remaining phase and
the only one with no survey recorded: `ApiHost.cs` is 3,283 lines, `app.js` is 498 KB and
unstructured with 9 pollers still to replace with the SSE stream, and the file carries the densest
source-scanning guards in the repo. The plan's phase 6 section is an outline, not a measurement.
Survey it and show the operator before editing anything.

Two smaller things are also open and are much cheaper:

- **`py.old/` deletion (phase 7)** is BLOCKED on a decision rather than on work. CI has a
  `py.old is immutable on pull requests` job, so deleting the directory means deleting the guard that
  protects it — and that guard exists because agents must not edit archived history, which is a
  different act from an operator deliberately removing the archive. 4.3 MB, recoverable from git.
  `README.md` and `.github/pull_request_template.md` reference it too. The companion
  `No Python files outside py.old` check should survive either way.
- **Phase 7's remaining sweep**: dead code, abstractions with a single implementation and no seam
  value, and duplicate logic surfaced by the moves. Nothing is known-stale; it needs a survey.

Version bumps touch SEVEN markers and two test classes enforce them. `RegressionGuardTests` checks
`Directory.Build.props`, `AnthillRuntime.Version`, the `**Current version:** vX.Y.Z` line in
README.md, and a matching CHANGELOG top entry. `DocsConsistencyTests` additionally requires the
literal `vX.Y.Z` in NORTH_STAR.md, ROADMAP.md and DASHBOARD_WORKSPACE.md, and requires ROADMAP's
`## vX.Y.Z` headings to stay unique and ascending. Those three docs use a prepend-and-demote
convention: the new release becomes `**Latest:**` / `**Shipping release:**`, the previous is demoted
to `Preceded by`.

Release recipe — do NOT use scripts/release.sh, its opening `git fetch` hangs on this machine and
every failure has been there:

    git add CHANGELOG.md README.md Directory.Build.props docs src tests
    git commit -m "<version>: <title>"
    git push origin <branch>
    gh pr create --head <branch> --fill --title "<version>: <title>"
    gh pr checks --watch
    gh pr merge --squash
    git checkout main
    git pull origin main
    git log --oneline -1        # MUST show the commit for the version about to be tagged
    git tag v<version>
    git push origin v<version>

That `git log` check is the only thing release.sh provided that the manual tag doesn't. Skipping it
once put a v3.8.13 tag on a v3.8.12 commit; the Release workflow caught it, but don't rely on that.
Commit with explicit paths — `data/` and `scripts/qualify.ps1` are untracked and not gitignored.
Pass `--title` explicitly: with more than one commit on the branch, `--fill` titles the PR after the
BRANCH, which is how v3.8.15 landed on main as "release/v3.8.15 (#193)" rather than under its own
commit message. Harmless, but that log line is what the tag check reads.

The shell is PowerShell 5.1 — `&&` is a parse error, so run the build and test commands as separate
statements.

Open items, from an external peer review verified claim by claim (four of five findings valid):
- Two UI interpolation sites not cleared in v3.8.13: a Proxmox container id at app.js:6629 (external
  data ANTHILL never validates) and a conversation approval action at 8101. Neither known
  exploitable. General shape worth knowing: most of the other 45 sites are safe only because
  unrelated validators happen to exclude apostrophes.
- The event bus cannot report drops. `BoundedChannelFullMode.DropOldest` means `TryWrite` succeeds
  while discarding, and `_dropped` only increments when it returns false — the comment at
  InProcessEventBus.cs:74 says so. Use the item-dropped callback.
- CI writes both test projects' `test-results.trx` into one flat directory, so one overwrites the
  other. `.Wait()`/`.Result` in Homelab tests emit xUnit1031 warnings, and two xUnit2031 warnings sit
  in `ToolCallingLoopTests.cs:179` and `UserDefinedToolTests.cs:329`. All warnings, none blocking.
- *(The documentation-drift finding was cleared after v3.8.15: README no longer claims the v2.x line,
  and the plan no longer labels shipped phases as pending.)*

After phase 5: phase 6 (UI decoupling — 9 pollers, ApiHost.cs at 3,283 lines, the densest
source-scanning guards in the repo) and phase 7 (delete py.old/, the superseded test/ dir and
test.txt, write ADR-007 — the architecture test phase 7 called for shipped early, in v3.8.8).
Ask before starting 6. The peer review recommends 6 before more console functionality, and recommends
the artifact/evidence graph (ADR-004) before any reputation or learning work, on the grounds that
reputation learned before reproducible evidence rewards persuasive prose rather than demonstrated
work.

If a session proposes something that contradicts docs/REFACTOR-PLAN.md, the plan is probably right —
it's the record of what was measured rather than assumed.
