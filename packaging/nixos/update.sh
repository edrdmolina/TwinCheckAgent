#!/usr/bin/env bash
# Updates an installed TwinCheck Scan Agent to the latest GitHub release.
#
#   ~/.local/share/TwinCheck/ScanAgent/update.sh            update to the latest release
#   ~/.local/share/TwinCheck/ScanAgent/update.sh --check    only report whether an update exists
#
# Keeps profiles, the API key, logs, and job history (it runs the release's reinstall.sh).
set -euo pipefail

# Everything runs inside main so bash has read the whole script before reinstall.sh
# replaces the installed copy of this file.
main() {
    local repository="${TWINCHECK_UPDATE_REPOSITORY:-edrdmolina/TwinCheckAgent}"
    local api_base="${TWINCHECK_UPDATE_API:-https://api.github.com}"
    local script_dir install_dir check_only=0 force=0 wanted_version=""
    script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    install_dir="${INSTALL_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/TwinCheck/ScanAgent}"
    if [[ -f "$script_dir/.twincheck-service-name" ]]; then
        install_dir="$script_dir"
    fi

    while [[ $# -gt 0 ]]; do
        case "$1" in
            --check) check_only=1; shift ;;
            --force) force=1; shift ;;
            --version)
                [[ $# -ge 2 && -n "$2" ]] || { echo "Option --version requires a value." >&2; exit 2; }
                wanted_version="${2#v}"
                shift 2
                ;;
            --install-dir)
                [[ $# -ge 2 && -n "$2" ]] || { echo "Option --install-dir requires a value." >&2; exit 2; }
                install_dir="$2"
                shift 2
                ;;
            -h|--help) usage; exit 0 ;;
            *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
        esac
    done

    if [[ "$(id -u)" -eq 0 ]]; then
        echo "Do not run the updater with sudo. It updates a user-level service." >&2
        exit 1
    fi

    if [[ ! -f "$install_dir/.twincheck-service-name" ]]; then
        echo "TwinCheck Scan Agent is not installed in $install_dir. Use install.sh from a release package first." >&2
        exit 1
    fi

    local installed_version="unknown"
    if [[ -f "$install_dir/package-version.txt" ]]; then
        installed_version="$(tr -d '\r\n' < "$install_dir/package-version.txt")"
    fi

    local release_path="releases/latest"
    [[ -n "$wanted_version" ]] && release_path="releases/tags/v$wanted_version"
    echo "Checking $repository for $( [[ -n "$wanted_version" ]] && echo "version $wanted_version" || echo "the latest release" )..."
    local release_json
    if ! release_json="$(fetch "$api_base/repos/$repository/$release_path")"; then
        echo "Could not reach GitHub to check for updates." >&2
        exit 1
    fi

    local tag latest_version archive_url checksum_url
    tag="$(json_string "$release_json" tag_name)"
    latest_version="${tag#v}"
    archive_url="$(asset_url "$release_json" '-nixos-x64\.tar\.gz')"
    checksum_url="$(asset_url "$release_json" '-nixos-x64\.tar\.gz\.sha256')"
    if [[ -z "$latest_version" || -z "$archive_url" || -z "$checksum_url" ]]; then
        echo "Release ${tag:-(unknown)} has no NixOS package with a checksum attached." >&2
        exit 1
    fi

    echo "Installed: $installed_version"
    echo "Available: $latest_version"
    if [[ "$installed_version" == "$latest_version" && "$force" -eq 0 ]]; then
        echo "TwinCheck Scan Agent is up to date."
        exit 0
    fi
    if [[ -z "$wanted_version" && "$force" -eq 0 && "$installed_version" != "unknown" ]] \
        && [[ "$(printf '%s\n%s\n' "$installed_version" "$latest_version" | sort -V | tail -n1)" == "$installed_version" ]]; then
        echo "The installed version is newer than the latest release. Nothing to do (use --force to reinstall $latest_version)."
        exit 0
    fi
    if [[ "$check_only" -eq 1 ]]; then
        echo "An update is available. Run update.sh without --check to install it."
        exit 0
    fi

    local agent_url
    agent_url="$(installed_agent_url "$install_dir")"
    ensure_no_running_jobs "$agent_url"

    local work
    work="$(mktemp -d)"
    # Expand now: work is local and gone by the time the EXIT trap runs.
    trap "rm -rf -- '$work'" EXIT
    local archive="$work/${archive_url##*/}"
    echo "Downloading ${archive_url##*/}..."
    fetch_to "$archive_url" "$archive"
    fetch_to "$checksum_url" "$work/checksum"

    local expected actual
    expected="$(awk '{print tolower($1); exit}' "$work/checksum")"
    actual="$(sha256sum "$archive" | awk '{print $1}')"
    if [[ ! "$expected" =~ ^[0-9a-f]{64}$ || "$expected" != "$actual" ]]; then
        echo "The download does not match its published SHA-256 checksum. Nothing was changed." >&2
        exit 1
    fi

    mkdir "$work/package"
    tar -xzf "$archive" -C "$work/package"
    local package_version
    package_version="$(tr -d '\r\n' < "$work/package/package-version.txt" 2>/dev/null || true)"
    if [[ "$package_version" != "$latest_version" || ! -x "$work/package/reinstall.sh" ]]; then
        echo "The downloaded package is not version $latest_version. Nothing was changed." >&2
        exit 1
    fi

    echo "Installing $latest_version (profiles, API key, logs, and job history are kept)..."
    local install_args=(--install-dir "$install_dir" --agent-url "$agent_url")
    "$work/package/reinstall.sh" "${install_args[@]}"
    echo "Updated TwinCheck Scan Agent from $installed_version to $latest_version."
}

usage() {
    cat <<'EOF'
Usage: update.sh [options]

Downloads the latest release from GitHub, verifies its SHA-256 checksum, and reinstalls
with the same install folder and agent URL. Profiles, the API key, logs, and job history
are kept. Refuses to run while a roll is being processed on local disk; queued uploads
resume after the update.

Options:
  --check              Only report whether an update is available.
  --version X.Y.Z      Install a specific release instead of the latest.
  --force              Reinstall even if that version is already installed.
  --install-dir PATH   Update a custom installation directory.
  -h, --help           Show this help.
EOF
}

# curl is not always on PATH on NixOS; fall back to the one in nixpkgs.
curl_cmd() {
    if command -v curl >/dev/null 2>&1; then
        curl "$@"
    else
        nix --extra-experimental-features "nix-command flakes" shell nixpkgs#curl --command curl "$@"
    fi
}

fetch() {
    curl_cmd -fsSL -H "Accept: application/vnd.github+json" "$1"
}

fetch_to() {
    if ! curl_cmd -fSL --retry 3 -o "$2" "$1"; then
        echo "Download failed: $1. Nothing was changed." >&2
        exit 1
    fi
}

json_string() {
    { printf '%s' "$1" | grep -oE "\"$2\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -n1 | sed -E 's/.*:[[:space:]]*"([^"]*)"/\1/'; } || true
}

asset_url() {
    { printf '%s' "$1" | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*"' \
        | sed -E 's/.*:[[:space:]]*"([^"]*)"/\1/' | grep -E -- "$2\$" | head -n1; } || true
}

installed_agent_url() {
    local launcher="$1/twincheck-scan-agent-api" url=""
    if [[ -f "$launcher" ]]; then
        url="$({ grep -oE -- "--urls [^ ]+" "$launcher" | head -n1 | sed -E "s/--urls //; s/^'//; s/'\$//"; } || true)"
    fi
    printf '%s' "${url:-https://localhost:3625}"
}

json_number() {
    { printf '%s' "$1" | grep -oE "\"$2\"[[:space:]]*:[[:space:]]*[0-9]+" | head -n1 | grep -oE '[0-9]+$'; } || true
}

ensure_no_running_jobs() {
    local agent_url="${1%/}" config_path api_key response active uploads
    config_path="${XDG_CONFIG_HOME:-$HOME/.config}/TwinCheck/ScanAgent/agent-config.json"
    api_key="$( [[ -f "$config_path" ]] && json_string "$(cat "$config_path")" apiKey || true)"
    if ! response="$(curl_cmd -fsSk --max-time 5 -H "X-Api-Key: $api_key" "$agent_url/api/scan/operations?limit=1" 2>/dev/null)"; then
        echo "The agent is not answering at $agent_url, so running jobs can't be checked. Continuing."
        return
    fi
    active="$(json_number "$response" localProcessingActive)"
    if [[ -z "$active" ]]; then
        # Agents before the upload queue: any queued or processing job blocks the update.
        active="$(printf '%s' "$response" | grep -cE '"status"[[:space:]]*:[[:space:]]*"(queued|processing)"' || true)"
    fi
    if [[ "$active" -gt 0 ]]; then
        echo "$active roll(s) are being processed. Let them finish, then run update.sh again. Nothing was changed." >&2
        exit 1
    fi
    uploads="$(json_number "$response" uploadsPending)"
    if [[ "${uploads:-0}" -gt 0 ]]; then
        echo "$uploads queued upload(s) will resume after the update."
    fi
}

main "$@"
