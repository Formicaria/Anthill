# ANTHILL — Swarm Intelligence Agent Framework

[![CI](https://github.com/thexonexone/operation-anthill/actions/workflows/ci.yml/badge.svg)](https://github.com/thexonexone/operation-anthill/actions/workflows/ci.yml)

> **v1.8.15.7** — .NET 9 / C++20 hybrid · self-hosted · Ollama-native · fully local by default

ANTHILL is a **local swarm-intelligence multi-agent framework** that orchestrates a colony of specialised AI agents (called *ants*) under the command of a *Queen* orchestrator. It runs entirely on your own hardware and uses [Ollama](https://ollama.com) as the default LLM backend — no cloud API keys required — while exposing a real-time colony console at `http://localhost:8713/ui`. Cloud providers (OpenAI, Anthropic, Perplexity, OpenRouter) can optionally be connected per-role from **Settings → Providers**; see [Model Providers](#model-providers).

> **New here?** [Quick Start](#quick-start) gets a colony running from source in ~10 minutes.
> Deploying to a server or home-lab instead? Jump straight to the
> [deployment chooser](#deploy-on-linux). Already running in an LXC container?
> [Update it in three commands](#updating-an-existing-anthill-container).

---

## Table of Contents

**Overview**

- [What Is ANTHILL?](#what-is-anthill)
- [Architecture](#architecture)
- [Resource Requirements](#resource-requirements)

**Getting Started**

- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start) — clone → pull models → configure → run
- [Configuration Reference](#configuration-reference)

**Deployment** *(pick one — see the [chooser table](#deploy-on-linux))*

- [Deploy on Linux](#deploy-on-linux) — from source · standalone binary · systemd · Docker · **LXC/Proxmox** (install *and* [update](#updating-an-existing-anthill-container))
- [Deploy on Windows](#deploy-on-windows) — from source · standalone exe · Windows Service

**Models**

- [Ollama Setup & Model Routing](#ollama-setup--model-routing)
- [Model Providers](#model-providers) — optional cloud providers (OpenAI, Anthropic, Perplexity, OpenRouter)

**Using the Colony**

- [Colony UI Guide](#colony-ui-guide)
- [The Approval Workflow](#the-approval-workflow) — how code changes get reviewed and applied
- [Self-Modification Missions](#self-modification-missions)
- [Long-Input / Specification-Ingestion Handling](#long-input--specification-ingestion-handling)

**Reference**

- [API Reference](#api-reference)
- [Authentication & Operator Accounts](#authentication--operator-accounts)
- [Security Model](#security-model)
- [Building from Source](#building-from-source)

**Help**

- [Troubleshooting](#troubleshooting)
- [Changelog](#changelog) · [License](#license)

> **Deploying to a home-lab/production box?** See **[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)**.
> Docker, LXC, and Windows deployments are all ready to use today — and, like the rest of
> ANTHILL, under continuous development and improvement.

---

## What Is ANTHILL?

ANTHILL is an **agent orchestration harness** — a system that decomposes a natural-language mission goal into a directed acyclic graph (DAG) of tasks, assigns each task to the most appropriate specialised agent, executes them in dependency order (with parallelism where safe), and synthesises the results into a final answer.

Think of it as an AI workforce:

- **You** submit a high-level goal: *"Audit the authentication module, find security issues, propose patches, and verify the fixes compile."*
- **The Queen** decomposes that into tasks and dispatches them to the right ants.
- **Ants** execute their tasks using tools (file reads, web search, shell commands, patch proposals).
- **Results flow** between ants as context — the researcher's summary feeds the coder; the coder's patch feeds the verifier.
- **You approve** any code changes before they are written to disk.

Everything runs **locally**. No data leaves your machine.

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                    Colony (ANTHILL)                  │
│                                                     │
│   ┌─────────┐        ┌─────────────────────────┐   │
│   │  Queen  │◄──────►│  Pheromone Memory (DB)   │   │
│   │(Planner │        │  missions · tasks · events│   │
│   │+Sched.) │        │  patches · approvals      │   │
│   └────┬────┘        └─────────────────────────┘   │
│        │                                            │
│   ┌────▼──────────────────────────────────────┐    │
│   │               Task DAG                    │    │
│   │  researcher ──► coder ──► builder         │    │
│   │       │                      │            │    │
│   │     file ──────────────► verifier         │    │
│   │       │                                   │    │
│   │      web                                  │    │
│   └───────────────────────────────────────────┘    │
│                                                     │
│   ┌──────────────────────────────────────────┐     │
│   │               Ollama Backend              │     │
│   │  llama3.3:70b · qwen2.5-coder:32b        │     │
│   │  llama3.1:8b  (or any supported model)   │     │
│   └──────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────┘
```

### The Queen

The Queen is the central orchestrator. It:
1. Calls the **Planner** to decompose a goal into a JSON task plan.
2. Resolves task dependencies (by UUID, by integer index, or by task title).
3. Dispatches tasks to the **TaskScheduler**, which executes them in topological order with configurable parallelism.
4. Collects results, scores the mission, and writes the final output.
5. Intercepts coder output to parse **patch proposals** and queue them for human approval.

### The Ants

| Ant | Role | Tools Available |
|-----|------|----------------|
| `researcher` | Gathers internal context, pheromone memory, and mission framing | `system_info`, `list_directory` |
| `file` | Read-only workspace inspection — lists dirs, reads source files | `list_directory`, `read_text_file` |
| `web` | External research via DuckDuckGo (when enabled) | `web_search` |
| `coder` | Proposes structured JSON patch sets — never writes files directly | `read_text_file` |
| `builder` | Synthesises all prior results into the final answer | `system_info` |
| `verifier` | Checks the result for accuracy, completeness, and safety | — |

### Pheromone Memory

ANTHILL records every event, task result, model call, and patch proposal in SQLite. It also tracks **pheromone trails** — reinforcement signals that strengthen successful task patterns and weaken failed ones. The Planner reads these trails and biases future mission plans toward strategies that have worked before.

### Dependency-Aware Scheduler

The TaskScheduler runs a dependency state machine. Tasks are dispatched as soon as all their dependencies are complete. Independent tasks run in parallel up to `max_parallel_workers`. Cycle detection uses the native C++ kernel (or a managed fallback) to prevent deadlocks.

### Patch Proposal & Approval Gate

When the coder ant produces output, ANTHILL parses it for a structured JSON patch proposal. Each proposal is:
1. Validated (safe relative path, allowed file type, no path traversal).
2. Saved to the `approval_requests` table with status `pending`.
3. Surfaced in the colony UI with an **Approve / Reject** button.
4. Applied to disk only after the user clicks **Approve** and then triggers `POST /apply/{id}`.

No code ever touches disk without explicit human approval.

---

## Resource Requirements

### Minimum (lightweight models)

| Component | Minimum |
|-----------|---------|
| CPU | 4-core x86-64 |
| RAM | 8 GB system RAM |
| GPU | None (CPU inference, slow) |
| Disk | 10 GB (models + DB) |
| OS | Windows 10+ or Linux (Ubuntu 22.04+) |
| .NET SDK | 9.0+ (build only) |
| Ollama | 0.3.0+ |

Minimum model: `ollama pull llama3.1:8b` (~5 GB). Inference is slow on CPU (~1–3 tok/s).

### Recommended (full performance)

| Component | Recommended |
|-----------|------------|
| CPU | 8+ core x86-64 |
| RAM | 32 GB system RAM |
| GPU | 1× NVIDIA RTX with 16+ GB VRAM |
| VRAM | 24 GB+ per GPU |
| Disk | 100 GB SSD |
| OS | Ubuntu 22.04 LTS or Windows 11 |
| .NET SDK | 9.0+ |
| Ollama | Latest |

### Maximum (dual-GPU, full model suite)

The configuration in this repo targets a **dual NVIDIA Titan RTX (2× 24 GB = 48 GB VRAM)** setup running:

| Role | Model | VRAM |
|------|-------|------|
| planner, researcher, verifier | `llama3.3:70b` | ~40 GB |
| coder, builder | `qwen2.5-coder:32b` | ~20 GB |
| web, fallback | `llama3.1:8b` | ~5 GB |

Ollama automatically distributes model layers across all available CUDA GPUs. With 48 GB total VRAM, all three models can be loaded simultaneously, giving near-instant responses with no eviction between ant calls.

---

## Prerequisites

### All Platforms

1. **Ollama** — [https://ollama.com/download](https://ollama.com/download)
2. **.NET 9 SDK** — [https://dotnet.microsoft.com/download/dotnet/9.0](https://dotnet.microsoft.com/download/dotnet/9.0) (build only; not needed if running a pre-built binary)
3. **Git** (to clone the repo)

### Linux Additional

- `build-essential` or equivalent (gcc/g++ for the optional native kernel)
- `cmake` 3.20+ (optional, for the native C++ kernel)
- `libssl-dev` (for .NET crypto)

### Windows Additional

- Visual Studio 2022 Build Tools **or** the standalone MSVC toolchain (optional, for the native kernel)
- CMake 3.20+ (optional)

---

## Quick Start

> **⚠ You must edit `.anthill/config.json` before starting.** See [Configuration Reference](#configuration-reference).

### 1. Clone

```bash
git clone https://github.com/thexonexone/operation-anthill.git
cd operation-anthill
```

### 2. Pull Ollama models

```bash
# On the machine running Ollama:
ollama pull llama3.1:8b            # minimum — fast, works on CPU
ollama pull llama3.3:70b           # recommended planner/researcher/verifier
ollama pull qwen2.5-coder:32b     # recommended coder/builder
```

### 3. Copy and edit the config

```bash
cp config.example.json .anthill/config.json
# Edit .anthill/config.json — set your Ollama host IP and model routes
```

**The only required change is:**
- `ollama_host` → the IP/hostname of the machine running Ollama

`api_host` already defaults to `0.0.0.0` (all interfaces) so ANTHILL is reachable at this
machine's LAN IP with no edits — set it to `127.0.0.1` if you want localhost-only instead. Both
`api_host`/`api_port` and `ollama_host`/`ollama_model` can also be set via the `ANTHILL_HOST`,
`ANTHILL_PORT`, `ANTHILL_OLLAMA_HOST`, and `ANTHILL_OLLAMA_MODEL` environment variables, which
take precedence over `config.json` — handy for Docker/LXC/Windows Service deployments where you'd
rather not bake settings into the image/profile.

### 4. Build and run

```bash
# Linux / macOS (builds kernel + .NET, runs tests, starts API)
./build.sh
dotnet run --project src/Anthill.Cli -- --api

# Windows (PowerShell)
.\build.ps1
dotnet run --project src\Anthill.Cli -- --api
```

> No API token is required to start. The web console is secured by **operator accounts**
> (username + password), not a shared key. See [Authentication & Operator Accounts](#authentication--operator-accounts).
> A static `ANTHILL_API_TOKEN` remains **optional** — set one only if you want a programmatic
> admin credential for scripts/CI (it must be ≥ 32 chars if set).

### 5. Open the UI and create your administrator

```
http://localhost:8713/ui
```

On first run the console shows a **one-time setup screen**: choose a username (default `admin`)
and a password. That account has full administrative rights. From then on, everyone signs in with
a username and password — there is no token to paste.

---

## Configuration Reference

Copy `config.example.json` to `.anthill/config.json` and edit the values marked `CHANGE_ME`.

```jsonc
{
  "config_version": "config-v1",
  "safety_profile": "SAFE_LOCAL",

  // ── Paths (relative to anthill.exe location) ────────────────────────────
  "workspace_root":     ".anthill",
  "db_path":            ".anthill/anthill.db",
  "backup_dir":         ".anthill/backups",
  "logs_dir":           ".anthill/logs",
  "exports_dir":        ".anthill/exports",

  // ── Agent workspace ──────────────────────────────────────────────────────
  // The root directory that agents are allowed to READ from (file ant).
  // For self-modification missions, point this at your source tree.
  // Agents can NEVER write outside this directory.
  "agent_workspace_dir": "/home/user/my-project",   // CHANGE_ME

  // ── API binding ──────────────────────────────────────────────────────────
  // Defaults to "0.0.0.0" (all interfaces) — container/LXC/service-friendly, reachable at
  // this machine's LAN IP with zero config. The operator login is the security boundary here,
  // not network isolation. Use "127.0.0.1" for localhost-only. ANTHILL_HOST / ANTHILL_PORT env
  // vars override both of these if set (highest precedence).
  "api_host": "0.0.0.0",
  "api_port": 8713,
  "api_auth_enabled": true,
  "api_token_env": "ANTHILL_API_TOKEN",             // env var that holds the token
  "cors_enabled": false,

  // ── Worker parallelism ───────────────────────────────────────────────────
  // api_job_workers   = number of concurrent missions (use 1 unless you have lots of VRAM)
  // max_parallel_workers = max ants running at the same time within one mission
  "api_job_workers": 1,
  "parallel_execution_enabled": true,
  "max_parallel_workers": 3,

  // ── Ollama connection ────────────────────────────────────────────────────
  // CHANGE_ME: set to the IP:port of the machine running Ollama.
  // If Ollama runs on the SAME machine as ANTHILL, use http://localhost:11434
  // If Ollama runs on a DIFFERENT machine, use http://OLLAMA_MACHINE_IP:11434
  //
  // To expose Ollama on the network, on the Ollama machine run:
  //   Linux:   sudo systemctl edit ollama --force  →  add Environment=OLLAMA_HOST=0.0.0.0:11434
  //   Windows: setx OLLAMA_HOST 0.0.0.0:11434 /M  then restart Ollama
  "use_ollama": true,
  "ollama_host": "http://OLLAMA_HOST_IP:11434",     // CHANGE_ME
  "ollama_model": "llama3.3:70b",                   // default model (used if no route matches)

  // ── Model routing ────────────────────────────────────────────────────────
  // Each ant role can use a different model. All must be pulled on the Ollama machine.
  // Smaller models (8b) are fine for web/fallback. Use 70b for planning/reasoning.
  // Use qwen2.5-coder for code tasks.
  "model_routes": {
    "planner":    { "provider": "ollama", "model": "llama3.3:70b" },
    "researcher": { "provider": "ollama", "model": "llama3.3:70b" },
    "coder":      { "provider": "ollama", "model": "qwen2.5-coder:32b" },
    "builder":    { "provider": "ollama", "model": "qwen2.5-coder:32b" },
    "verifier":   { "provider": "ollama", "model": "llama3.3:70b" },
    "web":        { "provider": "ollama", "model": "llama3.1:8b" },
    "fallback":   { "provider": "ollama", "model": "llama3.1:8b" }
  },

  // ── Feature gates ────────────────────────────────────────────────────────
  // All gates default to false for safety. Enable only what you need.
  "web_search_enabled":       true,     // DuckDuckGo HTML scraping, no API key needed
  "patch_application_enabled": true,    // Allow /apply to write approved patches to disk
  "file_writing_enabled":     true,     // Allow agents to write files (requires approval)
  "shell_tool_enabled":       true,     // Allow agents to run shell commands (sandboxed)
  "file_tools_enabled":       true,     // Allow agents to list/read workspace files

  // ── Limits ───────────────────────────────────────────────────────────────
  "max_web_searches_per_mission": 3,
  "max_sources_per_mission":      15,
  "max_context_packet_chars":     7000,
  "max_agent_message_content_chars": 2200
}
```

### Key variables at a glance

| Setting | What to change | Default |
|---------|----------------|---------|
| `ollama_host` | IP of Ollama machine | `http://localhost:11434` |
| `api_host` | Bind address for ANTHILL API | `0.0.0.0` (all interfaces) |
| `agent_workspace_dir` | Root dir agents can read/propose patches in | `.anthill/workspace` |
| `model_routes.*` | Model per ant role | see above |
| `api_job_workers` | Concurrent missions | `1` |
| `max_parallel_workers` | Parallel ants per mission | `3` |

The full autonomy knob set (budgets, kill switch, Strategist dedup/follow-ups, Phase 3
concurrency + aging, Phase 4 learning/retirement) is documented inline in
[`config.example.json`](config.example.json) and in [docs/AUTONOMY.md](docs/AUTONOMY.md).

---

## Deploy on Linux

Five options, one decision — where is ANTHILL going to live?

| You want | Use |
|----------|-----|
| To hack on the code and run it locally | [Option A — from source](#option-a--run-from-source-development) |
| One binary on a bare box, no .NET runtime needed | [Option B — self-contained binary](#option-b--self-contained-binary-production) |
| Always-on service on bare metal or a VM | [Option C — systemd service](#option-c--systemd-service-recommended-for-always-on) |
| A container on a Docker host | [Option D — Docker](#option-d--docker) |
| A Proxmox / LXD / incus container (home-lab) | [Option E — LXC](#option-e--lxc-automated-proxmox-or-plain-lxdincus) — one-shot installer, also the upgrade path |
| A Windows machine | [Deploy on Windows](#deploy-on-windows) |

### Option A — Run from source (development)

```bash
# 1. Install .NET 9 SDK
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y dotnet-sdk-9.0

# 2. Clone and build
git clone https://github.com/thexonexone/operation-anthill.git
cd operation-anthill
chmod +x build.sh
./build.sh

# 3. Configure
cp config.example.json .anthill/config.json
nano .anthill/config.json        # set ollama_host, api_host, agent_workspace_dir

# 4. Generate and set token
export ANTHILL_API_TOKEN="$(openssl rand -hex 24)"
echo "Your token: $ANTHILL_API_TOKEN"

# 5. Start
dotnet run --project src/Anthill.Cli -- --api
```

### Option B — Self-contained binary (production)

```bash
# Publish a self-contained Linux binary (no .NET runtime needed on target)
dotnet publish src/Anthill.Cli/Anthill.Cli.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:DebugType=none \
  -o ./publish/linux-x64

chmod +x ./publish/linux-x64/anthill

# Copy to target machine
scp -r ./publish/linux-x64/ user@target-machine:/opt/anthill/
scp config.example.json user@target-machine:/opt/anthill/.anthill/config.json
```

### Option C — systemd service (recommended for always-on)

Create `/etc/systemd/system/anthill.service`:

```ini
[Unit]
Description=ANTHILL Colony Agent API
After=network.target

[Service]
Type=simple
User=anthill
WorkingDirectory=/opt/anthill
ExecStart=/opt/anthill/anthill --api
Restart=on-failure
RestartSec=5s

# ── Set your token here ─────────────────────────────────────────────────────
# Generate with: openssl rand -hex 24
Environment=ANTHILL_API_TOKEN=CHANGE_ME_TO_YOUR_TOKEN_MIN_32_CHARS

# Optional: override Ollama host at service level
# Environment=OLLAMA_HOST=http://192.168.1.100:11434

# Security hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=/opt/anthill/.anthill

[Install]
WantedBy=multi-user.target
```

```bash
# Create a dedicated user
sudo useradd -r -s /bin/false -d /opt/anthill anthill
sudo chown -R anthill:anthill /opt/anthill

# Enable and start
sudo systemctl daemon-reload
sudo systemctl enable anthill
sudo systemctl start anthill
sudo systemctl status anthill

# View logs
sudo journalctl -u anthill -f
```

### Option D — Docker

A real, maintained `Dockerfile`, `docker-compose.yml`, and `.dockerignore` ship at the repo root.
No config file or token is required to start — ANTHILL self-seeds a container-safe default
`config.json` into a mounted volume on first boot, and binds all interfaces automatically
(protected by the operator login, not network isolation — see [Security Model](#security-model)).
See **[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)** for the fuller write-up this section summarizes.

#### Quick start

```bash
docker compose up -d --build
docker compose logs -f anthill   # watch for "Open the colony console at http://<your-lan-ip>:8713/ui"
```

Open the URL printed in the logs and create your admin account. That's it.

#### `docker-compose.yml` (shipped default — Linux host networking)

```yaml
services:
  anthill:
    build: .
    image: anthill:latest
    container_name: anthill
    network_mode: "host"
    environment:
      # - ANTHILL_OLLAMA_HOST=http://192.168.1.50:11434
      # - ANTHILL_PORT=8713
      # - ANTHILL_API_TOKEN=your-32-char-minimum-token-here
      - ANTHILL_HOST=0.0.0.0
    volumes:
      - anthill-data:/app/.anthill
      # - /path/on/host/to/your/project:/workspace
    restart: unless-stopped

volumes:
  anthill-data:
```

| Field | What it does |
|-------|--------------|
| `network_mode: "host"` | Container shares the host's network stack directly — no NAT/bridge translation. This is what makes ANTHILL's auto-detected reachable IP equal your actual host machine's LAN IP, and lets a local Ollama at `http://localhost:11434` just work with zero extra config. **Linux Docker hosts only** — see the bridge-mode alternative below for Windows/macOS Docker Desktop. |
| `environment: ANTHILL_HOST` | Bind address override (highest precedence over `config.json`). Already defaults to `0.0.0.0`; set here explicitly so `docker inspect` shows it without opening a shell. |
| `environment: ANTHILL_PORT` | Port override, default `8713`. Only needed if you're changing the port. |
| `environment: ANTHILL_OLLAMA_HOST` / `ANTHILL_OLLAMA_MODEL` | Point at a different machine's Ollama, or change the default model, without editing `config.json`. |
| `environment: ANTHILL_API_TOKEN` | Optional static bearer token (≥ 32 chars) for scripts/CI. Not required for normal use — the web UI creates an admin account on first launch. Generate one with `openssl rand -hex 24`. |
| `volumes: anthill-data:/app/.anthill` | Persists the SQLite DB, `config.json`, logs, backups, exports, and the AES-256-GCM field-encryption key across container recreates. Back this volume up like any other stateful volume. |
| `volumes: <host-path>:/workspace` (commented out) | Bind-mount a real project so the file ant can read/propose patches against it. Set `agent_workspace_dir` to the same path (`/workspace`) in `.anthill/config.json` (inside the `anthill-data` volume) to match. |
| `restart: unless-stopped` | Survives host reboots and crashes; doesn't restart after an explicit `docker compose stop`. |

#### Bridge mode (Windows/macOS Docker Desktop)

`network_mode: host` isn't supported on Docker Desktop. Use explicit port publishing instead:

```yaml
services:
  anthill:
    build: .
    image: anthill:latest
    container_name: anthill
    ports:
      - "8713:8713"
    environment:
      - ANTHILL_OLLAMA_HOST=http://host.docker.internal:11434
    extra_hosts:
      - "host.docker.internal:host-gateway"   # Linux Docker Engine also needs this line;
                                                # Docker Desktop for Mac/Windows provides it automatically
    volumes:
      - anthill-data:/app/.anthill
    restart: unless-stopped
volumes:
  anthill-data:
```

With bridge mode, open ANTHILL at `http://<docker-host-ip>:8713/ui` — the published-port address
— rather than whatever `NetworkUtil` detects inside the container's own network namespace (that
value is only meaningful in host-network mode).

#### Plain `docker build`/`docker run` (no compose)

```bash
docker build -t anthill:latest .
docker run -d \
  --name anthill \
  --network host \
  -v anthill-data:/app/.anthill \
  anthill:latest

# Bridge mode instead:
docker run -d \
  --name anthill \
  -p 8713:8713 \
  -e ANTHILL_OLLAMA_HOST=http://host.docker.internal:11434 \
  --add-host=host.docker.internal:host-gateway \
  -v anthill-data:/app/.anthill \
  anthill:latest
```

#### Dockerfile summary

Multi-stage: `mcr.microsoft.com/dotnet/sdk:9.0` publishes `Anthill.Cli` (framework-dependent, not
self-contained/single-file — avoids single-file extraction quirks in containers and keeps rebuild
layers smaller), then the runtime stage is `mcr.microsoft.com/dotnet/aspnet:9.0` running as a
non-root user, with a `HEALTHCHECK` hitting the app's own unauthenticated `GET /health` endpoint.
The optional native C++ kernel is **not** built in the image — ANTHILL falls back to a
bit-identical managed implementation automatically when it's absent (`native_kernel:
managed-fallback` in `/status`), so the image needs no cmake/g++ stage.

#### Upgrading

```bash
git pull
docker compose up -d --build
```

The named volume persists across rebuilds; only the image layers change.

### Option E — LXC (automated, Proxmox or plain LXD/incus)

A one-shot installer ships at `deploy/lxc/setup.sh` for deploying straight into a fresh
Debian 12+/Ubuntu 22.04+ LXC container — the common home-lab path (Proxmox, TrueNAS SCALE,
plain LXD/incus). See **[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#3-lxc-implemented)** for the full
guide (creating the container, upgrading, uninstalling).

#### Installing into a fresh container

```bash
# Inside a fresh Debian/Ubuntu LXC container, as root:
apt-get update && apt-get install -y curl ca-certificates git
curl -fsSL https://raw.githubusercontent.com/thexonexone/operation-anthill/main/deploy/lxc/setup.sh -o setup.sh
bash setup.sh
```

That clones the repo to `/opt/anthill/src`, installs the .NET 9 SDK if it isn't already present,
publishes a self-contained `linux-x64` binary, creates a dedicated unprivileged `anthill` system
user, and installs + starts a hardened systemd unit (`deploy/lxc/anthill.service.template` — same
`NoNewPrivileges`/`ProtectSystem=strict` posture as the manual
[Option C](#option-c--systemd-service-recommended-for-always-on) unit above). No special LXC
features (nesting, privileged mode) are required — this is a plain systemd service, not
Docker-in-LXC.

```bash
systemctl status anthill --no-pager
journalctl -u anthill -n 20 --no-pager   # look for the reachable URL
```

#### Updating an existing ANTHILL container

```bash
# Inside the LXC container, as root:
cd /opt/anthill/src
git pull
bash deploy/lxc/setup.sh
```

The installer detects it's running from the existing checkout, stops the service, republishes the
binary, and restarts it. Your database, `config.json`, operator accounts, and encryption key under
`/opt/anthill/.anthill` are untouched — this is a binary upgrade, not a reinstall.

### Exposing Ollama on the network (Linux)

If Ollama runs on a **separate machine** from ANTHILL, you must bind Ollama to all interfaces:

```bash
# Method 1 — environment variable (persistent via systemd override)
sudo systemctl edit ollama
```

Add this to the override file:
```ini
[Service]
Environment="OLLAMA_HOST=0.0.0.0:11434"
```

```bash
sudo systemctl daemon-reload && sudo systemctl restart ollama

# Verify (from another machine)
curl http://OLLAMA_MACHINE_IP:11434/api/tags

# Allow through firewall
sudo ufw allow 11434/tcp
```

---

## Deploy on Windows

### Option A — Run from source

```powershell
# 1. Install .NET 9 SDK from https://dotnet.microsoft.com/download/dotnet/9.0

# 2. Clone
git clone https://github.com/thexonexone/operation-anthill.git
cd operation-anthill

# 3. Build
.\build.ps1

# 4. Configure
Copy-Item config.example.json .anthill\config.json
notepad .anthill\config.json      # set ollama_host, api_host

# 5. Set token and start
$env:ANTHILL_API_TOKEN = "your-32-char-minimum-token-here"
dotnet run --project src\Anthill.Cli -- --api
```

### Option B — Self-contained exe (no .NET runtime needed)

```powershell
dotnet publish src\Anthill.Cli\Anthill.Cli.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:DebugType=none `
  -o .\publish\win-x64

# The result: .\publish\win-x64\anthill.exe  (~96 MB, no dependencies)
```

### Option C — Windows Service (always-on)

```powershell
# Install NSSM (Non-Sucking Service Manager) from https://nssm.cc
# Or use sc.exe:

$tokenValue = "your-32-char-minimum-token-here"   # CHANGE THIS

New-Service -Name "ANTHILL" `
  -BinaryPathName '"C:\anthill\anthill.exe" --api' `
  -DisplayName "ANTHILL Colony API" `
  -StartupType Automatic `
  -Description "ANTHILL swarm-intelligence agent framework"

# Set the token as a system environment variable for the service
[System.Environment]::SetEnvironmentVariable("ANTHILL_API_TOKEN", $tokenValue, "Machine")

Start-Service -Name "ANTHILL"
Get-Service -Name "ANTHILL"
```

> **Note:** this registers the published exe directly with the Service Control Manager and is
> ready to use today — start, stop, and automatic startup all work. Like every deployment path,
> it's under continuous improvement: deeper SCM integration (graceful-stop lifecycle events,
> service-specific logging via `Microsoft.Extensions.Hosting.WindowsServices` /
> `UseWindowsService()`) is on the roadmap — see [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for
> status.

### Exposing Ollama on the network (Windows)

```powershell
# Run on the machine that has Ollama installed:
[System.Environment]::SetEnvironmentVariable("OLLAMA_HOST", "0.0.0.0:11434", "Machine")

# Then restart Ollama from the system tray or:
Stop-Process -Name "ollama" -Force
Start-Process "ollama" -ArgumentList "serve"

# Open the firewall
netsh advfirewall firewall add rule `
  name="Ollama API" `
  protocol=TCP `
  dir=in `
  localport=11434 `
  action=allow

# Verify from ANTHILL machine:
curl http://OLLAMA_MACHINE_IP:11434/api/tags
```

---

## Ollama Setup & Model Routing

### Pulling models

```bash
# Essential (works on most hardware, ~5 GB)
ollama pull llama3.1:8b

# Recommended planner/researcher/verifier (~40 GB, needs 24+ GB VRAM)
ollama pull llama3.3:70b

# Recommended coder/builder (~20 GB, needs 16+ GB VRAM)
ollama pull qwen2.5-coder:32b

# Check what's loaded
ollama list
curl http://localhost:11434/api/tags | python3 -m json.tool
```

### Using the built-in model browser

In the colony UI, click **⚙ Settings → Models** tab. This fetches `/ollama/models` from your configured Ollama host and shows all available models with their sizes, highlighting which are currently active in your model routes.

### Multi-GPU configuration

Ollama automatically uses all available CUDA GPUs when `CUDA_VISIBLE_DEVICES` is not set. No extra configuration is needed. To limit which GPUs Ollama uses:

```bash
# Use only GPU 0
CUDA_VISIBLE_DEVICES=0 ollama serve

# Use GPUs 0 and 1 (default — use all)
CUDA_VISIBLE_DEVICES=0,1 ollama serve
```

### Routing a specific ant to a specific model

Easiest path: **Settings → Ant Config** in the colony UI — pick a provider and model per caste and
save. Under the hood this edits the same `model_routes` map you can also hand-edit in
`.anthill/config.json`:

```json
"model_routes": {
  "planner":    { "provider": "ollama", "model": "YOUR_MODEL_NAME" },
  "researcher": { "provider": "ollama", "model": "YOUR_MODEL_NAME" },
  "coder":      { "provider": "openai", "model": "gpt-4o-mini" },
  "builder":    { "provider": "anthropic", "model": "claude-sonnet-4-5" },
  "verifier":   { "provider": "ollama", "model": "YOUR_MODEL_NAME" },
  "web":        { "provider": "perplexity", "model": "sonar" },
  "fallback":   { "provider": "ollama", "model": "YOUR_MODEL_NAME" }
}
```

Model names must match exactly what `ollama list` shows for Ollama routes (e.g. `llama3.3:70b`,
not `llama3.3`). Any role routed to a non-`ollama` provider needs that provider connected first —
see [Model Providers](#model-providers).

---

## Model Providers

Ants aren't limited to local Ollama models. ANTHILL can route any role (planner, researcher,
coder, builder, verifier, web, fallback) to a paid cloud provider instead, mixing free-local and
paid-API routes in the same colony:

| Provider | Kind | API surface used |
|----------|------|-------------------|
| **Ollama** | Free · local | `/api/generate` on your configured `ollama_host` — no key needed |
| **OpenAI (ChatGPT)** | Paid | `/v1/chat/completions` |
| **Anthropic (Claude)** | Paid | `/v1/messages` |
| **Perplexity** | Paid | `/chat/completions` (web-grounded Sonar models) |
| **OpenRouter** | Paid | `/v1/chat/completions` (one key, many hosted models) |

### Connecting a provider

In the colony UI: **Settings → Providers**. Each card shows connection status, an API-key field,
an optional base-URL override, and **Save** / **Test Connection** / **Remove** actions. Testing
fires one small live request through the real routing path and records whether it succeeded.
Once a provider is connected, assign it to a caste in **Settings → Ant Config**.

### Security

- API keys are sealed at rest with the same AES-256-GCM field cipher (`FieldCipher`, see
  [Security Model](#security-model)) used elsewhere in the database — key resolution order is
  `ANTHILL_ENCRYPTION_KEY` env var, then an auto-generated 0600 key file under the workspace.
- Keys are **write-only** from the console's perspective: `GET /providers` never returns a key,
  only a `configured` boolean, `enabled` flag, and last-verification status.
- Managing connections requires the `manage_providers` permission, reading status requires
  `read_providers` — both admin-only by default (see [Roles](#roles)); a Mission Coordinator
  cannot see or touch provider connections.
- Deleting a connection immediately stops routing to it; any role still pointed at that provider
  will fail closed with a clear `ERROR: <provider> API key not configured` message rather than
  silently falling back to a different model.

### API

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/providers/catalog` | Static metadata for every known provider (name, kind, default model, curated model list, key help URL) |
| `GET` | `/providers` | Secret-free connection status for every keyed provider (configured, enabled, last verification) |
| `POST` | `/providers` | Add/update a connection `{"provider","api_key"?,"base_url"?,"enabled"?,"label"?}` — `api_key` omitted leaves the stored key untouched |
| `DELETE` | `/providers/{provider}` | Remove a connection |
| `POST` | `/providers/{provider}/test` | Fire a live probe call and record the verification result |

```bash
curl -X POST http://localhost:8713/providers -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"provider":"openai","api_key":"sk-..."}'
curl -X POST http://localhost:8713/providers/openai/test -H "Authorization: Bearer $TOKEN"
curl http://localhost:8713/providers -H "Authorization: Bearer $TOKEN"
```

---

## Colony UI Guide

Open `http://YOUR_ANTHILL_HOST:8713/ui` in a browser. On first load you'll be prompted to sign in (or create the initial admin account on first run).

### Navigation Rail

A collapsible left rail (240px expanded / 60px icon-only; click **‹** to toggle) provides instant access to all pages. Collapse state is remembered across sessions.

| Page | Contents |
|------|---------|
| **Overview** | 4 KPI cards (model calls, tasks, events, approvals), mission dispatch, recent jobs, live event feed |
| **Colony** | Live canvas visualisation — the main control surface |
| **Missions** | Mission dispatch + full job history — "View Result" jumps to the Results page with that mission expanded |
| **Results** | Every mission as a compact, expandable row (filter by completed/partial/failed). Expanding shows the full **Mission Report**: status in plain English, the mission's final output (separate from per-task outputs), every task's readable result, tangible changes (patches + approval state), problems, the objective that drove an autonomous run, and any objectives the mission created |
| **Event Log** | Filterable, searchable full-page event log |
| **Pheromones** | Colony pheromone trail table with prune button *(admin only)* |
| **Ant Config** | Per-caste name, colour, and provider + model route editor *(admin only)* |
| **Autonomy** | Director status/start/stop/kill-switch, objectives backlog editor, recent autonomous runs *(admin only)* |
| **Security** | Security posture (auth, safety profile, bind exposure, encryption), capability-gate toggles, workspace boundary, and operator-shell controls *(admin only)* |
| **Shell** | Direct interactive terminal into the host ANTHILL runs on — command history, streamed output, exit codes, settable working dir, and quick buttons for service status/logs/restart (the LXC installer adds a scoped polkit rule so `systemctl restart anthill` works without weakening the unit's hardening). Admin-only, config-gated, every command audit-logged *(admin only)* |
| **Settings** | Connection / Providers / Colony / Models / System Info tabs *(admin only)* |
| **Users** | Operator account management *(admin only)* |

### Colony Canvas (Colony page)

The centre canvas shows the live colony graph:
- **Gold node (centre)**: The Queen
- **Coloured nodes (ring)**: The 6 ant types (Researcher, Coder, Builder, Verifier, File, Web)
- **Smaller nodes**: Individual worker instances
- **Solid edges**: Queen-to-ant command channels
- **Dashed arrows**: Ant-to-ant data flow edges derived from the task dependency graph — appear during a mission and show which ant's output feeds which other ant
- **Particles**: Data flowing through active channels

**Interactions:**
- Hover a node → tooltip with status, task count, activity %
- Click a node → Agent Inspector (right panel) shows assigned tasks and pheromone strength
- Double-click a node → rename it (persisted to server)
- Drag a node → reposition it on the canvas (persisted)
- Scroll → zoom in/out
- Drag canvas background → pan
- **Ctrl+K** → focus mission input on Colony page
- **Ctrl+L** → jump to Event Log page

### Colony Left Sidebar

| Card | What it shows |
|------|--------------|
| Colony Status | Live model calls, task count, event count, pending approvals |
| ⚠ Pending Approvals | Appears when a coder ant has proposed code changes — Approve or Reject inline |
| Jobs | Active and recent missions with status, goal preview, View / Cancel buttons |

### Colony Right Sidebar

| Card | What it shows |
|------|--------------|
| Agent Inspector | Selected ant's status, activity bar, recent task list |
| Colony Events | Real-time event stream, colour-coded by type (mission / task / patch / approval / error) |

Click **⛶** on Colony Events to jump to the full Event Log page. Click **▾** on any card header to collapse it.

### Settings (Settings page)

**Connection tab**: API base URL, Ollama reachability status, active model.

**Providers tab**: Connect API keys for external model providers — OpenAI (ChatGPT), Anthropic
(Claude), Perplexity, and OpenRouter — alongside free local Ollama. Each provider is its own card:
paste a key, optionally override the base URL, **Save**, then **Test Connection** to fire a live
probe call and confirm it works. Keys are encrypted at rest (AES-256-GCM) and are never sent back
to the browser after saving — the console only ever shows a configured/verified status, not the
key itself. Remove a connection to stop routing to it (ants routed there fall back to Ollama).
See [Model Providers](#model-providers) below for the API and [Security Model](#security-model)
for how keys are stored.

**Colony tab**: Ollama host/model, feature-gate toggles, limits, and the autonomy knobs (budgets, concurrency, aging, learning) — all editable and persisted live.

**Models tab**: Lists all models pulled on your Ollama instance with sizes. Active route assignments are highlighted. Click a model name to copy it to clipboard.

**System Info tab**: Safety profile, feature flags, native kernel status, live diagnostics.

### Autonomy (Autonomy page)

*Admin only.* Control panel for the 24/7 Colony Director (see [Autonomy design doc](docs/AUTONOMY.md)).

- **Director Status card**: running/stopped state, missions this hour/day against budget, backlog
  size, kill-switch indicator, and Start/Stop buttons — plus, since Phase 3, a **Concurrency** KPI
  (effective / configured, with the ResourceGovernor's verdict when it's throttling) and live
  **In flight** / **Governor** rows showing exactly which missions are running right now and why
  concurrency is (or isn't) reduced. Disabled with an explanatory note unless
  `autonomy_enabled: true` is set in config.
- **Add Objective**: title, charter (the standing goal text), and priority — enqueues a new backlog
  entry.
- **Objectives table** (collapsible, scrolls after ~20 rows): every objective with status
  (pending/active/paused/done/failed), charter preview, priority +/- buttons, a **Score** column
  (the Phase 4 success EMA that biases selection — color-coded, "—" until the first run),
  pause/resume, and delete.
- **Recent Autonomous Runs table** (collapsible, scrolls after ~20 rows): each Director-driven
  mission with its objective, generated goal, outcome score, follow-ups created, and a **View**
  button that opens the full [Mission Report](#the-approval-workflow) — final output, per-task
  results in plain English, and every tangible change the run proposed with its approval state.

By default the Director only *proposes* — file changes land in the normal approval queue for a
human to review; if a run's report shows no patches and no pending approvals, that run changed
nothing on disk. **Phase 5 gated auto-apply** (Configuration → Security → Autonomous Auto-Apply,
off by default) can let it apply low-risk fixes on its own — but only patches matching your path
allowlist and size cap, and only if the workspace still builds + tests green afterward, with
automatic rollback on failure. Mission goals come from the Strategist (`autonomy_dedupe_similarity`,
`autonomy_max_followups_per_run`, `autonomy_max_objective_depth` govern dedup and follow-up
enqueueing), selection order from strict priority + aging + the Phase 4 learning bias, and
objectives that stop producing value (or loop on near-identical goals) are auto-paused with an
`objective_retired` event. All knobs are in `config.example.json` and
[docs/AUTONOMY.md](docs/AUTONOMY.md).

### Dispatching a Mission

Type a goal into the mission bar and press **Enter** or **▶**. The mission bar appears in three places:
- Bottom of the **Colony** canvas
- Top of the **Missions** page
- **Overview** page dispatch card

All three are enabled/disabled together — whichever is in flight, all three lock until it completes.

---

## The Approval Workflow

ANTHILL never writes code to disk without your explicit approval. The flow:

```
Mission runs
   └─► Coder ant produces JSON patch proposal
         └─► PatchProposalParser validates: path, file type, no traversal
               └─► Approval request saved to DB (status: pending)
                     └─► UI bell 🔔 lights up red with count
                           └─► "Pending Approvals" card appears in left panel
                                 └─► You click ✓ Approve or ✕ Reject
                                       └─► Approved: status → approved
                                             └─► POST /apply/{approval_id}
                                                   └─► Patch written to disk with backup
```

**What triggers an approval:**
- Any coder ant task that returns a JSON object with a `proposals` array
- Each proposal in the array gets its own approval request
- **Duplicates are deduped**: if an identical change (same file, change type, and content) is
  already pending, no second request is created — an `approval_request_deduped` event is logged
  instead. This keeps high-frequency autonomous runs from flooding the queue while a proposal
  awaits review.

**What requires old_content:**
- `change_type: "modify"` and `change_type: "delete"` use `old_content` to find the exact text to replace (like a surgical diff). If missing, the patch is rejected at apply time.
- `change_type: "add"` does not require `old_content` — it appends or creates.

**File types that can be patched** (configured in `AnthillRuntime.cs`):
`.cs .csproj .sln .props .targets .py .js .ts .tsx .jsx .html .css .json .yaml .yml .toml .sh .bat .ps1 .go .rs .java .kt .rb .php .sql .xml .md .txt`

**After approval, applying the patch:**

The UI **Approve** button only marks the request approved. To actually write the file, the patch must be applied separately. This is a deliberate two-step gate. In the UI, after approving, note the approval ID and call:

```bash
curl -X POST http://localhost:8713/apply/APPROVAL_ID \
  -H "Authorization: Bearer YOUR_TOKEN"
```

> The UI **✓ Approve & Queue Apply** button marks the request approved and queues the patch for application in one click. The backend `/apply/{id}` endpoint remains available for scripted use.

---

## Self-Modification Missions

ANTHILL supports missions where agents read the ANTHILL source code itself, propose improvements, and the user approves and applies them.

To enable this, set `agent_workspace_dir` to the ANTHILL source root:

```json
"agent_workspace_dir": "/path/to/operation-anthill/src"
```

The file ant can then list directories and read `.cs` files. The coder ant can propose patches. The Queen applies them only after you approve.

**Example self-modification mission:**

```
Read the file src/Anthill.Core/Configuration/AnthillRuntime.cs and identify
the MaxDynamicTasks constant. Then read src/Anthill.Core/Planning/Planner.cs
and find where the planner prompt is constructed. Propose a patch that increases
MaxDynamicTasks to 10 and adds a note to the planner prompt that says "Prefer
parallel task execution: assign independent tasks to different ants when possible."
Include exact old_content for all modifications.
```

> **Warning**: Approving and applying self-modification patches can break the running colony if the changes contain errors. Always review patches carefully before applying, and ensure you have a backup (ANTHILL creates one automatically in `.anthill/backups/` at mission start).

---

## Long-Input / Specification-Ingestion Handling

When you paste a large specification, architecture document, framework dump, or instruction set, the Queen must **not** funnel the whole thing into a single "Analyze Mission Goal" task — that overflows context and produces shallow results. ANTHILL detects this automatically.

### How it works

```
Mission goal larger than long_input_threshold?
   └─► Queen classifies mission_type = "spec_ingestion"  (logged as mission_classified)
         └─► Planner splits the document into bounded sections
               (markdown headings → ALL-CAPS labels → numbered headings → blank-line paragraphs,
                then greedily packed to max_section_chars, capped at max_section_tasks)
                 └─► One researcher "section_analysis" task per section
                       - non-critical (a failed/timed-out section never aborts the mission)
                       - run in parallel up to max_parallel_workers
                       - bounded retry (MaxAttempts = 2) → a timeout retries with the same small scope
                         └─► One builder "synthesis" task depends on ALL sections
                               - critical; proceeds even if some sections failed, noting the gap
                                 └─► verifier task checks the synthesized plan
```

### Failure semantics

- A **non-critical** task (a section) that fails or times out does **not** skip its dependents. The synthesis still waits for every section to reach a terminal state, then runs against whatever succeeded.
- Only a **critical** task failure fails the whole mission. Non-critical failures degrade the mission to `Partial`, never `Failed`.
- The verifier reports `Degraded Sections: N` when sections were lost, so partial output is preserved and visible.

This is implemented generically via a `critical` flag on every task (default `true`), so the same fault-tolerant fan-in mechanism is available to any future plan.

### Configuration

| Setting | Meaning | Default |
|---------|---------|---------|
| `spec_ingestion_enabled` | Master switch for long-input handling | `true` |
| `long_input_threshold` | Goal length (chars) above which a mission becomes spec ingestion | `6000` |
| `max_section_chars` | Maximum characters per section task | `3500` |
| `max_section_tasks` | Maximum number of section tasks (overflow merged into the last) | `6` (clamped 2–12) |

Set `spec_ingestion_enabled: false` to always use the normal single-plan path.

---

## API Reference

All endpoints (except `/`, `/health`, `/ui`) require:
```
Authorization: Bearer YOUR_TOKEN
```

### Core

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/` | API info |
| `GET` | `/health` | Health check (no auth) |
| `GET` | `/ui` | Colony web UI (no auth) |
| `GET` | `/status` | System stats — model calls, task count, pending approvals |
| `GET` | `/system/summary` | Header status: version, live Ollama reachability, default model, and a local-vs-providers routing breakdown |
| `GET` | `/update/check` | Whether a newer release is published on GitHub (`?force=1` bypasses the 30-min cache) |
| `GET` | `/maintenance/stats` | Disk free, DB size, backup count/size, table row counts |
| `POST` | `/maintenance/flush` | Prune old backups + old events + VACUUM; returns bytes freed |
| `POST` | `/maintenance/clear-missions` | Delete all mission history (keeps objectives/pheromones/users/config) |
| `POST` | `/maintenance/reset-config` | Reset settings to defaults, preserving connection settings |
| `POST` | `/objectives/clear` | Dump the entire objective backlog + run history |
| `POST` | `/jobs/cancel-all` · `/jobs/{id}/cancel` | Cancel all / one job (queued work dropped; a running mission finishes) |
| `GET` | `/selftest` | Run 15-check self-test suite |

### Missions & Jobs

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/missions` | Submit a mission `{"goal": "your goal here"}` |
| `GET` | `/jobs` | List all jobs |
| `GET` | `/jobs/{id}` | Get job detail including result and debug trace |
| `GET` | `/missions` | Mission history (plain text) |
| `GET` | `/missions/json` | Mission history as JSON (`?limit=`) — powers the Results page |
| `GET` | `/missions/{id}` | Mission detail (plain text) |
| `GET` | `/missions/{id}/report` | Structured readable report: final output, per-task results in plain English, patches + approval states, problems |
| `GET` | `/missions/{id}/graph` | Task DAG for a specific mission |

### Graph & Events

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/graph` | Live task dependency graph (JSON) |
| `GET` | `/events` | Full event log (plain text) |
| `GET` | `/tasks` | Task metrics |
| `GET` | `/messages` | Agent message metrics |

### Patches & Approvals

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/approvals` | Pending approval requests (plain text) |
| `GET` | `/approvals/{id}` | Approval detail |
| `POST` | `/approve/{id}` | Approve a patch proposal |
| `POST` | `/reject/{id}` | Reject a patch proposal `{"reason": "optional reason"}` |
| `POST` | `/apply/{id}` | Apply an approved patch to disk |
| `GET` | `/patches` | All patch proposals |
| `GET` | `/patches/{id}` | Patch detail |

### Configuration & Diagnostics

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/config` | Current runtime config (plain text) |
| `GET` | `/models` | Model router status and routes |
| `GET` | `/routes` | Active model route assignments |
| `GET` | `/pheromones` | Pheromone trail memory |
| `GET` | `/diagnostics` | Recent failure events and system diagnostics |
| `GET` | `/schema` | Database schema status |
| `GET` | `/memory` | Colony memory summary |
| `GET` | `/sources` | Saved research sources |
| `GET` | `/communication` | Agent communication metrics |

### Ollama Proxy

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/ollama/models` | List available Ollama models (proxied from Ollama host) |

### Operator Shell (admin only)

Admin-only host terminal behind the `operator_shell` permission (never granted to coordinators)
and the `operator_shell_enabled` config gate. Every command is audit-logged. See
[Security Model](#security-model).

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/shell/info` | Console status: enabled flag, default working dir, host, OS, timeout |
| `POST` | `/shell/exec` | Run one command `{"command","dir"?}` → `{exit_code, stdout, stderr, timed_out, dir, elapsed_seconds}` |

### Autonomy (24/7 Director)

The Director works a backlog of objectives unattended, under budgets and a kill switch, with all
file changes queued for human review. **Off by default**: requires `autonomy_enabled: true` in
config, and must be started explicitly (`--autonomous` at boot or `POST /autonomy/start`). See
[docs/AUTONOMY.md](docs/AUTONOMY.md).

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/autonomy/status` | Director state, budgets, counts, next objective |
| `POST` | `/autonomy/start` | Start the Director (clears the kill switch) |
| `POST` | `/autonomy/stop` | Stop the Director and engage the durable kill switch |
| `GET` | `/autonomy/runs` | Autonomous mission audit trail (`?objective_id=` to filter) |
| `GET` | `/objectives` | List backlog objectives (`?status=` to filter) |
| `POST` | `/objectives` | Add an objective `{"title","charter","priority"?,"max_runs"?}` |
| `GET` | `/objectives/{id}` | Objective detail |
| `PATCH` | `/objectives/{id}` | Update `{"status"?,"priority"?}` (pause/resume/reprioritize) |
| `DELETE` | `/objectives/{id}` | Remove an objective |

```bash
# Enable autonomy_enabled in config, then:
curl -X POST http://localhost:8713/objectives -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"title":"daily-deps-audit","charter":"Audit project dependencies and propose updates.","max_runs":3}'
curl -X POST http://localhost:8713/autonomy/start -H "Authorization: Bearer $TOKEN"
curl http://localhost:8713/autonomy/status -H "Authorization: Bearer $TOKEN"
# Halt anytime (also: create a .anthill/STOP file):
curl -X POST http://localhost:8713/autonomy/stop -H "Authorization: Bearer $TOKEN"
```

The kill switch is durable: `POST /autonomy/stop` writes `.anthill/STOP`, and the Director
refuses to run while that file exists. `POST /autonomy/start` clears it.

---

## Authentication & Operator Accounts

The web console is secured by **password-based operator accounts**, not a shared API key.

- **First run** — the UI shows a one-time setup screen to create the initial **administrator**
  (username + password). No accounts can be created any other way until one exists.
- **Login** — every subsequent visit requires a username and password. A successful login mints an
  in-memory **session token** (12-hour sliding expiry) used as the bearer credential. Sessions live
  only in process memory, so a restart logs everyone out and no session secret is ever written to disk.
- **Passwords** are stored only as salted **PBKDF2-SHA256** hashes (120k iterations), verified in
  constant time. Plaintext is never persisted.

### Roles

| Role | Can do |
|------|--------|
| **Administrator** | Everything: dispatch missions, approvals, settings, ant config, model provider connections, pheromone memory, autonomy control, and **user management**. |
| **Mission Coordinator** | Only **send missions to the Queen** and **read the event logs** (plus live status to watch them run). Settings, approvals, patches, autonomy, pheromones, ant config, provider connections, and user management are all denied. |

Admins manage accounts from the **👥 Users** page in the console: create users, assign roles, reset
passwords, enable/disable, or delete. Changing a user's password, role, or status immediately revokes
their active sessions. The last remaining administrator cannot be demoted, disabled, or deleted, so you
can never lock yourself out of admin.

### Lock-out recovery (CLI)

If you forget the admin password, reset it from the host shell (operates directly on the database):

```bash
dotnet run --project src/Anthill.Cli -- --set-password admin <new-password>
dotnet run --project src/Anthill.Cli -- --add-user <username> <password> admin    # or: coordinator
```

### Optional static token (programmatic access)

`ANTHILL_API_TOKEN` is **no longer required** and is not the web credential. If you set one (≥ 32
chars), it acts as a programmatic **admin** bearer for scripts/CI — convenient for automation, but
unnecessary for normal use. Leave it unset to rely purely on operator accounts.

---

## Security Model

| Control | Implementation |
|---------|---------------|
| Network binding | Listens on all interfaces (`0.0.0.0`) by default — container/LXC/service-friendly, reachable at this machine's LAN IP with zero config. Auth (not network isolation) is the boundary; set `api_host` to `127.0.0.1` (or `ANTHILL_HOST=127.0.0.1`) to restrict to localhost only |
| Operator accounts | Password login (PBKDF2-SHA256, salted, 120k iters); role-based authorization (admin / coordinator) |
| Sessions | In-memory bearer sessions with sliding 12h expiry; revoked on password/role/status change and restart |
| Optional static token | `ANTHILL_API_TOKEN` is optional; if set it must be ≥ 32 chars and acts as a programmatic admin credential |
| Constant-time auth | `CryptographicOperations.FixedTimeEquals` — immune to timing attacks |
| Rate limiting | `/missions`: 10/min/IP · auth failures: 20/min/IP (success clears the failure budget) |
| Security headers | `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, CSP, `Referrer-Policy: no-referrer` |
| SSRF guard | Private/loopback/link-local URLs are dropped before any agent sees them |
| Path traversal guard | All file paths are resolved against `agent_workspace_dir`; `..` and absolute paths are rejected |
| Prompt injection prefix | Every agent system prompt begins with an injection-resistance prefix |
| Encryption at rest | Sensitive columns encrypted with AES-256-GCM; key auto-generated to `.anthill/field.key` |
| DB hardening | Files chmod-600 (POSIX); pre-mission backup; fully parameterised SQL |
| Tool gates | Shell, file write, patch apply, web search all off by default; each gated by config |
| Operator shell console | Admin-only host terminal (Configuration → Shell). Four gates: authenticated + admin role (`operator_shell` never granted to coordinators) + `operator_shell_enabled` config gate + every command audit-logged with the operator's username. 60s/command timeout, output capped. Ships enabled for admins; disable on untrusted networks. Separate from the AI ants' allowlisted `shell_tool_enabled` (off by default) |
| No public API docs | No Swagger/OpenAPI surface exposed |

---

## Building from Source

### Full build (native kernel + .NET)

```bash
# Linux / macOS
./build.sh

# Windows
.\build.ps1
```

### .NET only (no C++ toolchain needed)

```bash
dotnet build Anthill.sln -c Release
```

The native C++ kernel is optional. If it's missing or fails to load at runtime, ANTHILL silently falls back to a **bit-identical managed (C#) implementation** and logs `"native_kernel: managed-fallback"` in `/status`.

### Publish a self-contained binary

```bash
# Linux x64
dotnet publish src/Anthill.Cli/Anthill.Cli.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=none \
  -o ./publish/linux-x64

# Windows x64
dotnet publish src\Anthill.Cli\Anthill.Cli.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=none `
  -o .\publish\win-x64
```

Prefer not to build it yourself? `.github/workflows/release.yml` builds both of these plus a
versioned Docker image and **publishes** them automatically on every tagged release
(`git tag vX.Y.Z && git push origin vX.Y.Z` — four-part maintenance tags like `vX.Y.Z.W` work
too) — see [docs/DEPLOYMENT.md §4](docs/DEPLOYMENT.md#4-releases). Grab a binary from the
[Releases page](https://github.com/thexonexone/operation-anthill/releases) or pull the image:

```bash
docker pull ghcr.io/thexonexone/operation-anthill:latest
```

### Run tests

```bash
dotnet test Anthill.sln -c Release
```

### Self-test (validates the running colony)

```bash
anthill --selftest
# or:
dotnet run --project src/Anthill.Cli -- --selftest
```

Runs 15 checks: DB schema, token security, SSRF guard, path guard, model routes, pheromone engine, and more.

### CLI commands

```bash
anthill --api                          # start API + colony UI on configured port
anthill --api --host 0.0.0.0           # override bind address
anthill --api --port 9000              # override port
anthill --api --ollama-host http://192.168.1.5:11434   # override Ollama host
anthill --api --ollama-model llama3.1:8b               # override default model
anthill --mission "summarise this repo"                # run a single mission from CLI
anthill --selftest                     # run diagnostics
anthill --status                       # print system status
anthill --version                      # print version
```

---

## Troubleshooting

### "ANTHILL_API_TOKEN is only N characters long"

Your token is too short. It must be **at least 32 characters**. Generate one:

```bash
# Linux
openssl rand -hex 24

# Windows PowerShell
-join ((65..90)+(97..122)+(48..57) | Get-Random -Count 40 | % {[char]$_})
```

### "Cannot reach Ollama" in the UI

1. Verify Ollama is running: `curl http://OLLAMA_HOST:11434/api/tags`
2. Verify `ollama_host` in `.anthill/config.json` is correct.
3. If Ollama is on another machine, ensure it's bound to `0.0.0.0:11434` (not just localhost).
4. Check firewall rules on the Ollama machine allow TCP 11434 inbound.
5. Restart ANTHILL after changing the config — config is read at startup only.

### "FOREIGN KEY constraint failed" on mission start

The SQLite foreign key constraint requires the mission row to exist before events are logged. This was a known bug fixed in v1.8.0 by calling `Memory.SaveMission(mission)` before the first `LogEvent`. If you see this on a fresh install, ensure you're running v1.8.0+.

### Approvals not appearing in the UI (0 pending)

Common causes:
1. **`old_content: null` in coder output** — fixed in v1.8.0 (`JsonStrOrNull` helper). Ensure you're on the latest build.
2. **Unsupported file extension** — the patch validator rejects file types not in `PatchAllowedSuffixes`. Check `/events` for `patch_proposal_parse_failed` events.
3. **Patch proposal JSON invalid** — the coder model produced malformed JSON. Check the debug trace in the job result.
4. **Wrong UI parsing** — the approvals endpoint returns plain text, not JSON. If you've modified the UI, ensure `pollApprovals()` uses `apiText()` not `api()`.

### Missions completing as "Partial"

Tasks are marked skipped when their dependency is missing or failed. Check:
1. The event log (`Ctrl+L`) for `task_skipped` events and their reason.
2. The debug trace in the job result for `Skipped` entries.
3. If all tasks depend on one that failed, the whole chain skips.

### Canvas shows "Web" label over "Researcher"

Fixed in v1.8.0. The ants were placed at angles -90° and 270° which are identical. Now spaced at 60° intervals: -90°, -30°, 30°, 90°, 150°, 210°.

### File ant reads wrong file

The model sometimes hallucinates a different filename than the one in the task description. The `agent_workspace_dir` limits what can be read, but the model must generate the correct relative path. Use explicit file paths in your mission goal: *"Read the file `src/Anthill.Core/Configuration/AnthillRuntime.cs` specifically."*

### `dotnet: command not found` on Linux

Install the .NET 9 SDK:
```bash
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y dotnet-sdk-9.0
```

### Port 8713 already in use

Either change `api_port` in `.anthill/config.json` or kill the existing process:
```bash
# Linux
lsof -i :8713 | grep LISTEN
kill $(lsof -t -i:8713)

# Windows
netstat -ano | findstr :8713
taskkill /PID <PID> /F
```

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the full version history.

---

## License

See [LICENSE](LICENSE) for details.

---

*ANTHILL — local swarm intelligence. No cloud. No API keys. No data leaves your machine.*
