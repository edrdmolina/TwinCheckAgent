# Builds the per-user Windows installer with Velopack:
#   artifacts\win-x64\releases\TwinCheckScanAgent-win-Setup.exe   (what operators run once)
#   artifacts\win-x64\releases\*.nupkg + releases.win.json        (what installed apps update from)
#
# Requires the vpk tool: dotnet tool install -g vpk
# For delta updates, download the previous release into the releases folder first:
#   vpk download github --repoUrl https://github.com/edrdmolina/TwinCheckAgent -o artifacts\win-x64\releases
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\win-x64",
    [string]$PackageVersion = "0.1.0",
    [string]$ExifToolDirectory = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRootFull = Join-Path $repoRoot $OutputRoot
$publishOutput = Join-Path $outputRootFull "publish"
$releasesOutput = Join-Path $outputRootFull "releases"

# Keep releases\: it may hold the previous release downloaded for delta packages.
Remove-Item $publishOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publishOutput, $releasesOutput | Out-Null

dotnet publish (Join-Path $repoRoot "src\TwinCheck.Agent.Gui\TwinCheck.Agent.Gui.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Version=$PackageVersion `
    -o $publishOutput
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

$metadataTools = Join-Path $publishOutput "tools\exiftool"
if ([string]::IsNullOrWhiteSpace($ExifToolDirectory)) {
    & (Join-Path $PSScriptRoot "install-exiftool-win.ps1") -Destination $metadataTools -Runtime $Runtime
} else {
    if (-not (Test-Path (Join-Path $ExifToolDirectory "exiftool.exe")) -or
        -not (Test-Path (Join-Path $ExifToolDirectory "exiftool_files"))) {
        throw "ExifToolDirectory must contain exiftool.exe and its exiftool_files directory."
    }
    New-Item -ItemType Directory -Force $metadataTools | Out-Null
    Copy-Item (Join-Path $ExifToolDirectory "*") $metadataTools -Recurse -Force
}

foreach ($required in @("TwinCheck.Agent.Gui.exe", "appsettings.json", "remove-legacy-install.ps1", "tools\exiftool\exiftool.exe")) {
    if (-not (Test-Path (Join-Path $publishOutput $required))) {
        throw "Publish output is missing $required."
    }
}

vpk pack `
    --packId TwinCheckScanAgent `
    --packVersion $PackageVersion `
    --packDir $publishOutput `
    --mainExe TwinCheck.Agent.Gui.exe `
    --packTitle "TwinCheck Scan Agent" `
    --packAuthors "Coastal Film Lab" `
    --icon (Join-Path $repoRoot "src\TwinCheck.Agent.Gui\Assets\avalonia-logo.ico") `
    --outputDir $releasesOutput
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (exit code $LASTEXITCODE)." }

Write-Host "Windows release ready:"
Get-ChildItem $releasesOutput | ForEach-Object { Write-Host "  $($_.FullName)" }
