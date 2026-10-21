param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "File not found: $ExePath"
}

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $signtool) {
    throw 'signtool.exe was not found. Install the Windows SDK signing tools.'
}

$signArgs = @('sign')
if ($PfxPath) {
    if (-not (Test-Path -LiteralPath $PfxPath)) {
        throw "PFX not found: $PfxPath"
    }
    $signArgs += @('/f', $PfxPath)
    if ($PfxPassword) {
        $signArgs += @('/p', $PfxPassword)
    }
}
elseif ($Thumbprint) {
    $signArgs += @('/sha1', $Thumbprint)
}
else {
    throw 'Provide -PfxPath or -Thumbprint. Do not use a self-signed certificate for public releases.'
}

$signArgs += @('/fd', 'SHA256')
if (-not $NoTimestamp) {
    $signArgs += @('/td', 'SHA256', '/tr', $TimestampUrl)
}
$signArgs += $ExePath

& $signtool.FullName @signArgs
if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE"
}

$signature = Get-AuthenticodeSignature -LiteralPath $ExePath
if ($signature.Status -eq 'NotSigned') {
    throw 'The file is still unsigned after signtool returned success.'
}

Write-Host "Signature status : $($signature.Status)"
Write-Host "Signer           : $($signature.SignerCertificate.Subject)"
Write-Host "Thumbprint       : $($signature.SignerCertificate.Thumbprint)"
if ($signature.TimeStamperCertificate) {
    Write-Host "Timestamp        : $($signature.TimeStamperCertificate.Subject)"
}
else {
    Write-Warning 'No timestamp was attached. The signature will expire with the certificate.'
}

if ($signature.Status -ne 'Valid') {
    Write-Warning "The signature chain is not trusted on this machine: $($signature.StatusMessage)"
    Write-Warning 'A self-signed test certificate will still show an untrusted publisher warning. Use a CA-issued or Azure Trusted Signing certificate for distribution.'
}
