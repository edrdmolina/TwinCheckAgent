#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_cmd="$repo_root/scripts/dotnet.sh"

cd "$repo_root"

"$dotnet_cmd" restore TwinCheckAgent.sln
"$dotnet_cmd" build TwinCheckAgent.sln --configuration Release --no-restore --nologo
"$dotnet_cmd" test tests/TwinCheck.Agent.Tests/TwinCheck.Agent.Tests.csproj \
    --configuration Release \
    --no-build \
    --nologo \
    --logger 'console;verbosity=normal'
