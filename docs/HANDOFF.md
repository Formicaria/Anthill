# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

The 3.8 line is CLOSED at v0.3.8.34 — the line was renumbered to v0 at this release. Two programs ran and both finished:

The Core/Modules refactor (v3.8.3–v3.8.18) took `Anthill.Core` from 34,247 lines to 24,973 with
nothing deleted, moved the reasoning providers, the homelab and the machine-touching tools into
`Anthill.Modules.*`, and made the boundary an assembly-reference test rather than a review habit.
`docs/adr/ADR-007-module-boundary.md` carries the rule.

The twelve-role activation program (v3.8.19–v0.3.8.34) took the roster from "twelve registered roles,
six of which had never run" to twelve with real triggers, enforced contracts, and learning that only
records verified outcomes. `docs/PLAN.md` is the single forward-looking document; the four planning
documents it replaced are in `docs/archive/v3/`.

THE FINDING WORTH CARRYING FORWARD. Eight subsystems were implemented, tested, and unreachable —
`VerificationRunner` had no production call site since v2.12; the archivist had never executed once;
handoffs were gated on success so the repair path could not fire; `ToolExecutionContext` had no
constructible input. Every one had passing tests, because the tests called the code directly and
nothing asked whether production could get there. When reviewing anything here, the question that
finds real defects is "does this have a call site on the path that matters", not "is it tested".

The second recurring shape: a check that answers a question ADJACENT to the one asked, and passes.
Found ELEVEN times. `docs/PLAN.md` §6 records both patterns with every instance.

READ THIS BEFORE YOU CALL ANYTHING CLEAN. v3.8.31 declared this repository production-ready. An
external source review of v3.8.29 then found five defects that were all still present, all with
passing tests over them: environmental failures charged to the ant for six releases; the verifier
compiling a tree the operator's applier could never produce; the tester→medic repair handoff dropped
on every attempt; readiness reporting the six core ants as blocked by flags that do not exist; and
"runs without an LLM" resting on a test of one method at one boundary.

They shared one shape — a test that BUILDS ITS OWN INPUT in the form its own side expects, so the two
halves of a boundary are each checked against an assumption instead of against each other. And the
v3.8.31 cleanup could not have found any of them: it swept for ABSENCE (TODOs, dead links, undeclared
vocabulary), and all five were things PRESENT and wired wrong.

So: a test for a cross-boundary value must obtain that value FROM THE PRODUCER. Never construct it.
`tests/Anthill.Tests/CrossBoundaryAgreementTests.cs` enforces this and carries three source-level
detectors; each was verified to FAIL against v3.8.31 before being kept, because a guard nobody has
watched fail is a guard nobody has tested.

Repo: C:\Users\jconn\OneDrive\Documents\vscode\anthill\operation-anthill. Origin is
`thexonexone/operation-anthill` and is the ONLY remote — there is no `upstream`. Build with
`-c Release` (that's what CI uses). The shell is PowerShell 5.1, so `&&` is a parse error — run
build and test as separate statements. I run the builds; you don't have a .NET SDK, and you have no
GitHub credentials either — you can read from origin but not push.

BEFORE HANDING OVER A RELEASE, resolve every type you referenced in EVERY changed .cs file — not
just the new test files. v0.3.8.38 broke the build on `ApiJobRegistry.cs` because that file has no
`using Anthill.Core.Memory;` (it fully-qualifies its one field), and a new method signature was
written as `SqliteMemory.MissionJobRow`. The pre-handover check covered the tests and skipped the
source files, which is exactly where the new type reference was. CI caught it in 62 seconds; the
check should have caught it before the push.

Do NOT use `scripts/release.sh` — its opening `git fetch` hangs on this machine. Use the manual
recipe below, INCLUDING the `git log` check before tagging.

State: main is at v0.3.8.34, tagged and green. 1,880+ tests.

VERSIONING CHANGED at this release. Everything before shipped as `v3.x`, which claims a maturity
Anthill has not earned — no live twelve-role mission, Phase 1 of AUTONOMY-10 unfinished, Phase 10 not
started. The line is now `v0.3.8.x`: the existing numbering with a `0.` in front. v1.0.0 is earned by
Phase 10's exit gate. Historical changelog headings keep their original `v3.x` form; the ordering
guard was taught the scheme rather than the record being rewritten.

NOT YET DONE, and honestly: no mission has ever run with `roster_profile: "full"` against a live
model. Everything above is verified by tests, and tests check what their author told them to check —
the twelve-role suite caught its own author's mistake on its first run. A real twelve-role mission is
the next thing worth doing and it belongs to the operator.

The FORWARD PROGRAM is now docs/AUTONOMY-10.md — ten phases, each with an exit gate that must pass
through the real composed runtime. PLAN.md answers only "where is the colony measurably now".
v3.9.0 is Phase 1's remainder: base hashes on patch proposals (nothing carries one today, so a patch
built against a stale read applies silently), delete/rename semantics, atomic staging over the live
tree, and an empty auto-apply allowlist proven to fail closed.

    Anthill.Core   34,247 -> 24,973   (-27%, nothing deleted)
    Anthill.SDK         0 ->  3,152
    Anthill.Modules     -    Reasoning, Homelab, Tools
    ApiHost.cs      3,227 ->    535   (+ 6 partials by resource)
    Anthill.UI          -    5 assets, no .csproj

