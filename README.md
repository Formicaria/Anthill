# ANTHILL

[![CI](https://github.com/Formicaria/Anthill/actions/workflows/ci.yml/badge.svg)](https://github.com/Formicaria/Anthill/actions/workflows/ci.yml)

**Current version:** v0.3.8.114

**Runs on:** Windows or Linux

**Web interface:** `http://localhost:8713/ui`

ANTHILL is a self-hosted AI workspace built around a simple idea: give the colony a goal, let the
Queen organize the work, and have specialized roles research, inspect, build, test, review, and
report back.

You use it through a browser, much like a normal AI chat. The difference is that a request can
become a structured mission with a visible task trail, safety checks, proposed file changes, and a
result you can inspect before anything is applied.

ANTHILL runs on your own hardware and keeps its history in a local SQLite database. The easiest
model setup is [Ollama](https://ollama.com/), although external model providers can also be added
after installation.

> **New here?** Start with the Windows or Linux instructions below. You do not need to edit a
> configuration file, build the source, or understand the colony architecture to get the first
> mission running.

## Pick the install that fits you

| Your setup | Best choice |
| --- | --- |
| Windows desktop | [Windows quick start](#windows-quick-start) |
| Linux desktop or a quick test server | [Linux quick start](#linux-quick-start) |
| Proxmox or an always-on Debian/Ubuntu server | [LXC and systemd install](#lxc-and-systemd-install) |
| Linux host where you already use Docker | [Docker](#docker) |
| You want to change ANTHILL itself | [Build from source](#build-from-source) |

## Before you install

ANTHILL is the application. The AI model normally runs through Ollama, either on the same computer
or on another machine on your network.

For the simplest first setup, you need:

- A 64-bit Windows or Linux computer
- A web browser
- Ollama with at least one chat model installed
- About 10 GB of free disk space for ANTHILL, its data, and a small model
- 8 GB of system RAM at minimum; 16 GB or more is much more comfortable

The model is what uses most of the RAM and GPU memory. ANTHILL itself is comparatively light. A GPU
is helpful but not required for a small Ollama model.

This guide uses `llama3.1:8b` as an approachable starter model. It is not hardcoded into ANTHILL,
and you can choose a different model that better fits your hardware.

## Windows quick start

**The desktop app.** `AnthillDesktop.exe` — included in the Windows download below — is ANTHILL
as a native Windows application: the same colony and the same console the server install runs, in
its own window instead of a browser tab. Double-click it and it boots the colony in-process
(bound to this computer only) and opens the console; if an ANTHILL server is already running on
this machine it attaches to that one instead of starting a second colony. If anything goes wrong
it says so in the window, and the full story is in `%LOCALAPPDATA%\Anthill\desktop.log`. It needs
the Microsoft Edge WebView2 Runtime, which Windows 11 and updated Windows 10 already include
(otherwise: [aka.ms/webview2](https://aka.ms/webview2)).

Using the desktop app? Do steps 1–2 below, then just run `AnthillDesktop.exe` — steps 3–4 are the
browser-based server route.

### 1. Install Ollama

Download and install [Ollama for Windows](https://ollama.com/download/windows).

Open **PowerShell** and download one model:

```powershell
ollama pull llama3.1:8b
```

If PowerShell says `ollama` is not recognized, close PowerShell, open it again, and retry.

### 2. Install ANTHILL

Download `anthill-setup-<version>.exe` from the
[latest release](https://github.com/Formicaria/Anthill/releases/latest) and run it. It walks
the normal Windows steps: license agreement, install location (Program Files by default), a
desktop icon (on by default — untick it in the same screen if you don't want one), a Start
Menu entry, and a standard uninstaller. When it finishes, hit the desktop icon.

Updates prompt inside the app and install the same way. Your colony's memory and settings
live under `%LOCALAPPDATA%\Anthill` and survive every update, reinstall, and uninstall.

Prefer a portable copy instead? The
[`anthill-<version>-win-x64.zip`](https://github.com/Formicaria/Anthill/releases/latest)
still exists — extract it somewhere permanent, such as:

```text
C:\Anthill
```

Either download already contains the .NET runtime. You do not need to install the .NET SDK.

### 3. Start ANTHILL

Open the extracted folder, right-click an empty area, and choose **Open in Terminal**. Then run:

```powershell
.\anthill.exe --api --host 127.0.0.1
```

Keep that terminal open while ANTHILL is running.

If Windows SmartScreen appears, make sure the file came from the official release link above
before choosing **More info â†’ Run anyway**.

### 4. Open the colony

Go to:

```text
http://localhost:8713/ui
```

Create the first administrator account, sign in, and continue to
[Your first mission](#your-first-mission).

The command above keeps ANTHILL local to that computer. If you later want to reach it from another
device on your private network, start it with `--host 0.0.0.0` and use the LAN address printed in
the terminal.

## Linux quick start

These instructions use the prebuilt release, so the .NET SDK is not required.

### 1. Install Ollama and a model

```bash
curl -fsSL https://ollama.com/install.sh | sh
ollama pull llama3.1:8b
```

### 2. Download and unpack ANTHILL

```bash
mkdir -p "$HOME/anthill"
cd "$HOME/anthill"
curl -fLO https://github.com/Formicaria/Anthill/releases/download/v0.3.8.114/anthill-0.3.8.114-linux-x64.tar.gz
tar --no-same-owner -xzf anthill-0.3.8.114-linux-x64.tar.gz
chmod +x anthill
```

### 3. Start it

```bash
./anthill --api --host 127.0.0.1
```

Open `http://localhost:8713/ui`, create the first administrator account, and continue to
[Your first mission](#your-first-mission).

For a headless server that should be available on your private network, use:

```bash
./anthill --api --host 0.0.0.0
```

Then open the LAN URL printed at startup. Do not expose port `8713` directly to the public internet.

## LXC and systemd install

This is the easiest always-on installation for Proxmox or a dedicated Debian/Ubuntu machine. The
installer creates an unprivileged `anthill` service account, builds ANTHILL, starts it with systemd,
and keeps its data across upgrades.

For a Proxmox LXC, a practical starting size is:

- Debian 12 or Ubuntu 22.04/24.04
- Unprivileged container
- 2 CPU cores
- 4 GB RAM
- 16 GB disk
- DHCP or a reserved LAN address

That is enough for ANTHILL itself. Ollama will usually run on a separate machine with more memory or
a GPU.

Open the container console as `root` and run:

```bash
apt-get update && apt-get install -y curl ca-certificates git
curl -fsSL https://raw.githubusercontent.com/Formicaria/Anthill/main/deploy/lxc/setup.sh -o /tmp/anthill-setup.sh
bash /tmp/anthill-setup.sh
```

Check that the service started:

```bash
systemctl status anthill --no-pager
journalctl -u anthill -n 30 --no-pager
```

The log prints the URL to open from another computer. Create the administrator account there.

If Ollama is on another machine, sign in and set its address under **Settings â†’ Colony â†’ Ollama
Host**, for example:

```text
http://192.168.1.50:11434
```

Ollama must be listening on the network, and your firewall must allow the ANTHILL machine to reach
port `11434`. See [Using Ollama on another machine](#using-ollama-on-another-machine).

The complete Proxmox, systemd, Docker, and Windows-service notes live in
[`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).

## Docker

Use this route if you are already comfortable with Docker. The included Compose file is designed
for a **Linux Docker host** and uses host networking so a local Ollama service works without extra
container networking.

```bash
git clone https://github.com/Formicaria/Anthill.git
cd Anthill
docker compose up -d --build
docker compose logs -f anthill
```

Open the URL shown in the logs. ANTHILL's database and configuration are stored in the named
`anthill-data` volume.

Docker Desktop on Windows and macOS needs bridge networking instead of the shipped host-network
configuration. Follow the bridge-mode example in [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).

## Your first mission

### 1. Confirm the model is ready

If Ollama has exactly one model installed, ANTHILL uses it automatically. If you have several,
ANTHILL will ask you to choose instead of guessing.

To select one manually:

1. Open **Settings**.
2. Choose the **Colony** tab.
3. Enter the exact model name, such as `llama3.1:8b`.
4. Click **Save Colony Settings**.

You can see installed model names with:

```bash
ollama list
```

### 2. Send a simple message

Open **Chat** and try:

```text
Explain how this colony processes a mission. Keep the answer short.
```

ANTHILL should create a mission, route the work, and return an answer in the conversation. The
mission details are there when you want them, but you do not need to understand every internal
event to use Chat.

### 3. Give it a project to work with

ANTHILL can only inspect files inside its configured workspace boundary.

1. Put the project in Git so you can restore it if needed.
2. In ANTHILL, open **Security â†’ Workspace Boundary**.
3. Set `agent_workspace_dir` to the absolute path of the project.
4. Save the setting.

For example:

```text
C:\Users\you\source\my-project
```

or:

```text
/home/you/source/my-project
```

For Docker, the project must also be bind-mounted into the container. The example is in
[`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).

Start with a read-only mission:

```text
Inspect this project and explain what it does. Do not change any files.
```

## What is enabled on a new install?

The colony ships with the full twelve-role roster available. Roles still run only when a
mission actually needs them; enabling the full roster does not force every role into every mission.

These are the fresh-install defaults that matter most:

| Area | Fresh-install behavior |
| --- | --- |
| Colony roster | `full` â€” all twelve roles are available, with per-role kill switches |
| Local model | Unchosen; the only installed Ollama model is selected automatically, otherwise ANTHILL asks |
| File access | Read tools are on, limited to `.anthill/workspace` until you choose another boundary |
| Web, AI shell, writes, and patch application | Off |
| Autonomy, auto-apply, homelab, and container execution | Off |
| Operator Shell | On for administrators; disable it under **Security** if you do not need a host terminal |
| Network bind | `0.0.0.0` by default; the desktop commands in this guide override it to `127.0.0.1` |

For colony-run missions, the `SAFE_LOCAL` safety profile keeps web search, the ant shell tool, file
writing, patch application, and unattended auto-apply closed until you deliberately enable them.
The read-only file tool remains available inside the workspace boundary.

Before allowing repository changes:

- Use a Git repository with a clean backup or remote.
- Keep the workspace boundary as narrow as possible.
- Review proposed changes and verification evidence.
- Leave auto-apply off until you have tested the full flow on a disposable project.
- Disable the admin Operator Shell if you do not need a browser-accessible terminal.

ANTHILL is still pre-1.0 software under active development. It has deliberate safety gates, but it
should not be trusted with irreplaceable files or unattended production changes. The measured
current state and known gaps are kept in [`docs/PLAN.md`](docs/PLAN.md).

## Using Ollama on another machine

On the Ollama machine, make Ollama listen on the network.

Linux:

```bash
sudo systemctl edit ollama
```

Add:

```ini
[Service]
Environment="OLLAMA_HOST=0.0.0.0:11434"
```

Then restart it:

```bash
sudo systemctl daemon-reload
sudo systemctl restart ollama
```

From the ANTHILL machine, confirm it is reachable:

```bash
curl http://OLLAMA_MACHINE_IP:11434/api/tags
```

Finally, set **Settings â†’ Colony â†’ Ollama Host** to:

```text
http://OLLAMA_MACHINE_IP:11434
```

Only expose Ollama to a trusted private network or protect it with an appropriate network boundary.

## Where ANTHILL keeps its data

ANTHILL creates its configuration automatically on first launch. You do **not** need to copy or edit
`config.example.json` to get started.

| Installation | Data location |
| --- | --- |
| Windows or Linux release archive | `.anthill` inside the folder you launch ANTHILL from |
| Source checkout | `<repo>/.anthill` |
| LXC installer | `/opt/anthill/.anthill` |
| Docker | The `anthill-data` volume, mounted at `/app/.anthill` |

That directory contains the database, configuration, logs, backups, exports, workspace, and local
encryption material. Back it up before upgrading or moving the installation. Do not publish it or
commit it to Git.

Most settings are easier and safer to change through the web interface. The generated configuration
file is `.anthill/config.json` if you need it for advanced deployment work.

Useful launch overrides:

| Option | What it changes |
| --- | --- |
| `--host 127.0.0.1` | Only this computer can open ANTHILL |
| `--host 0.0.0.0` | Devices on the private network can open it |
| `--port 8714` | Uses a different web port |
| `--ollama-host http://IP:11434` | Uses Ollama on another machine |
| `--ollama-model model:tag` | Selects a specific local model |

## Updating

Back up the `.anthill` data directory first.

### Windows or Linux release archive

1. Stop ANTHILL.
2. Download the newest archive from [GitHub Releases](https://github.com/Formicaria/Anthill/releases/latest).
3. Replace the program files with the files from the new archive.
4. Keep the existing `.anthill` directory.
5. Start ANTHILL again.

Database and configuration migrations run automatically at startup.

### LXC / systemd

```bash
cd /opt/anthill/src
git pull --ff-only
bash deploy/lxc/setup.sh
```

### Docker deployment

```bash
git pull --ff-only
docker compose up -d --build
```

The `anthill-data` volume remains in place.

### Source checkout

```bash
git pull --ff-only
dotnet build Anthill.sln -c Release
dotnet test Anthill.sln -c Release --no-build
```

## Troubleshooting

### The web page does not open

- Make sure the ANTHILL process is still running.
- Use `http://localhost:8713/ui` on the same computer.
- On another device, use the LAN URL printed at startup.
- Do not enter `http://0.0.0.0:8713`; `0.0.0.0` is a listening address, not a browser address.
- If port `8713` is busy, restart with `--port 8714` and open that port instead.
- For LAN access, make sure the host firewall allows the selected port on private networks.

### Ollama is unreachable or no model is selected

Check Ollama locally:

```bash
ollama list
curl http://localhost:11434/api/tags
```

If Ollama is on another machine, replace `localhost` with its IP address. If several models are
installed, choose one under **Settings â†’ Colony**.

### A mission cannot read the project

- Confirm **Security â†’ Workspace Boundary** points to the project's absolute path.
- Confirm the ANTHILL user has permission to read that directory.
- For Docker, confirm the directory is mounted inside the container.
- For an external coding agent, confirm its working directory is still inside the same boundary.

### Reset a broken configuration without deleting mission history

Stop ANTHILL and rename `.anthill/config.json` to `config.json.bak`. Start ANTHILL again and it will
create a fresh configuration. Your SQLite database remains in `.anthill/anthill.db`.

### Find the logs

LXC / systemd:

```bash
journalctl -u anthill -n 100 --no-pager
```

Docker:

```bash
docker compose logs --tail 100 anthill
```

Portable Windows or Linux installs print startup and runtime errors in the terminal where ANTHILL
was started.

If you open a [bug report](https://github.com/Formicaria/Anthill/issues/new/choose), include
your ANTHILL version, operating system, installation method, and the relevant error text. Remove API
keys, tokens, passwords, webhook URLs, and other secrets first.

## Command-line checks

Run these from the folder containing `anthill` or `anthill.exe`.

Linux:

```bash
./anthill --version
./anthill --selftest
./anthill --status
```

Windows PowerShell:

```powershell
.\anthill.exe --version
.\anthill.exe --selftest
.\anthill.exe --status
```

Run `anthill --help` for the complete command list.

## Build from source

You only need this section if you are developing ANTHILL or want to build your own binary.

Requirements:

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git
- Optional: CMake and a C++20 compiler for the native kernel

The native kernel is optional. Without a C++ toolchain, ANTHILL uses the managed C# implementation.

Clone and run:

```bash
git clone https://github.com/Formicaria/Anthill.git
cd Anthill
dotnet run --project src/Anthill.Cli -- --api --host 127.0.0.1
```

Run the full validation and publish flow:

Linux:

```bash
./build.sh
```

Windows PowerShell:

```powershell
.\build.ps1
```

Run only the tests:

```bash
dotnet test Anthill.sln -c Release
```

## How the repository is organized

```text
src/Anthill.Cli/          Command-line entry point
src/Anthill.Api/          Web API and runtime host
src/Anthill.Core/         Queen, mission flow, memory, policy, and domain logic
src/Anthill.Modules/      Reasoning, tools, and homelab integrations
src/Anthill.SDK/          Shared contracts for modules and tools
src/Anthill.UI/           Browser interface
tests/                    Automated test projects
deploy/lxc/               LXC and systemd installer
docs/                     Architecture, operations, and roadmap documentation
```

## Learn more

- [`docs/PLAN.md`](docs/PLAN.md) â€” what is working now and what is still missing
- [`docs/ANT_EXECUTION.md`](docs/ANT_EXECUTION.md) â€” canonical colony roles and execution gates
- [`docs/APPROVALS.md`](docs/APPROVALS.md) â€” patch and approval lifecycle
- [`docs/AUTONOMY.md`](docs/AUTONOMY.md) â€” Director, objectives, budgets, and stop controls
- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) â€” detailed deployment and service setup
- [`CHANGELOG.md`](CHANGELOG.md) â€” complete release history

## Current release

The shipped defaults make the full twelve-role roster available to new installations, confine
write-capable external agents to ANTHILL's workspace boundary, and ensures Archivist output exists
before the learning pass consumes it. Finalization steps are also recorded so they are not applied
twice during recovery.

That is the only release summary kept in this README. Older release notes belong in
[`CHANGELOG.md`](CHANGELOG.md).

## License

[Apache License 2.0](LICENSE)
