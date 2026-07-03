# ANTHILL

[![CI](https://github.com/thexonexone/operation-anthill/actions/workflows/ci.yml/badge.svg)](https://github.com/thexonexone/operation-anthill/actions/workflows/ci.yml)

**Current version:** v1.8.17  
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

The repo currently uses the v1.8.x line.

Recent important changes:

| Version | What changed |
|---|---|
| `v1.8.17` | Patch Center robustness and better empty/non-JSON API response handling. |
| `v1.8.16` | Objective lifecycle hardening, planner constraint handling, and the Visual Patch Center. |
| `v1.8.15.7` | Overview System Health panel. |
| `v1.8.15.5` | Completed Objectives box for loop-retired objectives. |
| `v1.8.15` | Gated auto-apply path with verification and rollback controls. |
| `v1.8.12` | ResourceGovernor and concurrent mission handling. |

For the full history, read `CHANGELOG.md`.

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
