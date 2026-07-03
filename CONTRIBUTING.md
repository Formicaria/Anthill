# Contributing to ANTHILL

ANTHILL uses an **issue-first, branch-per-issue, PR-to-main** workflow. Every code change is tracked
in GitHub before it is written, and every commit is traceable to an issue. This keeps the history
auditable and the release notes accurate.

## Golden rule

> **No commit without an issue.** File (or find) the GitHub issue that describes the problem or the
> feature *before* writing the fix. The branch, commits, and PR all reference that issue.

## One-time setup

```bash
gh auth login                 # authenticate the GitHub CLI
bash scripts/gh-setup.sh      # create the label scheme from .github/labels.yml
```

Create a milestone per release version as needed:

```bash
gh api repos/:owner/:repo/milestones -f title="v1.8.18" -f state=open
```

## The workflow for every change

1. **Open an issue.** Use the Bug report or Feature template. Set:
   - a `type:*` label (bug / enhancement / chore / security),
   - one or more `area:*` labels,
   - a `priority:*` label,
   - the target **milestone** (e.g. `v1.8.18`).

   ```bash
   gh issue create --title "[feat]: <summary>" --label type:enhancement,area:ui \
     --milestone v1.8.18 --body "<motivation + acceptance criteria>"
   ```

2. **Branch off `main`.** Name it `<type>/<issue-number>-<slug>`:
   - `feat/47-hud-dashboard`
   - `fix/48-patch-center-empty-json`
   - `chore/49-github-workflow`

   ```bash
   git checkout main && git pull
   git checkout -b feat/47-hud-dashboard
   ```

3. **Commit against the issue.** Reference it in every commit; the final commit may close it:

   ```bash
   git commit -m "v1.8.18: HUD command dashboard

   Adds the Overview status strip and system core.
   Refs #47"
   ```

   Use `Closes #47` / `Fixes #47` on the commit or PR that completes the work so GitHub auto-closes
   the issue when the PR merges.

4. **Open a PR into `main`.** The PR template checklist covers version markers, changelog, tests, and
   the non-regression / no-Python safety gates.

   ```bash
   git push -u origin feat/47-hud-dashboard
   gh pr create --fill --base main --title "v1.8.18: HUD command dashboard" --body "Closes #47"
   ```

5. **CI must be green** (Build & test on ubuntu + windows) before merge.

6. **Merge, then release.** After the PR merges to `main`, tag `main` to trigger the Release workflow
   (it verifies the tag equals `AnthillRuntime.Version` and publishes binaries + a GitHub Release from
   the matching `## v<version>` CHANGELOG section):

   ```bash
   git checkout main && git pull
   git tag v1.8.18 && git push origin v1.8.18
   ```

## Versioning

Each notable feature or fix is a patch/maintenance bump (`v1.8.x` / `v1.8.x.y`). When you change the
version, update **every** marker (the console checks and the release job enforce this):

- `src/Anthill.Core/Configuration/AnthillRuntime.cs` → `Version`
- `src/Anthill.Api/Ui/index.html` → `<title>`, auth-logo, `nav-logo-ver`
- `src/Anthill.Cli/Program.cs` and `src/Anthill.Api/Program.cs` entry-point comments
- `build.sh` header and the `README.md` version badge
- add a `## v<version>` section to `CHANGELOG.md`

## Hard constraints (enforced in review)

- **No Python.** Do not create or modify Python files; `py.old/` is archived history, not active code.
- **Additive over destructive.** Do not remove existing pages, routes, or API behavior.
- **Never break** mission submission, autonomy controls, or patch approval/apply.
- **Real data only** in the UI — labeled fallbacks (`—` / Unknown / empty state), never fabricated
  operational values.
- Preserve authentication/permission checks and the approve-then-apply write-safety model.

## Branch protection (recommended repo settings)

- Require a PR before merging to `main`.
- Require the CI status check to pass.
- Require branches to be up to date before merging.
- Restrict who can push tags (releases).
