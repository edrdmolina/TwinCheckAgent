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

- Process each roll on local disk (copy, BMP-to-TIFF conversion, metadata, checksums) before anything is written to the destination.
- Upload processed rolls through a durable queue: one file at a time, oldest roll first, each copied to a hidden partial name, verified on the destination, then renamed into place.
- Verify copied file length and SHA-256 checksum.
- Never overwrite an existing destination file.
- If a matching name exists with identical bytes, treat it as already done.
- If a matching name exists with different bytes, write `-v2`, `-v3`, etc.
- Route non-image files into `_review/` instead of deleting them.
- Rename the roll folder to `_processed-{order}-{roll}` once it is processed locally, so the watch folder is clear for the next roll, and delete it only after every file is verified on the destination and the manifest is written.
- Write a per-operation JSON manifest beside the committed roll folder.
- Queue long-running scans and persist operation progress so browser timeouts or agent restarts do not lose completion state.
- View recent jobs and their statuses in the agent GUI; cancel queued, watching, or settling jobs before file processing starts.
- View, pause, resume, and retry the upload queue in the agent GUI, and delete verified folders of originals from Cleanup.
- Store job tracking in local SQLite, with 30-day finished-job history, 180-day duplicate-request protection, and automatic migration from legacy job JSON files.
- Embed customer name, film stock, and local scanner identity in delivered photo metadata; expose `scan-metadata-v1` when ExifTool is available.

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

Profiles support Frontier polling, Frontier sentinel, and Noritsu daily-folder watching. Legacy `frontier-folder` and `noritsu-daily-watch` profile values are accepted as aliases. Scans must be saved in a roll folder inside the watch folder; image files directly in the watch folder are ignored. A request can name the exact roll folder or the watch folder; given the watch folder, the agent uses its only roll folder and refuses to guess when there are several.

Durable scan requests can set `waitForReady: true`. TwinCheckAgent then owns the watch and settle phases, takes the newest ready roll folder (for sentinel profiles, the newest folder with `export.done`, even if it was ready before the request), and processes only after the selected profile's readiness condition succeeds. If more than one roll folder with images or `export.done` is in the watch folder, the agent won't guess: the operation fails with `errorCode: "multiple-source-candidates"` and TwinCheckN asks the operator to choose a folder. Sentinel profiles always fail closed when `export.done` is absent, including legacy direct-process requests.

Each profile can optionally convert BMP scans to uncompressed `.tif` output. Conversion is off by default and verifies exact pixel equality before publishing. Like all originals, the BMP is deleted once the roll's upload is verified.

New polling profiles use a 30-second stable window and separate 3600-second settle and watch timeouts. A settle timeout fails closed: the folder is not marked ready and no files are processed. Prefer `frontier-sentinel-watch` with `export.done` whenever the scanner workflow can create a completion marker.

See [docs/OPERATIONS.md](docs/OPERATIONS.md) for profile setup, browser setup, Frontier/Noritsu workflows, rollback behavior, troubleshooting, and Ubuntu autostart.

Run tests:

```bash
./scripts/test.sh
```

## Install and Release

**Windows:** operators run `TwinCheckScanAgent-win-Setup.exe` from the latest
[release](https://github.com/edrdmolina/TwinCheckAgent/releases/latest). It installs for the
signed-in user without admin rights, runs the agent inside a tray app that starts at sign-in,
and updates itself from GitHub Releases. See [packaging/windows/README.md](packaging/windows/README.md).

**Publishing an update:** push a version tag. The [Release workflow](.github/workflows/release.yml)
tests, builds the Windows installer and update packages plus the NixOS tarball, and publishes
the GitHub release. Installed Windows agents pick it up within 6 hours; on NixOS, run
`~/.local/share/TwinCheck/ScanAgent/update.sh` on each machine.

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Build the Windows installer locally (requires `dotnet tool install -g vpk`):

```powershell
.\scripts\package-win.ps1 -PackageVersion 0.2.0
```

Build the NixOS package and `.tar.gz`:

```bash
./scripts/package-nixos.sh
```

The NixOS target intentionally uses the .NET 9 runtime from Nix; install it on the scanner computer with `nix profile install nixpkgs#dotnet-sdk_9`. The package ships Linux x64 native libraries only. It contains install, reinstall, update, and uninstall commands. Uninstall preserves profiles/logs unless the purge option is selected.

See [Windows test install](docs/WINDOWS_TEST_INSTALL.md) and [NixOS test install](docs/NIXOS_TEST_INSTALL.md) for the build and test workflow.

## Near-Term Work

- Add a packaged installer for macOS.
- Code-sign the Windows installer so SmartScreen doesn't warn on first install.
- Upgrade the Avalonia template packages to the current supported line.
