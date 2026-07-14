param(
    [string]$InstallDir = "$env:ProgramFiles\TwinCheck\ScanAgent",
    [string]$ServiceName = "TwinCheck Scan Agent",
    [string]$AgentUrl = "https://localhost:3625"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $scriptDir "uninstall.ps1") -InstallDir $InstallDir -ServiceName $ServiceName
& (Join-Path $scriptDir "install.ps1") -InstallDir $InstallDir -ServiceName $ServiceName -AgentUrl $AgentUrl
