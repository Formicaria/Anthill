# ANTHILL Changelog

> Versioning convention: each autonomy phase or notable feature ships as a patch bump.
> Phase 1 = **v1.8.1**, live console + operator accounts = **v1.8.2**, enterprise shell UI = **v1.8.3**,
> model provider connections = **v1.8.4**, Phase 2 autonomy (Strategist) = **v1.8.5**, container-style
> deployment (Docker) = **v1.8.6**, LXC deployment = **v1.8.7**, and so on.

## v1.8.7 — LXC deployment

No schema change. Second step of the container/LXC/Windows-Service deployment push (see
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)) — a one-shot installer for a fresh Debian/Ubuntu-family
LXC container, Proxmox or otherwise.

Added:

- **`deploy/lxc/setup.sh`** — unattended installer/upgrader for a fresh Debian 12+/Ubuntu 22.04+
  LXC container. Installs the .NET 9 SDK if missing (Microsoft's apt repo, resolved dynamically
  from `/etc/os-release` rather than hardcoded to one distro/version, with a `dotnet-install.sh`
  fallback for distros/versions Microsoft's repo doesn't have an entry for), clones/updates the
  repo, publishes a self-contained `linux-x64` binary, creates a dedicated unprivileged system
  user, installs + enables the systemd unit, and starts it. Idempotent — re-running it is the
  upgrade path (pulls latest, republishes, restarts). Configurable via `ANTHILL_REPO_URL`,
  `ANTHILL_INSTALL_DIR`, `ANTHILL_SERVICE_USER` env vars.
- **`deploy/lxc/anthill.service.template`** — the systemd unit `setup.sh` installs, with the same
  hardening as the manual systemd install already documented in the README (`NoNewPrivileges`,
  `PrivateTmp`, `ProtectSystem=strict`, scoped `ReadWritePaths`), plus `Environment=ANTHILL_HOME`
  for unambiguous workspace resolution and a generated `/etc/anthill/token.env` for an optional
  static API token.
- No special LXC features (nesting, privileged mode) are required — this is a plain systemd
  service, not Docker-in-LXC, so `NetworkUtil`'s auto-detected reachable IP works with zero extra
  networking config (LXC containers get a real interface directly, unlike Docker's bridge
  default).
- **README**: new "Option E — LXC" under Deploy on Linux, quick-start + pointer to the full guide.
- **`docs/DEPLOYMENT.md`**: §3 filled in — creating the container (Proxmox `pct` commands +
  generic LXD/incus note), installing, upgrading, customizing install location/service user,
  uninstalling. Roadmap table updated (LXC done, Windows Service next).
- **CI**: new `lint-lxc-installer` job — `shellcheck` (GitHub-hosted runners have it preinstalled)
  plus a `bash -n` syntax check on `deploy/lxc/setup.sh`.
- **Releases** (`.github/workflows/release.yml`, added after this version shipped): triggered by
  pushing a `vX.Y.Z` tag. A `verify-version` job fails loudly if the tag doesn't match
  `AnthillRuntime.Version` in the tagged commit, guarding against tagging before a version bump
  actually landed. Builds self-contained `linux-x64`/`win-x64` binaries and a versioned Docker
  image pushed to `ghcr.io/thexonexone/operation-anthill`, then opens a **draft** GitHub Release
  (never auto-published) with notes pulled from the matching `## vX.Y.Z` CHANGELOG section and
  the binaries/image attached. Also fixed the stray `YOUR_ORG`/`anthill-dotnet` placeholder repo
  references across `README.md` to the real `thexonexone/operation-anthill` URL and directory
  name. See [docs/DEPLOYMENT.md §4](docs/DEPLOYMENT.md#4-releases).

Validation:

- `bash -n deploy/lxc/setup.sh` passes (checked manually — no shellcheck available in the
  authoring environment, hence the new CI job to actually run it). Not run against a real LXC
  container; no LXC/Proxmox host available in the environment this was authored in. Try it on an
  actual container before trusting it in production, and watch the first CI run for the new
  shellcheck job.

## v1.8.6 — Container-style deployment: Docker, all-interfaces binding, env var config

No schema change. First step of ongoing work to make ANTHILL deployable like a normal home-lab
appliance — standalone Docker container today, LXC and Windows Service to follow (see
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)).

Added:

- **Docker packaging** — `Dockerfile` (multi-stage: SDK build stage, framework-dependent publish
  onto the `aspnet:9.0` runtime image, non-root user, unauthenticated `/health`-backed
  `HEALTHCHECK`), `docker-compose.yml` (defaults to `network_mode: host` on Linux so the
  container is reachable at the host's real LAN IP, with a bridge-mode alternative documented for
  Windows/macOS Docker Desktop), and `.dockerignore`. No config file or token required to start —
  ANTHILL self-seeds a container-safe default `config.json` into the mounted `.anthill` volume on
  first boot.
