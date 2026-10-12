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

### Job Storage and Retention

Job tracking lives in `jobs.sqlite3` in the agent state directory:

- Windows installer: `C:\ProgramData\TwinCheck\ScanAgent\jobs.sqlite3`
- NixOS installer: `~/.local/state/TwinCheck/ScanAgent/jobs.sqlite3` (or the configured XDG state directory)
- `TWINCHECK_AGENT_STATE_DIR` overrides the state directory on either platform.

Only active jobs are cached in the API process. The Jobs page queries indexed summaries directly from SQLite, and individual historical requests/results are fetched on demand. Progress updates change database fields without rewriting the full request or result. Completed API results remain stored for the history window to preserve polling compatibility; the manifest JSON files in destination roll folders remain the authoritative files for recovery and rollback. Configuration and text logs keep their existing formats.

Cleanup runs at startup and every hour. Completed, failed, and cancelled job details are deleted **30 days after finishing**. Their job keys, request fingerprints, final statuses, and completion timestamps remain for **180 days after finishing**, preventing duplicate processing during that period. Active jobs are never removed by age. Cleanup also gradually reclaims unused database pages; it never deletes scans, archives, or roll-folder manifests.

During the remaining key-retention period, fetching or re-submitting an identical job whose detailed history has expired returns HTTP **410 Gone**, with `code: "job-history-expired"`, the job key, final status, and completion timestamp. Reusing that key for a different request still returns an idempotency conflict. After 180 days, the key can be accepted again; existing roll-folder manifests may still prevent repeating file work.

On the first startup after upgrading, the agent automatically imports existing `operations/operation-*.json` files. Each readable file is removed only after its database transaction commits. Repeating an interrupted migration preserves newer database state. Unreadable legacy files are left in place and logged for inspection; conflicting records or failures to retire imported files stop startup so they can be resolved. Imported history is subject to the same retention policy.

Use **Cancel Selected Job** to cancel waiting jobs. Moving old JSON files no longer controls jobs after migration. Stop the API service before copying `jobs.sqlite3` for an offline backup; do not delete the database to cancel a job, because that also discards retained keys and recovery state. Keep the database on the scanner's local disk.

Preview Folders remains available for manual verification and troubleshooting.

## Noritsu Workflow

1. In TwinCheckN, open the roll.
2. Click Mark as Scanned.
3. Scan the roll on the Noritsu.
4. The agent detects the next new direct child folder in today’s `YYYYMMDD` folder.
5. TwinCheckN automatically processes the ready source, records the scan job, then advances status.

## Photo Metadata

Set **Scanner make**, **Scanner model**, and optional **Fallback artist** in the GUI's **Profiles → Photo Metadata** settings. For an SP500 profile, use `Fujifilm` and `SP500`. Blank settings preserve existing tags; the watermarks shown in the GUI are examples, not saved defaults.

TwinCheckN can send an optional top-level object on its existing scan-operation request:

```json
"metadata": {
  "customerName": "José Núñez",
  "filmStock": "Kodak Portra 400"
}
```

Customer name is written to EXIF Artist and XMP `dc:creator`. If it is absent or blank, the local profile's Artist is used when configured. Film stock is written to XMP `twincheck:FilmStock` and appended once to `dc:subject` keywords while preserving unrelated keywords. The namespace URI is **`urn:twincheck:scan-metadata:1.0`**, with prefix **`twincheck`**. EXIF Make/Model and their XMP equivalents come exclusively from the local profile; request `options.exif` values cannot override local identity or fallback Artist. Missing/blank values do not create empty or placeholder tags.

The agent edits only staged delivered output, after any BMP-to-TIFF conversion. It reads the requested tags back to verify them, recalculates the final file size and SHA256, and then runs normal staged checksum verification before finalization. Metadata edits use ExifTool without recompressing pixels. Archived originals stay byte-identical. JPEG, TIFF, and PNG support these tags; **BMP output must be converted to TIFF when metadata is requested**. Metadata writing or verification failure fails the job with `errorCode: "metadata-write-failed"`; it does not silently complete or archive the source. Dry runs do not write delivered metadata.

### Unicode policy

XMP stores the original Unicode name as UTF-8. EXIF Artist also stores the original name using the **Metadata Working Group UTF-8 convention**, explicitly configured through ExifTool's `-charset exif=UTF8`. This deliberately favors retaining names such as `José Núñez` over strict ASCII conformance or lossy transliteration. Legacy readers that assume ASCII may misdisplay EXIF Artist; XMP Creator is the standards-compliant Unicode value. See [ExifTool's character-set guidance](https://exiftool.org/faq.html#Q10).

### Capability and rollout

`GET /api/scan/health` advertises **`scan-metadata-v1`** only when the ExifTool executable is available. If unavailable, health includes a warning and omits this capability. Windows packages bundle ExifTool and its Perl runtime under `tools/exiftool`; NixOS installation resolves ExifTool from Nix and configures its absolute executable path. For development or other installations, install ExifTool on PATH or set **`TWINCHECK_EXIFTOOL_PATH`** to the full executable path. Windows package builds download ExifTool 13.59 with pinned official SHA256 hashes; `-ExifToolDirectory` allows an existing extracted distribution instead.

The prior agent **ignores unknown request fields**, including `metadata`; it does not reject them. TwinCheckN should still gate metadata on `scan-metadata-v1`, because an older agent otherwise accepts a job but silently ignores those fields.

Fingerprints hash the canonical typed request, not raw HTTP bytes. The serializer uses the fixed field order of the request and nested option/metadata DTOs, making incoming JSON/JSONB key order irrelevant. Null/absent `metadata` is omitted from serialization, preserving the original indented UTF-8 fingerprint bytes exactly; supplied metadata participates in the hash. An empty object is supplied metadata and is distinct from an absent object. Same key plus different metadata is a conflict, never a second scan. The pre-metadata API fixture in `tests/TwinCheck.Agent.Tests/Fixtures/legacy-job-fingerprint.json` verifies backward compatibility, including migration from saved jobs.

### Verification

After installing ExifTool, run the usual .NET tests and these isolated API checks against a built API:

```bash
python3 tests/api/sqlite-lifecycle.py
python3 tests/api/metadata-lifecycle.py
```

To retain a generated-photo scan, delivered output, archived original, ExifTool readback, and checksum evidence, pass a new output directory:

```bash
python3 tests/api/metadata-lifecycle.py --evidence-dir artifacts/metadata-evidence
```

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
