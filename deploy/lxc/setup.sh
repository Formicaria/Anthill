#!/usr/bin/env bash
# ANTHILL — unattended LXC / systemd install script.
#
# Run as root inside a fresh Debian 12+ or Ubuntu 22.04+ LXC container (Proxmox-created or
# otherwise — anything with systemd and apt). See docs/DEPLOYMENT.md §3 for the full walkthrough,
# including how to create the container itself.
#
# What it does: installs the .NET 9 SDK if missing, publishes ANTHILL as a self-contained
# linux-x64 binary, creates a dedicated unprivileged service user, installs + enables a systemd
# unit, and starts it. Safe to re-run — re-running pulls the latest source (if it manages the
# checkout itself), republishes, and restarts the service, so this doubles as the upgrade path.
#
# Env vars (all optional):
#   ANTHILL_REPO_URL      git remote to clone if not already run from inside a checkout
#                         (default: https://github.com/thexonexone/operation-anthill.git)
#   ANTHILL_INSTALL_DIR   base directory for the install (default: /opt/anthill)
#   ANTHILL_SERVICE_USER  dedicated system user the service runs as (default: anthill)

set -euo pipefail

REPO_URL="${ANTHILL_REPO_URL:-https://github.com/thexonexone/operation-anthill.git}"
INSTALL_DIR="${ANTHILL_INSTALL_DIR:-/opt/anthill}"
SERVICE_USER="${ANTHILL_SERVICE_USER:-anthill}"

