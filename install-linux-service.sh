#!/usr/bin/env bash
# Install FortGuard.LinuxSystemMetrics.Api as a systemd service (Debian/Ubuntu/Fedora/RHEL-style).
# Sources application code from: https://github.com/JojoSr/LinuxMonitor
#
# Listen port: pass as first argument, or --port N / -p N, or set LISTEN_PORT (or METRICS_PORT).
# API key (Bearer token): --api-key TOKEN / --auth-token TOKEN / -k TOKEN, or env AUTH_TOKEN.
# Example: sudo ./install-linux-service.sh 9100
#          sudo ./install-linux-service.sh --port 9100
#          sudo ./install-linux-service.sh --api-key 'YOUR_TOKEN' --port 9100
set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/fortguard-linux-system-metrics}"
SERVICE_NAME="${SERVICE_NAME:-fortguard-linux-system-metrics}"
RUN_AS_USER="${RUN_AS_USER:-fortguard-metrics}"
SELF_CONTAINED="${SELF_CONTAINED:-0}"

# GitHub repository (override to use a fork or mirror).
LINUX_MONITOR_REPO="${LINUX_MONITOR_REPO:-https://github.com/JojoSr/LinuxMonitor.git}"
# Branch or tag to deploy (default matches GitHub default branch).
GIT_REF="${GIT_REF:-main}"
# Where to clone or update the repo (must be writable by root).
LINUX_MONITOR_CLONE_DIR="${LINUX_MONITOR_CLONE_DIR:-/var/cache/fortguard-linux-system-metrics/LinuxMonitor}"

usage() {
  echo "Install FortGuard Linux System Metrics API from https://github.com/JojoSr/LinuxMonitor" >&2
  echo "Usage: $0 [OPTIONS] [PORT]" >&2
  echo "  PORT              Listen port (1-65535). Default: 8099" >&2
  echo "  -p, --port PORT   Same as positional PORT" >&2
  echo "  -k, --api-key TOKEN" >&2
  echo "  --auth-token TOKEN   Bearer token for /api/v1/* (written as Auth__Token; same as -k)" >&2
  echo "  -h, --help        Show this help" >&2
  echo "Environment:" >&2
  echo "  LISTEN_PORT, METRICS_PORT   Listen port (default 8099)" >&2
  echo "  AUTH_TOKEN                  Set Bearer token if not passed via --api-key (else keep existing or auto-generate)" >&2
  echo "  NO_AUTH=1                   Do not require auth (leave Auth__Token empty)" >&2
  echo "  SKIP_PREREQ_INSTALL=1       Skip automatic prerequisite installs (git, curl, .NET 8 SDK)" >&2
}

# --- prerequisites (install if missing) ------------------------------------

has_apt() { command -v apt-get >/dev/null 2>&1; }

pkg_update_and_install() {
  if has_apt; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq 2>/dev/null || apt-get update
    apt-get install -y "$@"
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y "$@"
  elif command -v yum >/dev/null 2>&1; then
    yum install -y "$@"
  elif command -v zypper >/dev/null 2>&1; then
    zypper --non-interactive install -y "$@"
  elif command -v apk >/dev/null 2>&1; then
    apk add --no-cache "$@"
  elif command -v pacman >/dev/null 2>&1; then
    pacman -Sy --noconfirm "$@"
  else
    echo "error: no supported package manager (apt-get, dnf, yum, zypper, apk, pacman)" >&2
    return 1
  fi
}

dotnet_8_sdk_present() {
  command -v dotnet >/dev/null 2>&1 || return 1
  dotnet --list-sdks 2>/dev/null | grep -qE '^8\.'
}

