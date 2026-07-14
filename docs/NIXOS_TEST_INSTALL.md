# NixOS Test Install

This is the fast test packaging path for the Fujifilm SP500 machine.

The package uses a framework-dependent .NET publish and a user-level systemd service. That is intentional for NixOS: it avoids generic Linux apphost binaries and keeps install/uninstall simple during live scanner testing.

## One-Time NixOS Prerequisite

Install .NET 9 through Nix:

```bash
nix profile install nixpkgs#dotnet-sdk_9
```

Open a new terminal and verify:

```bash
dotnet --version
```

If the GUI opens but complains about missing native graphics/font libraries, add the relevant desktop libraries to the NixOS machine configuration. Start with:

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

Then run:

```bash
sudo nixos-rebuild switch
```

## Build Package

From the repo root on a development machine:

```bash
./scripts/package-nixos.sh
```

Copy this folder to the NixOS SP500 machine:

```text
artifacts/nixos/package
```

## Install

On the NixOS SP500 machine:

```bash
cd path/to/package
./install.sh
```

Do not run the installer with `sudo`. It installs a user-level service for the scanner user.

The installer uses:

```text
~/.local/share/TwinCheck/ScanAgent
~/.config/TwinCheck/ScanAgent/agent-config.json
~/.local/state/TwinCheck/ScanAgent/logs
```

If the API should start even before the scanner user logs in:

```bash
./install.sh --enable-linger
```

## Configure

1. Open the TwinCheck Scan Agent desktop launcher, or run:

   ```bash
   ~/.local/share/TwinCheck/ScanAgent/twincheck-scan-agent-gui
   ```

2. Generate or confirm the API key.
3. Create the SP500 profile.
4. Select the correct Frontier watcher mode for this SP500 setup.
5. Set the source and destination folders.
6. Save config.
7. Click Refresh and confirm the API is connected.

## Browser Certificate

Open this once in the browser that will run TwinCheckN:

```text
https://localhost:3625
```

Accept the local development certificate if prompted.

## Verify

```bash
systemctl --user status twincheck-scan-agent
curl -k https://localhost:3625/
curl -k -H "X-Api-Key: YOUR_API_KEY" https://localhost:3625/api/scan/health
```

Service logs:

```bash
journalctl --user -u twincheck-scan-agent -n 100 --no-pager
```

## Rebuild And Reinstall

After code changes:

```bash
./scripts/package-nixos.sh
```

Copy the new `artifacts/nixos/package` folder to the NixOS machine and run:

```bash
./reinstall.sh
```

## Uninstall

```bash
./uninstall.sh
```

To remove config and logs too:

```bash
./uninstall.sh --purge-data
```

If you installed with `--enable-linger` and want to turn that off:

```bash
./uninstall.sh --disable-linger
```
