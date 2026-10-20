# TwinCheck Scan Agent Operations

## Install And Run

The agent has two parts:

- API: local HTTPS service at `https://localhost:3625`
- GUI: local dashboard for profiles, health diagnostics, logs, and setup guidance

On Windows both run in one tray app installed per user; see
[packaging/windows/README.md](../packaging/windows/README.md). On Linux and NixOS the API runs as
a separate service and the GUI connects to it.

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

`Convert BMP to lossless TIFF (.tif)` is an opt-in profile setting. When enabled, BMP images are written to the destination as single-page, uncompressed TIFF files after the agent verifies exact pixel equality. Other image formats are copied unchanged. Conversion happens in local staging, never on the destination share. Like all originals, the BMP files are deleted once every converted file is verified on the destination. Existing profiles and newly created profiles default to conversion off.

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

1. Scanner exports one roll folder into the configured target folder. Images saved directly in the target folder, outside a roll folder, are ignored.
2. In TwinCheckN, open the roll.
3. Click Mark as Scanned.
4. For polling profiles, the agent takes the newest child folder with images and waits for stability.
5. For sentinel profiles, the agent takes the newest folder with `export.done`, including one that was already ready before you clicked, then waits for stability.
6. TwinCheckN automatically processes the ready source, records the scan job, then advances status.

The agent only picks a folder on its own when it's the only roll in the watch folder. If two or more folders contain images or `export.done` (for example, a finished roll plus the next roll still scanning), the job stops and TwinCheckN shows **Choose the source folder**. Folders still waiting for `export.done` can't be selected. Pick the roll, then click **Mark as Scanned** again. Empty folders are ignored. The agent checks again after the folder settles, so a second roll that appears during that time also triggers the choice. Noritsu daily folders follow the same rule.

Processing is queued by the local agent. TwinCheckN polls the persisted operation state and can resume the status check after a browser timeout, popup closure, or agent restart. While the roll is being sent to the destination, TwinCheckN shows `uploading (n/N files)`; the roll is complete only once every file is verified there.

## Local Processing and the Upload Queue

Each roll goes through two stages:

1. **Local processing.** The agent copies the roll from the scanner folder into local staging, then converts BMPs, writes photo metadata, and verifies checksums there. Only one roll is processed at a time on each computer, across all scanner profiles, so a second scanner's roll waits its turn. When processing finishes, the roll folder is renamed in place to `_processed-{order}-{roll}` (with `-2`, `-3` if that name is taken) so the watch folder is clear for the next roll. The watcher and the folder picker ignore `_processed-…` folders.
2. **Upload.** The roll joins the computer's upload queue. One worker uploads one file at a time, oldest roll first. Each file is copied to a hidden `.<name>.twincheck-partial` file in the destination folder, flushed to the destination every 64 MB, verified by re-reading it there, then renamed to its final name. The next roll can be processed locally while an upload runs. After the last file, the agent writes the manifest and deletes the `_processed-…` folder and the local copy.

The `_processed-…` folder is deleted only after every file is verified and the manifest is written. Control files (`export.done`, `Thumbs.db`, `desktop.ini`, `._*`) are deleted with it. If the folder holds anything that wasn't uploaded, such as a subfolder or a file added after processing started, it is kept, the job's message says why, and it appears in **Cleanup**.

Local staging is under the agent's state folder: `~/.local/state/TwinCheck/ScanAgent/staging` on NixOS and `%LocalAppData%\TwinCheck\ScanAgent\staging` on Windows. Set `stagingDir` in `agent-config.json` to use another local folder; never point it at a network share or at `/tmp`, which can be in memory on NixOS. Before processing a roll, the agent checks that the staging disk has room for the roll, its largest file, and 1 GB more. If not, the job fails with a clear message and the roll folder stays where it is. Rolls waiting to upload keep their local copy, so leave room for several rolls if the destination may be unavailable for a while.

