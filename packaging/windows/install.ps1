param(
    [string]$InstallDir = "$env:ProgramFiles\TwinCheck\ScanAgent",
    [string]$ServiceName = "TwinCheck Scan Agent",
    [string]$AgentUrl = "https://localhost:3625",
    [System.Management.Automation.PSCredential]$ServiceCredential
)

$ErrorActionPreference = "Stop"

function Require-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this installer from an elevated PowerShell window."
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

function Grant-LogOnAsServiceRight {
    param([string]$AccountName)

    $sid = (New-Object Security.Principal.NTAccount($AccountName)).Translate([Security.Principal.SecurityIdentifier]).Value
    $sidEntry = "*$sid"
    $workDir = Join-Path $env:TEMP "twincheck-secedit-$([Guid]::NewGuid().ToString("N"))"
    $exportPath = Join-Path $workDir "export.inf"
    $importPath = Join-Path $workDir "import.inf"
    $databasePath = Join-Path $workDir "secedit.sdb"

    New-Item -ItemType Directory -Force $workDir | Out-Null

    try {
        secedit.exe /export /cfg $exportPath | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to export the local security policy (secedit exit code $LASTEXITCODE)."
        }
        $content = Get-Content $exportPath
        $rightName = "SeServiceLogonRight"
        $rightLineIndex = -1

        for ($i = 0; $i -lt $content.Count; $i++) {
            if ($content[$i] -match "^\s*$rightName\s*=") {
                $rightLineIndex = $i
                break
            }
        }

        if ($rightLineIndex -ge 0) {
            $existing = $content[$rightLineIndex].Split("=", 2)[1].Split(",") |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ }

            if ($existing -notcontains $sidEntry) {
                $content[$rightLineIndex] = "$rightName = " + (($existing + $sidEntry) -join ",")
                Set-Content -Path $importPath -Value $content -Encoding Unicode
                secedit.exe /configure /db $databasePath /cfg $importPath /areas USER_RIGHTS | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    throw "Unable to grant 'Log on as a service' (secedit exit code $LASTEXITCODE)."
                }
            }
            return
        }

        $privilegeIndex = -1
        for ($i = 0; $i -lt $content.Count; $i++) {
            if ($content[$i] -match "^\s*\[Privilege Rights\]\s*$") {
                $privilegeIndex = $i
                break
            }
        }

        if ($privilegeIndex -ge 0) {
            $before = $content[0..$privilegeIndex]
            $after = if ($privilegeIndex + 1 -lt $content.Count) { $content[($privilegeIndex + 1)..($content.Count - 1)] } else { @() }
            $content = @($before + "$rightName = $sidEntry" + $after)
        } else {
            $content = @($content + "" + "[Privilege Rights]" + "$rightName = $sidEntry")
        }

        Set-Content -Path $importPath -Value $content -Encoding Unicode
        secedit.exe /configure /db $databasePath /cfg $importPath /areas USER_RIGHTS | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to grant 'Log on as a service' (secedit exit code $LASTEXITCODE)."
        }
    } finally {
        Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Remove-AgentCertificates {
    param(
        [string]$FriendlyName,
        [string]$ThumbprintPath
    )

    $thumbprint = if (Test-Path $ThumbprintPath) {
        (Get-Content $ThumbprintPath -Raw).Trim()
    } else {
        $null
    }

    foreach ($store in @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root")) {
        Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object {
                if ($thumbprint) {
                    $_.Thumbprint -eq $thumbprint
                } else {
                    $_.FriendlyName -eq $FriendlyName
                }
            } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

function Wait-ServiceRemoval {
    param([string]$Name)

    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 500
    }

    throw "Windows did not finish removing service '$Name'. Wait a few seconds and run the installer again."
}

Require-Admin
Assert-SafeInstallDirectory -Path $InstallDir

$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$apiExe = Join-Path $InstallDir "TwinCheck.Agent.Api.exe"
$dataDir = Join-Path $env:ProgramData "TwinCheck\ScanAgent"
$configPath = Join-Path $dataDir "agent-config.json"
$logDir = Join-Path $dataDir "logs"
$certPath = Join-Path $dataDir "localhost.pfx"
$certExportPath = Join-Path $dataDir "localhost.cer"
$certThumbprintPath = Join-Path $dataDir "localhost-cert-thumbprint.txt"
$kestrelSettingsPath = Join-Path $InstallDir "appsettings.Production.json"
$certFriendlyName = "TwinCheck Scan Agent Localhost"
$installMetadataPath = Join-Path $InstallDir "install-metadata.json"
$uninstallRegistryPath = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TwinCheckScanAgent"
$packageVersionPath = Join-Path $sourceDir "package-version.txt"
$packageVersion = if (Test-Path $packageVersionPath) { (Get-Content $packageVersionPath -Raw).Trim() } else { "0.1.0" }

if (-not (Test-Path (Join-Path $sourceDir "TwinCheck.Agent.Api.exe"))) {
    throw "TwinCheck.Agent.Api.exe is missing. Run install.ps1 from the extracted Windows package."
}

if (-not (Test-Path (Join-Path $sourceDir "gui\TwinCheck.Agent.Gui.exe"))) {
    throw "gui\TwinCheck.Agent.Gui.exe is missing. Run install.ps1 from the extracted Windows package."
}

Write-Host "Installing TwinCheck Scan Agent to $InstallDir"
Write-Host "Using shared config at $configPath"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to remove the existing Windows service (sc.exe exit code $LASTEXITCODE)."
    }
    Wait-ServiceRemoval -Name $ServiceName
}

