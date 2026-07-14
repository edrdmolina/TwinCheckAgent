# TwinCheck Scan Agent Windows Test Package

Run these commands from an elevated PowerShell window.

## Install

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

The installer copies the app to `C:\Program Files\TwinCheck\ScanAgent`, installs the `TwinCheck Scan Agent` Windows service, creates a desktop shortcut, and sets shared config/log paths under `C:\ProgramData\TwinCheck\ScanAgent`.

## Reinstall after rebuilding

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\reinstall.ps1
```

## Uninstall

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\uninstall.ps1
```

Preserve config/logs by default. To remove `C:\ProgramData\TwinCheck\ScanAgent`:

```powershell
.\uninstall.ps1 -PurgeData
```

## Verify

```powershell
Get-Service "TwinCheck Scan Agent"
curl.exe -k https://localhost:3625/
curl.exe -k -H "X-Api-Key: YOUR_API_KEY" https://localhost:3625/api/scan/health
```

Open the GUI with the desktop shortcut or:

```powershell
& "$env:ProgramFiles\TwinCheck\ScanAgent\gui\TwinCheck.Agent.Gui.exe"
```
