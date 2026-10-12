# Windows Test Install

How to build, release, and test the per-user Windows installer. Operator instructions are in
[packaging/windows/README.md](../packaging/windows/README.md).

## Publish a release

Releases are built by GitHub Actions. Tag a commit on `master` and push the tag:

```bash
git tag v0.2.0
git push origin v0.2.0
```

The [Release workflow](../.github/workflows/release.yml) runs the tests, builds
`TwinCheckScanAgent-win-Setup.exe` and the update packages (including a delta from the
previous release), publishes the GitHub release, and attaches the NixOS tarball. Installed
Windows agents find the release within 6 hours and offer **Restart to update**.

Tags must be SemVer: `v1.2.3`, or `v1.2.3-beta.1` for a pre-release. Agents only update to
stable releases.

## Build locally

From the repository root in PowerShell, with the [vpk](https://docs.velopack.io) tool installed
(`dotnet tool install -g vpk`):

```powershell
.\scripts\package-win.ps1 -PackageVersion 0.2.0
```

Output:

```text
artifacts\win-x64\releases\TwinCheckScanAgent-win-Setup.exe
artifacts\win-x64\releases\TwinCheckScanAgent-0.2.0-full.nupkg
artifacts\win-x64\releases\releases.win.json
```

A locally built `Setup.exe` installs and runs normally. It still checks GitHub for updates,
so it updates to any newer published release.

## Test checklist

**Fresh install**, on a machine that never had TwinCheck:

1. Run `Setup.exe`. Expect no administrator prompt.
2. Accept the certificate prompt. The tray icon appears and the window opens.
3. `curl.exe https://localhost:3625/` succeeds without `-k`.
4. Copy the API key from **Setup** into TwinCheckN and run a scan.
5. Sign out and back in. The app starts minimized in the tray.
6. Run `Setup.exe` again. It shows the existing window instead of starting a second agent.

**Upgrade from the service version**, on a machine with the old `Install-TwinCheck.cmd` install:

1. Run `Setup.exe`. The banner asks to remove the old version.
2. Start a scan through the old service, then click **Remove old version**. Expect the app to
   refuse until the job finishes.
3. Click it again and approve the prompt. `Get-Service "TwinCheck Scan Agent"` now fails, and
   the old Program Files folder and the machine-wide `TWINCHECK_AGENT_*` environment variables are gone.
4. The same API key and profiles appear in **Setup**, and old jobs appear in **Jobs**.

**Update**:

1. With an older version installed, push a newer tag.
2. Wait for the update banner, or restart the app to check sooner.
3. Start a job. **Restart to update** stays disabled until it finishes.
4. Click it. The app restarts, and `/api/scan/health` reports the new version.

**Uninstall**: **Settings > Apps > TwinCheck Scan Agent > Uninstall** removes
`%LocalAppData%\TwinCheckScanAgent` and the `TwinCheckScanAgent` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. `%AppData%\TwinCheck\ScanAgent` is kept.
Delete it for a clean retest.
