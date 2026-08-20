#!/usr/bin/env bash
set -euo pipefail

install_dir="${INSTALL_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/TwinCheck/ScanAgent}"
service_name="${SERVICE_NAME:-twincheck-scan-agent}"
agent_url="${AGENT_URL:-https://localhost:3625}"
enable_linger=0
start_service=1
trust_certificate=0

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

usage() {
    cat <<'EOF'
Usage: ./install.sh [options]

Options:
  --install-dir PATH       Install somewhere other than the user data directory.
  --agent-url URL          Set the loopback API URL (default: https://localhost:3625).
  --enable-linger          Allow the user service to run before login.
  --trust-certificate      Ask dotnet to trust its localhost development certificate.
  --no-start               Install and enable the service without starting it.
  -h, --help               Show this help.
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
        --agent-url)
            require_value "$@"
            agent_url="$2"
            shift 2
            ;;
        --enable-linger)
            enable_linger=1
            shift
            ;;
        --trust-certificate)
            trust_certificate=1
            shift
            ;;
        --no-start)
            start_service=0
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
    echo "Do not run this installer with sudo. It installs a user-level service." >&2
    exit 1
fi

if [[ ! "$service_name" =~ ^[A-Za-z0-9_.@-]+$ ]]; then
    echo "Invalid service name: $service_name" >&2
    exit 2
fi

if [[ ! "$agent_url" =~ ^https?://(localhost|127\.0\.0\.1|\[::1\]):[0-9]+/?$ ]]; then
    echo "Agent URL must be an HTTP or HTTPS loopback URL with a port, such as https://localhost:3625." >&2
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

dotnet_bin="$(command -v dotnet || true)"
if [[ -z "$dotnet_bin" ]]; then
    cat >&2 <<'EOF'
dotnet was not found.

Install .NET 9 on NixOS first:
  nix profile install nixpkgs#dotnet-sdk_9

Then open a new terminal and run ./install.sh again.
EOF
    exit 1
fi

if ! "$dotnet_bin" --list-runtimes | grep -Eq '^Microsoft\.AspNetCore\.App 9\.'; then
    cat >&2 <<EOF
.NET 9 ASP.NET Core runtime was not found at $dotnet_bin.

Install it on NixOS with:
  nix profile install nixpkgs#dotnet-sdk_9
EOF
    exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl was not found. This package requires a NixOS systemd user session." >&2
    exit 1
fi

nix_bin="$(command -v nix || true)"
if [[ -z "$nix_bin" ]]; then
    echo "nix was not found. This installer must run inside a NixOS user environment." >&2
    exit 1
fi

echo "Resolving Avalonia GUI libraries from the Nix store..."
gui_dependencies_expression='let
  pkgs = import (builtins.getFlake "nixpkgs") { system = builtins.currentSystem; };
in pkgs.buildEnv {
  name = "twincheck-gui-libraries";
  paths = [ pkgs.fontconfig pkgs.libx11 pkgs.libice pkgs.libsm ];
  pathsToLink = [ "/lib" ];
  extraOutputsToInstall = [ "lib" ];
}'
if ! gui_store_path="$(
    "$nix_bin" \
        --extra-experimental-features "nix-command flakes" \
        build \
        --no-link \
        --print-out-paths \
        --impure \
        --expr "$gui_dependencies_expression"
)"; then
    echo "Unable to resolve the Avalonia GUI libraries through Nix." >&2
    exit 1
fi

gui_library_path="$gui_store_path/lib"
if [[ ! -e "$gui_library_path/libfontconfig.so.1" ]]; then
    echo "Nix resolved the GUI packages, but libfontconfig.so.1 is missing from $gui_library_path." >&2
    exit 1
fi

source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ "$source_dir" == "$install_dir" ]]; then
    echo "Run install.sh from the extracted package, not from the installed application directory." >&2
    exit 2
fi

if [[ ! -f "$source_dir/TwinCheck.Agent.Api.dll" || ! -f "$source_dir/gui/TwinCheck.Agent.Gui.dll" ]]; then
    echo "Application files are missing. Run install.sh from the extracted NixOS package." >&2
    exit 1
fi

package_version="0.1.0"
if [[ -f "$source_dir/package-version.txt" ]]; then
    package_version="$(tr -d '\r\n' < "$source_dir/package-version.txt")"
fi

config_dir="$config_home/TwinCheck/ScanAgent"
state_dir="$state_home/TwinCheck/ScanAgent"
log_dir="$state_dir/logs"
certificate_dir="$state_dir/https"
certificate_path="$certificate_dir/localhost.pfx"
certificate_password_path="$config_dir/https-certificate-password"
config_path="$config_dir/agent-config.json"
service_dir="$config_home/systemd/user"
service_path="$service_dir/$service_name.service"
desktop_dir="$data_home/applications"
desktop_path="$desktop_dir/twincheck-scan-agent.desktop"
api_launcher="$install_dir/twincheck-scan-agent-api"
gui_launcher="$install_dir/twincheck-scan-agent-gui"

printf 'Installing TwinCheck Scan Agent to %s\n' "$install_dir"
printf 'Using dotnet at %s\n' "$dotnet_bin"
printf 'Using config at %s\n' "$config_path"

systemctl --user stop "$service_name.service" >/dev/null 2>&1 || true
systemctl --user disable "$service_name.service" >/dev/null 2>&1 || true

mkdir -p "$install_dir" "$config_dir" "$log_dir" "$service_dir" "$desktop_dir"
chmod 700 "$config_dir" "$state_dir" "$log_dir"

