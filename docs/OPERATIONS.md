# TwinCheck Scan Agent Operations

## Install And Run

The agent has two processes:

- API: local HTTPS service at `https://localhost:3625`
- GUI: local dashboard for profiles, health diagnostics, logs, and setup guidance

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

- `frontier-polling-watch` (Frontier Polling Watcher): source is a target root with direct child roll folders.
- `frontier-sentinel-watch` (Frontier Sentinel Watcher): source is a target root where completed roll folders contain `export.done`.
- `noritsu-watch` (Noritsu Watcher): source is the Noritsu export root. The agent creates and watches the daily `YYYYMMDD` folder.

Legacy profile values `frontier-folder` and `noritsu-daily-watch` are accepted and normalized when profiles are saved.

`Stable seconds` is the required quiet period, `Settle timeout` is the maximum time an identified folder may take to become stable, and `Watch timeout` is the maximum time to wait for a new scanner folder. Settle timeout fails closed; it never processes a folder that is still changing. New polling profiles default to 30/3600/3600 seconds. Existing saved profiles retain their values and should be updated if they still use 5/120.

`Convert BMP to lossless TIFF (.tif)` is an opt-in profile setting. When enabled, BMP images are written to the destination as single-page, uncompressed TIFF files after the agent verifies exact pixel equality. Other image formats are copied unchanged. The original BMP files remain byte-for-byte intact in the destination's `_processed` archive. Existing profiles and newly created profiles default to conversion off.

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
5. Use Mark as Scanned for the selected roll. The durable operation starts the selected profile's watcher automatically; Preview Folders remains a diagnostic fallback.

## Frontier Workflow

1. Scanner exports one roll folder into the configured target folder.
2. In TwinCheckN, open the roll.
3. Click Mark as Scanned.
4. For polling profiles, the agent detects the next child folder with images and waits for stability.
5. For sentinel profiles, the agent waits for `export.done`, then waits for stability.
6. TwinCheckN automatically processes the ready source, records the scan job, then advances status.

Processing is queued by the local agent. TwinCheckN polls the persisted operation state and can resume the status check after a browser timeout, popup closure, or agent restart.

## Scan Jobs in the Agent GUI

Open **Jobs** in the TwinCheck Scan Agent GUI to see all active scan operations and the 100 most recent finished jobs across all profiles. The list shows each order and roll, profile, status, phase, and file progress. Select a job for its source candidate, resolved source, destination, timestamps, and any error. The page refreshes every five seconds while open; **Refresh Jobs** updates it immediately.

To stop a pending job, select it, click **Cancel Selected Job**, then confirm. Cancellation is available only while the operation is queued, watching for scanner output, or settling the folder. The agent persists the `cancelled` status so a restart does not resume that job. Cancelling the job does not move or delete scanner files. Once hashing, copying, verification, finalization, or archiving has started, the agent rejects cancellation to avoid interrupting file processing. The API also exposes `GET /api/scan/operations?limit=100` and `POST /api/scan/operations/{idempotencyKey}/cancel` with the configured API key.

For a NixOS scanner that has **not** received this version yet, pending operations can be set aside without deleting their records. Stop `twincheck-scan-agent.service` with `systemctl --user stop twincheck-scan-agent.service`, then move only operation JSON files whose status is `queued` or `processing` and phase is `queued`, `watching`, or `settling` from `~/.local/state/TwinCheck/ScanAgent/operations/` into a dated backup directory. If any active operation is in a later phase, inspect its output before moving records. Restart with `systemctl --user start twincheck-scan-agent.service`. A service restart alone resumes saved operations. This manual procedure changes only agent state; TwinCheckN may still show its previously saved scan record until its status check runs again.

Preview Folders remains available for manual verification and troubleshooting.

## Noritsu Workflow

1. In TwinCheckN, open the roll.
2. Click Mark as Scanned.
3. Scan the roll on the Noritsu.
4. The agent detects the next new direct child folder in today’s `YYYYMMDD` folder.
5. TwinCheckN automatically processes the ready source, records the scan job, then advances status.

## Rollback

Rollback is files-only. It moves files listed in the manifest back to the archived source folder and marks the manifest as rolled back. It does not change TwinCheckN roll or order status.

For converted scans, rollback places the generated `.tif` beside the archived original `.bmp`; it never stores TIFF content under a BMP filename.

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

- Use the GUI Overview and Diagnostics pages first. They check API reachability, profile readiness, source/destination paths, API key status, active watches, and recent operations.
- Use the GUI Logs page or `GET /api/scan/logs/recent?lines=200` to inspect recent agent log entries.
- `GET /api/scan/operations/{idempotencyKey}` reports durable phase and byte/file progress for a queued scan.
- Health warning for API key: generate a unique key in the GUI and update TwinCheckN.
- Source missing: confirm the profile path and LAN mount.
- Destination not writable: confirm filesystem permissions and NAS/LAN availability.
- Browser failed to fetch: open `https://localhost:3625` in the same browser and accept the certificate.
- Noritsu watch timeout: confirm the scanner exports a new direct child folder under today’s `YYYYMMDD` folder.
- Settle timeout: wait for scanner output to finish and retry. The agent intentionally processes nothing when stability cannot be proven.
