# ANTHILL — Container & Appliance Deployment

> Status: **Docker — IMPLEMENTED.** LXC and Windows Service packaging are next (see the roadmap
> at the bottom). This doc is the living reference for all three; it grows as each lands instead
> of being rewritten from scratch each time.

Goal: run ANTHILL the way most home-lab/production setups actually deploy things — a standalone
LXC container, a Docker container, or a Windows service — reachable at the IP of the machine it's
running on, the same way any other appliance/container on your network would be. No cloud
dependency, no separate reverse proxy required to make it reachable.

## 1. What changed to make this possible

Two small architectural changes underpin every deployment target in this doc, not just Docker:

- **ANTHILL now binds all interfaces (`0.0.0.0`) by default**, in every safety profile. Previously
  the default (and every safety profile's forced override) was `127.0.0.1`, which meant a fresh
  container/service install was unreachable from the network until someone edited `config.json`.
  The actual security boundary was already the operator login (password auth, PBKDF2-SHA256,
  role-based sessions — see [Security Model](../README.md#security-model)) and not network
  isolation, so this changes a cosmetic default, not the real safety rail. Set `api_host` to
  `127.0.0.1` (or `ANTHILL_HOST=127.0.0.1`) if you specifically want localhost-only.
- **`ANTHILL_HOST` / `ANTHILL_PORT` / `ANTHILL_OLLAMA_HOST` / `ANTHILL_OLLAMA_MODEL` environment
  variables now override `config.json`**, with the highest precedence of any config source. This
  is what makes container/LXC/service deployment clean: you configure the *deployment*
  (docker-compose `environment:`, an LXC profile, a Windows Service's env block) without baking
  settings into an image or editing a file inside a running container. It's also what makes the
  CLI's `--host`/`--port`/`--ollama-host`/`--ollama-model` flags actually take effect — they set
  these same env vars under the hood now (previously they wrote a static field that
  `AnthillRuntime.Initialize()` silently overwrote a moment later from `config.json`; this was a
  latent bug, now fixed as a side effect).
- **A reachable LAN IP is auto-detected and printed at startup** (`NetworkUtil.GetLikelyLanIPv4`,
  `Anthill.Core/Common/NetworkUtil.cs`) — a local UDP "connect" that never sends a packet, just
  asks the OS which interface/address it would route through. `0.0.0.0` isn't a URL you can open
  in a browser; this is what lets the console banner and `GET /status` (`reachable_ip` field)
  show you the actual address to use instead, on both Linux and Windows, without you having to run
  `ip addr`/`ipconfig` yourself.

None of this changes the actual security posture (auth is always on; see the config-level
comments in `AnthillConfig.cs`/`config.example.json`) — it changes what a fresh install looks
like on first boot.

## 2. Docker (implemented)

Ships at the repo root: `Dockerfile`, `docker-compose.yml`, `.dockerignore`.

### Quick start (Linux Docker host)

```bash
docker compose up -d --build
docker compose logs -f anthill
```

Watch the logs for a line like:

```
ANTHILL v1.8.6 API listening on http://0.0.0.0:8713
Open the colony console at http://192.168.1.50:8713/ui  (or http://localhost:8713/ui on this machine)
Listening on all network interfaces — protected by the operator login, not network isolation.
```

Open that URL and create your admin account. That's it — no config file to prepare first.

### Why `network_mode: host` by default

The shipped `docker-compose.yml` uses `network_mode: "host"`, which makes the container share the
host's network stack directly instead of Docker's usual NAT'd bridge network. Two consequences,
both desirable here:

1. ANTHILL's auto-detected reachable IP genuinely is your host machine's LAN IP — not a Docker
   bridge address like `172.17.0.2` that nothing else on your network can reach without a port
   publish.
2. A locally-running Ollama at `http://localhost:11434` (the common home-lab setup: Ollama and
   ANTHILL on the same box) just works, with no `host.docker.internal` plumbing.

This only works on **Linux** Docker hosts — it's the standard setup for Proxmox/LXC-hosted Docker,
bare Debian/Ubuntu, TrueNAS SCALE, Unraid, etc. If that's not you, use bridge mode instead:

### Bridge mode (Windows/macOS Docker Desktop, or if you just prefer explicit port publishing)

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
      - "host.docker.internal:host-gateway"   # Linux Docker Desktop / Docker Engine also needs this line;
                                                # Docker Desktop for Mac/Windows provides host.docker.internal automatically
    volumes:
      - anthill-data:/app/.anthill
    restart: unless-stopped
volumes:
  anthill-data:
```

With bridge mode, ANTHILL is reachable at `http://<docker-host-ip>:8713/ui` — the port-published
address — rather than whatever IP `NetworkUtil` detects inside the container's own network
namespace (that detected value is only meaningful in host-network mode).

### Persistence

Everything that needs to survive a container recreate lives under `/app/.anthill` inside the
container: the SQLite database, `config.json` (auto-seeded with container-safe defaults on first
boot if not already present), logs, backups, exports, and the AES-256-GCM field-encryption key.
The compose file mounts this as the named volume `anthill-data`. Back that volume up like you
would any other stateful container volume; there's nothing else to persist.

### Reaching a project directory for self-modification missions

If you want the file ant to read/propose patches against a real project on disk, bind-mount it
and point `agent_workspace_dir` in `.anthill/config.json` at the same path:

```yaml
    volumes:
      - anthill-data:/app/.anthill
      - /path/on/host/to/your/project:/workspace
```

```jsonc
// .anthill/config.json (inside the anthill-data volume)
"agent_workspace_dir": "/workspace"
```

### Upgrading

```bash
git pull
docker compose up -d --build
```

The named volume persists across this; only the image layers change.

### Optional: a static API token for scripts/CI

Not required for normal use (the web UI creates an admin account on first launch), but if you
want one for programmatic/CI access, uncomment `ANTHILL_API_TOKEN` in `docker-compose.yml` — it
must be at least 32 characters:

```bash
openssl rand -hex 24
```

### No native kernel in the image (by design)

The Dockerfile does not build the optional C++20 native compute kernel — ANTHILL falls back to a
bit-identical managed implementation automatically when the native library is absent (see
`src/Anthill.Core/Native/NativeKernel.cs`, and `native_kernel: managed-fallback` in `/status`).
This keeps the image free of a cmake/g++ build stage. If you want native-kernel acceleration
in-container, add a cmake stage before the `build` stage in the `Dockerfile` and copy the
resulting shared library into `native/anthill_kernel/` before `dotnet publish` runs.

## 3. LXC (planned — next up)

Target: a setup script + systemd unit for deploying directly inside a Proxmox-style Linux
container — install the .NET 9 runtime, publish/copy the app, register it as a systemd service
(the existing `README.md` → Deploy on Linux → Option C systemd unit is the starting point), and
document the couple of LXC-specific gotchas (unprivileged container capabilities, whether the
managed kernel fallback is required vs. building the native kernel with cmake inside the
container). Since LXC containers get a real network interface (unlike Docker's bridge default),
`NetworkUtil`'s auto-detected IP should just work here with no extra networking config at all —
this is expected to be the simplest of the three targets once written up.

## 4. Windows Service (planned — next up)

`README.md` already documents registering the published `anthill.exe` directly via
`New-Service`/`sc.exe` (Deploy on Windows → Option C), which works for basic start/stop today.
What's still needed for a *proper* Windows Service — graceful shutdown on service stop, correct
integration with the Service Control Manager's startup/shutdown timeouts, Windows Event Log
output instead of a console — is wiring up
[`Microsoft.Extensions.Hosting.WindowsServices`](https://learn.microsoft.com/dotnet/core/extensions/windows-service)
(`.UseWindowsService()` on the host builder in `src/Anthill.Api/ApiHost.cs`, plus a new
`Microsoft.Extensions.Hosting.WindowsServices` package reference). That package restore needs to
happen on a machine with real NuGet access; this doc will be updated with the install script once
it's built and verified.

## 5. Roadmap

| Target | Status | Key files |
|--------|--------|-----------|
| **Container-style IP binding + env var overrides** ✅ | **DONE.** Underpins all three targets below. | `NetworkUtil.cs`, `AnthillConfig.cs`, `AnthillRuntime.cs`, `Anthill.Cli/Program.cs`, `Anthill.Api/ApiHost.cs` |
| **Docker** ✅ | **DONE.** `Dockerfile`, `docker-compose.yml`, `.dockerignore` at repo root. | see §2 above |
| **LXC** | Planned next. | new `deploy/lxc/` setup script + systemd unit |
| **Windows Service** | Planned next. | `Microsoft.Extensions.Hosting.WindowsServices` integration in `ApiHost.cs`, install script |

Implementation order: container-style networking (done) → Docker (done) → LXC → Windows Service,
per the agreed build order.
