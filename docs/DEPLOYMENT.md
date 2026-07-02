# ANTHILL — Container & Appliance Deployment

> Status: **Docker, LXC, and Windows deployments are all ready to use today.** Like the rest of
> ANTHILL they remain under continuous development and improvement — the current refinement
> target is deeper Windows SCM integration (see the roadmap at the bottom). This doc is the
> living reference for all three; it grows as each improvement lands instead of being rewritten
> from scratch each time.

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

## 3. LXC (implemented)

Ships at `deploy/lxc/`: `setup.sh` (unattended installer/upgrader) and
`anthill.service.template` (the systemd unit it installs). Targets a fresh Debian 12+ or
Ubuntu 22.04+ LXC container — Proxmox-created or otherwise, anything with systemd and `apt`.

Unlike Docker, an LXC container gets a real network interface directly (no bridge/NAT layer to
work around), so `NetworkUtil`'s auto-detected reachable IP just works here with zero extra
networking config — this is the simplest of the three targets to actually use once it's running.

### Creating the container (Proxmox)

From the Proxmox host shell (or Datacenter → node → Create CT in the web UI):

```bash
# Download a template if you don't already have one (Debian 12 shown; Ubuntu 22.04/24.04 works too)
pveam update
pveam available --section system | grep debian-12
pveam download local debian-12-standard_12.*_amd64.tar.zst

# Create an unprivileged container — 2 vCPU / 4 GB RAM / 16 GB disk is plenty for ANTHILL itself
# (Ollama, if it runs on the same box, wants a lot more — see README's Resource Requirements)
pct create 200 local:vztmpl/debian-12-standard_12.*_amd64.tar.zst \
  --hostname anthill \
  --cores 2 --memory 4096 --swap 512 \
  --rootfs local-lvm:16 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp \
  --unprivileged 1 \
  --features nesting=0 \
  --onboot 1

pct start 200
pct enter 200
```

No special LXC features are required — this is a plain systemd service running a normal .NET
binary, not Docker-in-LXC, so `nesting`, `keyctl`, or privileged mode aren't needed. (`nesting`
only matters if you're planning to run Docker or another container runtime *inside* this same
LXC container — unrelated to ANTHILL itself.)

Using plain LXD/incus instead of Proxmox? `lxc launch images:debian/12 anthill` (or the
equivalent `incus launch`), then `lxc exec anthill -- bash` gets you to the same starting point —
everything below is identical.

### Installing ANTHILL

Inside the container:

```bash
apt-get update && apt-get install -y curl ca-certificates git
curl -fsSL https://raw.githubusercontent.com/thexonexone/operation-anthill/main/deploy/lxc/setup.sh -o setup.sh
bash setup.sh
```

That one script: installs the .NET 9 SDK (via Microsoft's apt repo, falling back to
`dotnet-install.sh` on distros/versions Microsoft's repo doesn't have an entry for yet), clones
the repo, publishes a self-contained `linux-x64` binary, creates a dedicated unprivileged
`anthill` system user, installs and enables the systemd unit, and starts the service.

Check the result:

```bash
systemctl status anthill --no-pager
journalctl -u anthill -n 20 --no-pager   # look for the "Open the colony console at http://..." line
```

Open the printed URL from another machine on your network and create your admin account.

### Upgrading

```bash
cd /opt/anthill/src   # setup.sh's default checkout location
git pull
bash /opt/anthill/src/deploy/lxc/setup.sh
```

Or just re-run the same one-liner from the install step — `setup.sh` detects the existing
checkout, pulls latest, republishes, and restarts the service. The named data directory
(`/opt/anthill/.anthill` by default: DB, `config.json`, logs, backups, exports, encryption key)
is untouched by an upgrade.

### Customizing the install location / service user

```bash
ANTHILL_INSTALL_DIR=/srv/anthill ANTHILL_SERVICE_USER=anthill-svc bash setup.sh
```

### Uninstalling

```bash
systemctl disable --now anthill
rm -f /etc/systemd/system/anthill.service
systemctl daemon-reload
rm -rf /opt/anthill /etc/anthill   # or just delete/destroy the whole LXC container instead
```

## 4. Releases