- **All-interfaces binding by default** — `api_host` now defaults to `0.0.0.0` (was `127.0.0.1`)
  in both `AnthillConfig`'s default and every safety profile's forced override. The operator login
  was already the real security boundary (auth is forced on in every profile regardless of bind
  host), so this changes what a fresh container/LXC/service install looks like on first boot, not
  the actual security posture. Set `api_host` to `127.0.0.1` (or `ANTHILL_HOST=127.0.0.1`)
  explicitly for a localhost-only install.
- **`ANTHILL_HOST` / `ANTHILL_PORT` / `ANTHILL_OLLAMA_HOST` / `ANTHILL_OLLAMA_MODEL` env vars** —
  new highest-precedence overrides (win over `config.json`) in `AnthillRuntime.ProjectConfig`,
  so container/LXC/service deployments can be configured entirely from the outside. This also
  fixes a latent bug: the CLI's `--host`/`--port`/`--ollama-host`/`--ollama-model` flags
  previously wrote a static field that `AnthillRuntime.Initialize()` immediately overwrote from
  `config.json` a moment later inside `ApiHost.Run()`, silently ignoring the flags; they now set
  these same env vars instead, which survive.
- **`NetworkUtil.GetLikelyLanIPv4()`** (`Anthill.Core/Common/NetworkUtil.cs`) — cross-platform,
  no-network-traffic LAN IP auto-detection (local UDP "connect", no packet sent), used to print a
  real, clickable URL in the startup console banner and in `GET /status` (`reachable_ip` field)
  instead of the unusable `0.0.0.0` bind address, on both Linux and Windows.
- **Config hygiene**: `config.example.json` had a trailing comma making it invalid JSON — copying
  it verbatim per the documented Quick Start steps would silently fall back to all-default config
  with just a console warning easy to miss in a container. Fixed, and added the three Phase 2
  Strategist knobs (`autonomy_dedupe_similarity`, `autonomy_max_followups_per_run`,
  `autonomy_max_objective_depth`) that were missing from the example file.
- **`Anthill.sln` fix**: the solution file only registered `Anthill.Tests`; `Anthill.Cli`,
  `Anthill.Api`, and `Anthill.Core` were never top-level entries, so `dotnet build Anthill.sln`
  (as documented in the README) silently never built the actual CLI/API projects — only
  `build.sh`/`build.ps1`'s explicit direct-publish step did. Added all three so the documented
  build/IDE-open workflow matches what actually ships.
- **README**: Docker section expanded to include the full `docker-compose.yml` walkthrough (field
  table, bridge-mode alternative for Windows/macOS Docker Desktop, plain `docker build`/`docker
  run` commands, Dockerfile summary, upgrade steps) instead of just pointing at
  `docs/DEPLOYMENT.md`; Security Model table gained a network-binding row; Windows Service section
  flagged as pending proper SCM integration (planned next, see `docs/DEPLOYMENT.md` §4); CI badge
  added.
- **CI** (`.github/workflows/ci.yml`, previously an empty placeholder): now runs on every push/PR
  to `main` (and manually via `workflow_dispatch`) — `dotnet build`/`dotnet test` on Linux and
  Windows, a self-contained `linux-x64` publish + `--selftest` run, and a Docker build + container
  boot smoke test that polls the `HEALTHCHECK` status and hits `GET /health` directly. This is the
  actual "does this work" signal going forward, since neither a .NET SDK nor a Docker daemon is
  available in the environment these changes were authored in.

Validation:

- Manual review only — no `dotnet` SDK or Docker daemon was available in the environment this
  change was authored in, so `docker build`/`docker compose up` and `dotnet build`/`dotnet test`
  have **not** been run against this change. Brace/paren balance checked by hand on every edited
  `.cs` file; `config.example.json` re-validated as parseable JSON. Run `dotnet build && dotnet
  test`, then `docker compose up -d --build` and confirm the console banner prints a real LAN IP,
  before relying on this in production.

## v1.8.5 — Autonomy Phase 2: Strategist

No schema change. Mission goals for the 24/7 Colony Director are now LLM-generated per objective
instead of charter-as-goal, with dedup against recent mission history and capped self-enqueued
follow-up objectives. See [docs/AUTONOMY.md](docs/AUTONOMY.md) for the full design.

Added:

