# ANTHILL session handoff

Paste the block below into a fresh session. Overwrite this file when it goes stale.

---

The ANTHILL Core/Modules refactor is COMPLETE as of v3.8.17 — phases 0 through 7, fifteen releases
from v3.8.3. `docs/REFACTOR-PLAN.md` is now a finished record rather than a plan: every phase names
the release it shipped in, two items are marked superseded with the measurement that superseded
them, and §6 carries the five rules the execution produced. Read it before proposing anything that
touches the module boundary.

Repo: C:\Users\jconn\OneDrive\Documents\vscode\anthill\operation-anthill. Origin is
`thexonexone/operation-anthill` and is the ONLY remote — there is no `upstream`. Build with
`-c Release` (that's what CI uses). The shell is PowerShell 5.1, so `&&` is a parse error — run
build and test as separate statements. I run the builds; you don't have a .NET SDK, and you have no
GitHub credentials either — you can read from origin but not push.

State: main is at v3.8.17, tagged and green.

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
    gh pr create --head <branch> --title "<version>: <title>" --body "..."
    gh pr checks --watch
    gh pr merge --squash
    git checkout main
    git pull origin main
    git log --oneline -1        # MUST show the commit for the version about to be tagged
    git tag v<version>
    git push origin v<version>

That `git log` check is the only thing release.sh provided that the manual tag doesn't. Skipping it
once put a v3.8.13 tag on a v3.8.12 commit. Commit with explicit paths — `data/` and
`scripts/qualify.ps1` are untracked and not gitignored. Pass `--title` explicitly: with more than
one commit on a branch `--fill` titles the PR after the BRANCH, which is how v3.8.15 landed on main
as "release/v3.8.15 (#193)".

If a session proposes something that contradicts docs/REFACTOR-PLAN.md, the plan is probably right —
it's the record of what was measured rather than assumed.
