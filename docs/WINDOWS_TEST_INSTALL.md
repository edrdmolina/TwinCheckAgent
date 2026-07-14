# Windows 10 Test Install

This is the fast test packaging path for Noritsu and Frontier SP3000 validation.

## Build Package

From the repo root on a development machine:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\package-win.ps1
```

Copy this folder to the Windows 10 scanner machine:

```text
artifacts\win-x64\package
```

## Install

On the Windows 10 scanner machine, open PowerShell as Administrator:

```powershell
cd path\to\package
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

## Configure

1. Open the TwinCheck Scan Agent desktop shortcut.
2. Generate or confirm the API key.
3. Create one profile for the Noritsu scanner.
4. Create one profile for the Frontier SP3000 LAN path if testing it from the same Windows 10 machine.
5. Save config.
6. Click Refresh and confirm the API is connected.

The installer sets the API service and GUI to use the same machine-wide paths:

```text
C:\ProgramData\TwinCheck\ScanAgent\agent-config.json
C:\ProgramData\TwinCheck\ScanAgent\logs
```

That avoids the Windows service reading a different profile/API-key config than the desktop GUI.

## Browser Certificate

Open this once in the browser that will run TwinCheckN:

```text
https://localhost:3625
```

Accept the local development certificate if prompted.

## Verify

```powershell
Get-Service "TwinCheck Scan Agent"
curl.exe -k https://localhost:3625/
curl.exe -k -H "X-Api-Key: YOUR_API_KEY" https://localhost:3625/api/scan/health
```

## Rebuild and Reinstall

After code changes:

```powershell
.\scripts\package-win.ps1
```

Copy the new `artifacts\win-x64\package` folder to the Windows 10 machine and run:

```powershell
.\reinstall.ps1
```

## Uninstall

```powershell
.\uninstall.ps1
```

To remove config and logs too:

```powershell
.\uninstall.ps1 -PurgeData
```
