# ANTHILL v1.8.0 build (Windows) — native C++ kernel first, then the .NET solution.
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

Write-Host "==> Done. Try:  dotnet run --project src\Anthill.Cli -- --selftest"
