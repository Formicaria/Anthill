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

State: main is at v3.8.15, tagged and green. Core is 25,267 lines, down from the 34,247 baseline.
Phases 0 through 5c step 3 are shipped. `Anthill.Core/Common` is down to FileSecurity,
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

NEXT: phase 5c step 4, the end of phase 5. **It is fully surveyed in REFACTOR-PLAN.md §5c item 4 —
read that before proposing anything.** The short version:

Six of the seven tool implementations (`DirectoryListTool`, `ReadTextFileTool`, `WriteTextFileTool`,
`ShellCommandTool`, `WebSearchTool`, `ApplyPatchTool`) move to a new `Anthill.Modules.Tools`,
registered through `IModuleContext.RegisterTool`. `SystemInfoTool` stays — operator decision; it
reports core internals rather than gating a capability. Copy `Anthill.Modules.Homelab.csproj` for the
new project: SDK reference only, no `Anthill.Core`, which `ModuleBoundaryTests` enforces from
assembly metadata.

Four things that release has to get right, all recorded in the plan with their resolutions:
`WorkspacePathGuard` becomes an SDK interface injected through the module constructor;
`ToolRegistry.ClassifyThrown` needs an SDK home; five `const` settings become SDK constants; and
`SafetyPolicy.ToolOptions` already carries `ToolRuntime.Live`, so the injected default needs no new
plumbing.

Two silent regressions to avoid: `Anthill.Cli/Program.cs` loads modules but never drains
`ContributedTools` (only `ApiHost.cs:138` does), and `Queen.Views` calls `apply_patch` by name at
lines 87 and 127. Three source-scanning guards break, and two of them —
`CallSiteAuditTests.EveryImplementedTool_IsRegisteredByTheCompositionRoot` and
`ToolInventoryTests.TheInventory_MatchesWhatTheCompositionRootRegisters` — encode "the composition
root is `Queen.BuildToolRegistry`" and need redesigning rather than a path edit.

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
