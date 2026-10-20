# TwinCheck Scan Agent — Windows 10/11

TwinCheck Scan Agent installs for the signed-in Windows user and runs as a tray app. The
scan agent API that TwinCheckN talks to (`https://localhost:3625`) runs inside that app, so
it's available whenever the scanner account is signed in. No administrator rights are needed
to install or update it.

The installer includes .NET and ExifTool, so nothing else needs to be installed.

## Install

1. Download `TwinCheckScanAgent-win-Setup.exe` from the latest
   [GitHub release](https://github.com/edrdmolina/TwinCheckAgent/releases/latest).
2. Double-click it. If Windows SmartScreen says "Windows protected your PC", click
   **More info**, then **Run anyway**. Builds aren't code-signed yet.
3. The app opens when the install finishes. Windows asks whether to install a certificate
   for "localhost". Click **Yes**. This lets Chrome and Edge connect to the agent without
   warnings, and Windows only asks once.
4. Open **Setup**, copy the API key into TwinCheckN, then create your scanner profiles and
   click **Save Config**.

Setup adds shortcuts to the desktop and Start menu and an entry under **Settings > Apps**.

### Upgrading from the old service version

Older builds installed a Windows service with an admin-only PowerShell script. On the first
start after installing this version, a yellow banner says the old version must be removed:

1. Let any scans that are running finish.
2. Click **Remove old version** and choose **Yes** in the Windows permission prompt.
   This is the only step that needs an administrator.

The app removes the old service, certificates, and Program Files folder. It copies your
profiles, API key, and job history from `C:\ProgramData\TwinCheck\ScanAgent` into your
user folder, so TwinCheckN keeps working without changes. The original files in
ProgramData are left in place; delete them once you've confirmed everything works.

## Everyday use

- The app starts automatically, minimized to the tray, when you sign in to Windows.
- Closing the window keeps the agent running in the tray. Open it again from the tray icon
  or the Start menu.
- To stop the agent, right-click the tray icon and choose **Quit**. If scans are running,
  the app asks first. Interrupted scans resume the next time the app starts.

## Updates

The app checks for a new release every 6 hours and downloads it in the background. When an
update is ready, a blue banner appears with **Restart to update**:

- The button is disabled while a roll is being processed on this computer and enables once it
  finishes. Rolls waiting, watching, or uploading resume after the restart.
- Restarting takes a few seconds. TwinCheckN can't reach the agent during that time.
- If nobody clicks the button, the update installs the next time the app starts, for
  example after a reboot or the next sign-in.

## NAS and network destinations

The agent runs as the signed-in user, so it can reach any share that user can open in
File Explorer, including mapped drive letters. No separate service account is needed.

Rolls are processed in local staging and then uploaded to the destination one file at a time;
the **Uploads** page shows the queue. Keep enough local disk space for several rolls. Scanners must
save each roll in its own folder inside the watch folder; after processing, the folder is renamed
to `_processed-{order}-{roll}` and deleted once every file is verified on the destination.

## Uninstall

Use **Settings > Apps > TwinCheck Scan Agent > Uninstall**. This removes the app and its
sign-in startup entry. Profiles, the API key, job history, and logs are kept in
`%AppData%\TwinCheck\ScanAgent`; delete that folder for a completely clean removal.

## Files

| What | Where |
|------|-------|
| App | `%LocalAppData%\TwinCheckScanAgent` |
| Config and API key | `%AppData%\TwinCheck\ScanAgent\agent-config.json` |
| Job history and upload queue | `%AppData%\TwinCheck\ScanAgent\jobs.sqlite3` |
| Local staging (rolls waiting to upload) | `%LocalAppData%\TwinCheck\ScanAgent\staging` |
| Logs | `%AppData%\TwinCheck\ScanAgent\logs` |
| HTTPS certificate | Current user's certificate store, "TwinCheck Scan Agent Localhost" |

## Troubleshooting

Check the agent from PowerShell:

```powershell
curl.exe https://localhost:3625/
curl.exe -H "X-Api-Key: YOUR_API_KEY" https://localhost:3625/api/scan/health
Get-Content "$env:APPDATA\TwinCheck\ScanAgent\logs\agent-*.log" -Tail 100
```

- **"Port 3625 is already in use"**: another program, often an old TwinCheck service that
  wasn't fully removed, is using the port. Find it with
  `Get-NetTCPConnection -LocalPort 3625 | Select-Object OwningProcess`, close it, then
  click **Retry**.
- **Browser certificate warning**: the certificate prompt was declined. Click
  **Trust certificate** in the app's banner and choose **Yes**.
- **Old version removal failed**: details are in
  `C:\ProgramData\TwinCheck\ScanAgent\logs\legacy-removal.log`.
