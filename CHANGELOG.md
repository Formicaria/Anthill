# ANTHILL Changelog

> Versioning convention: each autonomy phase or notable feature ships as a patch bump.
> Phase 1 = **v1.8.1**, live console + operator accounts = **v1.8.2**, and so on.

## v1.8.2 — Live colony console + operator accounts

Schema bumped to **v9**. The web console becomes a fully operable control plane, and the shared
API token is replaced by real password-based operator accounts with roles.

Added:

- **Operator accounts + roles (replaces token-only web auth)**: the console is now secured by
  password login, not a shared API key. First run shows a one-time setup screen that creates the
  initial **administrator**; thereafter everyone signs in with a username/password and receives an
  in-memory **session** (12-hour sliding expiry, dropped on restart). Two roles:
  - **Administrator** — full control, including user management.
  - **Mission Coordinator** — may *only* send missions to the Queen and read the event logs (plus
    live status to watch them); everything else is denied at the API and hidden in the UI.

  Passwords are salted **PBKDF2-SHA256** (120k iterations), verified in constant time. New `users`
  table (schema v9, migration `user_accounts`); endpoints `GET /auth/status`, `POST /auth/{setup,
  login,logout}`, `GET /auth/me`, and `GET/POST/PATCH/DELETE /users`. A 👥 Users management page
  lets admins create accounts, assign roles, reset passwords, enable/disable, and delete. Changing a
  user's password, role, or status immediately revokes their active sessions, and the last remaining
  administrator can never be demoted, disabled, or deleted. CLI recovery: `--add-user <u> <p> [role]`
  and `--set-password <u> <p>`.
- **`ANTHILL_API_TOKEN` is now optional** (and no longer required to boot). If set (≥ 32 chars) it
  acts as a programmatic **admin** bearer for scripts/CI; otherwise authentication is purely account-based.
- **Live, actionable console** (same deep-navy/yellow theme):
  - **Editable settings** — Ollama host/model, per-role model routes, capability gates, limits, and
    autonomy budgets are all editable from the page and persisted to `config.json`
    (`GET/POST /settings`). Whitelisted keys only; hard security boundaries stay file-controlled.
  - **Movable / renamable / recolorable ants** — drag to arrange the anthill, double-click to rename,
    set per-caste accent colours and model routes from a dedicated **Ant Configuration** page. Layout
    persists server-side (`GET/PUT /ui/state`, stored in `.anthill/ui_state.json`).
  - **Filterable Event Log** — by ant, event type, severity (errors/alarms), and time window, plus
    text search (`GET /events/json`).
  - **Pheromone Memory page** — strength table with success/failure/net counts and one-click pruning
    of weak and failure-dominant trails (`GET /pheromones/json`, `POST /pheromones/prune`).

Validation:

- `dotnet test`: **52/52** pass. Self-test **15/15** on a fresh database (migration ledger at 9 entries).
- Live HTTP smoke test: first-run setup, login, role gating (coordinator blocked from `/users`,
  `/settings`, `/pheromones/json`), session revocation on password change, logout, last-admin
  protection, and CLI password recovery all verified.

## v1.8.1 — spec-ingestion + autonomy Phases 0–1

Schema bumped to **v8**. Autonomy is fail-closed and off by default.

Added:

- **Long-input / specification-ingestion handling**: mission goals over `long_input_threshold`
  are split into bounded, non-critical section-analysis tasks (run in parallel), a synthesis
  task, then verification. A failed section degrades the mission to Partial instead of aborting
  it. New `Task.Critical` flag generalises fault-tolerant fan-in. Config: `spec_ingestion_enabled`,
  `long_input_threshold`, `max_section_chars`, `max_section_tasks`.
- **Autonomy Phase 0 (rails)**: `objectives` backlog + `autonomy_runs` audit-trail tables,
  domain models, durable kill switch (`.anthill/STOP` + in-process flag), rate `BudgetGuard`,
  and the run-outcome circuit breaker. Config: `autonomy_enabled`, `autonomy_poll_seconds`,
  `autonomy_max_missions_per_hour`, `autonomy_max_missions_per_day`, `autonomy_max_consecutive_failures`.
- **Autonomy Phase 1 (Director loop, MVP)**: `ColonyDirector` works the backlog one mission at
  a time (charter-as-goal; the LLM Strategist is Phase 2), queue-for-review writes only.
  `--autonomous` boot flag and a control plane: `GET /autonomy/status`, `POST /autonomy/{start,stop}`,
  `GET /autonomy/runs`, and `/objectives` CRUD.

Fixed:

- Self-test now passes 15/15 on a **fresh** database: system/sentinel missions are seeded so
  event/message probes satisfy the `events → missions` foreign key on a clean install.

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
