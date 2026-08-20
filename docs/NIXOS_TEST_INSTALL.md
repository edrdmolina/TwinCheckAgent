# NixOS Test Install

This is the repeatable package/install/uninstall path for the Fujifilm SP500 machine. It uses a framework-dependent .NET publish and a systemd user service, avoiding generic Linux apphost incompatibilities on NixOS.

## One-time NixOS prerequisite

On the scanner computer:

```bash
nix profile install nixpkgs#dotnet-sdk_9
```

Open a new terminal and confirm `dotnet --version` starts with `9.`.

If the Avalonia GUI reports missing native graphics or font libraries, add the relevant packages to the NixOS machine configuration. A reasonable starting point is:

```nix
environment.systemPackages = with pkgs; [
  dotnet-sdk_9
  fontconfig
  xorg.libX11
  xorg.libICE
  xorg.libSM
  xorg.libXi
  xorg.libXcursor
  xorg.libXrandr
  xorg.libXrender
  libGL
];
```

Then run `sudo nixos-rebuild switch`.

## Build the package

From the repository root:

```bash
./scripts/package-nixos.sh
```

If a repository copied from macOS contains AppleDouble sidecars, older
checkouts can fail with `CS2015` errors naming files such as
`._ScanProcessor.cs`. Inspect and remove only those metadata files, then retry:

```bash
find . -type f -name '._*' -print
find . -type f -name '._*' -delete
./scripts/package-nixos.sh
```

Current checkouts also exclude `._*` globally through `Directory.Build.props`,
so these binary sidecars are never treated as C# source.

To label a build with another version:

```bash
PACKAGE_VERSION=0.1.1 ./scripts/package-nixos.sh
```

The build produces:

```text
artifacts/nixos/package
artifacts/nixos/TwinCheck-Scan-Agent-0.1.0-nixos-x64.tar.gz
```

## Install

1. Copy the `.tar.gz` to the NixOS scanner computer.
2. Extract it and enter the extracted directory.
3. Run `./install.sh` as the normal scanner user, without `sudo`.

The installer uses:

```text
~/.local/share/TwinCheck/ScanAgent
~/.config/TwinCheck/ScanAgent/agent-config.json
~/.local/state/TwinCheck/ScanAgent/logs
~/.local/state/TwinCheck/ScanAgent/operations
```

It exports a local HTTPS certificate, configures Kestrel to use it, creates a
shared config with a unique API key when needed, creates the GUI launcher, and
starts `twincheck-scan-agent.service`. Reinstall preserves the shared API key
and profiles.

To run the API before the scanner user logs in:

```bash
./install.sh --enable-linger
```

### Required for profiles on NFS mounts

When either the source or destination is on NFS, persistently disable .NET's
automatic Unix advisory locks for the agent. A lost NFSv4.1 lock causes Linux
to return `EIO` to the locked .NET file descriptor even when a newly opened
descriptor can still read the file.

Run as the normal scanner user:

```bash
systemctl --user edit twincheck-scan-agent.service
```

Save this drop-in:

```ini
[Service]
Environment=DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1
```

Then reload, restart, and verify:

```bash
systemctl --user daemon-reload
systemctl --user restart twincheck-scan-agent.service
systemctl --user show twincheck-scan-agent.service \
  --property=Environment --value
```

The output must contain `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1`. This service
drop-in survives `./reinstall.sh`. Do not rely on
`systemctl --user set-environment` for the permanent configuration because it
can be cleared by a user-manager or machine restart.

With .NET locking disabled, configure readiness using `export.done` whenever
the scanner supports it. Otherwise use a quiet period of at least 30 seconds
and 3600-second settle/watch limits. The agent's single-worker queue,
source-change detection, staging checksums, and verification remain responsible
for processing safety.

After the acceptance scan, this command should produce no lost-lock output:

```bash
sudo journalctl -k --since '10 minutes ago' |
  grep -Ei 'nfs|lost.*locks|state|recover|rpc'
```

To undo the configuration, remove the added lines with `systemctl --user edit`,
then daemon-reload and restart the service. Also clear any temporary diagnostic
environment setting:

```bash
systemctl --user unset-environment DOTNET_SYSTEM_IO_DISABLEFILELOCKING
```

## Configure and verify

Open the desktop launcher or run:

```bash
~/.local/share/TwinCheck/ScanAgent/twincheck-scan-agent-gui
```

Create the scanner profile, set the source and destination, save it, and confirm **Refresh** connects. Open `https://localhost:3625` once in the TwinCheckN browser and accept the local certificate.

```bash
systemctl --user status twincheck-scan-agent
curl -k https://localhost:3625/
curl -k -H "X-Api-Key: YOUR_API_KEY" https://localhost:3625/api/scan/health
journalctl --user -u twincheck-scan-agent -n 100 --no-pager
```

## Reinstall

Build and extract the replacement archive, then run:

```bash
./reinstall.sh
```

Profiles, logs, and durable operation records are preserved.

## Uninstall

```bash
./uninstall.sh
```

For a completely clean test:

```bash
./uninstall.sh --purge-data
```

If lingering was enabled and should also be disabled:

```bash
./uninstall.sh --disable-linger
```

See the `README.md` included in the archive for HTTPS options and other details.