try_install_dotnet_sdk_via_distro() {
  if has_apt; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq 2>/dev/null || apt-get update
    apt-get install -y dotnet-sdk-8.0 || true
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y dotnet-sdk-8.0 || true
  elif command -v yum >/dev/null 2>&1; then
    yum install -y dotnet-sdk-8.0 || true
  elif command -v zypper >/dev/null 2>&1; then
    zypper --non-interactive install -y dotnet-sdk-8.0 || true
  elif command -v pacman >/dev/null 2>&1; then
    pacman -Sy --noconfirm dotnet-sdk || true
  elif command -v apk >/dev/null 2>&1; then
    apk add --no-cache dotnet8-sdk 2>/dev/null || apk add --no-cache dotnet-sdk-8 2>/dev/null || true
  fi
  dotnet_8_sdk_present
}

install_dotnet_sdk_via_microsoft_script() {
  local inst="${DOTNET_INSTALL_DIR:-/usr/share/dotnet}"
  local dl
  dl="$(mktemp)"
  echo "Installing .NET 8 SDK via Microsoft dotnet-install.sh -> $inst"
  install -d -m 0755 "$inst"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$dl"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$dl" https://dot.net/v1/dotnet-install.sh
  else
    echo "error: curl or wget required to download .NET SDK installer" >&2
    rm -f "$dl"
    return 1
  fi
  chmod +x "$dl"
  if ! "$dl" --channel 8.0 --install-dir "$inst"; then
    rm -f "$dl"
    echo "error: dotnet-install.sh failed." >&2
    return 1
  fi
  rm -f "$dl"
  ln -sf "$inst/dotnet" /usr/bin/dotnet
  export DOTNET_ROOT="$inst"
  export PATH="$inst:${PATH}"
  hash -r 2>/dev/null || true
  if ! dotnet_8_sdk_present; then
    echo "error: dotnet-install.sh finished but .NET 8 SDK is not on PATH." >&2
    return 1
  fi
  return 0
}

ensure_prerequisites() {
  echo "==> Checking prerequisites..."

  if ! command -v git >/dev/null 2>&1; then
    echo "==> Installing git..."
    pkg_update_and_install git || exit 1
  fi

  if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
    echo "==> Installing curl (or wget) and CA certificates..."
    pkg_update_and_install curl ca-certificates || pkg_update_and_install wget ca-certificates || exit 1
  fi

  if has_apt && command -v dpkg >/dev/null 2>&1 && ! dpkg -s ca-certificates >/dev/null 2>&1; then
    echo "==> Installing CA certificate bundle..."
    pkg_update_and_install ca-certificates || true
  elif command -v dnf >/dev/null 2>&1 && command -v rpm >/dev/null 2>&1 && ! rpm -q ca-certificates >/dev/null 2>&1; then
    dnf install -y ca-certificates || true
  fi

  if ! command -v openssl >/dev/null 2>&1; then
    echo "==> Installing openssl..."
    pkg_update_and_install openssl || true
  fi

  if dotnet_8_sdk_present; then
    echo "==> .NET 8 SDK already installed: $(command -v dotnet)"
    return 0
  fi

  echo "==> Installing .NET 8 SDK (trying distribution packages first)..."
  if try_install_dotnet_sdk_via_distro; then
    echo "==> .NET 8 SDK installed from distribution packages."
    return 0
  fi

  echo "==> Distribution packages unavailable or incomplete; using Microsoft install script..."
  if ! install_dotnet_sdk_via_microsoft_script; then
    echo "error: could not install .NET 8 SDK. Install manually:" >&2
    echo "  https://learn.microsoft.com/en-us/dotnet/core/install/linux" >&2
    echo "Or set SKIP_PREREQ_INSTALL=1 after installing the SDK yourself." >&2
    exit 1
  fi
  echo "==> .NET 8 SDK installed via dotnet-install.sh."
}

generate_api_token_hex() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 32
  else
    # shellcheck disable=SC2002
    head -c 32 /dev/urandom | base64 -w 0 2>/dev/null || head -c 32 /dev/urandom | base64 | tr -d '\n'
  fi
}

