# TwinCheck Scan Agent — NixOS

Run these scripts as the normal scanner user, not with `sudo`. The installer uses a systemd user service, which makes repeated test installs and removals isolated to that user.

## Prerequisite

Install .NET 9 through Nix:

```bash
nix profile install nixpkgs#dotnet-sdk_9
```

Open a new terminal and confirm `dotnet --version` starts with `9.`.

## Install

Extract the entire `.tar.gz`, enter the extracted directory, and run:

```bash
./install.sh
```

The installer:

- copies the app to `${XDG_DATA_HOME:-~/.local/share}/TwinCheck/ScanAgent`
- installs and starts `twincheck-scan-agent.service` for the current user
- creates a desktop launcher for the GUI
- exports a local HTTPS certificate for Kestrel
- creates a shared config with a unique API key when one does not already exist
- stores profiles under `${XDG_CONFIG_HOME:-~/.config}/TwinCheck/ScanAgent`
- stores logs and durable operation records under `${XDG_STATE_HOME:-~/.local/state}/TwinCheck/ScanAgent`

Reinstall preserves the shared API key and existing profiles.

Open `https://localhost:3625` once in the browser used for TwinCheckN and accept the local certificate. You can ask the .NET tooling to trust it during installation instead:

```bash
./install.sh --trust-certificate
```

If the service must run before this user logs in:

```bash
./install.sh --enable-linger
```

### NFS source or destination: disable .NET file locking

Use this configuration when a scanner profile reads from or writes to an NFS
mount. On Linux, .NET implements `FileShare` with Unix advisory locks. Some
NFSv4.1 client/server combinations can lose those locks during a scan, after
which Linux returns `EIO` (`Input/output error`) even though the file and mount
remain readable from a new process.

Create a persistent systemd user-service override as the normal scanner user:

```bash
systemctl --user edit twincheck-scan-agent.service
```

Add these lines, save, and exit the editor:

```ini
[Service]
Environment=DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1
```

Apply and verify the override:

```bash
systemctl --user daemon-reload
systemctl --user restart twincheck-scan-agent.service
systemctl --user show twincheck-scan-agent.service \
  --property=Environment --value
```

The last command must include:

```text
DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1
```

The drop-in survives agent reinstallations. `systemctl --user set-environment`
is suitable for a temporary diagnostic test only; it can be lost when the user
systemd manager or computer restarts.

Disabling .NET file locking means file readiness must be established through
the scanner's `export.done` sentinel when available, or through the configured
quiet period. For polling profiles without a sentinel, use at least 30 stable
seconds and 3600-second settle/watch limits.

After a real scan, confirm the kernel did not report another lost-lock event:

```bash
sudo journalctl -k --since '10 minutes ago' |
  grep -Ei 'nfs|lost.*locks|state|recover|rpc'
```

No output is expected. To remove the override, run
`systemctl --user edit twincheck-scan-agent.service`, delete the added lines,
then run the daemon-reload and restart commands again. If a temporary manager
environment was also used, remove it with:

```bash
systemctl --user unset-environment DOTNET_SYSTEM_IO_DISABLEFILELOCKING
```

### Local staging and the upload queue

Rolls are processed in local staging, `~/.local/state/TwinCheck/ScanAgent/staging`, and only the
finished files are written to the NAS: one file at a time, through the upload queue shown on the
GUI's **Uploads** page. ImageMagick and ExifTool never write to the NAS. Keep enough local disk space
for several rolls, because rolls waiting to upload keep their local copy; the agent refuses to start a
roll that doesn't fit. To use another local disk, set `"stagingDir"` in
`~/.config/TwinCheck/ScanAgent/agent-config.json`. Don't point it at the NAS or at `/tmp`.

Scanners must save each roll in its own folder inside the watch folder. After a roll is processed
locally, its folder is renamed to `_processed-{order}-{roll}`; it is deleted once every file is
verified on the NAS. See `docs/OPERATIONS.md` in the source repository for details.

## Update

When a new release is published, update from the installed copy:

```bash
~/.local/share/TwinCheck/ScanAgent/update.sh
```

The updater:

- checks the latest [GitHub release](https://github.com/edrdmolina/TwinCheckAgent/releases/latest) and does nothing if you're already on it
- refuses to run while a roll is being processed locally; rolls waiting, watching, or uploading resume after the update, and it says how many uploads are queued
- downloads the NixOS package and verifies its published SHA-256 checksum
- reinstalls with the same install folder and agent URL, keeping profiles, the API key, logs, job history, and any `systemctl --user edit` override

Other options:

```bash
~/.local/share/TwinCheck/ScanAgent/update.sh --check          # only report whether an update exists
~/.local/share/TwinCheck/ScanAgent/update.sh --version 0.3.0  # install a specific release
```

Run it as the scanner user, not with `sudo`. If `curl` isn't installed, the updater uses the one from nixpkgs.

Installs older than 0.1.2 don't include `update.sh`. Update those once by hand, as below; after that, `update.sh` works.

## Reinstall a downloaded package

Extract the archive into its own folder (it has no top-level folder) and run:

```bash
mkdir ~/twincheck-new && tar -xzf TwinCheck-Scan-Agent-*-nixos-x64.tar.gz -C ~/twincheck-new
cd ~/twincheck-new
./reinstall.sh
```

Reinstall preserves profiles, logs, and operation records.

## Uninstall

Run the uninstaller from the extracted package or the installed directory:

```bash
./uninstall.sh
```

```bash
~/.local/share/TwinCheck/ScanAgent/uninstall.sh
```

The default uninstall removes the app, service, desktop launcher, and exported PFX/password while preserving profiles, logs, job history, and rolls waiting to upload. Remove everything owned by the app with:

```bash
./uninstall.sh --purge-data
```

`--purge-data` also deletes the local copies of rolls still waiting to upload. Check that the GUI's **Uploads** page shows nothing waiting first; the originals of a waiting roll stay in its `_processed-…` folder.

If lingering was enabled and should also be disabled:

```bash
./uninstall.sh --disable-linger
```

The uninstaller intentionally does not run `dotnet dev-certs https --clean`, because that could remove a development certificate shared with another local .NET app.

## Verify

```bash
systemctl --user status twincheck-scan-agent
curl -k https://localhost:3625/
curl -k -H "X-Api-Key: YOUR_API_KEY" https://localhost:3625/api/scan/health
```

Logs:

```bash
journalctl --user -u twincheck-scan-agent -n 100 --no-pager
```

For an HTTP-only local diagnostic install:

```bash
./install.sh --agent-url http://localhost:3625
```

The default HTTPS URL is required when a secure TwinCheckN web page connects to the agent.

## Photo metadata dependency

The installer resolves ExifTool from Nix alongside the GUI libraries and sets `TWINCHECK_EXIFTOOL_PATH` in the API launcher. Health advertises `scan-metadata-v1` when it is available. Configure the scanner make/model under Profiles → Photo Metadata; enable TIFF conversion for BMP scans that need metadata.
