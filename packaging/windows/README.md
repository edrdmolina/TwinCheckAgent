# TwinCheck Scan Agent — Windows 10

This package is self-contained; the scanner computer does not need .NET installed.

## Install

Extract the entire ZIP, then double-click:

```text
Install-TwinCheck.cmd
```

Approve the Windows administrator prompt. The installer:

- copies the app to `C:\Program Files\TwinCheck\ScanAgent`
- installs and starts the `TwinCheck Scan Agent` Windows service
- creates and trusts a machine-local `localhost` HTTPS certificate
- creates a shared config with a unique API key when one does not already exist
- creates desktop and Start menu shortcuts
- adds TwinCheck Scan Agent to **Settings > Apps** for uninstall
- stores shared profiles, logs, and durable operation records under `C:\ProgramData\TwinCheck\ScanAgent`

For an advanced install, open PowerShell as Administrator and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1
```

## NAS or UNC destinations

`LocalSystem`, the default service account, usually cannot access paths such as `\\server\share\folder`. Install with a Windows account that can write to the share:

```powershell
$cred = Get-Credential
.\install.ps1 -ServiceCredential $cred
```

Use the full account name, such as `DESKTOP-V0ENIS3\admin`, and the account password. Windows services cannot sign in with a Windows Hello PIN. The installer grants this account access to the TwinCheck data folder and the local **Log on as a service** right.

You can validate the credential first:

```powershell
$cred = Get-Credential
Start-Process powershell -Credential $cred -ArgumentList "-NoExit", "-Command", "whoami"
```

## Reinstall a test build

Extract the replacement ZIP and double-click `Reinstall-TwinCheck.cmd`, or run:

```powershell
.\reinstall.ps1
```

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

Reinstall preserves the shared API key, profiles, logs, and operation records.

## Uninstall

Use any of these methods:

- **Settings > Apps > TwinCheck Scan Agent > Uninstall**
- **Start > TwinCheck Scan Agent > Uninstall TwinCheck Scan Agent**
- double-click `Uninstall-TwinCheck.cmd` in the extracted package
- run `.\uninstall.ps1` from an elevated PowerShell window

The default uninstall preserves profiles and logs for the next test install. To remove those too:

```powershell
.\uninstall.ps1 -PurgeData
```

## Verify

```powershell
Get-Service "TwinCheck Scan Agent"
curl.exe -k https://localhost:3625/
curl.exe -k -H "X-Api-Key: YOUR_API_KEY" https://localhost:3625/api/scan/health
```

Open the GUI from the desktop or Start menu shortcut.

## Troubleshooting

Check service state, Windows events, and agent logs:

```powershell
Get-Service "TwinCheck Scan Agent"

Get-EventLog -LogName Application -Newest 20 |
  Where-Object { $_.Source -like "*TwinCheck*" -or $_.Message -like "*TwinCheck*" } |
  Format-List

Get-Content "C:\ProgramData\TwinCheck\ScanAgent\logs\agent-*.log" -Tail 100
```

Check which Windows account runs the service:

```powershell
Get-WmiObject Win32_Service -Filter "Name='TwinCheck Scan Agent'" |
  Select-Object Name, StartName, State
```

If Event Viewer reports a missing HTTPS certificate, run this from the extracted package or installed application folder as Administrator:

```powershell
.\repair-certificate.ps1
```
