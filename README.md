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
- Queue long-running scans and persist operation progress so browser timeouts or agent restarts do not lose completion state.
- View recent jobs and their statuses in the agent GUI; cancel queued, watching, or settling jobs before file processing starts.

Destination folders use:

```text
<destinationDir>/<week-MM-DD-YY>/<orderNumber>/<orderNumber>-<rollNumber>/
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

The GUI writes this machine's local agent config to:

```text
~/.config/TwinCheck/ScanAgent/agent-config.json
```

Use the GUI to select the scanner source folder and destination folder, then save the active profile. The API reloads this local config on the next request.

Profiles support Frontier polling, Frontier sentinel, and Noritsu daily-folder watching. Legacy `frontier-folder` and `noritsu-daily-watch` profile values are accepted as aliases. The source folder can be either the exact roll folder containing images or a source root with roll subfolders. If multiple roll subfolders are present during a direct process request, the agent refuses to guess and asks for the exact folder.

Durable scan requests can set `waitForReady: true`. TwinCheckAgent then owns the watch and settle phases, resolves the newest scanner candidate, and processes only after the selected profile's readiness condition succeeds. Sentinel profiles always fail closed when `export.done` is absent, including legacy direct-process requests.

Each profile can optionally convert BMP scans to uncompressed `.tif` output. Conversion is off by default, verifies exact pixel equality before publishing, and preserves the original BMP unchanged in the `_processed` archive.

New polling profiles use a 30-second stable window and separate 3600-second settle and watch timeouts. A settle timeout fails closed: the folder is not marked ready and no files are processed. Prefer `frontier-sentinel-watch` with `export.done` whenever the scanner workflow can create a completion marker.

See [docs/OPERATIONS.md](docs/OPERATIONS.md) for profile setup, browser setup, Frontier/Noritsu workflows, rollback behavior, troubleshooting, and Ubuntu autostart.

Run tests:

```bash
./scripts/test.sh
```

## Build Installers

Build a self-contained Windows 10 ZIP from PowerShell:

```powershell
.\scripts\package-win.ps1
```

Build the NixOS package and `.tar.gz`:

```bash
./scripts/package-nixos.sh
```

The NixOS target intentionally uses the .NET 9 runtime from Nix; install it on the scanner computer with `nix profile install nixpkgs#dotnet-sdk_9`. Both packages contain install, reinstall, and uninstall commands. Uninstall preserves profiles/logs unless the purge option is selected.

See [Windows 10 test install](docs/WINDOWS_TEST_INSTALL.md) and [NixOS test install](docs/NIXOS_TEST_INSTALL.md) for the transfer and test workflow.

## Near-Term Work

- Add EXIF-on-copy processing.
- Add a packaged installer for macOS.
- Upgrade the Avalonia template packages to the current supported line.
