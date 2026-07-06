# ANTHILL centralized validation (v1.8.28, NORTH_STAR section 4).
# One command that runs every required recurring validation. CI runs the same steps.
# ASCII-only on purpose: Windows PowerShell 5.1 parses BOM-less .ps1 files as ANSI.
#
#   .\scripts\validate.ps1          # restore + build + test (includes regression guards)
#   .\scripts\validate.ps1 -Full    # also publish self-contained win-x64 + run --selftest
param([switch]$Full)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

Write-Host "==> dotnet restore" -ForegroundColor Cyan
dotnet restore Anthill.sln
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "==> dotnet build (Release)" -ForegroundColor Cyan
dotnet build Anthill.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "==> dotnet test (Release) - includes RegressionGuardTests (version markers, migration idempotence, UI glyph integrity, no-Python guard)" -ForegroundColor Cyan
dotnet test Anthill.sln -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit 1 }

if ($Full) {
    Write-Host "==> dotnet publish (win-x64, self-contained, single-file)" -ForegroundColor Cyan
    dotnet publish src/Anthill.Cli/Anthill.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o ./publish/validate-win-x64
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Write-Host "==> --selftest" -ForegroundColor Cyan
    $env:ANTHILL_API_TOKEN = "validate-" + [Guid]::NewGuid().ToString("N")
    & ./publish/validate-win-x64/anthill.exe --selftest
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    Write-Host "==> node --check on embedded UI JavaScript" -ForegroundColor Cyan
    $html = Get-Content -Raw src/Anthill.Api/Ui/index.html
    $blocks = [regex]::Matches($html, '(?s)<script\b[^>]*>(.*?)</script>') |
        ForEach-Object { $_.Groups[1].Value } | Where-Object { $_.Trim() }
    $js = ($blocks -join "`n;`n")
    $tmp = Join-Path $env:TEMP "anthill_ui_validate.js"
    [IO.File]::WriteAllText($tmp, $js, [Text.UTF8Encoding]::new($false))
    node --check $tmp
    if ($LASTEXITCODE -ne 0) { exit 1 }
} else {
    Write-Host "==> SKIP node --check (node not installed; CI ui-integrity job still enforces it)" -ForegroundColor Yellow
}

Write-Host "==> ALL VALIDATIONS PASSED" -ForegroundColor Green
