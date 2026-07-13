#!/usr/bin/env bash

set -euo pipefail

if [[ -n "${DOTNET_BIN:-}" ]]; then
    dotnet_bin="$DOTNET_BIN"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet_bin="$HOME/.dotnet/dotnet"
else
    dotnet_bin="$(command -v dotnet || true)"
fi

if [[ -z "$dotnet_bin" ]]; then
    echo "The .NET 9 SDK is required but dotnet was not found." >&2
    exit 1
fi

if ! "$dotnet_bin" --list-sdks | grep -q '^9\.0\.'; then
    echo "The .NET 9 SDK is required but was not found at: $dotnet_bin" >&2
    echo "Install it with: curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0" >&2
    exit 1
fi

exec "$dotnet_bin" "$@"
