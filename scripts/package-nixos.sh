#!/usr/bin/env bash
set -euo pipefail

configuration="${CONFIGURATION:-Release}"
output_root="${OUTPUT_ROOT:-artifacts/nixos}"
package_version="${PACKAGE_VERSION:-0.1.0}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root_full="$repo_root/$output_root"
api_output="$output_root_full/api"
gui_output="$output_root_full/gui"
package_output="$output_root_full/package"
archive_path="$output_root_full/TwinCheck-Scan-Agent-$package_version-nixos-x64.tar.gz"
dotnet_cmd="$repo_root/scripts/dotnet.sh"

rm -rf "$output_root_full"
mkdir -p "$api_output" "$gui_output" "$package_output"

"$dotnet_cmd" publish "$repo_root/src/TwinCheck.Agent.Api/TwinCheck.Agent.Api.csproj" \
    -c "$configuration" \
    --self-contained false \
    -p:UseAppHost=false \
    -p:Version="$package_version" \
    -o "$api_output"

"$dotnet_cmd" publish "$repo_root/src/TwinCheck.Agent.Gui/TwinCheck.Agent.Gui.csproj" \
    -c "$configuration" \
    --self-contained false \
    -p:UseAppHost=false \
    -p:Version="$package_version" \
    -o "$gui_output"

cp -R "$api_output"/. "$package_output"/
mkdir -p "$package_output/gui"
cp -R "$gui_output"/. "$package_output/gui"/
cp "$repo_root/packaging/nixos/install.sh" "$package_output/install.sh"
cp "$repo_root/packaging/nixos/uninstall.sh" "$package_output/uninstall.sh"
cp "$repo_root/packaging/nixos/reinstall.sh" "$package_output/reinstall.sh"
cp "$repo_root/packaging/nixos/README.md" "$package_output/README.md"
printf '%s\n' "$package_version" > "$package_output/package-version.txt"
chmod +x "$package_output/install.sh" "$package_output/uninstall.sh" "$package_output/reinstall.sh"
tar -C "$package_output" -czf "$archive_path" .

echo "NixOS package ready:"
echo "$package_output"
echo "NixOS archive ready:"
echo "$archive_path"