Five of six success criteria are met. The sixth is honest and open: "a new integration is added as a
module with zero Core edits" has never been demonstrated, because every module so far was an
EXTRACTION rather than an addition. The first genuinely new capability is the test of whether the
boundary works, and it has not been run.

WHAT IS NOT DONE, and was deliberately left:

- **Nine `/events/json` pollers in `app.js`.** They are the fallback the SSE stream was shipped in
  front of in v3.8.3. Replacing them is a console change, not a boundary one.
- **In-core consumers still hold `SqliteMemory` concretely.** Phase 3 shipped the two store
  interfaces a module needed and deferred retargeting the rest — internal tidiness across a very
  large number of call sites, changing nothing about what is possible.
- **Five phase-3 store interfaces were never written** (`IMissionStore`, `IWorkerStore`,
  `IWorkspaceStore`, `ISkillStore`, `IJobStore`). Three modules came out without them. Write one
  when a module needs it, not before.

The peer review's standing recommendation, still unactioned: the artifact/evidence graph (ADR-004)
before any reputation or learning work, on the grounds that reputation learned before reproducible
evidence rewards persuasive prose rather than demonstrated work. Roadmap v3.9.0 is reserved for it.

Two open items from that review remain, neither known exploitable:
- Two UI interpolation sites not cleared in v3.8.13 — a Proxmox container id at app.js:6629
  (external data ANTHILL never validates) and a conversation approval action at 8101. Most of the
  other 45 sites are safe only because unrelated validators happen to exclude apostrophes.
- The event bus cannot report drops. `BoundedChannelFullMode.DropOldest` means `TryWrite` succeeds
  while discarding, and `_dropped` only increments when it returns false — the comment at
  InProcessEventBus.cs:74 says so. Use the item-dropped callback.
- CI writes both test projects' `test-results.trx` into one flat directory, so one overwrites the
  other. Assorted xUnit1031/xUnit2031 warnings; all warnings, none blocking.

Version bumps touch FIVE markers, and two test classes enforce them.

CORRECTED v3.8.33 — this paragraph named NORTH_STAR.md, ROADMAP.md and DASHBOARD_WORKSPACE.md, all
three of which were archived at v3.8.24 when PLAN.md replaced them. It had been telling every fresh
session to edit documents that no longer exist. The markers actually enforced are:

1. `<AnthillVersion>` in `Directory.Build.props`      (RegressionGuardTests)
2. `AnthillRuntime.Version`                            (RegressionGuardTests)
3. `**Current version:** vX.Y.Z` in README.md          (RegressionGuardTests)
4. A `## vX.Y.Z` entry in CHANGELOG.md, which must be the TOP entry  (RegressionGuardTests)
5. The literal `vX.Y.Z` somewhere in docs/PLAN.md      (DocsConsistencyTests)

DocsConsistencyTests also requires CHANGELOG's `## vX.Y.Z` headings to stay unique and DESCENDING,
and every `docs/*.md` link in a live document to resolve.

Release recipe — do NOT use scripts/release.sh, its opening `git fetch` hangs on this machine and
every failure has been there:

    git add CHANGELOG.md README.md Directory.Build.props docs src tests
    git commit -m "<version>: <title>"
    git push origin <branch>
    gh pr create --head <branch> --title "<version>: <title>" --body "..."
    gh pr checks --watch
    gh pr merge --squash
    git checkout main
    git pull origin main
    git log --oneline -1        # MUST show the commit for the version about to be tagged
    git tag v<version>
    git push origin v<version>

BEFORE TAGGING, CHECK THE TAG MATCHES THE CODE (added v0.3.8.34):

    # every marker must already read <version>; a branch with no version bump will look green
    git show HEAD:Directory.Build.props | Select-String AnthillVersion
    git show HEAD:src/Anthill.Core/Configuration/AnthillRuntime.cs | Select-String 'Version ='

PR #215 is why. It was titled "v3.8.34", CI was green, and it changed six files with NO version bump
— no Directory.Build.props, no CHANGELOG. The guards check that the markers AGREE with each other,
not that they MOVED, so a bump-less branch passes everything and the tag lands on a commit that still
calls itself the previous version. That is the same failure as the v3.8.13 tag on a v3.8.12 commit,
reached from the other direction.

That `git log` check is the only thing release.sh provided that the manual tag doesn't. Skipping it
once put a v3.8.13 tag on a v3.8.12 commit. Commit with explicit paths — `data/` and
`scripts/qualify.ps1` are untracked and not gitignored. Pass `--title` explicitly: with more than
one commit on a branch `--fill` titles the PR after the BRANCH, which is how v3.8.15 landed on main
as "release/v3.8.15 (#193)".

If a session proposes something that contradicts docs/archive/v3/REFACTOR-PLAN.md, the plan is probably right —
it's the record of what was measured rather than assumed.
