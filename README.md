# TwinCheck Scan Agent

[![CI](https://github.com/edrdmolina/TwinCheckAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/edrdmolina/TwinCheckAgent/actions/workflows/ci.yml)

Local multi-OS scan agent and GUI for TwinCheckN. This project is the clean rebuild of Scan-Lab focused on never losing scan files.

## Projects

- `src/TwinCheck.Agent.Core` - shared safety-first file processing, config, health, and manifest logic.
- `src/TwinCheck.Agent.Api` - local HTTPS-capable ASP.NET Core API intended for `https://localhost:3625`.
- `src/TwinCheck.Agent.Gui` - Avalonia monitor/control shell for Windows, macOS, and Linux.
- `tests/TwinCheck.Agent.Tests` - safety tests for dry runs, copy/verify, review routing, collisions, idempotency, and root validation.

## Safety Model

The processor currently implements these rules:

- Copy files into destination staging before final placement.
- Verify copied file length and SHA-256 checksum.
- Never overwrite an existing destination file.
- If a matching name exists with identical bytes, treat it as already done.
- If a matching name exists with different bytes, write `-v2`, `-v3`, etc.
- Route non-image files into `_review/` instead of deleting them.
- Archive the original source folder under `_processed/` only after copy/verify succeeds.
- Write a per-operation JSON manifest beside the committed roll folder.

Destination folders currently use:

```text
<destinationDir>/<orderNumber>-<rollNumber>/
```

File names default to:

```text
{orderNumber}-{rollNumber}-{imgNumber}
```

## Run Locally

This solution targets .NET 9, matching the SDK used by CI. Verify the SDK with:

```bash
./scripts/dotnet.sh --list-sdks
```

On Linux, if .NET 9 is installed under `~/.dotnet`, the repository test script
finds it automatically. Run the complete restore, Release build, and test flow
with:

```bash
./scripts/test.sh
```

Create development folders:

```bash
mkdir -p /tmp/twincheck-agent/source/inbox /tmp/twincheck-agent/destination
```

Run the API:

```bash
./scripts/dotnet.sh run --project src/TwinCheck.Agent.Api
```

Development config uses:

```text
X-Api-Key: dev-local-key
```

Health check:

```bash
curl -k -H 'X-Api-Key: dev-local-key' https://localhost:3625/api/scan/health
```

Depending on the local launch profile, ASP.NET may choose a different port until HTTPS binding is finalized.

Run the GUI shell:

```bash
./scripts/dotnet.sh run --project src/TwinCheck.Agent.Gui
```

Run tests:

```bash
./scripts/test.sh
```

## Near-Term Work

- Bind API launch settings to `https://localhost:3625` with local certificate guidance.
- Add persistent local config editing from the GUI.
- Add GUI polling for `/api/scan/health` and recent manifests.
- Add BMP to TIFF and EXIF-on-copy processing.
- Add candidate folder endpoint.
- Add service/autostart installers for Linux, Windows, and macOS.
- Upgrade the Avalonia template packages to the current supported line.
