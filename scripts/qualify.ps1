# ANTHILL phase qualification harness (v3.0.0).
#
# WHY THIS EXISTS
# ---------------
# A V3 phase completes when its exit gates are RECORDED AS PASSING, not when its code merges.
# validate.ps1 answers "is it green?" to a human reading a console. This answers "what exactly
# happened?" in a machine-readable artifact written into the repo, so the result can be reviewed
# at full fidelity instead of summarised, truncated, or paraphrased in transit.
#
# It writes:
#   data/reports/qualification-<version>.json   full structured result
#   data/reports/qualification-<version>.md     human-readable summary
#
# Both are gitignored: they are measurements of one run on one machine, not source.
#
#   .\scripts\qualify.ps1                       build + test + inventory audit
#   .\scripts\qualify.ps1 -DbSnapshot <path>    ALSO measure the upgrade of a real database
#   .\scripts\qualify.ps1 -Full                 ALSO publish + selftest
#
# ASCII-only on purpose: Windows PowerShell 5.1 parses BOM-less .ps1 files as ANSI.
param(
    [string]$DbSnapshot = "",
    [switch]$Full
)
$ErrorActionPreference = "Continue"   # a failing gate is DATA, not a reason to abort the report
Set-Location (Join-Path $PSScriptRoot "..")

$reportDir = "data/reports"
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$version = (Select-String -Path "src/Anthill.Core/Configuration/AnthillRuntime.cs" `
    -Pattern 'Version\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
$gates = @()

function Add-Gate($name, $passed, $detail) {
    $script:gates += [pscustomobject]@{ gate = $name; passed = [bool]$passed; detail = "$detail" }
    $mark = if ($passed) { "PASS" } else { "FAIL" }
    $color = if ($passed) { "Green" } else { "Red" }
    Write-Host ("[{0}] {1} - {2}" -f $mark, $name, $detail) -ForegroundColor $color
}

# ---- build ---------------------------------------------------------------------------------------

Write-Host "==> restore + build (Release)" -ForegroundColor Cyan
$buildLog = Join-Path $reportDir "build.log"
dotnet restore Anthill.sln *>&1 | Tee-Object -FilePath $buildLog | Out-Null
$restoreOk = $LASTEXITCODE -eq 0
dotnet build Anthill.sln -c Release --no-restore *>&1 | Tee-Object -FilePath $buildLog -Append | Out-Null
$buildOk = $LASTEXITCODE -eq 0
$buildErrors = (Select-String -Path $buildLog -Pattern ': error ' -ErrorAction SilentlyContinue |
    ForEach-Object { $_.Line.Trim() } | Select-Object -Unique -First 25)
Add-Gate "build" ($restoreOk -and $buildOk) $(if ($buildOk) { "clean" } else { "$($buildErrors.Count) error(s)" })

# ---- tests ---------------------------------------------------------------------------------------

$testResults = @()
$testSummary = "not run (build failed)"
if ($buildOk) {
    Write-Host "==> test (Release), structured results" -ForegroundColor Cyan
    $trxDir = Join-Path $reportDir "trx"
    Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $trxDir | Out-Null
    dotnet test Anthill.sln -c Release --no-build --logger "trx;LogFileName=results.trx" `
        --results-directory $trxDir *>&1 | Tee-Object -FilePath (Join-Path $reportDir "test.log") | Out-Null
    $testsOk = $LASTEXITCODE -eq 0

    # Parse every TRX: totals plus the NAME and MESSAGE of each failure. A failure list is the
    # part worth reading; the console tail usually truncates exactly this.
    $total = 0; $passed = 0; $failed = 0
    foreach ($trx in Get-ChildItem -Path $trxDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue) {
        [xml]$x = Get-Content -Raw $trx.FullName
        $c = $x.TestRun.ResultSummary.Counters
        $total += [int]$c.total; $passed += [int]$c.passed; $failed += [int]$c.failed
        foreach ($r in @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" })) {
            $testResults += [pscustomobject]@{
                test    = $r.testName
                message = ($r.Output.ErrorInfo.Message -replace '\s+', ' ').Trim()
                stack   = ($r.Output.ErrorInfo.StackTrace -split "`n" | Select-Object -First 3) -join " | "
            }
        }
    }
    $testSummary = "$passed/$total passed, $failed failed"
    Add-Gate "tests" ($testsOk -and $failed -eq 0) $testSummary
}

# ---- call-site audit -----------------------------------------------------------------------------
# The v3.0.0 gate. Reported separately from the test total because it is a phase exit gate in its
# own right, not just one more green check.

