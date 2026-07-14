#!/usr/bin/env bash
set -euo pipefail

install_dir="${INSTALL_DIR:-$HOME/.local/share/TwinCheck/ScanAgent}"
service_name="${SERVICE_NAME:-twincheck-scan-agent}"
purge_data=0
disable_linger=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)
            install_dir="$2"
            shift 2
            ;;
        --purge-data)
            purge_data=1
            shift
            ;;
        --disable-linger)
            disable_linger=1
            shift
            ;;
        *)
            echo "Unknown option: $1" >&2
            exit 2
            ;;
    esac
done

if [[ "$(id -u)" -eq 0 ]]; then
    echo "Do not run this uninstaller with sudo. It removes a user-level service." >&2
    exit 1
fi

config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
state_home="${XDG_STATE_HOME:-$HOME/.local/state}"
service_path="$config_home/systemd/user/$service_name.service"
desktop_path="$data_home/applications/twincheck-scan-agent.desktop"

systemctl --user stop "$service_name.service" >/dev/null 2>&1 || true
systemctl --user disable "$service_name.service" >/dev/null 2>&1 || true
rm -f "$service_path" "$desktop_path"
systemctl --user daemon-reload >/dev/null 2>&1 || true
rm -rf "$install_dir"

if [[ "$purge_data" -eq 1 ]]; then
    rm -rf "$config_home/TwinCheck/ScanAgent" "$state_home/TwinCheck/ScanAgent"
fi

if [[ "$disable_linger" -eq 1 ]]; then
    sudo loginctl disable-linger "$USER"
fi

echo "Uninstalled TwinCheck Scan Agent."
if [[ "$purge_data" -eq 0 ]]; then
    echo "Config and logs were preserved. Re-run with --purge-data to remove them."
fi
