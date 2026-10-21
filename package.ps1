param(
    [string]$Version = '0.7.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Build a self-contained single-file exe, then stage a portable folder and zip.
& (Join-Path $root 'build.ps1') -Configuration Release -Publish
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$publish = Join-Path $root 'artifacts\publish\win-x64'
$exe = Join-Path $publish 'VitaTrace.exe'
if (-not (Test-Path $exe)) {
    throw "Publish output not found: $exe"
}

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "VitaTrace-$Version-win-x64"
$zip = Join-Path $dist "VitaTrace-$Version-win-x64.zip"

if (Test-Path $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item -LiteralPath $exe -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'assets') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $root 'packaging\install.ps1') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'packaging\uninstall.ps1') -Destination $stage

# Keep the portable package small: no PDBs, no build cache, no user data.
if (Test-Path $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ASCII

Write-Host "package : $zip"
Write-Host "sha256  : $hash"
