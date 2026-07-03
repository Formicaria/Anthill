#!/usr/bin/env bash
# One-time (idempotent) GitHub setup for the ANTHILL enterprise workflow:
# creates the label scheme from .github/labels.yml. Requires the gh CLI, authenticated.
#
#   gh auth login          # once, if not already
#   bash scripts/gh-setup.sh
#
# Milestones are created per-version as needed, e.g.:
#   gh api repos/:owner/:repo/milestones -f title="v1.8.18" -f state=open
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
labels_file="$repo_root/.github/labels.yml"

# Enable the release guard (pre-push hook) — blocks tagging a version before it's merged to main.
echo "Enabling release guard (core.hooksPath=.githooks) …"
git -C "$repo_root" config core.hooksPath .githooks
chmod +x "$repo_root/.githooks/"* "$repo_root/scripts/"*.sh 2>/dev/null || true
echo "  ✓ pre-push guard active; release with: bash scripts/release.sh"

command -v gh >/dev/null || { echo "gh CLI not found. Install: https://cli.github.com/"; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "gh not authenticated. Run: gh auth login"; exit 1; }

echo "Syncing labels from .github/labels.yml …"
# Parse the simple YAML list ourselves (no yq dependency).
name=""; color=""; desc=""
flush() {
  [ -z "$name" ] && return 0
  # create, or update if it already exists
  gh label create "$name" --color "$color" --description "$desc" --force >/dev/null \
    && echo "  ✓ $name"
  name=""; color=""; desc=""
}
while IFS= read -r line; do
  case "$line" in
    "- "*)
      flush
      name=$(sed -E 's/.*name: *"([^"]*)".*/\1/' <<<"$line")
      color=$(sed -E 's/.*color: *"([^"]*)".*/\1/' <<<"$line")
      desc=$(sed -E 's/.*description: *"([^"]*)".*/\1/' <<<"$line")
      ;;
  esac
done < "$labels_file"
flush
echo "Done. Labels are in place."
