param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\win-x64",
    [string]$PackageVersion = "0.1.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootFull = Join-Path $repoRoot $OutputRoot
$apiOutput = Join-Path $outputRootFull "api"
$guiOutput = Join-Path $outputRootFull "gui"
$packageOutput = Join-Path $outputRootFull "package"
$archivePath = Join-Path $outputRootFull "TwinCheck-Scan-Agent-$PackageVersion-$Runtime.zip"

Remove-Item $outputRootFull -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $apiOutput, $guiOutput, $packageOutput | Out-Null

dotnet publish (Join-Path $repoRoot "src\TwinCheck.Agent.Api\TwinCheck.Agent.Api.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$PackageVersion `
    -o $apiOutput

dotnet publish (Join-Path $repoRoot "src\TwinCheck.Agent.Gui\TwinCheck.Agent.Gui.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$PackageVersion `
    -o $guiOutput

Copy-Item (Join-Path $apiOutput "*") $packageOutput -Recurse -Force
New-Item -ItemType Directory -Force (Join-Path $packageOutput "gui") | Out-Null
Copy-Item (Join-Path $guiOutput "*") (Join-Path $packageOutput "gui") -Recurse -Force
Copy-Item (Join-Path $repoRoot "packaging\windows\install.ps1") $packageOutput -Force
Copy-Item (Join-Path $repoRoot "packaging\windows\uninstall.ps1") $packageOutput -Force
Copy-Item (Join-Path $repoRoot "packaging\windows\reinstall.ps1") $packageOutput -Force
Copy-Item (Join-Path $repoRoot "packaging\windows\repair-certificate.ps1") $packageOutput -Force
Copy-Item (Join-Path $repoRoot "packaging\windows\Install-TwinCheck.cmd") $packageOutput -Force
Copy-Item (Join-Path $repoRoot "packaging\windows\Reinstall-TwinCheck.cmd") $packageOutput -Force
Copy-Item (Join-Path $repoRoot "packaging\windows\Uninstall-TwinCheck.cmd") $packageOutput -Force
Copy-Item (Join-Path $repoRoot "packaging\windows\README.md") $packageOutput -Force
Set-Content -Path (Join-Path $packageOutput "package-version.txt") -Value $PackageVersion -Encoding ASCII

Compress-Archive -Path (Join-Path $packageOutput "*") -DestinationPath $archivePath -CompressionLevel Optimal -Force

Write-Host "Windows package ready:"
Write-Host $packageOutput
Write-Host "Windows archive ready:"
Write-Host $archivePath
