# Builds the ClearForge portable Windows package (REL-2c).
# Produces dist\clearforge-windows-x64-<version>.zip containing the release
# app plus the published engine under engine\ (the layout resolveEnginePath
# checks first). Requires: .NET SDK (global.json), Flutter stable.
# Unsigned portable build — see docs/installation.md and REL-3 for signing.
[CmdletBinding()]
param(
    [string]$Version = '0.1.0-m1'
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\.."
$rel = Join-Path $repo 'apps\desktop_flutter\build\windows\x64\runner\Release'
$pub = Join-Path $repo 'native\ClearForge.Host\bin\publish'
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist 'stage\ClearForge'

Push-Location $repo
try {
    # Self-contained win-x64: larger package, zero .NET-runtime prerequisite.
    dotnet publish native/ClearForge.Host -c Release -r win-x64 `
        --self-contained true -o $pub --nologo
    if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
    Push-Location apps\desktop_flutter
    flutter build windows --release
    if ($LASTEXITCODE) { throw "flutter build failed ($LASTEXITCODE)" }
    Pop-Location

    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path "$stage\engine" | Out-Null
    Copy-Item "$rel\*" $stage -Recurse -Exclude 'engine'
    Copy-Item "$pub\*" "$stage\engine" -Recurse

    # Drop non-Windows runtime natives (e_sqlite3 ships every platform).
    $rt = "$stage\engine\runtimes"
    if (Test-Path $rt) {
        Get-ChildItem $rt -Directory |
            Where-Object Name -notlike 'win-*' |
            Remove-Item -Recurse -Force
    }

    # Deterministic package contents: no pdb, no test artifacts.
    Get-ChildItem $stage -Recurse -Filter '*.pdb' | Remove-Item -Force

    $zip = Join-Path $dist "clearforge-windows-x64-$Version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
    $size = (Get-Item $zip).Length / 1MB
    Write-Host ("`nPackaged -> {0}  ({1:N1} MB)" -f $zip, $size)
}
finally { Pop-Location }
