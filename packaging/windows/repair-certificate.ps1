param(
    [string]$InstallDir = "$env:ProgramFiles\TwinCheck\ScanAgent",
    [string]$ServiceName = "TwinCheck Scan Agent"
)

$ErrorActionPreference = "Stop"

function Require-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this repair script from an elevated PowerShell window."
    }
}

Require-Admin

$dataDir = Join-Path $env:ProgramData "TwinCheck\ScanAgent"
$certPath = Join-Path $dataDir "localhost.pfx"
$certExportPath = Join-Path $dataDir "localhost.cer"
$certThumbprintPath = Join-Path $dataDir "localhost-cert-thumbprint.txt"
$settingsPath = Join-Path $InstallDir "appsettings.Production.json"
$certFriendlyName = "TwinCheck Scan Agent Localhost"

Write-Host "Repairing TwinCheck Scan Agent HTTPS certificate..."
Write-Host "Install dir: $InstallDir"
Write-Host "Settings path: $settingsPath"

if (-not (Test-Path $InstallDir)) {
    throw "Install directory does not exist: $InstallDir"
}

$oldCertThumbprint = if (Test-Path $certThumbprintPath) {
    (Get-Content $certThumbprintPath -Raw).Trim()
} else {
    $null
}

New-Item -ItemType Directory -Force $dataDir | Out-Null

foreach ($store in @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root")) {
    Get-ChildItem $store -ErrorAction SilentlyContinue |
        Where-Object {
            if ($oldCertThumbprint) {
                $_.Thumbprint -eq $oldCertThumbprint
            } else {
                $_.FriendlyName -eq $certFriendlyName
            }
        } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

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
$kestrelSettings | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsPath -Encoding UTF8

Restart-Service -Name $ServiceName -Force

Write-Host "Certificate repaired."
Write-Host "Service status:"
Get-Service -Name $ServiceName
Write-Host "Verify with:"
Write-Host "curl.exe -k https://localhost:3625/"
