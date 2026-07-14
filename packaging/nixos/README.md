# TwinCheck Scan Agent NixOS Test Package

Run these scripts as the normal scanner user, not with `sudo`.

## Prerequisite

Install .NET 9 through Nix so the portable package can run without generic Linux apphost issues:

```bash
nix profile install nixpkgs#dotnet-sdk_9
```

Open a new terminal after installing .NET.

## Install

```bash
./install.sh
```

The installer:

- copies the app to `~/.local/share/TwinCheck/ScanAgent`
- installs and starts the `twincheck-scan-agent` user service
- creates a desktop launcher for the GUI
- uses shared user config/log paths:
  - `~/.config/TwinCheck/ScanAgent/agent-config.json`
  - `~/.local/state/TwinCheck/ScanAgent/logs`

If the service should start before the user logs in:

```bash
./install.sh --enable-linger
```

## Reinstall After A New Build

```bash
./reinstall.sh
```

## Uninstall

```bash
./uninstall.sh
```

Remove saved config/logs too:

```bash
./uninstall.sh --purge-data
```

If you installed with `--enable-linger` and want to turn that off:

```bash
./uninstall.sh --disable-linger
```

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
