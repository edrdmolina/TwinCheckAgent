param(
    [string]$InstallDir = "$env:ProgramFiles\TwinCheck\ScanAgent",
    [string]$ServiceName = "TwinCheck Scan Agent",
    [string]$AgentUrl = "https://localhost:3625"
)

$ErrorActionPreference = "Stop"

function Require-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this installer from an elevated PowerShell window."
    }
}

Require-Admin

$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$apiExe = Join-Path $InstallDir "TwinCheck.Agent.Api.exe"
$dataDir = Join-Path $env:ProgramData "TwinCheck\ScanAgent"
$configPath = Join-Path $dataDir "agent-config.json"
$logDir = Join-Path $dataDir "logs"

Write-Host "Installing TwinCheck Scan Agent to $InstallDir"
Write-Host "Using shared config at $configPath"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force $InstallDir | Out-Null
New-Item -ItemType Directory -Force $dataDir, $logDir | Out-Null
Copy-Item (Join-Path $sourceDir "*") $InstallDir -Recurse -Force -Exclude "install.ps1","uninstall.ps1","reinstall.ps1","README.md"

[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_CONFIG_PATH", $configPath, "Machine")
[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_LOG_DIR", $logDir, "Machine")
$env:TWINCHECK_AGENT_CONFIG_PATH = $configPath
$env:TWINCHECK_AGENT_LOG_DIR = $logDir

$binaryPath = "`"$apiExe`" --urls $AgentUrl"
New-Service `
    -Name $ServiceName `
    -DisplayName $ServiceName `
    -BinaryPathName $binaryPath `
    -StartupType Automatic `
    -Description "Local TwinCheck scanner file movement API."

Start-Service -Name $ServiceName

$shortcutPath = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "TwinCheck Scan Agent.lnk"
$guiExe = Join-Path $InstallDir "gui\TwinCheck.Agent.Gui.exe"
if (Test-Path $guiExe) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $guiExe
    $shortcut.WorkingDirectory = Split-Path -Parent $guiExe
    $shortcut.Save()
}

Write-Host "Installed and started $ServiceName."
Write-Host "Config path: $configPath"
Write-Host "Log folder: $logDir"
Write-Host "Open https://localhost:3625 in the browser once and accept the local certificate if prompted."