- **`Anthill.Core.Autonomy.Strategist`** — turns an objective + recent `autonomy_runs` history +
  top pheromone trails into a concrete mission goal via a new `strategist` model-router role.
  Always computes the deterministic charter-as-goal fallback first; any router failure, missing
  router, or unparseable response falls back to it — never blocks or throws. `StrategistResult`
  reports `Source` (`"strategist"` or `"fallback"`) for auditability.
- **Dedup** — rejects a generated goal that's a near-duplicate of a recent completed/partial run
  for the same objective (`TextUtil.ExtractKeywords` containment-ratio overlap, threshold
  `autonomy_dedupe_similarity`, default `0.8`); a rejected goal falls back to the charter goal.
- **Follow-up objectives** — the Strategist can propose follow-ups in its JSON response;
  `ColonyDirector` saves them only after a successful mission, capped by
  `autonomy_max_followups_per_run` (default `1`) and `autonomy_max_objective_depth` (default `3`,
  walked via new `SqliteMemory.ObjectiveDepth`). Follow-ups inherit `ParentObjectiveId` and run at
  `Priority - 1`.
- **New config knobs** (all fail-closed): `autonomy_dedupe_similarity`,
  `autonomy_max_followups_per_run`, `autonomy_max_objective_depth`.
- **`ColonyDirector.RunObjectiveOnce`** rewritten to call `Strategist.GenerateGoal`; logs
  `goal_source`/`strategist_notes` on `autonomy_mission_started` and `follow_ups_created` on
  `autonomy_mission_finished`. Writes remain queue-only — the Strategist only chooses the next
  mission, never applies patches.
- **UI: Autonomy page** *(admin only)* — Director status card (start/stop, budgets, kill switch),
  objectives backlog editor (add/pause-resume/reprioritize/delete), and a recent-runs table
  showing goal source and outcome per run.

Validation:

- Offline `StrategistTests` cover the no-router fallback path (never blocks/throws) and
  `ObjectiveDepth` walking/edge cases. The LLM-driven generation/dedup paths require a live
  provider and aren't testable offline.
- Manual review only — no `dotnet` SDK was available in the environment this change was authored
  in, so this release has **not** been compiled or run. Run `dotnet build`/`dotnet test`, then
  exercise the Autonomy page end-to-end (add an objective, start the Director, confirm a run
  appears with a goal source) before shipping.

## v1.8.4 — Model provider connections

Schema bumped to **v10** (new `provider_credentials` table, migration `model_provider_connections`).
Ants can now be routed to paid cloud model providers — OpenAI (ChatGPT), Anthropic (Claude),
Perplexity, and OpenRouter — alongside free local Ollama, with API keys managed from the console.

Added:

- **`provider_credentials` table** — one row per external provider. The API key is sealed at rest
  with the existing AES-256-GCM `FieldCipher` (same key resolution as other encrypted columns:
  `ANTHILL_ENCRYPTION_KEY` env var, else an auto-generated 0600 workspace key file) and is never
  read back over the API; only a `configured` boolean, `enabled` flag, optional base-URL override,
  and last-verification status are ever exposed.
- **Real provider clients** (`Anthill.Core.Models.ProviderClients`) — `OpenAiCompatibleClient`
  (shared by OpenAI, Perplexity, and OpenRouter, which all speak the same `{model, messages}` →
  `choices[0].message.content` contract) and `AnthropicClient` (Messages API, `x-api-key` header,
  `content[]` block array). Both fail closed with `ERROR:` sentinel strings on missing keys,
  auth failures, timeouts, or transport errors, matching `OllamaClient`'s existing contract so the
  rest of the colony (retries, pheromone scoring, event logging) needs no changes.
- **`ProviderCatalog`** — static metadata (display name, free/paid kind, curated model list, key
  help URL, default endpoint) for every known provider, driving both the API's `/providers/catalog`
  response and the console's dropdowns.
