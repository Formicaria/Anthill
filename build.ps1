# ANTHILL v1.8.15 build (Windows) — native C++ kernel first, then .NET 9 solution + publish.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$kernel = Join-Path $root "native\anthill_kernel"

Write-Host "==> Building native compute kernel (C++20)"
if (Get-Command cmake -ErrorAction SilentlyContinue) {
    cmake -S $kernel -B (Join-Path $kernel "build") -DCMAKE_BUILD_TYPE=Release | Out-Null
    cmake --build (Join-Path $kernel "build") --config Release
    Get-ChildItem -Path (Join-Path $kernel "build") -Recurse -Filter "*anthill_kernel*.dll" |
        ForEach-Object { Copy-Item $_.FullName $kernel -Force }
} else {
    Write-Warning "cmake not found - the .NET build will use the managed kernel fallback."
}

Write-Host "==> Restoring and building the .NET solution"
dotnet build (Join-Path $root "Anthill.sln") -c Release

Write-Host "==> Running tests"
dotnet test (Join-Path $root "Anthill.sln") -c Release --no-build

Write-Host "==> Publishing self-contained single-file exe (win-x64)"
$publishOut = Join-Path $root "publish\win-x64"
dotnet publish (Join-Path $root "src\Anthill.Cli\Anthill.Cli.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $publishOut

$exe = Join-Path $publishOut "anthill.exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "==> anthill.exe ready at $exe  ($size MB)"
    Write-Host "==> Usage: anthill.exe --api      # start API + colony UI"
    Write-Host "==>        anthill.exe --selftest  # run diagnostics"
} else {
    Write-Warning "Publish succeeded but anthill.exe was not found at expected path."
}

Write-Host "==> Publishing self-contained single-file exe (win-x64)"
$distDir = Join-Path $root "dist\win-x64"
dotnet publish (Join-Path $root "src\Anthill.Cli\Anthill.Cli.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $distDir

Write-Host ""
Write-Host "==> Done."
Write-Host "    Executable : $distDir\anthill.exe"
Write-Host "    Run it     : $distDir\anthill.exe --api"
Write-Host "    Then open  : http://localhost:8765/ui"