The queue is stored in `jobs.sqlite3`, so a restart resumes every roll where it stopped: finished files are kept, a leftover partial file is replaced, and a roll whose manifest was already written only has its cleanup finished.

### Upload errors and stalls

- **Errors** (for example the share refusing a write): the roll shows `retrying` with the error and the next attempt time. Retries wait 30 seconds, doubling up to 5 minutes, and continue until the upload succeeds. Use **Retry Now** to skip the wait. A roll fails (`errorCode: "upload-failed"`) only when its local copy is missing or damaged; the `_processed-…` folder is then kept.
- **Stalls**: a write to a hung NFS share never returns, so it can't be retried or cancelled. If a file makes no progress for 5 minutes, the agent logs the file and step, and the roll shows `stalled — NAS not responding` in **Uploads** and in the job's message. It clears by itself if the share recovers. Otherwise fix the share, then restart the agent; the upload resumes. Local processing that stops making progress for 5 minutes is reported the same way in the job's message.

### Uploads in the Agent GUI

Open **Uploads** to see the queue: each roll's scanner profile, status (`waiting`, `uploading n/N`, `retrying`, `stalled`, `paused`, `done`, `failed`), progress, and time queued. Finished rolls stay listed for 30 days. Select a roll for its folders, attempts, and last error.

- **Pause Uploads** stops after the file being uploaded is finished and verified; **Resume Uploads** continues. Pausing is useful during NAS maintenance. Local processing continues while uploads are paused.
- **Retry Now** retries a roll that is waiting to retry.
- **Open Local Copy** and **Open Destination** open the roll's folders in the file manager. Opening a folder on a hung share freezes the file manager, as with any other program.

Queued uploads can't be cancelled or deleted, because the local copy may be the only one.

The API exposes `GET /api/scan/uploads`, `POST /api/scan/uploads/pause`, `POST /api/scan/uploads/resume`, and `POST /api/scan/uploads/{idempotencyKey}/retry`.

### Cleanup

The **Cleanup** section of **Uploads** lists:

- folders of originals kept by earlier agent versions in each destination's `_processed/` folder
- `_processed-…` roll folders that were kept because they held items that weren't uploaded

Click **Scan** to look for them; matching old archives to their manifests searches the destination, which can take a while on a large share. A folder is **verified** when a finished, not rolled back manifest refers to it and every finished file it lists exists on the destination with the right size. Verified folders can be deleted with **Delete Selected** or **Delete All Verified**, after a confirmation showing the count and size. The agent checks each folder again before deleting it. Unverified folders show the reason and can't be deleted from here. **Open Folder** opens the selected folder. The API exposes `GET /api/scan/cleanup` and `POST /api/scan/cleanup/delete` with a `paths` list.

## Scan Jobs in the Agent GUI

Open **Jobs** in the TwinCheck Scan Agent GUI to see all active scan operations and the 100 most recent finished jobs across all profiles. The list shows each order and roll, profile, status, phase, and file progress. Select a job for its source candidate, resolved source, destination, timestamps, and any error. The page refreshes every five seconds while open; **Refresh Jobs** updates it immediately.

To stop a pending job, select it, click **Cancel Selected Job**, then confirm. Cancellation is available only while the operation is queued, watching for scanner output, or settling the folder. The agent persists the `cancelled` status so a restart does not resume that job. Cancelling the job does not move or delete scanner files. Once local processing or the upload has started, the agent rejects cancellation to avoid interrupting file processing. The API also exposes `GET /api/scan/operations?limit=100` and `POST /api/scan/operations/{idempotencyKey}/cancel` with the configured API key.

### Job Storage and Retention

Job tracking lives in `jobs.sqlite3` in the agent state directory:

- Windows: `%AppData%\TwinCheck\ScanAgent\jobs.sqlite3` (older service installs used `C:\ProgramData\TwinCheck\ScanAgent`; the tray app copies that data on first start)
- NixOS installer: `~/.local/state/TwinCheck/ScanAgent/jobs.sqlite3` (or the configured XDG state directory)
- `TWINCHECK_AGENT_STATE_DIR` overrides the state directory on either platform.

