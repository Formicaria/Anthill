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

# ── ANTHILL banner — shown on every install / upgrade run ──
_anthill_banner="$(cd "$(dirname "$0")/.." 2>/dev/null && pwd)/anthill-banner.txt"
[ -f "$_anthill_banner" ] && cat "$_anthill_banner"

[ "$(id -u)" -eq 0 ] || die "Run this as root (inside the container: 'pct enter <vmid>' on Proxmox, then run this script, or 'pct exec <vmid> -- bash $0')."
command -v apt-get >/dev/null 2>&1 || die "This script targets Debian/Ubuntu-family containers (needs apt-get). For other distros, follow the manual steps in README.md 'Deploy on Linux' instead."

# ---------------------------------------------------------------------------
log "Checking for the .NET 9 SDK"
# ---------------------------------------------------------------------------
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^9\.'; then
    echo "    dotnet 9 SDK already present — skipping install."
else
    apt-get update -qq
    # shellcheck disable=SC1091  # /etc/os-release only exists at runtime; nothing to follow statically
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
log "Provisioning the auto-apply workspace, git identity, and deploy-key slot"
# ---------------------------------------------------------------------------
# The autonomous auto-apply loop (Settings → Security → Auto-Apply) verifies a patch, then can
# commit it to a standalone branch "<github-username>-anthill" and push via an SSH deploy key.
# For that to work unattended on a locked-down systemd host, four things must already be in place —
# exactly the steps that previously had to be done by hand on the container:
#   1. a git checkout the agent can WRITE to. The agent workspace defaults to
#      $INSTALL_DIR/.anthill/workspace, which already sits under the unit's ReadWritePaths=.anthill,
#      so it is writable out of the box — we just make it an actual clone so git-commit has a repo.
#   2. a git identity for the service user, so `git commit` never fails with "Please tell me who you
#      are" (the app also sets an inline identity; this also covers any manual/first-run git).
#   3. a safe.directory entry, so git won't refuse a tree with "dubious ownership" after a root clone.
#   4. a private ~/.ssh slot (700) for the deploy key referenced BY PATH in the config. The key
#      itself is NEVER generated or stored by this script — the operator drops it in and points the
#      config at it (Settings → Security → Auto-Apply → "SSH deploy key path").
WORKSPACE_DIR="$INSTALL_DIR/.anthill/workspace"
if [ ! -d "$WORKSPACE_DIR/.git" ]; then
    if [ -z "$(ls -A "$WORKSPACE_DIR" 2>/dev/null)" ]; then
        command -v git >/dev/null 2>&1 || apt-get install -y git
        log "Cloning a writable agent workspace at $WORKSPACE_DIR"
        git clone "$REPO_URL" "$WORKSPACE_DIR"
    else
        echo "    WARNING: $WORKSPACE_DIR exists and is not a git checkout — leaving it untouched. Auto-apply git-commit needs a git repo here; move or clear it and re-run to enable that path."
    fi
else
    git -C "$WORKSPACE_DIR" remote set-url origin "$REPO_URL" 2>/dev/null || true
fi

# If the operator has already set a GitHub username (an upgrade re-run), make sure the standalone
# branch exists and is checked out — the app refuses to auto-commit unless the workspace is already
# on it, and never on main. On a first-ever install the username isn't known yet, so the workspace
# stays on the default branch until the operator configures it and re-runs this script.
CONFIG_JSON="$INSTALL_DIR/.anthill/config.json"
GH_USER=""
if [ -f "$CONFIG_JSON" ]; then
    GH_USER="$(grep -oE '"autonomy_autoapply_git_username"[[:space:]]*:[[:space:]]*"[^"]*"' "$CONFIG_JSON" | sed -E 's/.*"([^"]*)"[[:space:]]*$/\1/')"
fi
if [ -n "$GH_USER" ] && [ -d "$WORKSPACE_DIR/.git" ]; then
    BRANCH="${GH_USER}-anthill"
    log "Ensuring standalone auto-apply branch '$BRANCH' is checked out in the workspace"
    git -C "$WORKSPACE_DIR" fetch origin 2>/dev/null || true
    git -C "$WORKSPACE_DIR" checkout -B "$BRANCH" 2>/dev/null || true
