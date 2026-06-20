# ANTHILL Core v1.8.0 — .NET / native C++ hybrid

This is the language-migration release of ANTHILL: the v1.7.1 Python harness, ported
faithfully to an idiomatic **.NET 8 (C#)** solution with a **native C++ compute kernel**
for the hot numeric paths. It preserves the swarm-intelligence architecture — a Queen that
plans and orchestrates, specialised ant agents, a dependency-aware scheduler, visible
pheromone memory, and a secured local API + colony UI — while moving the harness onto a
platform built for long-running services, structured concurrency, and first-class crypto.

The end goal of the project is an OS/application-grade agent harness with a live, navigable
colony UI. This release lays the .NET foundation for that: a class library engine, a hosted
web API + single-page colony console, a CLI, a native kernel, and a test suite.

## Why C# (with a C++ kernel)

A swarm orchestration harness is overwhelmingly *coordination* code: async agent dispatch,
an HTTP API, a SQL database, JSON contracts, caching, and crypto. That is squarely .NET
territory — ASP.NET Core, `Microsoft.Data.Sqlite`, `System.Text.Json`, `IMemoryCache`, and
AES-GCM are all in-box and cross-platform. C++ would force hand-rolling all of that.

The few genuinely numeric hot paths — pheromone reinforcement/decay, mission scoring, and
dependency-graph cycle detection — run on every event and benefit from a deterministic,
allocation-free native implementation. Those live in `native/anthill_kernel` (C++20) and are
called over a tiny C ABI via P/Invoke. If the native library is missing, the managed binding
falls back to a **bit-identical** C# implementation, so the colony never blocks on the native
build. This is the hybrid split you selected.

## Solution layout

```
anthill-dotnet/
  Anthill.sln
  Directory.Build.props              shared TFM (net8.0) / lang version / version
  native/anthill_kernel/             C++20 kernel: pheromone math, mission scoring, cycle detection
  src/Anthill.Core/                  the engine (class library)
    Configuration/                   AnthillConfig, AnthillRuntime (constants, gates, bootstrap)
    Common/                          time, text, json, validation, URL/SSRF safety, file hardening
    Domain/                          enums, Task/Mission/Event/... models, dependency contract, context packets
    Security/                        token strength, constant-time compare, rate limiter, AES-GCM cipher, path guard
    Memory/                          SqliteMemory (schema v7, migrations, FTS, caching, encryption)
    Scheduling/                      TaskScheduler — the dependency state machine (native-backed cycle detection)
    Models/                          ModelRouter + Ollama/placeholder clients
    Tools/                           tool registry + system_info/list_directory/read_text_file/shell/web_search/apply_patch
    Planning/                        Planner (dynamic JSON plan + deterministic fallback)
    Pheromones/                      PheromoneEngine (native scoring), PatchProposalParser
    Agents/                          6 ants + SourceQualityEngine
    Orchestration/                   Queen (mission engine + approvals/patch apply + formatter/view surface)
    Diagnostics/                     SelfTest harness (15 checks)
    Native/                          NativeKernel P/Invoke binding + managed fallback
  src/Anthill.Api/                   ASP.NET Core minimal API + embedded colony UI
  src/Anthill.Cli/                   `anthill` command-line entry point
  tests/Anthill.Tests/               xUnit: scheduler, security, native kernel
```

## Faithful module mapping (Python → .NET)

| Python (`anthill/…`)                | .NET (`Anthill.Core/…`)                              |
|-------------------------------------|------------------------------------------------------|
| `runtime.py` config block           | `Configuration/AnthillRuntime.cs`, `AnthillConfig.cs`|
| `runtime.py` helpers/utils          | `Common/*` (TextUtil, UrlSafety, Validation, Json…)  |
| `runtime.py` models                 | `Domain/Models.cs`, `Domain/Enums.cs`                |
| `_RateLimiter`, token validators    | `Security/RateLimiter.cs`, `Security/TokenSecurity.cs`|
| `SQLiteMemory`                      | `Memory/SqliteMemory.*.cs`                           |
| `core/scheduler.py` `TaskScheduler` | `Scheduling/TaskScheduler.cs`                        |
| `ModelRouter`, `OllamaClient`       | `Models/ModelRouter.cs`                              |
| tools (`BaseTool`, registry, …)     | `Tools/Tools.cs`                                     |
| `Planner`                           | `Planning/Planner.cs`                                |
| `PheromoneEngine`, patch parser     | `Pheromones/PheromoneEngine.cs`                      |
| `*Ant` classes                      | `Agents/Ants.cs`                                     |
| `Queen`                             | `Orchestration/Queen.cs`, `Queen.Views.cs`          |
| `run_selftest`                      | `Diagnostics/SelfTest.cs`                            |
| FastAPI app + UI HTML               | `Anthill.Api/ApiHost.cs`, `Ui/index.html`           |
| `main()` CLI                        | `Anthill.Cli/Program.cs`                             |

## Security, encryption, caching, error handling

Everything the v1.7.1 security baseline guaranteed is preserved, and the migration adds
encryption at rest:

- **Token enforcement** — the API refuses to start unless `ANTHILL_API_TOKEN` is ≥ 32 chars
  and not the placeholder; comparison is constant-time (`CryptographicOperations.FixedTimeEquals`).
- **Rate limiting** — `/missions` capped at 10/min/IP; failed-auth attempts capped at 20/min/IP,
  and successful auth never consumes the failure bucket.
- **Security headers** on every response (`X-Frame-Options`, `X-Content-Type-Options`, CSP, `Referrer-Policy`).
- **No public docs endpoints** — there is no Swagger/OpenAPI surface.
- **SSRF / local filtering** — private, loopback, link-local, reserved, and non-http(s) targets
  are dropped before any agent sees them.
- **Prompt-injection prefix** on every agent system prompt.
- **DB hardening + pre-mission backup** — SQLite files are chmod-600 (POSIX) and snapshotted at mission start.
- **Tool gates fail closed** — shell, file writing, patch application, and web search are off by default.
- **Encryption at rest (new)** — sensitive columns (patch bodies, decision notes) are sealed with
  **AES-256-GCM** via `Security/FieldCipher.cs`. Set `ANTHILL_ENCRYPTION_KEY` (32-byte base64/hex)
  or let ANTHILL generate an owner-only key file under `.anthill/`.
- **Caching** — `IMemoryCache` read-through layer over hot memory reads (recent missions, top
  pheromone trails, memory views) with generation-based invalidation on writes.
- **Error handling** — every tool, model call, DB op, and ant runs inside try/catch boundaries
  that degrade gracefully (sentinel results, logged failure events, bounded retries) rather than
  crashing the colony.

## Database

`Microsoft.Data.Sqlite` with WAL journaling, a 30s busy timeout, and foreign keys on. Every
statement is fully parameterised (no string concatenation into SQL). The schema is **v7** with
the same 13 tables, the same migration ledger, and the same `_ensure_columns` ALTER-guard
tolerance for older databases, plus a few indexes for the hottest lookups.

## Build & run

Prerequisites: the **.NET 8 SDK**, and (optional, for the native path) **CMake + a C++20 compiler**.

```bash
# from anthill-dotnet/
./build.sh            # builds the C++ kernel, then the solution, then runs tests
# or on Windows:  ./build.ps1
```

Run the self-test:

```bash
dotnet run --project src/Anthill.Cli -- --selftest
```

Run a mission from the CLI:

```bash
dotnet run --project src/Anthill.Cli -- --mission "summarize the files in my project"
```

Launch the secured API + colony UI:

```bash
export ANTHILL_API_TOKEN="$(head -c 32 /dev/urandom | base64)"   # >= 32 chars, not the placeholder
dotnet run --project src/Anthill.Cli -- --api
# open http://127.0.0.1:8713/ui  and paste the token to connect
```

Optional local model (same as the Python build):

```bash
ollama pull llama3.1:8b
```

## Notes for the commit

- Tag this as **v1.8.0** — a platform/language migration is a minor-version architectural shift,
  not a patch. The roadmap items earmarked for v1.7.2 (mission recovery, scheduler diagnostics,
  structured event/ledger work) remain open and are a natural next step on this foundation.
- Keep `.anthill/`, `*.db`, and `field.key` out of version control (see `.gitignore`).
- The original Python tree is untouched alongside this folder for reference/diffing.
