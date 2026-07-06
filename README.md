# ANTHILL

[![CI](https://github.com/thexonexone/operation-anthill/actions/workflows/ci.yml/badge.svg)](https://github.com/thexonexone/operation-anthill/actions/workflows/ci.yml)

**Current version:** v1.11.0
**Stack:** .NET 9 with optional C++20 native kernel  
**Default runtime:** local Ollama  
**Web UI:** `http://localhost:8713/ui`

ANTHILL is a local task-orchestration app. You give it a goal, it breaks that goal into tasks, routes those tasks through specialized roles, stores the run history, and shows the result in a browser console.

It is built to run on your own hardware first. Ollama is the default model backend. External providers can be added later from **Settings → Providers** if you want to route specific roles to OpenAI, Anthropic, Perplexity, or OpenRouter.

---

## Contents

- [What ANTHILL Does](#what-anthill-does)
- [Current Version Notes](#current-version-notes)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Run Options](#run-options)
- [Using the Web UI](#using-the-web-ui)
- [Patch Review and File Changes](#patch-review-and-file-changes)
- [Autonomy](#autonomy)
- [Useful API Endpoints](#useful-api-endpoints)
- [Build, Test, and Publish](#build-test-and-publish)
- [Updating](#updating)
- [Troubleshooting](#troubleshooting)
- [Project Layout](#project-layout)
- [License](#license)

---

## What ANTHILL Does

ANTHILL runs a local workflow loop:

1. You submit a mission.
2. The Queen plans the mission.
3. Tasks are assigned to roles such as `researcher`, `file`, `web`, `coder`, `builder`, and `verifier`.
4. Tasks run in dependency order.
5. Results, events, patches, approvals, and objective history are stored in SQLite.
6. The web UI shows the mission result, task flow, patches, approvals, and system status.

The active codebase is .NET-first. The optional native C++ kernel is used for some dependency/scheduler work when available. If the native kernel is not present, ANTHILL falls back to the managed C# implementation.

`py.old/` is historical archive material only. It should not be used for current development.

---

## Current Version Notes

The repo currently uses the v1.10.x line.

Recent important changes:

| Version | What changed |
|---|---|
| `v1.11.0` | **Health checks + notifications** (NORTH_STAR Phase 7): ping/HTTP/TCP/service-URL checks (plus disk/uptime placeholders) run on the shared homelab scheduler against **allowlisted targets only**, under strict timeouts, with per-target failure streaks promoting to **incident candidates** at 3 consecutive failures. Config-gated **Slack/Discord/generic webhook notifications** (off by default; URLs never logged). Health panel on the Homelab page: add/run/delete checks, live summary, test-notify. No auto-remediation anywhere. |
| `v1.10.0` | **Inventory + service registry** (NORTH_STAR Phase 6) with a new **Homelab page in the console** (hosts, services, ports, dependencies, recent changes, JSON import/export) — plus two operator-facing fixes: **Patch Center "Apply" no longer 403s** (the `apply_patch` capability gate now follows `patch_application_enabled`; error toasts surface the server's reason), and homelab gates are editable from Settings. |
| `v1.9.1` | **Homelab scheduler + mock-provider harness** (NORTH_STAR Phase 5): five deterministic, network-free fake providers (Proxmox/DNS/DHCP/firewall/health) run through the shared `HomelabScheduler` pattern — jittered intervals, failure backoff, global concurrency cap, persisted job state, target-allowlist discipline — plus a reusable test harness every real provider (v1.10+) must pass, `GET /homelab/providers` statuses, and a `homelab_mock_providers_enabled` gate (off by default). Still zero real network calls. |
| `v1.9.0` | **Homelab foundation** (NORTH_STAR Phase 4): read-only backend groundwork for the V2 Homelab Command Center — `HomelabRepository` (15 new SQLite tables), provider interfaces, operator-managed **target allowlist** (isolated from the general SSRF guard), **write-only credential store** with audit events, new `homelab_operator` role + homelab permissions (action gates OFF until V2.1), disabled-by-default `HomelabScheduler` skeleton, 8 visible-only homelab ants, permission-scoped `/homelab/*` endpoints, [docs/HOMELAB.md](docs/HOMELAB.md), and the `Anthill.Tests.Homelab` suite. No infrastructure control of any kind. |
| `v1.8.29.1` | Auto-apply end-to-end on a fresh LXC install: (1) coder **add-vs-modify** — an `add` for a file that already exists is applied as a backed-up overwrite (`add_overwrite`) instead of hard-refusing, and the coder prompt now picks `add`/`modify` by existence; (2) **default paths** — enabling auto-apply seeds an editable `docs/**` + `src/**` allowlist so it's never a silent no-op; (3) **LXC provisioning** in `setup.sh` — git-checkout workspace, service-user git identity + `safe.directory`, standalone-branch checkout, and a private deploy-key slot, all idempotent. |
| `v1.8.27` | Docs: added **[docs/NORTH_STAR.md](docs/NORTH_STAR.md)** as the single canonical roadmap / build order (v1.8.27 → V3.0), and marked the older roadmap docs (`ROADMAP.md`, `UI_ROADMAP.md`, `AUTONOMY.md`) as subsystem history pointing to it. No runtime change. |
| `v1.8.26.1` | Harden auto-apply git for the systemd sandbox: set the commit identity inline (`git -c user.name/user.email`) so `commit` never fails without host git config, and write ssh `known_hosts` to `/tmp` (writable under `PrivateTmp`) so the push works without `.ssh` in `ReadWritePaths`. |
| `v1.8.26` | Auto-apply **git integration**: commit verified changes to a standalone branch `<github-username>-anthill` and (optionally) push it via an **SSH deploy key** (referenced by path — never stored). One-way sync only (origin/main → branch); **main is never committed to, pushed, or merged into**. New Security → Auto-Apply fields (username → branch, remote, key path, push toggle). |
| `v1.8.25.4` | Fix: the **Autonomous Auto-Apply** toggles ("Enable auto-apply" and "Git-commit verified changes") never saved — `saveSecurity()` collected toggle state only from other containers, so these two flipped visually but were dropped from the payload. Both now persist. |
| `v1.8.25.3` | Fix: approved patches were un-appliable — `ApproveRequest` flipped only the approval record, never the patch, so the Patch Center's Apply button (gated on patch status `approved`) never appeared. Approving now flips the patch to `approved`, and the UI also honors `approval_status`. |
| `v1.8.25.2` | CI: new `ui-integrity` job fails the build on any UI glyph corruption (`�`, bare `>?<` icons, `>? Label` buttons, `'?':'?'` carets) + a `node --check` of the embedded JS — so the recurring corruption can never merge again. CI-only. |
| `v1.8.25.1` | Fix: repair residual UTF-8 glyph corruption in the console — 19 icon glyphs (collapse/caret `▾`, send `▶`, close `✕`, expand `⛶`, pheromone `✓`/`✕` headers) plus 4 JS-literal carets, the apply-warning `⚠`, and the autonomy-running `●` badge had been flattened to `?`. The legitimate `?` help-shortcut key is preserved. |
| `v1.8.25` | UI Phase 10 — Full Command Center Polish: Ctrl+K command palette with global mission-memory search, header notification center with unread badge, `g`-key page navigation + `?` shortcuts help, saved layout restore, and a first-login onboarding tour. UI roadmap complete. |
| `v1.8.24` | UI Phase 7 — Visual Patch Center 2.0: grouping (status/risk/file/mission/objective); operator approve/reject for pending patches with no approval record; operator-edited **alternative patches** behind the normal approval gate; **unbiased verify & auto-approve** (apply-with-backup → build+test → always restore → approve only if green; apply stays manual). |
| `v1.8.23.3` | CI now uploads a release-ready `anthill-linux-x64-v<version>.tar.gz` artifact from every successful run (after publish + selftest pass) — find it under the run's **Artifacts** on the Actions tab. |
| `v1.8.23.2` | Fix: Patch Center empty HTTP 500 — `GET /patches` was registered twice (legacy text list + structured list), throwing `AmbiguousMatchException` in routing. Removed the duplicate and added a boot-time duplicate-route guard that fails loudly instead of silently 500ing. |
| `v1.8.23.1` | Fix: repair UTF-8 corruption in the console (28 button icons + 354 replacement chars from the v1.8.23 save); harden API responses so serialization failures return a real error instead of an empty HTTP 500. |
| `v1.8.23` | Phase 9 Memory + Pheromone Explorer — success/failure and loop-pattern visualization, mission memory search, and prune controls. |
| `v1.8.22` | Phase 8 Ant Inspector + Performance Observatory (+ ASCII banner on setup/shell); **Ant Capability Profiles + Worker Runtime** — 17 role definitions with permission contracts, sub-worker selection per task, capability validation in planner/queen, worker telemetry, and `/colony/registry` + `/colony/workers/telemetry` endpoints. |
| `v1.8.21` | Fix: autonomous auto-apply now persists on deployments without a build toolchain (`autonomy_autoapply_keep_without_verify`); clearer keep/revert reporting. |
| `v1.8.20` | Objective Command Board (lifecycle lanes) + Mission Timeline & Task DAG viewer. |
| `v1.8.19` | Colony Live Canvas 2.0 — caste legend + live pheromone-trail HUD, pheromone drift, real task-load inspector. |
| `v1.8.18.1` | Fix: Patch Center empty HTTP 500 — scrub invalid UTF-16 in JSON responses. |
| `v1.8.18` | Mission Composer + plan preview (dry-run planner endpoint, approve/reject before dispatch). |
| `v1.8.17` | Colony Command Center HUD (design system + Overview command dashboard). |
| `v1.8.16` | Objective lifecycle hardening, planner constraint handling, and the Visual Patch Center. |
| `v1.8.15.7` | Overview System Health panel. |
| `v1.8.15.5` | Completed Objectives box for loop-retired objectives. |
| `v1.8.15` | Gated auto-apply path with verification and rollback controls. |
| `v1.8.12` | ResourceGovernor and concurrent mission handling. |

For the full history, read `CHANGELOG.md`. For the ordered roadmap and long-term direction (through the V2 Homelab Command Center and V3 autonomous operator), see **[docs/NORTH_STAR.md](docs/NORTH_STAR.md)** — the canonical build order.

---

## Requirements

### Minimum

| Component | Minimum |
|---|---|
| CPU | 4-core x86-64 |
| RAM | 8 GB |
| Disk | 10 GB+ |
| OS | Windows 10+ or Ubuntu 22.04+ |
| .NET SDK | 9.0+ if building from source |
| Ollama | Required for the default local route |

Minimum model:

```bash
ollama pull llama3.1:8b
```

### Recommended

| Component | Recommended |
|---|---|
| CPU | 8+ cores |
| RAM | 32 GB+ |
| GPU | NVIDIA GPU with 16 GB+ VRAM |
| Disk | 100 GB SSD |
| OS | Ubuntu 22.04 LTS or Windows 11 |
| Ollama | Latest release |

Recommended models:

```bash
ollama pull llama3.1:8b
ollama pull llama3.3:70b
ollama pull qwen2.5-coder:32b
```

Use the smaller model first if you only want to verify the install.

---

## Quick Start

### 1. Clone the repo

```bash
git clone https://github.com/thexonexone/operation-anthill.git
cd operation-anthill
```

### 2. Pull at least one Ollama model

```bash
ollama pull llama3.1:8b
```

Optional larger routes:

```bash
ollama pull llama3.3:70b
ollama pull qwen2.5-coder:32b
```

### 3. Copy the config

Linux/macOS:

```bash
mkdir -p .anthill
cp config.example.json .anthill/config.json
```

Windows PowerShell:

```powershell
New-Item -ItemType Directory -Force .anthill
Copy-Item config.example.json .anthill\config.json
```

### 4. Edit `.anthill/config.json`

At minimum, check these values:

```jsonc
{
  "api_host": "0.0.0.0",
  "api_port": 8713,
  "use_ollama": true,
  "ollama_host": "http://localhost:11434",
  "ollama_model": "llama3.1:8b",
  "agent_workspace_dir": ".anthill/workspace"
}
```

If Ollama runs on another machine, change:

```jsonc
"ollama_host": "http://OLLAMA_MACHINE_IP:11434"
```

### 5. Build and run

Linux/macOS:

```bash
./build.sh
dotnet run --project src/Anthill.Cli -- --api
```

Windows PowerShell:

```powershell
.\build.ps1
dotnet run --project src\Anthill.Cli -- --api
```

### 6. Open the UI

```text
http://localhost:8713/ui
```

On first launch, create the first administrator account. After that, log in with username and password.

---

## Configuration

Main config file:

```text
.anthill/config.json
```

Common settings:

| Setting | Purpose |
|---|---|
| `api_host` | API bind address. Use `0.0.0.0` for LAN/container access or `127.0.0.1` for local-only. |
| `api_port` | Default is `8713`. |
| `ollama_host` | Ollama API URL. |
| `ollama_model` | Default model when no role-specific route matches. |
| `agent_workspace_dir` | Root folder ANTHILL may inspect and propose patches against. |
| `api_job_workers` | Concurrent missions. Keep at `1` unless you know your hardware can handle more. |
| `max_parallel_workers` | Parallel tasks inside one mission. |
| `web_search_enabled` | Enables external web search. |
| `patch_application_enabled` | Allows approved patches to be applied. |
| `file_writing_enabled` | Allows file writes after approval. |
| `shell_tool_enabled` | Enables allowlisted shell/check commands. |
| `file_tools_enabled` | Allows workspace file listing/reading. |

Environment variables can override common settings:

| Variable | Overrides |
|---|---|
| `ANTHILL_HOST` | `api_host` |
| `ANTHILL_PORT` | `api_port` |
| `ANTHILL_OLLAMA_HOST` | `ollama_host` |
| `ANTHILL_OLLAMA_MODEL` | `ollama_model` |
| `ANTHILL_API_TOKEN` | Optional programmatic admin token, minimum 32 characters |

Normal web login does not require `ANTHILL_API_TOKEN`.

### Autonomous auto-apply (advanced)

Gated auto-apply lets the Director apply low-risk patches without human review, then **verify and roll
back** if the check fails. It is OFF by default and does nothing without a path allowlist.

| Setting | Purpose |
|---|---|
| `autonomy_autoapply_enabled` | Master switch (default `false`). |
| `autonomy_autoapply_paths` | Workspace-relative globs a patch must match (empty = nothing eligible). |
| `autonomy_autoapply_verify_cmd` | Verify command run in `agent_workspace_dir`. Empty = built-in `dotnet build && dotnet test`. |
| `autonomy_autoapply_keep_without_verify` | If `true` **and** no verify command is set, keep applied patches without verifying (default `false`). |
| `autonomy_autoapply_git_commit` | After keeping, `git add` + `git commit` locally (never pushed). |

**Important:** the built-in verify needs a *buildable* `agent_workspace_dir` (a checkout with a
solution) **and** the dotnet SDK on the host. On a published-binary deployment (e.g. an LXC without
the SDK), point `agent_workspace_dir` at a real checkout and set `autonomy_autoapply_verify_cmd` to a
check it can run — **or** set `autonomy_autoapply_keep_without_verify: true` to keep changes without a
verify gate. Otherwise every auto-applied patch fails verify and is rolled back (nothing persists).

---

## Run Options

### Run from source

```bash
dotnet run --project src/Anthill.Cli -- --api
```

### Run a self-test

```bash
dotnet run --project src/Anthill.Cli -- --selftest
```

### Print status

```bash
dotnet run --project src/Anthill.Cli -- --status
```

### Run one mission from CLI

```bash
dotnet run --project src/Anthill.Cli -- --mission "Summarize this repository."
```

---

## Deploy on Linux

### Option A — source checkout

```bash
git clone https://github.com/thexonexone/operation-anthill.git
cd operation-anthill
./build.sh
cp config.example.json .anthill/config.json
nano .anthill/config.json
dotnet run --project src/Anthill.Cli -- --api
```

### Option B — Docker

```bash
docker compose up -d --build
docker compose logs -f anthill
```

Open the URL printed in the logs.

Update Docker deployment:

```bash
git pull
docker compose up -d --build
```

### Option C — LXC / Proxmox

Inside a fresh Debian or Ubuntu LXC container:

```bash
apt-get update && apt-get install -y curl ca-certificates git
curl -fsSL https://raw.githubusercontent.com/thexonexone/operation-anthill/main/deploy/lxc/setup.sh -o setup.sh
bash setup.sh
```

Check service status:

```bash
systemctl status anthill --no-pager
journalctl -u anthill -n 50 --no-pager
```

Update an existing LXC install:

```bash
cd /opt/anthill/src
git pull
bash deploy/lxc/setup.sh
```

### Option D — systemd service

Use the LXC installer when possible. For a manual service install, publish the binary, place it under `/opt/anthill`, create a dedicated `anthill` user, and run it with:

```text
/opt/anthill/anthill --api
```

The service should run as an unprivileged user and keep write access limited to `.anthill`.

---

## Deploy on Windows

### Run from source

```powershell
git clone https://github.com/thexonexone/operation-anthill.git
cd operation-anthill
.\build.ps1
Copy-Item config.example.json .anthill\config.json
notepad .anthill\config.json
dotnet run --project src\Anthill.Cli -- --api
```

### Publish a standalone exe

```powershell
dotnet publish src\Anthill.Cli\Anthill.Cli.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:DebugType=none `
  -o .\publish\win-x64
```

Run:

```powershell
.\publish\win-x64\anthill.exe --api
```

---

## Ollama Setup

If Ollama is on the same machine:

```jsonc
"ollama_host": "http://localhost:11434"
```

If Ollama is on another machine:

```jsonc
"ollama_host": "http://192.168.1.50:11434"
```

On Linux, expose Ollama to the network with a systemd override:

```bash
sudo systemctl edit ollama
```

Add:

```ini
[Service]
Environment="OLLAMA_HOST=0.0.0.0:11434"
```

Then restart:

```bash
sudo systemctl daemon-reload
sudo systemctl restart ollama
curl http://OLLAMA_MACHINE_IP:11434/api/tags
```

On Windows, set the machine environment variable:

```powershell
[System.Environment]::SetEnvironmentVariable("OLLAMA_HOST", "0.0.0.0:11434", "Machine")
Stop-Process -Name "ollama" -Force
Start-Process "ollama" -ArgumentList "serve"
```

---

## Model Routes

Routes are configured in `.anthill/config.json` or from **Settings → Ant Config**.

Example:

```jsonc
"model_routes": {
  "planner":    { "provider": "ollama", "model": "llama3.1:8b" },
  "researcher": { "provider": "ollama", "model": "llama3.1:8b" },
  "coder":      { "provider": "ollama", "model": "qwen2.5-coder:32b" },
  "builder":    { "provider": "ollama", "model": "qwen2.5-coder:32b" },
  "verifier":   { "provider": "ollama", "model": "llama3.1:8b" },
  "web":        { "provider": "ollama", "model": "llama3.1:8b" },
  "fallback":   { "provider": "ollama", "model": "llama3.1:8b" }
}
```

If a provider route points to OpenAI, Anthropic, Perplexity, or OpenRouter, connect that provider first in **Settings → Providers**.

---

## Using the Web UI

Open:

```text
http://YOUR_HOST:8713/ui
```

Main pages:

| Page | Purpose |
|---|---|
| Overview | System health, recent activity, mission entry, status cards. |
| Colony | Live role graph and task flow. |
| Missions | Submit missions and view job history. |
| Results | Expand completed missions and read final reports. |
| Event Log | Searchable event history. |
| Pheromones | Stored task-pattern history and pruning controls. |
| Ant Config | Role names, colors, providers, and model routes. |
| Autonomy | Director status, objectives, runs, and completed objectives. |
| Security | Auth, feature gates, workspace boundary, shell controls. |
| Shell | Admin terminal, if enabled. |
| Settings | Connection, providers, models, maintenance, system info. |
| Users | Admin account management. |

Keyboard shortcuts may be available in the UI, such as focusing the mission input or jumping to the Event Log.

---

## Patch Review and File Changes

ANTHILL does not write code changes directly from a task result.

The normal flow is:

```text
Mission runs
  -> coder produces a structured patch proposal
  -> proposal is validated
  -> approval request is saved
  -> operator reviews it in the UI
  -> operator approves or rejects it
  -> approved patch may then be applied
```

Key rules:

- File changes require approval.
- Approval and apply are separate steps unless you use the UI's combined approve-and-queue option.
- Patch proposals are stored and visible in the Patch Center.
- `modify` and `delete` patches need exact `old_content`.
- `add` patches can create or append content, depending on the proposal.
- Backups are created before applied changes.

Apply an approved patch from the API:

```bash
curl -X POST http://localhost:8713/apply/APPROVAL_ID \
  -H "Authorization: Bearer YOUR_TOKEN"
```

Use the UI for normal review when possible.

---

## Autonomy

Autonomy is off by default. When enabled, the Director works through objectives under configured limits.

Common controls are in **Autonomy** and **Security**.

Important behavior:

- Objectives can be pending, active, paused, completed, stopped, looping, or failed.
- One-shot and verification-only objectives should end cleanly when finished.
- Loop detection is for repeated work that is not discovering anything new.
- Completed or loop-retired objectives appear in the completed-objective area.
- Low-risk auto-apply is gated and off by default.
- Auto-apply verifies changes and rolls them back if checks fail.

Useful endpoints:

```text
GET  /autonomy/status
POST /autonomy/start
POST /autonomy/stop
GET  /objectives
POST /objectives
GET  /autonomy/runs
POST /objectives/clear
```

---

## Useful API Endpoints

Most endpoints require an authenticated session or bearer token.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/health` | Basic health check. |
| `GET` | `/status` | System status. |
| `GET` | `/system/summary` | Header/system summary. |
| `POST` | `/missions` | Submit a mission. |
| `GET` | `/jobs` | Job list. |
| `GET` | `/missions/json` | Mission history as JSON. |
| `GET` | `/missions/{id}/report` | Structured mission report. |
| `GET` | `/events` | Event log. |
| `GET` | `/patches` | Patch Center list. |
| `GET` | `/patches/{id}/detail` | Patch detail/diff data. |
| `GET` | `/approvals` | Pending approvals. |
| `POST` | `/approve/{id}` | Approve a patch. |
| `POST` | `/reject/{id}` | Reject a patch. |
| `POST` | `/apply/{id}` | Apply an approved patch. |
| `GET` | `/config` | Runtime config summary. |
| `GET` | `/models` | Model route status. |
| `GET` | `/ollama/models` | Models available from Ollama. |
| `GET` | `/maintenance/stats` | Disk/DB/backup stats. |
| `POST` | `/maintenance/flush` | Prune old backups/events and compact DB. |
| `POST` | `/maintenance/clear-missions` | Clear mission history. |
| `POST` | `/maintenance/reset-config` | Reset tunable settings. |

---

## Authentication

On first run, the UI creates the first administrator.

Roles:

| Role | Access |
|---|---|
| Administrator | Full access. |
| Mission Coordinator | Submit missions and read allowed status/event data. |

Notes:

- Web login uses username and password.
- Passwords are stored as salted PBKDF2-SHA256 hashes.
- Sessions are in memory and expire.
- Restarting the service logs users out.
- `ANTHILL_API_TOKEN` is optional and intended for scripts/CI.

Reset an admin password from the host:

```bash
dotnet run --project src/Anthill.Cli -- --set-password admin <new-password>
```

Add a user:

```bash
dotnet run --project src/Anthill.Cli -- --add-user <username> <password> admin
```

Use `coordinator` instead of `admin` for limited access.

---

## Security Notes

ANTHILL is meant to be run on hardware you control.

Main controls:

- Password-based operator login.
- Role-based permissions.
- Optional static token for scripts.
- Rate limits on mission and auth endpoints.
- Path traversal checks.
- Workspace boundary through `agent_workspace_dir`.
- Web search, shell, file writes, patch apply, and auto-apply are gated by config.
- Sensitive provider keys are encrypted at rest.
- Operator shell is admin-only and audit-logged.

For local-only access, set:

```jsonc
"api_host": "127.0.0.1"
```

For LAN or container access, use:

```jsonc
"api_host": "0.0.0.0"
```

Only expose the UI/API to networks you trust.

---

## Build, Test, and Publish

### Full build

Linux/macOS:

```bash
./build.sh
```

Windows:

```powershell
.\build.ps1
```

### .NET-only build

```bash
dotnet build Anthill.sln -c Release
```

### Run tests

```bash
dotnet test Anthill.sln -c Release
```

### Publish Linux binary

```bash
dotnet publish src/Anthill.Cli/Anthill.Cli.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=none \
  -o ./publish/linux-x64
```

### Publish Windows binary

```powershell
dotnet publish src\Anthill.Cli\Anthill.Cli.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=none `
  -o .\publish\win-x64
```

---

## Updating

### Source checkout

```bash
git pull
./build.sh
dotnet run --project src/Anthill.Cli -- --api
```

### LXC install

```bash
cd /opt/anthill/src
git pull
bash deploy/lxc/setup.sh
```

### Docker

```bash
git pull
docker compose up -d --build
```

Your `.anthill` data, config, database, users, logs, backups, and keys should remain in place during normal updates.

---

## Troubleshooting

### UI cannot reach Ollama

Check Ollama:

```bash
curl http://localhost:11434/api/tags
```

If Ollama is remote:

```bash
curl http://OLLAMA_MACHINE_IP:11434/api/tags
```

Then confirm `.anthill/config.json`:

```jsonc
"ollama_host": "http://OLLAMA_MACHINE_IP:11434"
```

Restart ANTHILL after changing config.

### Port 8713 is already in use

Linux:

```bash
lsof -i :8713
kill $(lsof -t -i:8713)
```

Windows:

```powershell
netstat -ano | findstr :8713
taskkill /PID <PID> /F
```

Or change:

```jsonc
"api_port": 9000
```

### dotnet command not found

Install .NET 9 SDK.

Ubuntu example:

```bash
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-9.0
```

### No approvals are appearing

Check:

- the mission actually requested a file change
- patch proposals were created
- the target file type is allowed by runtime config
- the proposal includes `old_content` for modify/delete
- Event Log for `patch_proposal_parse_failed`
- Patch Center for rejected, failed, or superseded patches

### Mission keeps repeating

Check:

- objective `max_runs`
- whether the objective is one-shot or standing
- Autonomy page for completed/stopped/looping status
- Completed Objectives detail
- Event Log for objective lifecycle events

### Disk usage is growing

Use:

```text
Settings → Maintenance → Flush Cache
```

Or call:

```bash
curl -X POST http://localhost:8713/maintenance/flush \
  -H "Authorization: Bearer YOUR_TOKEN"
```

---

## Project Layout

```text
.github/workflows/        CI and release workflows
deploy/lxc/               LXC/Proxmox installer and service templates
docs/                     Deployment, autonomy, roadmap, and design notes
native/anthill_kernel/    Optional C++20 native kernel
src/                      Active .NET source
test/Anthill.Tests/       Test project path
tests/Anthill.Tests/      Test project path
Anthill.sln               Solution file
build.sh                  Linux/macOS build script
build.ps1                 Windows build script
config.example.json       Default runtime config
Dockerfile                Container build
docker-compose.yml        Docker Compose deployment
py.old/                   Historical archive only; not active code
```

---

## License

See `LICENSE`.

---

## Notes for Future README Updates

Keep this README short and practical.

Good additions:

- exact install commands
- exact config values
- current version notes
- deployment fixes
- troubleshooting steps

Avoid:

- long marketing descriptions
- repeated explanations
- outdated runtime notes
- fake feature language
- Python-era instructions
- duplicate deployment docs already covered in `docs/DEPLOYMENT.md`