if [[ ! -f "$config_path" ]]; then
    api_key="tcn-$(od -An -N24 -tx1 /dev/urandom | tr -d '[:space:]')"
    cat > "$config_path" <<EOF
{
  "agentName": "TwinCheck Scan Agent",
  "version": "$package_version",
  "apiKey": "$api_key",
  "allowedSourceRoots": [],
  "allowedDestinationRoots": [],
  "activeProfileId": null,
  "profiles": []
}
EOF
    chmod 600 "$config_path"
    echo "Created shared config with a unique API key."
fi

cp -R "$source_dir"/. "$install_dir"/
printf '%s\n' "$service_name" > "$install_dir/.twincheck-service-name"

certificate_password=""
if [[ "$agent_url" == https://* ]]; then
    mkdir -p "$certificate_dir"
    chmod 700 "$certificate_dir"
    certificate_password="$(od -An -N24 -tx1 /dev/urandom | tr -d '[:space:]')"
    rm -f "$certificate_path"

    certificate_args=(https --export-path "$certificate_path" --password "$certificate_password" --quiet)
    if [[ "$trust_certificate" -eq 1 ]]; then
        certificate_args+=(--trust)
    fi

    if ! "$dotnet_bin" dev-certs "${certificate_args[@]}"; then
        rm -f "$certificate_path"
        cat >&2 <<'EOF'
Unable to create the localhost HTTPS certificate.
Run `dotnet dev-certs https` for diagnostics, then run this installer again.
You can use --agent-url http://localhost:3625 for an HTTP-only local test.
EOF
        exit 1
    fi

    printf '%s\n' "$certificate_password" > "$certificate_password_path"
    chmod 600 "$certificate_path" "$certificate_password_path"
else
    rm -f "$certificate_path" "$certificate_password_path"
fi

{
    printf '#!/usr/bin/env bash\n'
    printf 'set -euo pipefail\n'
    printf 'export TWINCHECK_AGENT_CONFIG_PATH=%q\n' "$config_path"
    printf 'export TWINCHECK_AGENT_LOG_DIR=%q\n' "$log_dir"
    printf 'export TWINCHECK_AGENT_STATE_DIR=%q\n' "$state_dir"
    printf 'export DOTNET_ENVIRONMENT=Production\n'
    if [[ "$agent_url" == https://* ]]; then
        printf 'export ASPNETCORE_Kestrel__Certificates__Default__Path=%q\n' "$certificate_path"
        printf 'export ASPNETCORE_Kestrel__Certificates__Default__Password=%q\n' "$certificate_password"
    fi
    printf 'exec %q %q --urls %q "$@"\n' "$dotnet_bin" "$install_dir/TwinCheck.Agent.Api.dll" "$agent_url"
} > "$api_launcher"

{
    printf '#!/usr/bin/env bash\n'
    printf 'set -euo pipefail\n'
    printf 'export TWINCHECK_AGENT_CONFIG_PATH=%q\n' "$config_path"
    printf 'export TWINCHECK_AGENT_LOG_DIR=%q\n' "$log_dir"
    printf 'export TWINCHECK_AGENT_STATE_DIR=%q\n' "$state_dir"
    printf 'export DOTNET_CLI_TELEMETRY_OPTOUT=1\n'
    printf 'export DOTNET_NOLOGO=1\n'
    printf 'export LD_LIBRARY_PATH=%q${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\n' "$gui_library_path"
    printf 'exec %q %q "$@"\n' "$dotnet_bin" "$install_dir/gui/TwinCheck.Agent.Gui.dll"
} > "$gui_launcher"

chmod 700 "$api_launcher" "$gui_launcher"

systemd_quote() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    value="${value//%/%%}"
    printf '"%s"' "$value"
}

systemd_path() {
    local value="$1"
    value="${value//%/%%}"
    printf '%s' "$value"
}

cat > "$service_path" <<EOF
[Unit]
Description=TwinCheck Scan Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$(systemd_path "$install_dir")
ExecStart=$(systemd_quote "$api_launcher")
Restart=on-failure
RestartSec=3
UMask=0077

[Install]
WantedBy=default.target
EOF

desktop_exec="${gui_launcher//\\/\\\\}"
desktop_exec="${desktop_exec//\"/\\\"}"
cat > "$desktop_path" <<EOF
[Desktop Entry]
Type=Application
Name=TwinCheck Scan Agent
Comment=Local scanner monitor and profile control
Exec="$desktop_exec"
Terminal=false
Categories=Utility;
EOF

systemctl --user daemon-reload
systemctl --user enable "$service_name.service"

if [[ "$enable_linger" -eq 1 ]]; then
    sudo loginctl enable-linger "$(id -un)"
fi

if [[ "$start_service" -eq 1 ]]; then
    systemctl --user restart "$service_name.service"
    sleep 1
    if ! systemctl --user is-active --quiet "$service_name.service"; then
        echo "The service did not remain active. Recent logs:" >&2
        journalctl --user -u "$service_name.service" -n 50 --no-pager >&2 || true
        exit 1
    fi
fi

echo "Installed TwinCheck Scan Agent."
echo "Service: $service_name.service"
echo "Config path: $config_path"
echo "Log folder: $log_dir"
echo "GUI launcher: $gui_launcher"
if [[ "$agent_url" == https://* ]]; then
    echo "HTTPS certificate: $certificate_path"
    if [[ "$trust_certificate" -eq 0 ]]; then
        echo "Open $agent_url in the TwinCheckN browser once and accept the local certificate."
    fi
fi
