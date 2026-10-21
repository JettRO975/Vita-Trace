param(
    [string]$Version = '0.7.0',
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$stage = Join-Path $root "dist\VitaTrace-$Version-win-x64"
$exe = Join-Path $stage 'VitaTrace.exe'
$zip = Join-Path $root "dist\VitaTrace-$Version-win-x64.zip"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Unsigned staging exe not found: $exe. Run package.ps1 first."
}

$signArgs = @{
    ExePath = $exe
    TimestampUrl = $TimestampUrl
}
if ($PfxPath) { $signArgs['PfxPath'] = $PfxPath }
if ($PfxPassword) { $signArgs['PfxPassword'] = $PfxPassword }
if ($Thumbprint) { $signArgs['Thumbprint'] = $Thumbprint }
if ($NoTimestamp) { $signArgs['NoTimestamp'] = $true }

& (Join-Path $root 'sign.ps1') @signArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ASCII

Write-Host "signed package : $zip"
Write-Host "sha256         : $hash"
