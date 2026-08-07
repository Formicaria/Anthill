# ANTHILL session handoff

Paste the block below into a fresh session. Delete this file, or overwrite it, when it goes stale.

---

Continuing the ANTHILL Core/Modules refactor. Read docs/REFACTOR-PLAN.md first — it has every
phase's survey, the decisions taken and why, and the measurements. Don't re-survey what's already
recorded there; two sections now carry corrections written at execution time, and those are the
accurate ones.

State: repo at C:\Users\jconn\OneDrive\Documents\vscode\anthill\operation-anthill. Origin is
`thexonexone/operation-anthill` and is the ONLY remote — there is no `upstream`, so release with
`$env:RELEASE_REMOTE = "origin"` in PowerShell, not the `RELEASE_REMOTE=upstream bash ...` form,
which is bash syntax and fails in PowerShell. PRs open normally with `gh pr create --head <branch>`.

Done since the last handoff: v3.8.12 (phase 5c step 2 first half) and v3.8.13 (a security fix),
both merged to main and green. Tags may lag — the release script's fetch has been timing out on
this machine, and the tag is the only step outstanding. Nothing else depends on it.

v3.8.12 — `UrlSafety` and `Validation` moved to `Anthill.SDK.Common`. Zero call sites changed,
because all four projects already carry `global using Anthill.SDK.Common;`. Both stay static and the
two impure methods take an optional options argument; `null` resolves through `SafetyPolicy` to
`SsrfRuntime.Live` and `ToolRuntime.Live`, installed by a `[ModuleInitializer]` in Core rather than a
composition-root call, because SelfTest, PheromoneEngine, Queen.Views and most of the test suite
reach these helpers without building a colony. `IToolRuntimeOptions` gained `BlockedPathParts`; it
already declared the other two patch gates.

v3.8.13 — a model-supplied patch filename could dispatch a second UI action in the operator's
session. `data-onclick` is a micro-interpreter that splits on `;` and resolves names against
`window`; the filename sat in a quoted argument and `escapeHtml` did not encode apostrophes.
`ValidateSafePatchPath` had no reason to object, since quotes are not traversal. Fixed structurally —
plain `data-*` attribute plus a fixed action map — because escaping cannot work here:
`getAttribute()` decodes entities before the parser runs.

Next: `TextUtil` to the SDK — the second half of 5c step 2, SURVEYED and recorded in the plan. 119
bare references need no edit, 2 `Common.TextUtil` need rewriting, 18 consuming files in `src` plus
`JsonSafetyTests.cs`. Exactly one mutable setting, `WebSearchKeywords`, which belongs on
`IToolRuntimeOptions` alongside `WebSearchEnabled`. `MaxResultSummaryChars` and
`TokenEstimateCharsPerToken` are `const` and become SDK constants, as the id caps did in v3.8.12.
Then `IToolKindExecutor` + `ToolDefinition`, then the seven tool implementations.

Four rules, each learned the hard way:

Before moving type T, enumerate every FULL qualified string —
`grep -rno "[A-Za-z.]*\bT\b" --include="*.cs" src tests | sed 's/.*://' | sort | uniq -c` — and
decide per form. Matching a suffix once rewrote twelve `Anthill.Core.Contracts.FailureClass` into a
namespace that never existed. Where a name exists twice, also determine which one each file means.

Before adding a MEMBER to a published interface, enumerate its implementers the same way, including
test fakes. Adding `BlockedPathParts` broke the build on a private `Gates` class in
`ToolRuntimeOptionsTests`. The compiler catches it, so it costs a build cycle rather than a defect.

A file is only as movable as its most qualified reference. Checking using statements is not a purity
check.

One release in the working tree at a time. Don't start edits for N+1 until N is committed.

Version bumps touch SEVEN markers, and two separate test classes enforce them. `RegressionGuardTests`
checks `Directory.Build.props`, `AnthillRuntime.Version`, the `**Current version:** vX.Y.Z` line in
README.md, and that CHANGELOG has a matching top entry. `DocsConsistencyTests` additionally requires
the literal `vX.Y.Z` to appear in NORTH_STAR.md, ROADMAP.md and DASHBOARD_WORKSPACE.md, and requires
ROADMAP's `## vX.Y.Z` headings to stay unique and ascending. Those last three docs use a
prepend-and-demote convention: the new release becomes `**Latest:**` / `**Shipping release:**` and
the previous one is demoted to `Preceded by`.

Build with `-c Release` (that's what CI uses). I run the builds; you don't have a .NET SDK. Commit
with explicit paths — `git add CHANGELOG.md README.md Directory.Build.props docs src tests` — because
`data/` and `scripts/qualify.ps1` are untracked and `.gitignore` does not cover them.

Open, from an external peer review that was verified claim by claim — four of five findings valid:

- Two UI interpolation sites I could not clear in v3.8.13: a Proxmox container id at app.js:6629,
  which is external data ANTHILL never validates, and a conversation approval action at 8101 whose
  origin was not traced. Neither is known-exploitable. Note the general shape: most of the other 45
  sites are safe only because unrelated validators happen to exclude apostrophes.
- The event bus cannot report drops. `BoundedChannelFullMode.DropOldest` means `TryWrite` succeeds
  while discarding, and `_dropped` only increments when it returns false — the code comment at
  InProcessEventBus.cs:74 says as much. Use the item-dropped callback.
- Repository drift: README says v3.x at the top and "the repo currently uses the v2.x line" at line
  55; the plan still labels phases 2b, 4b and 5b as pending though they shipped. `py.old/`, `test/`
  and `test.txt` are all still present — that is phase 7's scope.
- CI writes both test projects' `test-results.trx` into one flat directory, so one overwrites the
  other. `.Wait()`/`.Result` in Homelab tests emit xUnit1031 warnings.

Phases 6 (UI decoupling — 9 pollers, ApiHost.cs at 3,277 lines) and 7 (delete py.old/, the superseded
test/ dir, write ADR-007) are still open. Ask before starting 6. The peer review recommends phase 6
before more console functionality, and recommends the artifact/evidence graph (ADR-004) before any
reputation or learning work, on the grounds that reputation learned before reproducible evidence
rewards persuasive prose rather than demonstrated work.

If the next session proposes something that contradicts docs/REFACTOR-PLAN.md, the plan is probably
right — it's the record of what was measured rather than assumed.