Only active jobs are cached in the API process. The Jobs page queries indexed summaries directly from SQLite, and individual historical requests/results are fetched on demand. Progress updates change database fields without rewriting the full request or result. Completed API results remain stored for the history window to preserve polling compatibility; the manifest JSON files in destination roll folders remain the authoritative files for recovery and rollback. Configuration and text logs keep their existing formats.

Cleanup runs at startup and every hour. Completed, failed, and cancelled job details are deleted **30 days after finishing**. Their job keys, request fingerprints, final statuses, and completion timestamps remain for **180 days after finishing**, preventing duplicate processing during that period. Active jobs are never removed by age. Finished upload queue entries are deleted after the same 30 days. Cleanup also gradually reclaims unused database pages; it never deletes scans, archives, or roll-folder manifests.

During the remaining key-retention period, fetching or re-submitting an identical job whose detailed history has expired returns HTTP **410 Gone**, with `code: "job-history-expired"`, the job key, final status, and completion timestamp. Reusing that key for a different request still returns an idempotency conflict. After 180 days, the key can be accepted again; existing roll-folder manifests may still prevent repeating file work.

On the first startup after upgrading, the agent automatically imports existing `operations/operation-*.json` files. Each readable file is removed only after its database transaction commits. Repeating an interrupted migration preserves newer database state. Unreadable legacy files are left in place and logged for inspection; conflicting records or failures to retire imported files stop startup so they can be resolved. Imported history is subject to the same retention policy.

Use **Cancel Selected Job** to cancel waiting jobs. Moving old JSON files no longer controls jobs after migration. Stop the agent (Windows: **Quit** from the tray icon; Linux: stop the service) before copying `jobs.sqlite3` for an offline backup; do not delete the database to cancel a job, because that also discards retained keys and recovery state. Keep the database on the scanner's local disk.

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

The agent edits only staged delivered output, after any BMP-to-TIFF conversion. It reads the requested tags back to verify them, recalculates the final file size and SHA256, and then runs normal staged checksum verification before finalization. Metadata edits use ExifTool without recompressing pixels. Originals are never edited; they are deleted once the upload is verified. JPEG, TIFF, and PNG support these tags; **BMP output must be converted to TIFF when metadata is requested**. Metadata writing or verification failure fails the job with `errorCode: "metadata-write-failed"`; it does not silently complete, and the roll folder stays where it is. Dry runs do not write delivered metadata.

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

To retain a generated-photo scan, delivered output, ExifTool readback, and checksum evidence, pass a new output directory:

```bash
python3 tests/api/metadata-lifecycle.py --evidence-dir artifacts/metadata-evidence
```

## Rollback

Rollback is files-only. It moves the files listed in the manifest out of the roll folder into `<destinationRoot>/_rolledback/{order}-{roll}-{timestamp}/` and marks the manifest as rolled back. Originals are deleted after upload, so rolled-back files get their own folder, outside every watch folder, where they can't be picked up as a new roll. Rollback does not change TwinCheckN roll or order status. Converted scans keep their `.tif` name.

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
- Watch folder "contains no roll folders": the scanner saved images directly in the watch folder. Rolls must be saved in their own folder inside it.
- Upload `stalled — NAS not responding`: the destination share stopped answering. On Linux, `sudo dmesg -T | grep -iE 'nfs|blocked for more than'` shows hung writes. Fix the share or network, then restart the agent if the stall doesn't clear.
- "Not enough local disk space": free space on the staging disk, or set `stagingDir` to a larger local disk. The roll was left in place.
- Updates wait only while a roll is being processed locally. Waiting, watching, and uploading jobs resume after the restart.
- Settle timeout: wait for scanner output to finish and retry. The agent intentionally processes nothing when stability cannot be proven.
