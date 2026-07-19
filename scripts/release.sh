#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# ANTHILL guarded release. Run this on main AFTER the version-bump PR is merged.
# It refuses to tag unless everything the release workflow needs is true, then
# tags + pushes (which triggers .github/workflows/release.yml).
#
#   bash scripts/release.sh
#   RELEASE_REMOTE=upstream bash scripts/release.sh   # explicit remote override
#
# Checks (all must pass):
#   • you are on main
#   • local main == <remote>/main (pull first)
#   • the tag vX.Y.Z[.W] does not already exist
#   • CHANGELOG.md has a matching "## vX.Y.Z[.W]" section
# The pre-push hook (.githooks/pre-push) is a second line of defence.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail
verfile="src/Anthill.Core/Configuration/AnthillRuntime.cs"

ver="$(grep -o 'Version = "[^"]*"' "$verfile" | head -1 | sed 's/.*"\(.*\)"/\1/')"
[ -n "$ver" ] || { echo "✗ Could not read Version from $verfile"; exit 1; }
tag="v$ver"

# v2.3.2: the canonical repo is not always 'origin' (a fork-origin + upstream clone pushed the
# v2.3.0 tag to the fork, so no Release workflow ran on the real repo). Use RELEASE_REMOTE if
# set, else the remote that main tracks, else origin.
remote="${RELEASE_REMOTE:-$(git config branch.main.remote 2>/dev/null || true)}"
remote="${remote:-origin}"
echo "→ Releasing via remote '$remote' ($(git remote get-url "$remote" 2>/dev/null || echo '?')). Override with RELEASE_REMOTE=<name>."

branch="$(git rev-parse --abbrev-ref HEAD)"
[ "$branch" = "main" ] || { echo "✗ On '$branch', not main. Run: git checkout main && git pull"; exit 1; }

git fetch -q "$remote" main || { echo "✗ Could not fetch $remote/main."; exit 1; }
if [ "$(git rev-parse HEAD)" != "$(git rev-parse "$remote/main")" ]; then
  echo "✗ Local main is not in sync with $remote/main."
  echo "  The version-bump PR is probably not merged yet, or you need: git pull $remote main"
  exit 1
fi

grep -q "^## v$ver\b" CHANGELOG.md || { echo "✗ CHANGELOG.md has no '## v$ver' section (release notes)."; exit 1; }

if git rev-parse "$tag" >/dev/null 2>&1; then
  echo "✗ Tag $tag already exists locally. To re-release: git tag -d $tag && git push $remote :refs/tags/$tag"
  exit 1
fi

echo "✓ main is synced with $remote, Version=$ver, CHANGELOG has ## v$ver."
read -r -p "Release $tag via $remote? [y/N] " ok
case "$ok" in y|Y|yes) : ;; *) echo "Aborted."; exit 0 ;; esac

git tag "$tag"
git push "$remote" "$tag"
echo "✓ Pushed $tag to $remote — Release workflow will build binaries + publish the GitHub Release."
