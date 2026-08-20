#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="${INSTALL_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/TwinCheck/ScanAgent}"
service_name="${SERVICE_NAME:-twincheck-scan-agent}"
purge_data=0
disable_linger=0

if [[ -f "$script_dir/.twincheck-service-name" ]]; then
    install_dir="$script_dir"
    service_name="$(<"$script_dir/.twincheck-service-name")"
fi

usage() {
    cat <<'EOF'
Usage: ./uninstall.sh [options]

Options:
  --install-dir PATH  Remove a custom installation directory.
  --purge-data        Also remove saved profiles, certificate, and logs.
  --disable-linger    Disable systemd user lingering for this user.
  -h, --help          Show this help.
EOF
}

require_value() {
    if [[ $# -lt 2 || -z "$2" ]]; then
        echo "Option $1 requires a value." >&2
        exit 2
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)
            require_value "$@"
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
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ "$(id -u)" -eq 0 ]]; then
    echo "Do not run this uninstaller with sudo. It removes a user-level service." >&2
    exit 1
fi

if [[ ! "$service_name" =~ ^[A-Za-z0-9_.@-]+$ ]]; then
    echo "Invalid service name: $service_name" >&2
    exit 2
fi

if [[ "$install_dir" != /* ]]; then
    echo "Install directory must be an absolute path: $install_dir" >&2
    exit 2
fi

config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
state_home="${XDG_STATE_HOME:-$HOME/.local/state}"

case "$install_dir" in
    /|"$HOME"|"$config_home"|"$data_home"|"$state_home")
        echo "Refusing unsafe install directory: $install_dir" >&2
        exit 2
        ;;
esac

service_path="$config_home/systemd/user/$service_name.service"
desktop_path="$data_home/applications/twincheck-scan-agent.desktop"
config_dir="$config_home/TwinCheck/ScanAgent"
state_dir="$state_home/TwinCheck/ScanAgent"

if command -v systemctl >/dev/null 2>&1; then
    systemctl --user stop "$service_name.service" >/dev/null 2>&1 || true
    systemctl --user disable "$service_name.service" >/dev/null 2>&1 || true
fi

rm -f "$service_path" "$desktop_path"

if command -v systemctl >/dev/null 2>&1; then
    systemctl --user daemon-reload >/dev/null 2>&1 || true
    systemctl --user reset-failed "$service_name.service" >/dev/null 2>&1 || true
fi

rm -rf "$install_dir"
rm -f "$config_dir/https-certificate-password"
rm -rf "$state_dir/https"

if [[ "$purge_data" -eq 1 ]]; then
    rm -rf "$config_dir" "$state_dir"
fi

if [[ "$disable_linger" -eq 1 ]]; then
    sudo loginctl disable-linger "$(id -un)"
fi

echo "Uninstalled TwinCheck Scan Agent."
if [[ "$purge_data" -eq 0 ]]; then
    echo "Profiles and logs were preserved. Re-run with --purge-data to remove them."
fi
