param(
    [string]$InstallDir = "$env:ProgramFiles\TwinCheck\ScanAgent",
    [string]$ServiceName = "TwinCheck Scan Agent",
    [string]$AgentUrl = "https://localhost:3625",
    [System.Management.Automation.PSCredential]$ServiceCredential
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $scriptDir "uninstall.ps1") -InstallDir $InstallDir -ServiceName $ServiceName
if ($ServiceCredential) {
    & (Join-Path $scriptDir "install.ps1") -InstallDir $InstallDir -ServiceName $ServiceName -AgentUrl $AgentUrl -ServiceCredential $ServiceCredential
} else {
    & (Join-Path $scriptDir "install.ps1") -InstallDir $InstallDir -ServiceName $ServiceName -AgentUrl $AgentUrl
}
