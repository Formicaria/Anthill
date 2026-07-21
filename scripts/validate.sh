#!/usr/bin/env bash
# ANTHILL centralized validation (v1.8.28, NORTH_STAR §4).
# One command that runs every required recurring validation. CI runs the same steps.
#
#   ./scripts/validate.sh           # restore + build + test (includes regression guards)
#   ./scripts/validate.sh --full    # also publish self-contained linux-x64 + run --selftest
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> dotnet restore"
dotnet restore Anthill.sln

echo "==> dotnet build (Release)"
dotnet build Anthill.sln -c Release --no-restore

echo "==> dotnet test (Release) — includes RegressionGuardTests (version markers, migration idempotence, UI glyph integrity, no-Python guard)"
dotnet test Anthill.sln -c Release --no-build

if [[ "${1:-}" == "--full" ]]; then
  echo "==> dotnet publish (linux-x64, self-contained, single-file)"
  dotnet publish src/Anthill.Cli/Anthill.Cli.csproj -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o ./publish/validate-linux-x64
  echo "==> --selftest"
  chmod +x ./publish/validate-linux-x64/anthill
  ANTHILL_API_TOKEN="validate-$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')" \
    ./publish/validate-linux-x64/anthill --selftest
fi

if command -v node >/dev/null 2>&1; then
  echo "==> node --check on the console script (v2.6.3: JS lives in Ui/app.js, not inline)"
  node --check src/Anthill.Api/Ui/app.js
  # Any inline <script> content that might creep back into index.html is also checked.
  awk 'BEGIN{RS="</script>"} /<script[^>]*>[^<]/{sub(/.*<script[^>]*>/,""); print; print "\n;\n"}' \
    src/Anthill.Api/Ui/index.html > /tmp/anthill_ui_validate.js
  node --check /tmp/anthill_ui_validate.js
else
  echo "==> SKIP node --check (node not installed; CI ui-integrity job still enforces it)"
fi

echo "==> ALL VALIDATIONS PASSED"
