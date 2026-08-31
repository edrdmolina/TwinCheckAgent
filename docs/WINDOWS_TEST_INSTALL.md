# Windows 10 Test Install

This is the repeatable package/install/uninstall path for Noritsu and Frontier SP3000 validation.

## Build the package

From the repository root in PowerShell:

```powershell
.\scripts\package-win.ps1
```

To label a build with another version:

```powershell
.\scripts\package-win.ps1 -PackageVersion 0.1.1
```

The build produces both an unpacked directory and a transfer-ready ZIP:

```text
artifacts\win-x64\package
artifacts\win-x64\TwinCheck-Scan-Agent-0.1.0-win-x64.zip
```

The package is self-contained, so Windows 10 does not need .NET installed.

## Install

1. Copy the ZIP to the Windows 10 scanner computer.
2. Extract the entire ZIP.
3. Double-click `Install-TwinCheck.cmd`.
4. Approve the administrator prompt.

For a NAS/UNC destination, use an elevated PowerShell window so you can supply the service account:

```powershell
$cred = Get-Credential
.\install.ps1 -ServiceCredential $cred
```

The installer uses shared machine paths:

```text
C:\Program Files\TwinCheck\ScanAgent
C:\ProgramData\TwinCheck\ScanAgent\agent-config.json
C:\ProgramData\TwinCheck\ScanAgent\logs
C:\ProgramData\TwinCheck\ScanAgent\operations
```

It also creates and trusts the local HTTPS certificate, creates a shared config
with a unique API key when needed, installs the Windows service, creates
desktop/Start menu shortcuts, and registers the app under **Settings > Apps**.
Reinstall preserves the shared key and profiles.

## Configure and verify

1. Open TwinCheck Scan Agent from the desktop or Start menu.
2. Generate or confirm the API key.
3. Create the Noritsu and/or Frontier profile.
4. Set source and destination folders and save.
5. Click **Refresh** and confirm the API is connected.

```powershell
Get-Service "TwinCheck Scan Agent"
curl.exe -k https://localhost:3625/
curl.exe -k -H "X-Api-Key: YOUR_API_KEY" https://localhost:3625/api/scan/health
```

## Reinstall

Build and extract the replacement ZIP, then double-click `Reinstall-TwinCheck.cmd`. Profiles, logs, and durable operation records are preserved.

For a NAS/UNC service account:

```powershell
$cred = Get-Credential
.\reinstall.ps1 -ServiceCredential $cred
```

If PowerShell reports that running scripts is disabled, allow scripts only for
the current PowerShell session, unblock the downloaded script, and retry in the
same window so `$cred` remains available:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
Unblock-File .\reinstall.ps1
.\reinstall.ps1 -ServiceCredential $cred
```

The `Process` policy expires when that PowerShell window closes. Only bypass
the policy when the package came from a trusted source. If the command is still
blocked, run `Get-ExecutionPolicy -List`; a defined `MachinePolicy` or
`UserPolicy` is controlled by Group Policy and requires an administrator.

## Uninstall

Use **Settings > Apps**, the Start menu uninstall shortcut, or double-click `Uninstall-TwinCheck.cmd` in the extracted package.

The default uninstall preserves profiles and logs. For a completely clean test:

```powershell
.\uninstall.ps1 -PurgeData
```

See the `README.md` included in the ZIP for service-account and certificate troubleshooting.