extract_auth_token_from_env_file() {
  local f="$1"
  local line v
  [[ -f "$f" ]] || { printf ''; return 0; }
  line=$(grep -E '^[[:space:]]*Auth__Token=' "$f" 2>/dev/null | tail -1) || true
  [[ -n "$line" ]] || { printf ''; return 0; }
  v="${line#*=}"
  v="$(printf '%s' "$v" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//;s/^"//;s/"$//;s/^'"'"'//;s/'"'"'$//')"
  printf '%s' "$v"
}

set_env_file_auth_token() {
  local file="$1" token="$2"
  local tmp replaced=0
  tmp="$(mktemp "${TMPDIR:-/tmp}/fortguard-env.XXXXXX")"
  if [[ -f "$file" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
      if [[ "$line" =~ ^[[:space:]]*Auth__Token= ]]; then
        printf 'Auth__Token=%s\n' "$token"
        replaced=1
      else
        printf '%s\n' "$line"
      fi
    done <"$file" >"$tmp"
  fi
  if [[ "$replaced" -eq 0 ]]; then
    printf 'Auth__Token=%s\n' "$token" >>"$tmp"
  fi
  mv "$tmp" "$file"
}

# Port: env LISTEN_PORT / METRICS_PORT, then CLI.
# API key from CLI wins over AUTH_TOKEN env (see token block below).
LISTEN_PORT="${LISTEN_PORT:-${METRICS_PORT:-8099}}"
INSTALL_API_KEY=""
POSITIONAL=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    -p|--port)
      [[ -n "${2:-}" ]] || { echo "error: $1 requires a port number" >&2; exit 1; }
      LISTEN_PORT="$2"
      shift 2
      ;;
    -k|--api-key|--auth-token)
      [[ -n "${2:-}" ]] || { echo "error: $1 requires a token value" >&2; exit 1; }
      INSTALL_API_KEY="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    -*)
      echo "error: unknown option: $1" >&2
      usage
      exit 1
      ;;
    *)
      POSITIONAL+=("$1")
      shift
      ;;
  esac
