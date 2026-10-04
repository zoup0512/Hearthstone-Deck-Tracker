# Packages the Release build into a portable zip at the repository root, named
# with the exe version and its compile timestamp:
#   Hearthstone.Deck.Tracker-v<version>-<yyyyMMdd-HHmmss>.zip
# With -NoBuild it only packages the current output (used by the post-build hook);
# otherwise it builds the solution first.

Param(
    [switch]$NoBuild,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$baseDir = $(Resolve-Path "$PSScriptRoot\..").Path
$releaseDir = "$baseDir\Hearthstone Deck Tracker\bin\x64\$Configuration"
$cleanDir = "$baseDir\Hearthstone Deck Tracker\bin\x64\Hearthstone Deck Tracker"

if(!$NoBuild) {
    $dotnetCandidates = @(
        $env:HDT_DOTNET_EXE,
        "$HOME\dotnet-sdk-8\dotnet.exe",
        "dotnet"
    ) | Where-Object { $_ }
    $dotnet = $null
    foreach($candidate in $dotnetCandidates) {
        $resolved = Get-Command $candidate -ErrorAction SilentlyContinue
        if($resolved) { $dotnet = $resolved.Source; break }
        if(Test-Path $candidate) { $dotnet = $candidate; break }
    }
    if(!$dotnet) { throw "dotnet not found (set HDT_DOTNET_EXE or install the SDK)" }
    & $dotnet build "$baseDir\Hearthstone Deck Tracker.sln" -c $Configuration -p:Platform=x64
    if($LASTEXITCODE -ne 0) { throw "Build failed" }
}

# Refresh the clean output folder the same way CI does.
Push-Location $releaseDir
try {
    & cmd.exe /c "$baseDir\build-scripts\release_post_build.bat" | Out-Null
} finally {
    Pop-Location
}

$exe = "$cleanDir\Hearthstone Deck Tracker.exe"
if(!(Test-Path $exe)) { throw "Packaged exe not found: $exe" }
$version = (Get-Item $exe).VersionInfo.ProductVersion
$timestamp = (Get-Item $exe).LastWriteTime.ToString("yyyyMMdd-HHmmss")
$zip = "$baseDir\Hearthstone.Deck.Tracker-v$version-$timestamp.zip"

$sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "$(${env:ProgramFiles(x86)})\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
Push-Location "$baseDir\Hearthstone Deck Tracker\bin\x64"
try {
    if($sevenZip) {
        & $sevenZip a -r -mx9 $zip "Hearthstone Deck Tracker" | Out-Null
        if($LASTEXITCODE -ne 0) { throw "7z failed with exit code $LASTEXITCODE" }
    } else {
        if(Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path $cleanDir -DestinationPath $zip -CompressionLevel Optimal
    }
} finally {
    Pop-Location
}

Write-Host "Packaged: $zip"
Write-Output $zip
