<!--
  ANTHILL pull request. One PR per issue. Keep the diff focused.
  The PR title should read like the release/commit line, e.g.:
    v1.8.18: <short summary>
-->

## Summary

<!-- What this PR does, in 1–3 sentences. -->

Closes #<!-- issue number -->

## Changes

<!-- Bullet the notable backend / API / UI changes. -->
-

## Version

- [ ] `AnthillRuntime.Version` bumped and all version markers updated (title, auth logo, nav-logo-ver, `build.sh`, `README.md`, CLI/API entry-point comments)
- [ ] `CHANGELOG.md` has a matching `## v<version>` section (the Release workflow extracts it)

## Verification

<!-- What you ran / checked. -->
- [ ] `dotnet build` + `dotnet test` green (or CI is green on this branch)
- [ ] Console JS parses / no new console errors (UI changes)
- [ ] Manual QA of touched areas

## Safety / non-regression

- [ ] Mission submission, autonomy controls, and patch approval/apply still work
- [ ] Settings, Users, Security, Shell, Event Log, and navigation intact
- [ ] No Python files created or modified; `py.old/` untouched
- [ ] No fake/simulated values presented as real data (UI)

## Screenshots / notes

<!-- Optional: before/after for UI, or extra context for reviewers. -->
