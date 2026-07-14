#!/usr/bin/env bash
set -euo pipefail

install_dir="${INSTALL_DIR:-$HOME/.local/share/TwinCheck/ScanAgent}"
service_name="${SERVICE_NAME:-twincheck-scan-agent}"
agent_url="${AGENT_URL:-https://localhost:3625}"
enable_linger=0
start_service=1

while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)
            install_dir="$2"
            shift 2
            ;;
        --agent-url)
            agent_url="$2"
            shift 2
            ;;
        --enable-linger)
            enable_linger=1
            shift
            ;;
        --no-start)
            start_service=0
            shift
            ;;
        *)
            echo "Unknown option: $1" >&2
            exit 2
            ;;
    esac
done

if [[ "$(id -u)" -eq 0 ]]; then
    echo "Do not run this installer with sudo. It installs a user-level service." >&2
    exit 1
fi

dotnet_bin="$(command -v dotnet || true)"
if [[ -z "$dotnet_bin" ]]; then
    cat >&2 <<'EOF'
dotnet was not found.

Install .NET 9 on NixOS first, for example:
  nix profile install nixpkgs#dotnet-sdk_9

Then open a new terminal and run ./install.sh again.
EOF
    exit 1
fi

source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
config_dir="${XDG_CONFIG_HOME:-$HOME/.config}/TwinCheck/ScanAgent"
state_dir="${XDG_STATE_HOME:-$HOME/.local/state}/TwinCheck/ScanAgent"
log_dir="$state_dir/logs"
config_path="$config_dir/agent-config.json"
service_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
service_path="$service_dir/$service_name.service"
desktop_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
desktop_path="$desktop_dir/twincheck-scan-agent.desktop"
gui_launcher="$install_dir/twincheck-scan-agent-gui"

echo "Installing TwinCheck Scan Agent to $install_dir"
echo "Using dotnet at $dotnet_bin"
echo "Using config at $config_path"

if systemctl --user list-unit-files "$service_name.service" >/dev/null 2>&1; then
    systemctl --user stop "$service_name.service" >/dev/null 2>&1 || true
    systemctl --user disable "$service_name.service" >/dev/null 2>&1 || true
fi

mkdir -p "$install_dir" "$config_dir" "$log_dir" "$service_dir" "$desktop_dir"
cp -R "$source_dir"/. "$install_dir"/

cat > "$gui_launcher" <<EOF
#!/usr/bin/env bash
set -euo pipefail
export TWINCHECK_AGENT_CONFIG_PATH="$config_path"
export TWINCHECK_AGENT_LOG_DIR="$log_dir"
exec "$dotnet_bin" "$install_dir/gui/TwinCheck.Agent.Gui.dll" "\$@"
EOF
chmod +x "$gui_launcher"

cat > "$service_path" <<EOF
[Unit]
Description=TwinCheck Scan Agent
After=network-online.target

[Service]
Type=simple
WorkingDirectory="$install_dir"
ExecStart="$dotnet_bin" "$install_dir/TwinCheck.Agent.Api.dll" --urls "$agent_url"
Restart=on-failure
RestartSec=3
Environment="TWINCHECK_AGENT_CONFIG_PATH=$config_path"
Environment="TWINCHECK_AGENT_LOG_DIR=$log_dir"
Environment="DOTNET_ENVIRONMENT=Production"

[Install]
WantedBy=default.target
EOF

cat > "$desktop_path" <<EOF
[Desktop Entry]
Type=Application
Name=TwinCheck Scan Agent
Comment=Local scanner monitor and profile control
Exec=$gui_launcher
Terminal=false
Categories=Utility;
EOF

systemctl --user daemon-reload
systemctl --user enable "$service_name.service"

if [[ "$enable_linger" -eq 1 ]]; then
    sudo loginctl enable-linger "$USER"
fi

if [[ "$start_service" -eq 1 ]]; then
    systemctl --user restart "$service_name.service"
fi

echo "Installed TwinCheck Scan Agent."
echo "Service: $service_name.service"
echo "Config path: $config_path"
echo "Log folder: $log_dir"
echo "GUI launcher: $gui_launcher"
echo "Open https://localhost:3625 in the browser once and accept the local certificate if prompted."
