#!/usr/bin/env python3
"""Smoke-test a built API with isolated SQLite state (Python standard library only)."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import time
from datetime import datetime, timedelta, timezone
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def scan_request(key):
    return dict(idempotencyKey=key, profileId="smoke", sourceDir=None,
                destinationDir=None, orderNumber="B123", rollNumber="1", naming=None,
                scanKind="original", rescanNumber=None, options=None, watchId=None,
                waitForReady=True, dryRun=False)


def timestamp(value):
    # Match DateTimeOffset's round-trip format, including seven fractional digits.
    return value.strftime("%Y-%m-%dT%H:%M:%S.") + f"{value.microsecond:06d}0+00:00"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api", type=Path, default=Path(__file__).resolve().parents[2] /
                        "src/TwinCheck.Agent.Api/bin/Debug/net9.0/TwinCheck.Agent.Api.dll")
    args = parser.parse_args()
    api = args.api.resolve()
    assert api.is_file(), f"Build the API first: {api}"
    with tempfile.TemporaryDirectory(prefix="twincheck-api-sqlite-") as directory:
        root = Path(directory)
        source, output = root / "source", root / "output"
        source.mkdir()
        output.mkdir()
        operations = root / "state/operations"
        operations.mkdir(parents=True)
        now = datetime.now(timezone.utc)
        for key, age in (("recent", 1), ("retained", 31), ("expired", 181)):
            request = scan_request(key)
            fingerprint = hashlib.sha256(json.dumps(request, indent=2).encode()).hexdigest()
            state = dict(operationId=key, idempotencyKey=key, requestFingerprint=fingerprint,
                         request=request, status="cancelled", phase="cancelled",
                         createdAt=timestamp(now - timedelta(days=age + 1)),
                         completedAt=timestamp(now - timedelta(days=age)))
            (operations / f"operation-{key}.json").write_text(json.dumps(state))
        config = root / "agent-config.json"
        config.write_text(json.dumps(dict(
            apiKey="sqlite-smoke-key", allowedSourceRoots=[str(source)],
            allowedDestinationRoots=[str(output)], activeProfileId="smoke",
            profiles=[dict(id="smoke", name="Smoke", sourceDir=str(source),
                           destinationDir=str(output), scannerMode="frontier-polling-watch",
                           watchTimeoutSeconds=60, settlePollSeconds=1)])))
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        env = os.environ | dict(
            DOTNET_ROLL_FORWARD="Major", TWINCHECK_AGENT_STATE_DIR=str(root / "state"),
            TWINCHECK_AGENT_CONFIG_PATH=str(config), TWINCHECK_AGENT_LOG_DIR=str(root / "logs"),
            ASPNETCORE_ENVIRONMENT="Production", ASPNETCORE_URLS=base)
        process = None
        log_path = root / "api.log"

        def call(method, path, body=None):
            request = Request(base + path, method=method, headers={
                "X-Api-Key": "sqlite-smoke-key", "Content-Type": "application/json"},
                data=None if body is None else json.dumps(body).encode())
            try:
                response = urlopen(request, timeout=3)
            except HTTPError as error:
                response = error
            with response:
                return response.status, json.loads(response.read())

        def start():
            nonlocal process
            with log_path.open("a") as log:
                process = subprocess.Popen(["dotnet", str(api), "--urls", base],
                                           cwd=root, env=env, stdout=log, stderr=log)
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(log_path.read_text())
                try:
                    if call("GET", "/")[0] == 200:
                        return
                except (URLError, TimeoutError):
                    pass
                time.sleep(0.1)
            raise RuntimeError("API startup timed out\n" + log_path.read_text())

        def stop():
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()

        try:
            start()
            code, body = call("GET", "/api/scan/operations")
            assert code == 200 and [row["idempotencyKey"] for row in body["operations"]] == ["recent"], body
            for method, path, payload in (
                ("GET", "/api/scan/operations/retained", None),
                ("POST", "/api/scan/operations", scan_request("retained")),
                ("POST", "/api/scan/operations/retained/cancel", None),
            ):
                code, body = call(method, path, payload)
                assert code == 410 and body["code"] == "job-history-expired" and body["status"] == "cancelled", body
            changed = scan_request("retained") | dict(rollNumber="2")
            code, body = call("POST", "/api/scan/operations", changed)
            assert code == 409 and body["code"] == "idempotency-conflict", body
            assert call("GET", "/api/scan/operations/expired")[0] == 404
            assert not list(operations.glob("*.json"))
            code, body = call("POST", "/api/scan/operations", scan_request("waiting"))
            assert code == 202, body
            code, body = call("POST", "/api/scan/operations/waiting/cancel")
            assert code == 200 and body["operation"]["status"] == "cancelled", body
            stop()
            start()
            code, body = call("GET", "/api/scan/operations/waiting")
            assert code == 200 and body["operation"]["status"] == "cancelled", body
            with sqlite3.connect(root / "state/jobs.sqlite3") as database:
                assert database.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
                assert database.execute("PRAGMA auto_vacuum").fetchone()[0] == 2
                assert database.execute("SELECT COUNT(*) FROM job_keys").fetchone()[0] == 3
            assert not list(output.iterdir()), "Cancelling a waiting job must not produce scan output"
            print("Passed: migration, retention, HTTP 410/409/404, cancellation, restart, and database integrity.")
        finally:
            stop()


if __name__ == "__main__":
    main()
