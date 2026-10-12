# Removes the pre-tray TwinCheck Scan Agent install: the LocalSystem Windows service, its
# Program Files folder, machine certificates, shortcuts, Apps entry, and machine-wide
# TWINCHECK_AGENT_* variables. The tray app runs this elevated on first start.
#
# Data is never deleted: the tray app copies config, job history, and logs from -DataDir
# into the user's %AppData% folder after this script succeeds.
param(
    [string]$DataDir = (Join-Path $env:ProgramData "TwinCheck\ScanAgent"),
    [string]$ServiceName = "TwinCheck Scan Agent"
)

$ErrorActionPreference = "Stop"

function Assert-SafeInstallDirectory {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $protectedPaths = @(
        [IO.Path]::GetPathRoot($fullPath),
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:ProgramData,
        $env:SystemRoot
    ) | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }

    if ($protectedPaths -contains $fullPath) {
        throw "Refusing unsafe install directory: $Path"
    }
}

$logDir = Join-Path $DataDir "logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
Start-Transcript -Path (Join-Path $logDir "legacy-removal.log") -Append | Out-Null

try {
    $installDir = "$env:ProgramFiles\TwinCheck\ScanAgent"
    $installMetadataPath = Join-Path $installDir "install-metadata.json"
    if (Test-Path $installMetadataPath) {
        $installMetadata = Get-Content $installMetadataPath -Raw | ConvertFrom-Json
        if ($installMetadata.InstallDir) { $installDir = $installMetadata.InstallDir }
        if ($installMetadata.ServiceName) { $ServiceName = $installMetadata.ServiceName }
    }
    Assert-SafeInstallDirectory -Path $installDir

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "Stopping and removing $ServiceName..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to remove the Windows service (sc.exe exit code $LASTEXITCODE)."
        }

        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
            throw "Windows did not finish removing service '$ServiceName'."
        }
    }

    $certFriendlyName = "TwinCheck Scan Agent Localhost"
    $certThumbprintPath = Join-Path $DataDir "localhost-cert-thumbprint.txt"
    $certThumbprint = if (Test-Path $certThumbprintPath) { (Get-Content $certThumbprintPath -Raw).Trim() } else { $null }
    foreach ($store in @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root")) {
        Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object {
                if ($certThumbprint) { $_.Thumbprint -eq $certThumbprint } else { $_.FriendlyName -eq $certFriendlyName }
            } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "TwinCheck Scan Agent.lnk") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "TwinCheck Scan Agent") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TwinCheckScanAgent" -Recurse -Force -ErrorAction SilentlyContinue

    [Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_CONFIG_PATH", $null, "Machine")
    [Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_LOG_DIR", $null, "Machine")
    [Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_STATE_DIR", $null, "Machine")

    Write-Host "Removed the legacy TwinCheck Scan Agent service install. Data in $DataDir was kept."
    exit 0
} catch {
    Write-Host "Legacy removal failed: $_"
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
