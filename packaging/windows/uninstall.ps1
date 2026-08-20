param(
    [string]$InstallDir,
    [string]$ServiceName,
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

Require-Admin

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$installMetadataPath = Join-Path $scriptDir "install-metadata.json"
$installMetadata = if (Test-Path $installMetadataPath) {
    Get-Content $installMetadataPath -Raw | ConvertFrom-Json
} else {
    $null
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = if ($installMetadata -and $installMetadata.InstallDir) {
        $installMetadata.InstallDir
    } else {
        "$env:ProgramFiles\TwinCheck\ScanAgent"
    }
}

if ([string]::IsNullOrWhiteSpace($ServiceName)) {
    $ServiceName = if ($installMetadata -and $installMetadata.ServiceName) {
        $installMetadata.ServiceName
    } else {
        "TwinCheck Scan Agent"
    }
}

Assert-SafeInstallDirectory -Path $InstallDir

$certFriendlyName = "TwinCheck Scan Agent Localhost"
$dataDir = Join-Path $env:ProgramData "TwinCheck\ScanAgent"
$certThumbprintPath = Join-Path $dataDir "localhost-cert-thumbprint.txt"
$certThumbprint = if (Test-Path $certThumbprintPath) {
    (Get-Content $certThumbprintPath -Raw).Trim()
} else {
    $null
}
$uninstallRegistryPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TwinCheckScanAgent"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping $ServiceName..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to remove the Windows service (sc.exe exit code $LASTEXITCODE)."
    }

    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
            break
        }
        Start-Sleep -Milliseconds 500
    }

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        throw "Windows did not finish removing service '$ServiceName'. Wait a few seconds and run the uninstaller again."
    }
}

Get-Process -Name "TwinCheck.Agent.Gui" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

foreach ($store in @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root")) {
    Get-ChildItem $store -ErrorAction SilentlyContinue |
        Where-Object {
            if ($certThumbprint) {
                $_.Thumbprint -eq $certThumbprint
            } else {
                $_.FriendlyName -eq $certFriendlyName
            }
        } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "TwinCheck Scan Agent.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "TwinCheck Scan Agent") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $uninstallRegistryPath -Recurse -Force -ErrorAction SilentlyContinue
[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_CONFIG_PATH", $null, "Machine")
[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_LOG_DIR", $null, "Machine")
[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_STATE_DIR", $null, "Machine")

Remove-Item (Join-Path $dataDir "localhost.pfx") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dataDir "localhost.cer") -Force -ErrorAction SilentlyContinue
Remove-Item $certThumbprintPath -Force -ErrorAction SilentlyContinue

if ($PurgeData) {
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:APPDATA "TwinCheck\ScanAgent") -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Uninstalled TwinCheck Scan Agent."
if (-not $PurgeData) {
    Write-Host "Config and logs were preserved. Re-run with -PurgeData to remove them."
}