done
if [[ ${#POSITIONAL[@]} -gt 1 ]]; then
  echo "error: too many arguments: ${POSITIONAL[*]}" >&2
  usage
  exit 1
fi
if [[ ${#POSITIONAL[@]} -eq 1 ]]; then
  LISTEN_PORT="${POSITIONAL[0]}"
fi
if ! [[ "$LISTEN_PORT" =~ ^[0-9]+$ ]] || [[ "$LISTEN_PORT" -lt 1 || "$LISTEN_PORT" -gt 65535 ]]; then
  echo "error: invalid port: $LISTEN_PORT (use 1-65535)" >&2
  exit 1
fi
PORT="$LISTEN_PORT"

if [[ "${EUID:-0}" -ne 0 ]]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

SKIP_PREREQ_INSTALL="${SKIP_PREREQ_INSTALL:-0}"
if [[ "$SKIP_PREREQ_INSTALL" != "1" ]]; then
  ensure_prerequisites
else
  echo "==> SKIP_PREREQ_INSTALL=1 — skipping automatic prerequisite installation."
fi

if ! command -v git >/dev/null 2>&1; then
  echo "error: git is required. Install git or re-run without SKIP_PREREQ_INSTALL=1." >&2
  exit 1
fi

if ! dotnet_8_sdk_present; then
  echo "error: .NET 8 SDK not found. Install dotnet-sdk-8.0 or re-run without SKIP_PREREQ_INSTALL=1." >&2
  exit 1
fi

install -d -m 0755 "$(dirname "$LINUX_MONITOR_CLONE_DIR")"

if [[ -d "$LINUX_MONITOR_CLONE_DIR/.git" ]]; then
  echo "Updating clone: $LINUX_MONITOR_CLONE_DIR"
  git -C "$LINUX_MONITOR_CLONE_DIR" fetch --depth 1 origin "$GIT_REF"
  git -C "$LINUX_MONITOR_CLONE_DIR" reset --hard "origin/$GIT_REF"
else
  echo "Cloning $LINUX_MONITOR_REPO (ref: $GIT_REF) -> $LINUX_MONITOR_CLONE_DIR"
  rm -rf "$LINUX_MONITOR_CLONE_DIR"
  git clone --depth 1 --branch "$GIT_REF" "$LINUX_MONITOR_REPO" "$LINUX_MONITOR_CLONE_DIR"
fi

PROJECT_DIR="$LINUX_MONITOR_CLONE_DIR/FortGuard.LinuxSystemMetrics.Api"
if [[ ! -f "$PROJECT_DIR/FortGuard.LinuxSystemMetrics.Api.csproj" ]]; then
  echo "Expected project file missing: $PROJECT_DIR/FortGuard.LinuxSystemMetrics.Api.csproj" >&2
  echo "Check that branch '$GIT_REF' exists on the remote and contains the API project." >&2
  exit 1
fi

PUBLISH_DIR="$LINUX_MONITOR_CLONE_DIR/publish"
rm -rf "$PUBLISH_DIR"

if [[ "$SELF_CONTAINED" == "1" ]]; then
  echo "Publishing self-contained for linux-x64..."
  dotnet publish "$PROJECT_DIR/FortGuard.LinuxSystemMetrics.Api.csproj" \
    -c Release \
    -o "$PUBLISH_DIR" \
    -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=false
else
  echo "Publishing framework-dependent (requires ASP.NET Core Runtime 8.x on this host)..."
  dotnet publish "$PROJECT_DIR/FortGuard.LinuxSystemMetrics.Api.csproj" \
    -c Release \
    -o "$PUBLISH_DIR" \
    --self-contained false
fi

install -d -m 0755 "$INSTALL_DIR"
find "$INSTALL_DIR" -mindepth 1 -delete
cp -a "$PUBLISH_DIR"/. "$INSTALL_DIR/"

if ! id -u "$RUN_AS_USER" >/dev/null 2>&1; then
  useradd --system --home "$INSTALL_DIR" --shell /usr/sbin/nologin "$RUN_AS_USER"
fi
chown -R "$RUN_AS_USER:$RUN_AS_USER" "$INSTALL_DIR"

ENV_FILE="/etc/default/${SERVICE_NAME}"
LISTEN_URL="http://0.0.0.0:${PORT}"
NO_AUTH="${NO_AUTH:-0}"

if [[ ! -f "$ENV_FILE" ]]; then
  cat >"$ENV_FILE" <<EOF
# FortGuard Linux System Metrics API — environment for systemd
# Bound by install script (re-run install to change port).
ASPNETCORE_URLS=${LISTEN_URL}
# Bearer token for /api/v1/* (auto-generated on first install if empty; use NO_AUTH=1 to disable).
Auth__Token=
DOTNET_ENVIRONMENT=Production
EOF
  chmod 0640 "$ENV_FILE"
  chown root:"$RUN_AS_USER" "$ENV_FILE"
else
  if grep -q '^[[:space:]]*ASPNETCORE_URLS=' "$ENV_FILE" 2>/dev/null; then
    sed -i "s|^[[:space:]]*ASPNETCORE_URLS=.*|ASPNETCORE_URLS=${LISTEN_URL}|" "$ENV_FILE"
  else
    printf '\nASPNETCORE_URLS=%s\n' "$LISTEN_URL" >>"$ENV_FILE"
  fi
fi

API_TOKEN=""
API_TOKEN_NEW=0
if [[ "$NO_AUTH" == "1" ]]; then
  API_TOKEN=""
elif [[ -n "${INSTALL_API_KEY:-}" ]]; then
  API_TOKEN="$INSTALL_API_KEY"
  API_TOKEN_NEW=0
elif [[ -n "${AUTH_TOKEN:-}" ]]; then
  API_TOKEN="$AUTH_TOKEN"
  API_TOKEN_NEW=0
else
  EXISTING_TOKEN="$(extract_auth_token_from_env_file "$ENV_FILE")"
  if [[ -n "$EXISTING_TOKEN" ]]; then
    API_TOKEN="$EXISTING_TOKEN"
  else
    API_TOKEN="$(generate_api_token_hex)"
    API_TOKEN_NEW=1
  fi
fi
set_env_file_auth_token "$ENV_FILE" "$API_TOKEN"
chmod 0640 "$ENV_FILE"
chown root:"$RUN_AS_USER" "$ENV_FILE"

EXEC_START=""
if [[ "$SELF_CONTAINED" == "1" ]]; then
  EXEC_START="$INSTALL_DIR/FortGuard.LinuxSystemMetrics.Api"
else
  EXEC_START="/usr/bin/dotnet $INSTALL_DIR/FortGuard.LinuxSystemMetrics.Api.dll"
fi

UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
cat >"$UNIT_PATH" <<EOF
[Unit]
Description=FortGuard Linux System Metrics API
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$RUN_AS_USER
Group=$RUN_AS_USER
WorkingDirectory=$INSTALL_DIR
EnvironmentFile=-$ENV_FILE
ExecStart=$EXEC_START
Restart=always
RestartSec=5
NoNewPrivileges=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
EOF

chmod 0644 "$UNIT_PATH"

systemctl daemon-reload
systemctl enable "$SERVICE_NAME.service"
systemctl restart "$SERVICE_NAME.service"
systemctl --no-pager --full status "$SERVICE_NAME.service" || true

PRIMARY_IP="127.0.0.1"
if command -v hostname >/dev/null 2>&1; then
  _hi="$(hostname -I 2>/dev/null | awk '{print $1}')"
  [[ -n "${_hi:-}" ]] && PRIMARY_IP="$_hi"
fi
SHORT_HOST="$(hostname 2>/dev/null || echo localhost)"
BASE_URL="http://${PRIMARY_IP}:${PORT}"

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  FortGuard Linux System Metrics API — connection details"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Hostname:     ${SHORT_HOST}"
echo "  Listen URL:   ${LISTEN_URL}  (all interfaces)"
echo "  Reachable as: ${BASE_URL}  (first detected IPv4; use your IP/DNS if different)"
echo ""
echo "  Health (no auth):"
echo "    curl -sS \"${BASE_URL}/health\""
echo ""
if [[ -n "$API_TOKEN" ]]; then
  echo "  Metrics & summary (Bearer token required):"
  if [[ "$API_TOKEN_NEW" -eq 1 ]]; then
    echo "    curl -sS -H \"Authorization: Bearer ${API_TOKEN}\" \"${BASE_URL}/api/v1/metrics\""
    echo "    curl -sS -H \"Authorization: Bearer ${API_TOKEN}\" \"${BASE_URL}/api/v1/metrics/summary\""
    echo ""
    echo "  New API token (save securely; also in ${ENV_FILE} as Auth__Token):"
    echo "    ${API_TOKEN}"
    echo ""
  else
    echo "    curl -sS -H \"Authorization: Bearer <token>\" \"${BASE_URL}/api/v1/metrics\""
    echo "    curl -sS -H \"Authorization: Bearer <token>\" \"${BASE_URL}/api/v1/metrics/summary\""
    echo ""
    echo "  Token: stored in ${ENV_FILE} (Auth__Token). View: sudo grep '^Auth__Token=' ${ENV_FILE}"
    echo ""
  fi
else
  echo "  Metrics (no Bearer auth — NO_AUTH=1):"
  echo "    curl -sS \"${BASE_URL}/api/v1/metrics\""
  echo "    curl -sS \"${BASE_URL}/api/v1/metrics/summary\""
  echo ""
fi
echo "  Config:       ${ENV_FILE}"
echo "  Install dir:  ${INSTALL_DIR}"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Source repo: $LINUX_MONITOR_REPO @ $GIT_REF"
echo "Clone path:  $LINUX_MONITOR_CLONE_DIR"