New-Item -ItemType Directory -Force $InstallDir | Out-Null
New-Item -ItemType Directory -Force $dataDir, $logDir | Out-Null

if (-not (Test-Path $configPath)) {
    $apiKeyBytes = New-Object byte[] 24
    $randomNumberGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $randomNumberGenerator.GetBytes($apiKeyBytes)
    } finally {
        $randomNumberGenerator.Dispose()
    }

    $apiKey = "tcn-" + [BitConverter]::ToString($apiKeyBytes).Replace("-", "").ToLowerInvariant()
    [ordered]@{
        AgentName = "TwinCheck Scan Agent"
        Version = $packageVersion
        ApiKey = $apiKey
        AllowedSourceRoots = @()
        AllowedDestinationRoots = @()
        ActiveProfileId = $null
        Profiles = @()
    } | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8
    Write-Host "Created shared config with a unique API key."
}

$excludedPackageFiles = @(
    "install.ps1",
    "reinstall.ps1",
    "Install-TwinCheck.cmd",
    "Reinstall-TwinCheck.cmd",
    "README.md"
)
Get-ChildItem $sourceDir |
    Where-Object { $excludedPackageFiles -notcontains $_.Name } |
    Copy-Item -Destination $InstallDir -Recurse -Force

Copy-Item (Join-Path $sourceDir "uninstall.ps1") $InstallDir -Force
if (Test-Path (Join-Path $sourceDir "repair-certificate.ps1")) {
    Copy-Item (Join-Path $sourceDir "repair-certificate.ps1") $InstallDir -Force
}

[ordered]@{
    InstallDir = $InstallDir
    ServiceName = $ServiceName
} | ConvertTo-Json | Set-Content -Path $installMetadataPath -Encoding UTF8

$existingCertThumbprintPath = $certThumbprintPath
Remove-AgentCertificates -FriendlyName $certFriendlyName -ThumbprintPath $existingCertThumbprintPath
Remove-Item $certPath, $certExportPath, $certThumbprintPath -Force -ErrorAction SilentlyContinue
$certPassword = [Guid]::NewGuid().ToString("N")
$secureCertPassword = ConvertTo-SecureString $certPassword -AsPlainText -Force
$cert = New-SelfSignedCertificate `
    -DnsName "localhost" `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -FriendlyName $certFriendlyName `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5)

Export-PfxCertificate -Cert $cert -FilePath $certPath -Password $secureCertPassword | Out-Null
Export-Certificate -Cert $cert -FilePath $certExportPath | Out-Null
Import-Certificate -FilePath $certExportPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
Set-Content -Path $certThumbprintPath -Value $cert.Thumbprint -Encoding ASCII

$kestrelSettings = [ordered]@{
    Kestrel = [ordered]@{
        Certificates = [ordered]@{
            Default = [ordered]@{
                Path = $certPath
                Password = $certPassword
            }
        }
    }
}
$kestrelSettings | ConvertTo-Json -Depth 10 | Set-Content -Path $kestrelSettingsPath -Encoding UTF8

[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_CONFIG_PATH", $configPath, "Machine")
[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_LOG_DIR", $logDir, "Machine")
[Environment]::SetEnvironmentVariable("TWINCHECK_AGENT_STATE_DIR", $dataDir, "Machine")
$env:TWINCHECK_AGENT_CONFIG_PATH = $configPath
$env:TWINCHECK_AGENT_LOG_DIR = $logDir
$env:TWINCHECK_AGENT_STATE_DIR = $dataDir

$binaryPath = "`"$apiExe`" --urls $AgentUrl"
$newServiceArgs = @{
    Name = $ServiceName
    DisplayName = $ServiceName
    BinaryPathName = $binaryPath
    StartupType = "Automatic"
    Description = "Local TwinCheck scanner file movement API."
}
if ($ServiceCredential) {
    Write-Host "Installing $ServiceName to run as $($ServiceCredential.UserName)..."
    Write-Host "Granting 'Log on as a service' to $($ServiceCredential.UserName)..."
    Grant-LogOnAsServiceRight -AccountName $ServiceCredential.UserName
    $serviceAccountName = $ServiceCredential.UserName
    & icacls.exe $dataDir /grant "${serviceAccountName}:(OI)(CI)M" /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to grant the service account access to $dataDir (icacls exit code $LASTEXITCODE)."
    }
    $newServiceArgs.Credential = $ServiceCredential
}