log() { echo "==> $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "Run this as root (inside the container: 'pct enter <vmid>' on Proxmox, then run this script, or 'pct exec <vmid> -- bash $0')."
command -v apt-get >/dev/null 2>&1 || die "This script targets Debian/Ubuntu-family containers (needs apt-get). For other distros, follow the manual steps in README.md 'Deploy on Linux' instead."

# ---------------------------------------------------------------------------
log "Checking for the .NET 9 SDK"
# ---------------------------------------------------------------------------
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^9\.'; then
    echo "    dotnet 9 SDK already present — skipping install."
else
    apt-get update -qq
    . /etc/os-release
    PKG_URL="https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb"
    if curl -fsSL "$PKG_URL" -o /tmp/packages-microsoft-prod.deb 2>/dev/null; then
        log "Installing .NET 9 SDK via Microsoft's apt repository (${ID} ${VERSION_ID})"
        dpkg -i /tmp/packages-microsoft-prod.deb
        rm -f /tmp/packages-microsoft-prod.deb
        apt-get update -qq
        apt-get install -y dotnet-sdk-9.0
    else
        # No Microsoft apt repo entry for this exact distro/version (common on very new or
        # unusual releases) — fall back to the official install script, which works on any
        # glibc Linux regardless of distro. Alpine/musl-based templates are NOT supported by
        # either path; use a Debian/Ubuntu template instead.
        log "No Microsoft apt repo for ${ID} ${VERSION_ID} — falling back to dotnet-install.sh"
        apt-get install -y curl ca-certificates
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet
        ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
        rm -f /tmp/dotnet-install.sh
    fi
fi
dotnet --info | head -5

# ---------------------------------------------------------------------------
log "Fetching ANTHILL source"
# ---------------------------------------------------------------------------
if [ -f "./Anthill.sln" ]; then
    SRC_DIR="$(pwd)"
    echo "    Running from an existing checkout: $SRC_DIR"
else
    command -v git >/dev/null 2>&1 || apt-get install -y git
    mkdir -p "$INSTALL_DIR"
    if [ -d "$INSTALL_DIR/src/.git" ]; then
        log "Existing checkout found — pulling latest"
        git -C "$INSTALL_DIR/src" pull --ff-only
    else
        log "Cloning $REPO_URL"
        git clone "$REPO_URL" "$INSTALL_DIR/src"
    fi
    SRC_DIR="$INSTALL_DIR/src"
fi

# ---------------------------------------------------------------------------
log "Publishing self-contained linux-x64 binary"
# ---------------------------------------------------------------------------
# Stop the service first if this is an upgrade (re-run on an already-installed instance): the
# publish step below overwrites the binary in place at $BIN_DIR/anthill, and .NET's single-file
# bundler does an in-place file copy rather than write-then-atomic-rename. On Linux, opening a
# currently-executing binary for direct write access fails with ETXTBSY ("text file busy") — you
# can replace a running program's file via rename, but not overwrite it in place while it's
# executing. No-op (and harmless) on a first-ever install, since the unit doesn't exist yet.
systemctl stop anthill 2>/dev/null || true

# Wipe obj/bin for the three published projects before every publish. A `dotnet publish` that
# reuses obj/ incremental state left over from a previous run — especially one that was
# interrupted partway (e.g. the ETXTBSY case above, which used to kill the process mid-bundle) —
# can decide native runtime assets (like SQLitePCLRaw's e_sqlite3.so) are already "up to date"
# and skip copying them into the output directory, even though they're not actually there. That
# produces a binary that builds and runs the systemd unit fine but immediately SIGABRTs the
# instant it touches SQLite: `DllNotFoundException: Unable to load shared library 'e_sqlite3'`.
# A full clean makes every install/upgrade a from-scratch publish — a few extra seconds, but a
# script meant to be re-run unattended for upgrades needs to be correct more than it needs to be
# fast.
log "Cleaning previous build output (avoids stale incremental state from a prior interrupted publish)"
rm -rf \
    "$SRC_DIR/src/Anthill.Cli/obj" "$SRC_DIR/src/Anthill.Cli/bin" \
    "$SRC_DIR/src/Anthill.Core/obj" "$SRC_DIR/src/Anthill.Core/bin" \
    "$SRC_DIR/src/Anthill.Api/obj" "$SRC_DIR/src/Anthill.Api/bin"

BIN_DIR="$INSTALL_DIR/bin"
dotnet publish "$SRC_DIR/src/Anthill.Cli/Anthill.Cli.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=none \
    -o "$BIN_DIR"
chmod +x "$BIN_DIR/anthill"

# Verify the SQLite native library actually landed — this is the exact failure mode being
# guarded against above, so fail loudly here instead of leaving it to a silent SIGABRT loop
# under systemd.
if ! ls "$BIN_DIR"/*e_sqlite3* >/dev/null 2>&1; then
    die "Publish completed but no e_sqlite3 native library was found in $BIN_DIR — SQLite will fail to load at runtime. Try re-running this script; if it persists, run 'dotnet nuget locals all --clear' and retry."
fi

# ---------------------------------------------------------------------------
log "Creating dedicated service user + data directory"
# ---------------------------------------------------------------------------
id -u "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --shell /usr/sbin/nologin --home-dir "$INSTALL_DIR" "$SERVICE_USER"
mkdir -p "$INSTALL_DIR/.anthill"
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

# ---------------------------------------------------------------------------
log "Generating a static API token (optional — the web UI creates an admin account regardless)"
# ---------------------------------------------------------------------------
mkdir -p /etc/anthill
TOKEN_FILE="/etc/anthill/token.env"
if [ ! -f "$TOKEN_FILE" ]; then
    echo "ANTHILL_API_TOKEN=$(openssl rand -hex 24)" > "$TOKEN_FILE"
    chmod 600 "$TOKEN_FILE"
    chown root:"$SERVICE_USER" "$TOKEN_FILE"
    chmod 640 "$TOKEN_FILE"
fi

# ---------------------------------------------------------------------------
log "Installing systemd unit"
# ---------------------------------------------------------------------------
sed \
    -e "s|__INSTALL_DIR__|$INSTALL_DIR|g" \
    -e "s|__SERVICE_USER__|$SERVICE_USER|g" \
    "$SRC_DIR/deploy/lxc/anthill.service.template" > /etc/systemd/system/anthill.service

systemctl daemon-reload
systemctl enable anthill >/dev/null
systemctl restart anthill

sleep 2
echo
log "Done. Service status:"
systemctl status anthill --no-pager || true
echo
echo "Reachable URL is printed in the service log — check it with:"
echo "    journalctl -u anthill -n 20 --no-pager"
echo
echo "Re-run this script any time to pull the latest source, republish, and restart the service."
