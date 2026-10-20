param(
    [Parameter(Mandatory=$true)][string]$Destination,
    [ValidateSet("win-x64", "win-x86")][string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$version = "13.59"
$bits = if ($Runtime -eq "win-x86") { "32" } else { "64" }
$expectedHash = if ($bits -eq "32") {
    "fe9a55d28b05c1b0e18877b4881f40d83db222f90018963473cff798e8bf05af"
} else {
    "44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec"
}
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("twincheck-exiftool-" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Force $work | Out-Null
    $zip = Join-Path $work "exiftool.zip"
    # SourceForge serves its HTML download page to PowerShell's browser-like user agent; a download tool gets the file.
    Invoke-WebRequest -Uri "https://downloads.sourceforge.net/project/exiftool/exiftool-${version}_${bits}.zip" -OutFile $zip -UserAgent "Wget"
    if ((Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant() -ne $expectedHash) {
        throw "The ExifTool download does not match the official SHA256 checksum."
    }
    Expand-Archive -Path $zip -DestinationPath (Join-Path $work "expanded")
    $exe = Get-ChildItem (Join-Path $work "expanded") -Recurse -File -Filter "exiftool(-k).exe" | Select-Object -First 1
    if (-not $exe) { throw "The ExifTool distribution is missing exiftool(-k).exe." }
    $files = Join-Path $exe.DirectoryName "exiftool_files"
    if (-not (Test-Path $files)) { throw "The ExifTool distribution is missing exiftool_files." }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Copy-Item $exe.FullName (Join-Path $Destination "exiftool.exe") -Force
    Copy-Item $files $Destination -Recurse -Force
    if ((& (Join-Path $Destination "exiftool.exe") -ver) -ne $version) { throw "ExifTool did not start correctly." }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
