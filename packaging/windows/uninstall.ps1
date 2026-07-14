param(
    [string]$InstallDir = "$env:ProgramFiles\TwinCheck\ScanAgent",
    [string]$ServiceName = "TwinCheck Scan Agent",
    [switch]$PurgeData
)

$ErrorActionPreference = "Stop"

function Require-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this uninstaller from an elevated PowerShell window."
    }
}

Require-Admin

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping $ServiceName..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "TwinCheck Scan Agent.lnk") -Force -ErrorAction SilentlyContinue
[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_CONFIG_PATH", $null, "Machine")
[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_LOG_DIR", $null, "Machine")

if ($PurgeData) {
    Remove-Item (Join-Path $env:ProgramData "TwinCheck\ScanAgent") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:APPDATA "TwinCheck\ScanAgent") -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Uninstalled TwinCheck Scan Agent."
if (-not $PurgeData) {
    Write-Host "Config and logs were preserved. Re-run with -PurgeData to remove them."
}