fi

# Run a git command as the service user, tolerating hosts without `runuser` (fall back to `su`).
run_as_service() { runuser -u "$SERVICE_USER" -- "$@" 2>/dev/null || su -s /bin/sh "$SERVICE_USER" -c "$(printf '%q ' "$@")" 2>/dev/null || true; }
run_as_service git config --global user.name  "ANTHILL Auto-Apply"
run_as_service git config --global user.email  "anthill@localhost"
[ -d "$WORKSPACE_DIR/.git" ] && run_as_service git config --global --add safe.directory "$WORKSPACE_DIR"

# Private deploy-key slot — directory + perms only; the key is provided by the operator, never here.
SSH_DIR="$INSTALL_DIR/.ssh"
mkdir -p "$SSH_DIR"
chmod 700 "$SSH_DIR"
if [ ! -e "$SSH_DIR/README" ]; then
    cat > "$SSH_DIR/README" <<'SSHREADME'
Drop a repo-scoped SSH *deploy key* here (e.g. anthill_deploy) to let auto-apply PUSH the standalone
branch. Generate it on your workstation, add the .pub as a WRITE-enabled deploy key on the GitHub
repo, then copy the PRIVATE key here (chmod 600) and set its path in
Settings → Security → Auto-Apply → "SSH deploy key path" (and enable "Push branch to origin").
The key never leaves this host and is referenced by path only — it is never stored in the app config.

  ssh-keygen -t ed25519 -f anthill_deploy -C anthill-deploy   # run on your workstation
SSHREADME
fi
find "$SSH_DIR" -maxdepth 1 -type f -exec chmod 600 {} \; 2>/dev/null || true
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR/.anthill" "$SSH_DIR"

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

# ---------------------------------------------------------------------------
log "Installing polkit + scoped rule for operator-shell service control"
# ---------------------------------------------------------------------------
# NoNewPrivileges=true blocks sudo, so the admin-only operator Shell manages the service over
# D-Bus via polkit. Install polkit natively (like the .NET SDK) so `systemctl restart anthill`
# works from the console out of the box. Package name varies by distro; try newest first.
if ! command -v pkaction >/dev/null 2>&1 && [ ! -d /etc/polkit-1 ]; then
    apt-get update -qq || true
    apt-get install -y polkitd 2>/dev/null \
        || apt-get install -y polkit 2>/dev/null \
        || apt-get install -y policykit-1 2>/dev/null \
        || echo "    WARNING: could not install polkit via apt — operator-shell service control will be unavailable."
fi

if [ -d /etc/polkit-1 ]; then
    # Modern polkit (>= 0.106, e.g. Debian 12): JS rule scoped to ONLY the anthill.service unit.
    mkdir -p /etc/polkit-1/rules.d
    sed -e "s|__SERVICE_USER__|$SERVICE_USER|g" \
        "$SRC_DIR/deploy/lxc/anthill-polkit.rules.template" > /etc/polkit-1/rules.d/49-anthill.rules
    # Legacy polkit (0.105, e.g. Ubuntu 22.04) ignores JS rules — provide a .pkla fallback too.
    # .pkla can only scope by action (not per-unit), so it's a touch broader; the JS rule above
    # takes precedence wherever it's supported.
    mkdir -p /etc/polkit-1/localauthority/50-local.d
    cat > /etc/polkit-1/localauthority/50-local.d/49-anthill.pkla <<PKLA
[Allow ANTHILL service user to manage systemd units]
Identity=unix-user:$SERVICE_USER
Action=org.freedesktop.systemd1.manage-units
ResultActive=yes
ResultInactive=yes
ResultAny=yes
PKLA
    systemctl enable --now polkit 2>/dev/null || systemctl enable --now polkitd 2>/dev/null || true
    systemctl try-restart polkit 2>/dev/null || systemctl try-restart polkitd 2>/dev/null || true
    echo "    polkit rule installed — the operator Shell can now restart/stop/start the service."
else
    echo "    polkit unavailable — operator-shell service control (systemctl restart anthill) will not work on this host."
fi

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