- **`ModelRouter` keyed-client routing** — OpenAI/Anthropic/Perplexity/OpenRouter clients are built
  fresh on every call (not cached like Ollama's) so a rotated or removed key takes effect
  immediately without a process restart.
- **New endpoints**: `GET /providers/catalog`, `GET /providers`, `POST /providers` (upsert — a
  blank `api_key` on update leaves the stored key untouched), `DELETE /providers/{provider}`,
  `POST /providers/{provider}/test` (fires one live probe call through the real routing path and
  records the verification result). Gated by two new permissions, `read_providers` and
  `manage_providers`, both admin-only like `manage_settings`/`manage_users`.
- **Settings → Providers tab** — a card per provider with API-key input, optional base-URL
  override, Save / Test Connection / Remove actions, and a status pill (not connected / connected /
  verified / verification failed).
- **Ant Config provider + model routing** — each caste's model route editor now has a Provider
  selector (Ollama or any connected external provider) in addition to the existing model picker;
  saving writes `{provider, model}` into `model_routes` exactly as before.

Validation:

- Manual review only — no `dotnet` SDK was available in the environment this change was authored
  in, so this release has **not** been compiled or run. Run `dotnet build` and exercise
  `POST /providers`, `POST /providers/{provider}/test`, and an Ant Config route change before
  shipping.

## v1.8.3 — Enterprise shell UI

No schema change. The web console is rebuilt as a full enterprise-grade shell; all existing API
endpoints, auth, and backend behaviour are unchanged.

Added:

- **Left navigation rail** — 240 px expanded / 60 px icon-only collapsed, toggled by the **‹**
  button; collapse state persisted in `localStorage`. Eight nav items with icons: Overview, Colony,
  Missions, Event Log, Pheromones, Ant Config, Settings, Users.
- **8 routed pages** — `showPage(id)` swaps the active page, updates the nav active state, updates
  the header title, and fires a per-page `PAGE_ENTER` callback. All former modal overlays are now
  full pages:

  | Page | Replaces |
  |------|---------|
  | **Overview** | *(new)* — KPI grid, mission dispatch, recent jobs, live event feed |
  | **Colony** | Canvas page (unchanged layout; canvas now fills `#colony-canvas-area`) |
  | **Missions** | *(new)* — dedicated dispatch + full job history |
  | **Event Log** | `log-modal` overlay |
  | **Pheromones** | `phero-modal` overlay |
  | **Ant Config** | `antcfg-modal` overlay |
  | **Settings** | `settings-overlay` |
  | **Users** | `users-modal` overlay |

- **Fixed top header (50 px)** containing: page title (left), active mission goal + status dot
  (centre), model badge / connection badge / approval bell / logout button (right). The approval
  bell is always visible regardless of active page; clicking it navigates to Colony and scrolls
  to the Approvals card.
- **KPI cards on Overview** — Model Calls, Tasks Done, Events, Approvals; updated by the same
  `pollStatus()` that drives the Colony sidebar (both sets of stat elements are kept in sync).
- **Three mission dispatch inputs** — Colony canvas bar (`mission-input`), Missions page
  (`ms-mission-input`), Overview page (`ov-mission-input`); all feed the same `dispatchMission()`
  function and `enableInput()` locks/unlocks all three simultaneously.
- **Canvas mouse coordinate fix** — `mousedown`, `mousemove`, `mouseup`, `dblclick`, and `wheel`
  handlers now derive canvas-local coordinates via `canvas.getBoundingClientRect()` instead of the
  former `e.clientY - 50` hardcode. Hit-testing and zoom-pivot are accurate at every zoom level and
  nav-rail state.
- **Canvas resize fix** — `resize()` reads from `document.getElementById('colony-canvas-area')`
  (the flex container) instead of the former `#cw` element, so the canvas fills its area exactly
  regardless of sidebar widths.
- **Keyboard shortcuts** — `Ctrl+K` focuses the mission input on Colony; `Ctrl+L` navigates to
  Event Log; `Escape` closes the result overlay and rename popover.
- **`ov-jobs-list` / `ov-feed-list`** — the Overview page renders job lists and the live event
  feed (capped at 20 entries) from the same poll functions (`pollJobs`, `pollEvents`) that drive
  the Colony sidebar; no additional polling.
- **Card collapse** — every card in the Colony sidebars has a **▾** toggle; collapsed state is
  persisted in `localStorage` keyed by card id.
- **Result overlay** — position-fixed over the entire shell, shown when viewing a job result;
  includes Copy, Refresh, and Close controls.

Changed:

- All event IDs and API calls are **unchanged** — only the surrounding layout changed.
- `pollJobs()` now renders to three containers: `jobs-list` (colony left), `ov-jobs-list`
  (overview), `ms-jobs-list` (missions page).
- `pollEvents()` renders to `feed-list` (colony right) and `ov-feed-list` (overview, capped at 20).
- `pollStatus()` updates both `s-calls/tasks/events/approvals` (colony) and the `ov-*` equivalents.
- `applyRoleVisibility()` now also hides/shows nav items and the approval bell per role, not just
  page-level controls.

Validation:

- ID audit: all 143 required element IDs verified present in the final `index.html` (automated
  check with PowerShell regex pass).
- JS balance: brace count delta = 0; backtick count even (no unclosed template literals).
- No unreplaced CSS/JS/HTML placeholder markers; single `<style>` and `<script>` block.
- All original API endpoints and auth flow verified unchanged (no backend edits in this release).

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