if ($buildOk) {
    $auditFailed = $testResults | Where-Object { $_.test -like "*CallSite*" -or $_.test -like "*Inventory*" }
    Add-Gate "call_site_audit" ($auditFailed.Count -eq 0) `
        $(if ($auditFailed.Count -eq 0) { "no declaration-without-consumer defects" }
          else { "$($auditFailed.Count) inventory/audit test(s) failing" })
}

# ---- database upgrade ----------------------------------------------------------------------------
# The gate that cannot be measured without a real database. Given a snapshot, this upgrades a COPY
# and reports what happened. Without one, it records NOT MEASURED rather than assuming success --
# an unmeasured gate is not a passing gate.

if ($DbSnapshot -and (Test-Path $DbSnapshot)) {
    Write-Host "==> database upgrade against a real snapshot" -ForegroundColor Cyan
    $work = Join-Path $reportDir "dbcheck"
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $work ".anthill") | Out-Null
    $dbCopy = Join-Path $work ".anthill/anthill.db"
    Copy-Item $DbSnapshot $dbCopy -Force
    $sizeBefore = (Get-Item $dbCopy).Length

    # Isolated home so the upgrade cannot touch the working installation.
    $prevHome = $env:ANTHILL_HOME
    $env:ANTHILL_HOME = (Resolve-Path $work).Path
    $env:ANTHILL_API_TOKEN = "qualify-" + [Guid]::NewGuid().ToString("N")
    dotnet run --project src/Anthill.Cli/Anthill.Cli.csproj -c Release --no-build -- --selftest `
        *>&1 | Tee-Object -FilePath (Join-Path $reportDir "dbcheck.log") | Out-Null
    $upgradeOk = $LASTEXITCODE -eq 0
    $env:ANTHILL_HOME = $prevHome

    Add-Gate "db_upgrade" $upgradeOk `
        ("snapshot {0:N0} -> {1:N0} bytes; see dbcheck.log" -f $sizeBefore, (Get-Item $dbCopy).Length)
    Write-Host "    upgraded copy left at $dbCopy for inspection" -ForegroundColor DarkGray
}
else {
    Add-Gate "db_upgrade" $false `
        "NOT MEASURED - re-run with -DbSnapshot <path-to-production-anthill.db>"
}

# ---- publish + selftest --------------------------------------------------------------------------

if ($Full -and $buildOk) {
    Write-Host "==> publish + selftest" -ForegroundColor Cyan
    dotnet publish src/Anthill.Cli/Anthill.Cli.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:DebugType=none -o ./publish/qualify-win-x64 *>&1 |
        Tee-Object -FilePath (Join-Path $reportDir "publish.log") | Out-Null
    $publishOk = $LASTEXITCODE -eq 0
    if ($publishOk) {
        $env:ANTHILL_API_TOKEN = "qualify-" + [Guid]::NewGuid().ToString("N")
        & ./publish/qualify-win-x64/anthill.exe --selftest *>&1 |
            Tee-Object -FilePath (Join-Path $reportDir "selftest.log") | Out-Null
        Add-Gate "selftest" ($LASTEXITCODE -eq 0) "published binary self-test"
    }
    else { Add-Gate "selftest" $false "publish failed" }
}

# ---- write the record ----------------------------------------------------------------------------

$allPassed = -not ($gates | Where-Object { -not $_.passed })
$record = [ordered]@{
    version      = $version
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
    machine      = $env:COMPUTERNAME
    qualified    = $allPassed
    gates        = $gates
    build_errors = $buildErrors
    failed_tests = $testResults
    test_summary = $testSummary
}
$jsonPath = Join-Path $reportDir "qualification-$version.json"
$record | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 $jsonPath

$md = @("# Phase Qualification - v$version", "",
        "Generated $($record.generated_at) on $($record.machine).", "",
        "## Verdict: $(if ($allPassed) { 'QUALIFIED' } else { 'NOT QUALIFIED' })", "",
        "| Gate | Result | Detail |", "|---|---|---|")
foreach ($g in $gates) { $md += "| $($g.gate) | $(if ($g.passed) { 'PASS' } else { 'FAIL' }) | $($g.detail) |" }
if ($testResults.Count -gt 0) {
    $md += @("", "## Failing tests", "")
    foreach ($t in $testResults) { $md += "- ``$($t.test)`` - $($t.message)" }
}
if ($buildErrors.Count -gt 0) {
    $md += @("", "## Build errors", "")
    foreach ($e in $buildErrors) { $md += "- ``$e``" }
}
$mdPath = Join-Path $reportDir "qualification-$version.md"
($md -join "`n") | Set-Content -Encoding UTF8 $mdPath

Write-Host ""
Write-Host ("==> {0}" -f $(if ($allPassed) { "QUALIFIED" } else { "NOT QUALIFIED" })) `
    -ForegroundColor $(if ($allPassed) { "Green" } else { "Red" })
Write-Host "    $jsonPath"
Write-Host "    $mdPath"
if (-not $allPassed) { exit 1 }