`.github/workflows/release.yml` builds a tagged release: self-contained `linux-x64`/`win-x64`
binaries, a versioned Docker image pushed to `ghcr.io/thexonexone/operation-anthill`, and a
GitHub Release with notes pulled straight from the matching `## vX.Y.Z` section of
`CHANGELOG.md`.

To cut a release once a version bump is committed to `main`:

```bash
git tag v1.8.7
git push origin v1.8.7
```

The workflow's first job (`verify-version`) fails loudly if the tag doesn't match
`AnthillRuntime.Version` in the pushed commit — this guards against tagging before the version
bump actually landed. If it fails: bump the version, commit, delete the bad tag
(`git tag -d v1.8.7 && git push origin :refs/tags/v1.8.7`), and re-tag.

**The release and the container package are published automatically** on tag push — no manual
"Publish" step. Each tag produces: the self-contained `linux-x64`/`win-x64` archives attached to
a published GitHub Release (marked "latest"), and `ghcr.io/<repo>:<version>` + `:latest` pushed to
GitHub Packages (GHCR). Four-part maintenance tags (`vX.Y.Z.W`) are handled the same as three-part
releases.

First run only: GHCR creates the container package as **private** by default. Make it public once
at `github.com/users/<you>/packages/container/operation-anthill/settings → Change visibility` so
`docker pull` works without a login.

### Operator-shell service control (polkit)

The systemd unit runs with `NoNewPrivileges=true`, which blocks `sudo`/setuid escalation — so the
admin-only operator Shell console (running as the `anthill` service user) can't `sudo systemctl
restart`. `setup.sh` therefore installs a **scoped polkit rule**
(`/etc/polkit-1/rules.d/49-anthill.rules`, from `deploy/lxc/anthill-polkit.rules.template`) that
authorizes the service user to manage **only** the `anthill.service` unit (restart/stop/start/
status) over D-Bus — systemd performs the action, so no privilege escalation is needed and the
unit's hardening is untouched. The Shell tab's "Restart service", "Service status", and "Recent
logs" buttons use it. Nothing else is granted — no other units, no package or system management;
upgrades still run `bash deploy/lxc/setup.sh` from a root shell. If polkit isn't installed the rule
is skipped (the installer says so) and service control from the console won't be available.

## 5. Windows Service (ready — refinements ongoing)

Windows deployment is ready to use today: `README.md` documents registering the published
`anthill.exe` directly via `New-Service`/`sc.exe` (Deploy on Windows → Option C) — start, stop,
and automatic startup all work. Like the other deployment paths, it's under continuous
improvement. The current refinement target — graceful shutdown on service stop, correct
integration with the Service Control Manager's startup/shutdown timeouts, Windows Event Log
output instead of a console — is wiring up
[`Microsoft.Extensions.Hosting.WindowsServices`](https://learn.microsoft.com/dotnet/core/extensions/windows-service)
(`.UseWindowsService()` on the host builder in `src/Anthill.Api/ApiHost.cs`, plus a new
`Microsoft.Extensions.Hosting.WindowsServices` package reference). That package restore needs to
happen on a machine with real NuGet access; this doc will be updated with the install script once
it's built and verified.

## 6. Roadmap

| Target | Status | Key files |
|--------|--------|-----------|
| **Container-style IP binding + env var overrides** ✅ | **DONE.** Underpins all three targets below. | `NetworkUtil.cs`, `AnthillConfig.cs`, `AnthillRuntime.cs`, `Anthill.Cli/Program.cs`, `Anthill.Api/ApiHost.cs` |
| **Docker** ✅ | **DONE.** `Dockerfile`, `docker-compose.yml`, `.dockerignore` at repo root. | see §2 above |
| **LXC** ✅ | **DONE.** `deploy/lxc/setup.sh` + `anthill.service.template`. | see §3 above |
| **Tagged releases** ✅ | **DONE.** Binaries + Docker image (GHCR) + published GitHub Release, all automatic on tag push. | `.github/workflows/release.yml`, see §4 above |
| **Windows Service** ✅ | **READY** via `New-Service`/`sc.exe` registration (see §5). Refinement in progress: `UseWindowsService()` SCM integration + install script. | `Microsoft.Extensions.Hosting.WindowsServices` integration in `ApiHost.cs`, install script |

Implementation order: container-style networking (done) → Docker (done) → LXC (done) → Windows
Service (ready; SCM refinements ongoing), per the agreed build order.
