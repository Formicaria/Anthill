# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

Continuing the ANTHILL Core/Modules refactor. Read docs/REFACTOR-PLAN.md first — it has every
phase's survey, the decisions taken and why, and the measurements. Don't re-survey what's already
recorded there. Two sections carry corrections written at execution time; those are the accurate
ones.

Repo: C:\Users\jconn\OneDrive\Documents\vscode\anthill\operation-anthill. Origin is
`thexonexone/operation-anthill` and is the ONLY remote — there is no `upstream`. Build with
`-c Release` (that's what CI uses). I run the builds; you don't have a .NET SDK.

State: main is at v3.8.14, tagged and green. Core is 25,426 lines, down from the 34,247 baseline.
Phases 0 through 5c step 2 are shipped. `Anthill.Core/Common` is down to FileSecurity,
MissionConstraints and NetworkUtil, none of them on the tool path.

Recent releases, for the patterns they establish:
- v3.8.12 — `UrlSafety` + `Validation` to `Anthill.SDK.Common`. Zero call sites changed, because all
  four projects already carry `global using Anthill.SDK.Common;`. Both stay static; the two impure
  methods take an optional options argument, and `null` resolves through `SafetyPolicy` to
  `SsrfRuntime.Live` / `ToolRuntime.Live`, installed by a `[ModuleInitializer]` in Core rather than a
  composition-root call — because SelfTest, PheromoneEngine, Queen.Views and most of the test suite
  reach these helpers without ever building a colony.
- v3.8.13 — a model-supplied patch filename could dispatch a second UI action in the operator's
  session. `data-onclick` is a micro-interpreter that splits on `;` and resolves names against
  `window`. Fixed structurally with a plain `data-*` attribute plus a fixed action map, because
  escaping cannot work: `getAttribute()` decodes entities before the parser runs.
- v3.8.14 — `TextUtil` to the SDK. 119 of 121 references needed no edit; one mutable setting
  (`WebSearchKeywords`) joined `IToolRuntimeOptions` beside `WebSearchEnabled`.

NEXT: phase 5c steps 3 and 4, the end of phase 5.

Step 3 — `IToolKindExecutor` + `ToolDefinition` to the SDK. `ToolDefinition.cs` is 222 lines and the
plan records it as entangled with `ToolAuthorization` and `ToolInventory`; survey that entanglement
before moving anything. Note only one `ToolKind` (`Http`) is registered today, so the indirection has
to be built before it can be used. `src/Anthill.Core/Tools` is 2,005 lines total.

Step 4 — the seven `ITool` implementations (`SystemInfoTool`, `DirectoryListTool`, `ReadTextFileTool`,
`WriteTextFileTool`, `ShellCommandTool`, `WebSearchTool`, `ApplyPatchTool`) to a new
`Anthill.Modules.Tools` project, registered through `IModuleContext.RegisterTool`. The plumbing
already exists and needs no further wiring: `RegisterTool` has been typed since v3.8.10, `ModuleHost`
buffers contributions, `ApiHost` drains them into `Queen.Tools`, and all seven already take
`IToolRuntimeOptions`. Copy `Anthill.Modules.Homelab.csproj` for the module — SDK reference only, no
`Anthill.Core`, which `ModuleBoundaryTests` enforces from assembly metadata.

Staying in the core, decided and recorded: `ToolRegistry`, `ToolAuthorization`, `ToolInventory`,
workspaces and sandbox. The "workspaces follow the tools" idea was measured and abandoned — it would
have required `Mission` and `Task` in the SDK, which is the core renamed.

Four rules, each learned the hard way:

1. Before moving type T, enumerate every FULL qualified string —
   `grep -rno "[A-Za-z.]*\bT\b" --include="*.cs" src tests | sed 's/.*://' | sort | uniq -c` — and
   decide per form. Matching a suffix once rewrote twelve `Anthill.Core.Contracts.FailureClass` into
   a namespace that never existed. Where a name exists twice, determine which one each file means.
2. Before adding a MEMBER to a published interface, enumerate its implementers the same way,
   including test fakes. Adding `BlockedPathParts` broke the build on a private `Gates` class in
   `ToolRuntimeOptionsTests`.
3. A file is only as movable as its most qualified reference. Checking using statements is not a
   purity check.
4. One release in the working tree at a time. Don't start edits for N+1 until N is committed.

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
    gh pr create --head <branch> --fill
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

Open items, from an external peer review verified claim by claim (four of five findings valid):
- Two UI interpolation sites not cleared in v3.8.13: a Proxmox container id at app.js:6629 (external
  data ANTHILL never validates) and a conversation approval action at 8101. Neither known
  exploitable. General shape worth knowing: most of the other 45 sites are safe only because
  unrelated validators happen to exclude apostrophes.
- The event bus cannot report drops. `BoundedChannelFullMode.DropOldest` means `TryWrite` succeeds
  while discarding, and `_dropped` only increments when it returns false — the comment at
  InProcessEventBus.cs:74 says so. Use the item-dropped callback.
- Drift: README says v3.x at the top and "the repo currently uses the v2.x line" at line 55; the plan
  still labels phases 2b, 4b and 5b pending though they shipped.
- CI writes both test projects' `test-results.trx` into one flat directory, so one overwrites the
  other. `.Wait()`/`.Result` in Homelab tests emit xUnit1031 warnings.

After phase 5: phase 6 (UI decoupling — 9 pollers, ApiHost.cs at 3,283 lines, the densest
source-scanning guards in the repo) and phase 7 (delete py.old/, the superseded test/ dir and
test.txt, write ADR-007). Ask before starting 6. The peer review recommends 6 before more console
functionality, and recommends the artifact/evidence graph (ADR-004) before any reputation or learning
work, on the grounds that reputation learned before reproducible evidence rewards persuasive prose
rather than demonstrated work.

If a session proposes something that contradicts docs/REFACTOR-PLAN.md, the plan is probably right —
it's the record of what was measured rather than assumed.
