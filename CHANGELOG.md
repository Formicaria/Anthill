# ANTHILL Changelog

## v1.8.0 — .NET / native C++ hybrid migration

Language and platform migration of the v1.7.1 scheduler-hardening checkpoint from Python to
an idiomatic .NET 8 (C#) solution with a native C++20 compute kernel. Behaviour is preserved;
the harness gains a service-grade runtime, encryption at rest, and a caching speed layer.

Added:

- Native C++20 compute kernel (`native/anthill_kernel`) exposing a C ABI for pheromone
  reinforcement/decay, mission scoring, and dependency-graph cycle detection, bound via P/Invoke
  (`Native/NativeKernel.cs`) with a bit-identical managed fallback when the library is absent.
- AES-256-GCM field encryption for sensitive columns at rest (`Security/FieldCipher.cs`),
  keyed from `ANTHILL_ENCRYPTION_KEY` or an auto-generated owner-only workspace key file.
- `IMemoryCache` read-through speed layer over hot memory reads with generation-based invalidation.
- xUnit test suite: scheduler regressions, security regressions, and native-kernel equivalence.
- `Anthill.Cli` (`anthill`) with `--mission`, `--api`, `--selftest`, `--status`, `--config`, `--routes`.
- Reworked, animated colony console UI (Queen core, branching task tunnels, pheromone trails, live events).

Changed:

- `anthill/runtime.py` (7,177 lines) decomposed into a structured `Anthill.Core` class library;
  the FastAPI app became an ASP.NET Core minimal-API host (`Anthill.Api`).
- Pheromone trail strength updates remain exact (`round(strength + delta, 4)` clamped); mission
  scoring and cycle detection now run through the native kernel.
- Database access moved to `Microsoft.Data.Sqlite` with WAL, busy timeout, foreign keys, fully
  parameterised statements, and indexes on the hottest lookups.
- Version bumped to **1.8.0** to mark the language migration (schema version unchanged at 7).

Preserved (v1.6.4 / v1.7.x security + scheduler invariants):

- API token strength validation and constant-time comparison at boot.
- Failed-auth rate limiter (counts failures only); mission submission rate limiting.
- Security headers on every response; no public docs/OpenAPI endpoints.
- SSRF/local/private/non-http(s) URL filtering before agents or source records see a URL.
- Prompt-injection system-boundary prefix on every agent prompt.
- SQLite file hardening and automatic pre-mission DB backup.
- Tool gates (shell, file writing, patch application, web search) fail closed by default.
- Scheduler ownership of dependency validation, ready/blocked/skipped transitions, bounded
  retries, duplicate-id safety, lifecycle metadata, and metadata-first `task-graph-v2` export.
- Default graph export excludes full result summaries; `read_graph_results` gates the full export.

Validation:

- Native kernel: builds with g++/CMake; behaviour verified against the Python contract.
- `dotnet build` / `dotnet test`: scheduler, security, and native-kernel suites.
- `anthill --selftest`: framework self-test harness (15 checks).

## v1.7.1 (Python, prior baseline)

Scheduler hardening checkpoint. See the original `anthill_v1_7_1_codex_handoff/CHANGELOG.md`
for the full Python history through v1.6.4.
