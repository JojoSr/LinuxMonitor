# FortGuard Linux System Metrics API

## Summary

**FortGuard.LinuxSystemMetrics.Api** is a small ASP.NET Core **.NET 8** service that collects **Linux host metrics** (CPU, memory, swap, disk mounts, network totals, processes, logged-in users, and basic thermal/fan data where `/sys` exposes it) and serves them over HTTP. The JSON shape matches the **FortGuard / Home Assistant “System Metrics API”** add-on so the same clients can consume either service.

- **Platform:** metrics collection runs **only on Linux** (reads `/proc`, `/sys`, etc.). On other OSes, `/health` still responds; `/api/v1/*` returns HTTP 503.
- **Deployment:** optional **systemd** install via `install-linux-service.sh`, which clones or updates from **[JojoSr/LinuxMonitor](https://github.com/JojoSr/LinuxMonitor)** and publishes the app to `/opt/fortguard-linux-system-metrics` by default.
- **Security:** `/api/v1/*` can require a **Bearer token** when `Auth__Token` is set (e.g. in `/etc/default/fortguard-linux-system-metrics`). The install script can **generate** a token if none is configured.

## Usage

### HTTP endpoints

| Method | Path | Auth |
|--------|------|------|
| `GET` | `/health` | No |
| `GET` | `/api/v1/metrics` | Bearer, if `Auth__Token` is set |
| `GET` | `/api/v1/metrics/summary` | Bearer, if `Auth__Token` is set |

Example (replace host, port, and token as appropriate):

```bash
curl -sS "http://localhost:8099/health"
curl -sS -H "Authorization: Bearer YOUR_TOKEN" "http://localhost:8099/api/v1/metrics"
curl -sS -H "Authorization: Bearer YOUR_TOKEN" "http://localhost:8099/api/v1/metrics/summary"
```

### Configuration

Priority for the listen URL is:

1. Environment variable **`ASPNETCORE_URLS`** (recommended for production; the install script writes this in `/etc/default/fortguard-linux-system-metrics`).
2. `Urls` in `appsettings.json` (default `http://0.0.0.0:8099`).

Optional Bearer auth:

- Set **`Auth__Token`** in configuration or, under systemd, **`Auth__Token`** in the same `/etc/default/...` file (ASP.NET Core maps `Auth__Token` → `Auth:Token`).

Other settings in `appsettings.json`:

- **`Metrics:CpuSampleSeconds`** — interval between CPU samples (default `0.25`).
- **`Metrics:MaxProcesses`** — max rows in `processes.top_by_cpu` (default `40`, max `200`).

### Local development (Linux)

From `FortGuard.LinuxSystemMetrics.Api`:

```bash
dotnet run
```

Then open `http://localhost:8099/health` (or the URL shown in the console).

## Installation

### Prerequisites

- **Linux** host with **root** (for systemd, `/etc/default`, and package installation).
- A supported **package manager**: `apt-get`, `dnf`/`yum`, `zypper`, `apk`, or `pacman`.
- The install script **installs missing prerequisites** when possible: **git**, **curl** (or **wget**), **CA certificates**, **openssl**, and **.NET 8 SDK** (tries distro `dotnet-sdk-8.0` first, then [Microsoft’s `dotnet-install.sh`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)).
- Set **`SKIP_PREREQ_INSTALL=1`** if you manage dependencies yourself (corporate mirror, air-gapped host, etc.).
- For a **framework-dependent** publish (default), the **ASP.NET Core 8** shared runtime is satisfied when the SDK is installed on the same machine.
- Optional: **OpenSSL** (preferred for token generation); otherwise the script falls back to `/dev/urandom` + `base64`.

### Install with systemd

1. Copy `install-linux-service.sh` onto the server (or clone this repo).
2. Make it executable: `chmod +x install-linux-service.sh`.
3. Run as root, optionally passing a **port** (default **8099**):

```bash
sudo ./install-linux-service.sh 9100
# or
sudo ./install-linux-service.sh --port 9100
```

The script will:

- Clone or update **`https://github.com/JojoSr/LinuxMonitor.git`** (override with `LINUX_MONITOR_REPO`).
- Publish the API to **`/opt/fortguard-linux-system-metrics`** (override with `INSTALL_DIR`).
- Create user **`fortguard-metrics`** (override with `RUN_AS_USER`).
- Install **`fortguard-linux-system-metrics.service`** and start it.
- Write **`/etc/default/fortguard-linux-system-metrics`** with `ASPNETCORE_URLS` and **`Auth__Token`** (auto-generated on first install if empty).
- Print **connection details** and example `curl` commands.

### Environment variables (install script)

| Variable | Description |
|----------|-------------|
| `LISTEN_PORT` / `METRICS_PORT` | Listen port if not passed on the command line (default `8099`). |
| `AUTH_TOKEN` | Set the Bearer token explicitly (not printed by the script). |
| `NO_AUTH=1` | Leave `Auth__Token` empty (no auth on `/api/v1/*`). |
| `SELF_CONTAINED=1` | Publish **self-contained** `linux-x64` (no ASP.NET runtime on the host). |
| `LINUX_MONITOR_REPO` | Git URL (default `https://github.com/JojoSr/LinuxMonitor.git`). |
| `GIT_REF` | Branch or tag (default `main`). |
| `LINUX_MONITOR_CLONE_DIR` | Clone/cache directory (default under `/var/cache/fortguard-linux-system-metrics`). |
| `INSTALL_DIR` | Install root (default `/opt/fortguard-linux-system-metrics`). |
| `SERVICE_NAME` | systemd unit base name (default `fortguard-linux-system-metrics`). |
| `SKIP_PREREQ_INSTALL` | Set to `1` to skip automatic installs of git, curl, .NET 8 SDK, etc. |
| `DOTNET_INSTALL_DIR` | Directory for Microsoft `dotnet-install.sh` (default `/usr/share/dotnet`). |

### Service management

```bash
sudo systemctl status fortguard-linux-system-metrics
sudo systemctl restart fortguard-linux-system-metrics
```

After editing `/etc/default/fortguard-linux-system-metrics`, restart the service so changes apply.

### Uninstall

From the same directory as the install script (or copy `uninstall-linux-service.sh` to the server):

```bash
chmod +x uninstall-linux-service.sh
sudo ./uninstall-linux-service.sh          # confirmation prompt
sudo ./uninstall-linux-service.sh --yes    # no prompt
```

This **stops and disables** the systemd unit, removes `/etc/systemd/system/fortguard-linux-system-metrics.service`, `/etc/default/fortguard-linux-system-metrics`, `/opt/fortguard-linux-system-metrics`, and the default git clone under `/var/cache/fortguard-linux-system-metrics/LinuxMonitor`.

Optional environment variables (must match a custom install if you overrode paths):

| Variable | Effect |
|----------|--------|
| `SKIP_REMOVE_CLONE=1` | Keep the clone/build cache directory |
| `PURGE_USER=1` | Delete the `fortguard-metrics` system user |

The script does **not** remove a shared .NET SDK/runtime under `/usr/share/dotnet` or `/usr/bin/dotnet`.
