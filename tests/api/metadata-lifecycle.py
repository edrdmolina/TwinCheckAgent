#!/usr/bin/env python3
"""Run a real API scan job on a generated PNG and collect delivered metadata evidence."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import tempfile
import time
import zlib
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def png():
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    rows = b"".join(b"\0" + bytes([180, 30, 70]) * 32 for _ in range(24))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 32, 24, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b"")


def reverse_keys(value):
    if isinstance(value, dict):
        return {key: reverse_keys(item) for key, item in reversed(list(value.items()))}
    if isinstance(value, list):
        return [reverse_keys(item) for item in value]
    return value


def run(api, root, exiftool):
    root.mkdir(parents=True, exist_ok=True)
    source = root / "source" / "José's roll"
    output = root / "output"
    source.mkdir(parents=True)
    output.mkdir()
    original_bytes = png()
    (source / "frame001.png").write_bytes(original_bytes)
    original_hash = hashlib.sha256(original_bytes).hexdigest()
    config = root / "config.json"
    config.write_text(json.dumps(dict(
        apiKey="metadata-test-key", allowedSourceRoots=[str(root / "source")],
        allowedDestinationRoots=[str(output)], profiles=[dict(
            id="sp500", name="Fujifilm SP500", sourceDir=str(root / "source"),
            destinationDir=str(output), scannerMode="frontier-polling-watch",
            options=dict(bmpToTiff=True, exif=dict(make="Fujifilm", model="SP500")))])))
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    base = f"http://127.0.0.1:{port}"
    env = os.environ | dict(
        DOTNET_ROLL_FORWARD="Major", TWINCHECK_AGENT_STATE_DIR=str(root / "state"),
        TWINCHECK_AGENT_CONFIG_PATH=str(config), TWINCHECK_AGENT_LOG_DIR=str(root / "logs"),
        TWINCHECK_EXIFTOOL_PATH=str(exiftool), ASPNETCORE_ENVIRONMENT="Production")
    with (root / "api.log").open("w") as log:
        process = subprocess.Popen(["dotnet", str(api), "--urls", base], cwd=root, env=env, stdout=log, stderr=log)

    def call(method, path, body=None):
        request = Request(base + path, method=method,
                          headers={"X-Api-Key": "metadata-test-key", "Content-Type": "application/json"},
                          data=None if body is None else json.dumps(body, ensure_ascii=False).encode())
        try:
            response = urlopen(request, timeout=5)
        except HTTPError as error:
            response = error
        with response:
            return response.status, json.loads(response.read())

    try:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError((root / "api.log").read_text())
            try:
                if call("GET", "/")[0] == 200:
                    break
            except URLError:
                pass
            time.sleep(.1)
        code, health = call("GET", "/api/scan/health")
        assert code == 200 and "scan-metadata-v1" in health["capabilities"], health
        request = dict(
            idempotencyKey="metadata-evidence", profileId="sp500", sourceDir=str(source),
            orderNumber="B123", rollNumber="1", waitForReady=False,
            metadata=dict(customerName="José Núñez", filmStock="Kodak Portra 400",
                          make="Ignored request make", model="Ignored request model"),
            options=dict(bmpToTiff=True, exif=dict(make="Injected make", model="Injected model", artist="Injected artist")),
            futureUnknownField="accepted and ignored")
        code, body = call("POST", "/api/scan/operations", request)
        assert code == 202, body
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            code, body = call("GET", "/api/scan/operations/metadata-evidence")
            if body["operation"]["status"] in ("completed", "failed"):
                break
            time.sleep(.1)
        assert body["operation"]["status"] == "completed", body
        result = body["operation"]["result"]
        file = result["manifest"]["files"][0]
        delivered = Path(file["destinationPath"])
        # The roll was processed in local staging, uploaded, verified, and then its originals were deleted.
        processed = Path(result["manifest"]["sourceArchiveDir"])
        assert processed.name == "_processed-B123-1" and processed.parent == source.parent, processed
        assert not source.exists() and not processed.exists()
        assert not any((root / "state" / "staging").iterdir())
        assert hashlib.sha256(delivered.read_bytes()).hexdigest() == file["sha256"]
        assert file["sourceSha256"] == original_hash and file["sha256"] != original_hash
        code, retry = call("POST", "/api/scan/operations", reverse_keys(request))
        assert code == 200 and retry["operation"]["idempotencyKey"] == "metadata-evidence", retry
        changed = request | dict(metadata=dict(customerName="José Núñez", filmStock="Ilford HP5 Plus"))
        code, conflict = call("POST", "/api/scan/operations", changed)
        assert code == 409 and conflict["code"] == "idempotency-conflict", conflict
        code, jobs = call("GET", "/api/scan/operations")
        assert code == 200 and len(jobs["operations"]) == 1, jobs
        assert jobs["localProcessingActive"] == 0 and jobs["uploadsPending"] == 0, jobs
        code, uploads = call("GET", "/api/scan/uploads")
        assert code == 200 and [row["status"] for row in uploads["uploads"]] == ["done"], uploads

        tags = json.loads(subprocess.check_output([
            str(exiftool), "-j", "-struct", "-G1", "-charset", "exif=UTF8", "-charset", "filename=UTF8",
            "-EXIF:Artist", "-EXIF:Make", "-EXIF:Model", "-XMP-dc:Creator", "-XMP-dc:Subject",
            "-XMP:FilmStock", str(delivered)], encoding="utf-8"))[0]
        assert tags["IFD0:Artist"] == "José Núñez"
        assert tags["IFD0:Make"] == "Fujifilm" and tags["IFD0:Model"] == "SP500"
        assert tags["XMP-dc:Creator"] == ["José Núñez"]
        assert tags["XMP-twincheck:FilmStock"] == "Kodak Portra 400"
        assert "Kodak Portra 400" in tags["XMP-dc:Subject"]
        (root / "exiftool-output.json").write_text(json.dumps(tags, ensure_ascii=False, indent=2) + "\n")
        (root / "verification.json").write_text(json.dumps(dict(
            source="Generated PNG; processed through a real Scanner Agent HTTP job",
            capability="scan-metadata-v1", namespace="urn:twincheck:scan-metadata:1.0", prefix="twincheck",
            delivered=str(delivered), originalSha256=original_hash, sourceSha256=file["sourceSha256"],
            deliveredSha256=file["sha256"], deliveredChecksumVerified=True, originalDeletedAfterUpload=True,
            reorderedRetryAccepted=True, changedMetadataConflict=True, unknownFieldsAccepted=True), indent=2) + "\n")
        print(json.dumps(tags, ensure_ascii=False, indent=2))
        print("Passed: real scan pipeline, Unicode metadata, local scanner identity, retries/conflicts, upload queue, and original cleanup.")
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    repo = Path(__file__).resolve().parents[2]
    parser.add_argument("--api", type=Path, default=repo / "src/TwinCheck.Agent.Api/bin/Debug/net9.0/TwinCheck.Agent.Api.dll")
    parser.add_argument("--evidence-dir", type=Path)
    args = parser.parse_args()
    exiftool = os.environ.get("TWINCHECK_EXIFTOOL_PATH") or shutil.which("exiftool")
    assert exiftool, "Install ExifTool or set TWINCHECK_EXIFTOOL_PATH"
    if args.evidence_dir:
        assert not args.evidence_dir.exists(), "Choose a new evidence directory"
        run(args.api.resolve(), args.evidence_dir.resolve(), Path(exiftool).resolve())
    else:
        with tempfile.TemporaryDirectory(prefix="twincheck-metadata-api-") as directory:
            run(args.api.resolve(), Path(directory), Path(exiftool).resolve())


if __name__ == "__main__":
    main()