New-Service @newServiceArgs

try {
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 1
    if ((Get-Service -Name $ServiceName).Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        throw "$ServiceName did not remain running after startup."
    }
} catch {
    Write-Host ""
    Write-Host "Service failed to start. Recent Service Control Manager events:"
    Get-EventLog -LogName System -Newest 20 |
        Where-Object { $_.Source -eq "Service Control Manager" -and $_.Message -like "*$ServiceName*" } |
        Select-Object TimeGenerated, EntryType, EventID, Message |
        Format-List

    Write-Host "Recent TwinCheck application events:"
    Get-EventLog -LogName Application -Newest 20 |
        Where-Object { $_.Source -like "*TwinCheck*" -or $_.Message -like "*TwinCheck*" } |
        Select-Object TimeGenerated, EntryType, Source, EventID, Message |
        Format-List

    throw
}

$shortcutPath = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "TwinCheck Scan Agent.lnk"
$guiExe = Join-Path $InstallDir "gui\TwinCheck.Agent.Gui.exe"
$shell = New-Object -ComObject WScript.Shell
if (Test-Path $guiExe) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $guiExe
    $shortcut.WorkingDirectory = Split-Path -Parent $guiExe
    $shortcut.Save()
}

$programsDir = Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "TwinCheck Scan Agent"
New-Item -ItemType Directory -Force $programsDir | Out-Null
if (Test-Path $guiExe) {
    $startMenuShortcut = $shell.CreateShortcut((Join-Path $programsDir "TwinCheck Scan Agent.lnk"))
    $startMenuShortcut.TargetPath = $guiExe
    $startMenuShortcut.WorkingDirectory = Split-Path -Parent $guiExe
    $startMenuShortcut.Save()
}

$installedUninstaller = Join-Path $InstallDir "uninstall.ps1"
$installedUninstallLauncher = Join-Path $InstallDir "Uninstall-TwinCheck.cmd"
$uninstallCommand = "`"$installedUninstallLauncher`""
$quietUninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installedUninstaller`""
$uninstallShortcut = $shell.CreateShortcut((Join-Path $programsDir "Uninstall TwinCheck Scan Agent.lnk"))
$uninstallShortcut.TargetPath = $installedUninstallLauncher
$uninstallShortcut.WorkingDirectory = $env:SystemRoot
$uninstallShortcut.Save()

New-Item -Path $uninstallRegistryPath -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "DisplayName" -Value "TwinCheck Scan Agent" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "DisplayVersion" -Value $packageVersion -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "Publisher" -Value "Coastal Film Lab" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "InstallLocation" -Value $InstallDir -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "DisplayIcon" -Value $guiExe -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "UninstallString" -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "QuietUninstallString" -Value $quietUninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "NoModify" -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "NoRepair" -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallRegistryPath -Name "InstallDate" -Value (Get-Date -Format "yyyyMMdd") -PropertyType String -Force | Out-Null

Write-Host "Installed and started $ServiceName."
Write-Host "Config path: $configPath"
Write-Host "Log folder: $logDir"
Write-Host "HTTPS certificate: $certPath"
Write-Host "Uninstall from Windows Settings > Apps, the Start menu, or uninstall.ps1."
if ($ServiceCredential) {
    Write-Host "Service account: $($ServiceCredential.UserName)"
} else {
    Write-Host "Service account: LocalSystem. Use -ServiceCredential for UNC/NAS paths that require user credentials."
}
Write-Host "Open https://localhost:3625 in the browser once and accept the local certificate if prompted."
