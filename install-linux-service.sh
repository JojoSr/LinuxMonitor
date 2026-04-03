#!/usr/bin/env bash
# Install FortGuard.LinuxSystemMetrics.Api as a systemd service (Debian/Ubuntu/Fedora/RHEL-style).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="${SCRIPT_DIR}/FortGuard.LinuxSystemMetrics.Api"
INSTALL_DIR="${INSTALL_DIR:-/opt/fortguard-linux-system-metrics}"
SERVICE_NAME="${SERVICE_NAME:-fortguard-linux-system-metrics}"
RUN_AS_USER="${RUN_AS_USER:-fortguard-metrics}"
SELF_CONTAINED="${SELF_CONTAINED:-0}"

if [[ ! -d "$PROJECT_DIR" ]]; then
  echo "Expected project at $PROJECT_DIR" >&2
  exit 1
fi

if [[ "${EUID:-0}" -ne 0 ]]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK not found. Install .NET 8 SDK, or set SELF_CONTAINED=1 on a machine with the SDK and copy the publish folder." >&2
  exit 1
fi

PUBLISH_DIR="${SCRIPT_DIR}/publish"
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
if [[ ! -f "$ENV_FILE" ]]; then
  cat >"$ENV_FILE" <<'EOF'
# FortGuard Linux System Metrics API — environment for systemd
ASPNETCORE_URLS=http://0.0.0.0:8099
# Optional Bearer token for /api/v1/* (leave empty to disable auth).
Auth__Token=
DOTNET_ENVIRONMENT=Production
EOF
  chmod 0640 "$ENV_FILE"
  chown root:"$RUN_AS_USER" "$ENV_FILE"
fi

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

echo ""
echo "Installed to $INSTALL_DIR"
echo "Environment: $ENV_FILE (edit ASPNETCORE_URLS / Auth__Token, then: systemctl restart $SERVICE_NAME)"
echo "Endpoints: GET http://<host>:8099/health  GET http://<host>:8099/api/v1/metrics"
