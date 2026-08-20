#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT

test_home="$test_root/home"
fake_bin="$test_root/bin"
package_dir="$test_root/package"
mkdir -p "$test_home" "$fake_bin" "$package_dir/gui"

cp "$repo_root/packaging/nixos/install.sh" "$package_dir/install.sh"
cp "$repo_root/packaging/nixos/uninstall.sh" "$package_dir/uninstall.sh"
cp "$repo_root/packaging/nixos/reinstall.sh" "$package_dir/reinstall.sh"
touch "$package_dir/TwinCheck.Agent.Api.dll" "$package_dir/gui/TwinCheck.Agent.Gui.dll"
chmod +x "$package_dir/install.sh" "$package_dir/uninstall.sh" "$package_dir/reinstall.sh"

cat > "$fake_bin/dotnet" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

if [[ "${1:-}" == "--list-runtimes" ]]; then
    echo "Microsoft.AspNetCore.App 9.0.0 [/fake/shared/Microsoft.AspNetCore.App]"
    echo "Microsoft.NETCore.App 9.0.0 [/fake/shared/Microsoft.NETCore.App]"
    exit 0
fi

if [[ "${1:-}" == "dev-certs" ]]; then
    export_path=""
    shift
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --export-path|-ep)
                export_path="$2"
                shift 2
                ;;
            *)
                shift
                ;;
        esac
    done
    : > "$export_path"
    exit 0
fi

exit 0
EOF

cat > "$fake_bin/systemctl" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF

cat > "$fake_bin/journalctl" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF

cat > "$fake_bin/nix" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

store_path="$FAKE_NIX_STORE_ROOT/twincheck-gui-libraries"
mkdir -p "$store_path/lib"
touch "$store_path/lib/libfontconfig.so.1"
printf '%s\n' "$store_path"
EOF

chmod +x "$fake_bin/dotnet" "$fake_bin/systemctl" "$fake_bin/journalctl" "$fake_bin/nix"

export HOME="$test_home"
export XDG_CONFIG_HOME="$test_home/config"
export XDG_DATA_HOME="$test_home/data"
export XDG_STATE_HOME="$test_home/state"
export PATH="$fake_bin:$PATH"
export FAKE_NIX_STORE_ROOT="$test_root/nix-store"

"$package_dir/install.sh" --no-start

default_install="$XDG_DATA_HOME/TwinCheck/ScanAgent"
service_file="$XDG_CONFIG_HOME/systemd/user/twincheck-scan-agent.service"
config_file="$XDG_CONFIG_HOME/TwinCheck/ScanAgent/agent-config.json"
[[ -x "$default_install/twincheck-scan-agent-api" ]]
[[ -x "$default_install/twincheck-scan-agent-gui" ]]
[[ -f "$config_file" ]]
[[ -f "$XDG_STATE_HOME/TwinCheck/ScanAgent/https/localhost.pfx" ]]
[[ -f "$XDG_CONFIG_HOME/TwinCheck/ScanAgent/https-certificate-password" ]]
grep -Eq '"apiKey": "tcn-[0-9a-f]{48}"' "$config_file"
if grep -Eq '"apiKey": "(change-me|dev-local-key)"' "$config_file"; then
    echo "Installer left an insecure default API key in the shared config." >&2
    exit 1
fi
grep -Fq 'ExecStart=' "$service_file"
grep -Fq "WorkingDirectory=$default_install" "$service_file"
if command -v systemd-analyze >/dev/null 2>&1; then
    systemd-analyze --user verify "$service_file"
fi
grep -Fq 'ASPNETCORE_Kestrel__Certificates__Default__Path' "$default_install/twincheck-scan-agent-api"
grep -Fq 'TWINCHECK_AGENT_STATE_DIR=' "$default_install/twincheck-scan-agent-api"
grep -Fq 'LD_LIBRARY_PATH=' "$default_install/twincheck-scan-agent-gui"

"$package_dir/uninstall.sh"
[[ ! -e "$default_install" ]]
[[ ! -e "$XDG_STATE_HOME/TwinCheck/ScanAgent/https" ]]

custom_install="$test_root/custom install"
"$package_dir/install.sh" --install-dir "$custom_install" --agent-url http://localhost:3625 --no-start
[[ -x "$custom_install/uninstall.sh" ]]
[[ ! -e "$XDG_STATE_HOME/TwinCheck/ScanAgent/https/localhost.pfx" ]]

"$custom_install/uninstall.sh" --purge-data
[[ ! -e "$custom_install" ]]
[[ ! -e "$XDG_CONFIG_HOME/TwinCheck/ScanAgent" ]]
[[ ! -e "$XDG_STATE_HOME/TwinCheck/ScanAgent" ]]

echo "NixOS installer lifecycle test passed."
