# TwinCheck Scan Agent Operations

## Install And Run

The agent has two processes:

- API: local HTTPS service at `https://localhost:3625`
- GUI: local profile editor and health monitor

For Ubuntu testing:

```bash
git clone https://github.com/edrdmolina/TwinCheckAgent.git
cd TwinCheckAgent
./scripts/dotnet.sh run --project src/TwinCheck.Agent.Api
./scripts/dotnet.sh run --project src/TwinCheck.Agent.Gui
```

Open the GUI, create scanner profiles, generate a unique API key, and save.

## Profiles

Each scanner should have its own profile.

- `frontier-folder`: source is an existing roll folder or a target root with roll folders.
- `noritsu-daily-watch`: source is the Noritsu export root. The agent creates and watches the daily `YYYYMMDD` folder.

Destination output uses:

```text
<destinationRoot>/<week-MM-DD-YY>/<orderNumber>/<orderNumber>-<rollNumber>/
```

QC rescans use:

```text
<destinationRoot>/<week-MM-DD-YY>/<orderNumber>/<orderNumber>-<rollNumber>-rescan-2/
```

## Browser Setup

In TwinCheckN:

1. Open Scanning.
2. Enter the agent URL, API key, and profile.
3. Click Test Agent.
4. Accept the local certificate in the dispatch browser if prompted.
5. Use Preview Folders or Start Noritsu Watch before marking a roll scanned.

## Frontier Workflow

1. Scanner exports one roll folder into the configured target folder.
2. In TwinCheckN, open the roll.
3. Click Preview Folders to verify image count and destination.
4. Choose the folder or click Mark as scanned.
5. TwinCheckN moves files first, records the scan job, then advances status.

## Noritsu Workflow

1. In TwinCheckN, open the roll.
2. Click Start Noritsu Watch.
3. Scan the roll on the Noritsu.
4. The agent detects the next new direct child folder in today’s `YYYYMMDD` folder.
5. Confirm the detected source folder.
6. TwinCheckN moves files first, records the scan job, then advances status.

## Rollback

Rollback is files-only. It moves files listed in the manifest back to the archived source folder and marks the manifest as rolled back. It does not change TwinCheckN roll or order status.

## Ubuntu Autostart

Create `/etc/systemd/system/twincheck-scan-agent.service`:

```ini
[Unit]
Description=TwinCheck Scan Agent API
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/TwinCheckAgent
ExecStart=/usr/bin/dotnet run --project /opt/TwinCheckAgent/src/TwinCheck.Agent.Api
Restart=always
RestartSec=5
Environment=ASPNETCORE_URLS=https://localhost:3625

[Install]
WantedBy=multi-user.target
```

Then:

```bash
sudo systemctl daemon-reload
sudo systemctl enable twincheck-scan-agent
sudo systemctl start twincheck-scan-agent
sudo systemctl status twincheck-scan-agent
```

For production packaging, replace `dotnet run` with a published binary path.

## Troubleshooting

- Health warning for API key: generate a unique key in the GUI and update TwinCheckN.
- Source missing: confirm the profile path and LAN mount.
- Destination not writable: confirm filesystem permissions and NAS/LAN availability.
- Browser failed to fetch: open `https://localhost:3625` in the same browser and accept the certificate.
- Noritsu watch timeout: confirm the scanner exports a new direct child folder under today’s `YYYYMMDD` folder.
