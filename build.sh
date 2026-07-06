#!/usr/bin/env bash
# ANTHILL v1.8.29.1 build — native C++ kernel first, then the .NET solution.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==> Building native compute kernel (C++20)"
KERNEL_DIR="$ROOT/native/anthill_kernel"
if command -v cmake >/dev/null 2>&1; then
  cmake -S "$KERNEL_DIR" -B "$KERNEL_DIR/build" -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build "$KERNEL_DIR/build" --config Release
  # Surface the artifact next to the kernel sources so Anthill.Core's csproj can copy it.
  find "$KERNEL_DIR/build" -name '*anthill_kernel*' \( -name '*.so' -o -name '*.dll' -o -name '*.dylib' \) \
    -exec cp {} "$KERNEL_DIR/" \; 2>/dev/null || true
elif command -v g++ >/dev/null 2>&1; then
  echo "    cmake not found; building with g++ directly"
  g++ -std=c++20 -O2 -fPIC -shared "$KERNEL_DIR/anthill_kernel.cpp" -o "$KERNEL_DIR/libanthill_kernel.so"
else
  echo "    WARNING: no C++ toolchain found — the .NET build will use the managed kernel fallback."
fi

echo "==> Restoring and building the .NET solution"
dotnet build "$ROOT/Anthill.sln" -c Release

echo "==> Running tests"
dotnet test "$ROOT/Anthill.sln" -c Release --no-build

echo "==> Publishing self-contained Linux x64 binary to dist/linux-x64/"
dotnet publish "$ROOT/src/Anthill.Cli/Anthill.Cli.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=none \
  -o "$ROOT/dist/linux-x64"
echo "    Binary: $ROOT/dist/linux-x64/anthill"

echo "==> Done."
echo "    To start:   export ANTHILL_API_TOKEN=<your-token>"
echo "                ./dist/linux-x64/anthill --api"
echo "    Or dev run: dotnet run --project src/Anthill.Cli -- --api"
echo "    Self-test:  dotnet run --project src/Anthill.Cli -- --selftest"
