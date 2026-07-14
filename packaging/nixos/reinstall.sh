#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_args=("$@")
uninstall_args=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)
            uninstall_args+=(--install-dir "$2")
            shift 2
            ;;
        *)
            shift
            ;;
    esac
done

"$script_dir/uninstall.sh" "${uninstall_args[@]}"
"$script_dir/install.sh" "${install_args[@]}"
