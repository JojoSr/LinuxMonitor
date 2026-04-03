#!/usr/bin/env bash
# Remove FortGuard.LinuxSystemMetrics.Api systemd service and installed files.
# Mirrors defaults from install-linux-service.sh.
#
# Usage:
#   sudo ./uninstall-linux-service.sh           # prompts for confirmation
#   sudo ./uninstall-linux-service.sh --yes     # non-interactive
#
# Environment (override if you changed them at install time):
#   INSTALL_DIR, SERVICE_NAME, RUN_AS_USER, LINUX_MONITOR_CLONE_DIR
#   SKIP_REMOVE_CLONE=1   Keep the git clone under /var/cache/...
#   PURGE_USER=1          Remove the fortguard-metrics system user (if unused)
set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/fortguard-linux-system-metrics}"
SERVICE_NAME="${SERVICE_NAME:-fortguard-linux-system-metrics}"
RUN_AS_USER="${RUN_AS_USER:-fortguard-metrics}"
LINUX_MONITOR_CLONE_DIR="${LINUX_MONITOR_CLONE_DIR:-/var/cache/fortguard-linux-system-metrics/LinuxMonitor}"
SKIP_REMOVE_CLONE="${SKIP_REMOVE_CLONE:-0}"
PURGE_USER="${PURGE_USER:-0}"

usage() {
  echo "Uninstall FortGuard Linux System Metrics API (systemd service)." >&2
  echo "Usage: $0 [--yes|-y]" >&2
  echo "  --yes, -y     Skip confirmation prompt" >&2
  echo "Environment:" >&2
  echo "  INSTALL_DIR=$INSTALL_DIR" >&2
  echo "  SERVICE_NAME=$SERVICE_NAME" >&2
  echo "  RUN_AS_USER=$RUN_AS_USER" >&2
  echo "  LINUX_MONITOR_CLONE_DIR=$LINUX_MONITOR_CLONE_DIR" >&2
  echo "  SKIP_REMOVE_CLONE=1   Do not delete the clone/build cache directory" >&2
  echo "  PURGE_USER=1          Run userdel on $RUN_AS_USER (only if safe for your system)" >&2
}

AUTO_YES=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes|-y)
      AUTO_YES=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown option: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ "${EUID:-0}" -ne 0 ]]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
  echo "error: systemctl not found; this script targets systemd-based Linux." >&2
  exit 1
fi

ENV_FILE="/etc/default/${SERVICE_NAME}"
UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"

echo "FortGuard Linux System Metrics — uninstall"
echo "  Service unit:     $UNIT_PATH"
echo "  Environment file: $ENV_FILE"
echo "  Install directory: $INSTALL_DIR"
echo "  Git clone dir:     $LINUX_MONITOR_CLONE_DIR (remove: $([[ "$SKIP_REMOVE_CLONE" == "1" ]] && echo no || echo yes))"
echo "  Remove user $RUN_AS_USER: $([[ "$PURGE_USER" == "1" ]] && echo yes || echo no)"
echo ""

if [[ "$AUTO_YES" != "1" ]]; then
  read -r -p "Continue? [y/N] " reply
  case "$reply" in
    [yY][eE][sS]|[yY]) ;;
    *)
      echo "Aborted."
      exit 0
      ;;
  esac
fi

if systemctl list-unit-files "${SERVICE_NAME}.service" &>/dev/null || [[ -f "$UNIT_PATH" ]]; then
  if systemctl is-active --quiet "${SERVICE_NAME}.service" 2>/dev/null; then
    echo "==> Stopping ${SERVICE_NAME}.service ..."
    systemctl stop "${SERVICE_NAME}.service" || true
  fi
  if systemctl is-enabled --quiet "${SERVICE_NAME}.service" 2>/dev/null; then
    echo "==> Disabling ${SERVICE_NAME}.service ..."
    systemctl disable "${SERVICE_NAME}.service" || true
  fi
fi

if [[ -f "$UNIT_PATH" ]]; then
  echo "==> Removing $UNIT_PATH"
  rm -f "$UNIT_PATH"
fi

systemctl daemon-reload 2>/dev/null || true

if [[ -f "$ENV_FILE" ]]; then
  echo "==> Removing $ENV_FILE"
  rm -f "$ENV_FILE"
fi

if [[ -d "$INSTALL_DIR" ]]; then
  echo "==> Removing $INSTALL_DIR"
  rm -rf "$INSTALL_DIR"
fi

if [[ "$SKIP_REMOVE_CLONE" != "1" && -d "$LINUX_MONITOR_CLONE_DIR" ]]; then
  echo "==> Removing clone/cache $LINUX_MONITOR_CLONE_DIR"
  rm -rf "$LINUX_MONITOR_CLONE_DIR"
  parent="$(dirname "$LINUX_MONITOR_CLONE_DIR")"
  if [[ -d "$parent" ]] && [[ -z "$(ls -A "$parent" 2>/dev/null)" ]]; then
    echo "==> Removing empty parent $parent"
    rmdir "$parent" 2>/dev/null || true
  fi
fi

if [[ "$PURGE_USER" == "1" ]]; then
  if id -u "$RUN_AS_USER" >/dev/null 2>&1; then
    echo "==> Removing user $RUN_AS_USER"
    userdel "$RUN_AS_USER" 2>/dev/null || userdel -r "$RUN_AS_USER" 2>/dev/null || true
  else
    echo "==> User $RUN_AS_USER does not exist (skipping userdel)"
  fi
else
  echo "==> Leaving system user $RUN_AS_USER (set PURGE_USER=1 to remove)"
fi

echo ""
echo "Uninstall finished."
echo "Note: If you installed .NET only for this service (e.g. via dotnet-install.sh to /usr/share/dotnet), remove it separately if no longer needed."
echo "      This script does not remove /usr/bin/dotnet or shared .NET installations."
