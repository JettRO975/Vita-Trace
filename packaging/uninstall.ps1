# Per-user uninstall for Vita Trace. No administrator rights required.
param(
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\VitaTrace'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Vita Trace.lnk'
$data = Join-Path $env:LOCALAPPDATA 'VitaTrace'

$running = Get-Process -Name VitaTrace -ErrorAction SilentlyContinue
if ($running) {
    throw 'Vita Trace is running. Close it from the tray menu (Exit), then run uninstall.ps1 again.'
}

if (Test-Path $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
    Write-Host "Removed shortcut: $shortcutPath"
}

if (Test-Path $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Host "Removed application: $target"
}

if ($RemoveData) {
    if (Test-Path $data) {
        Remove-Item -LiteralPath $data -Recurse -Force
        Write-Host "Removed user data: $data"
    }
} else {
    Write-Host "User data kept at: $data (run with -RemoveData to delete settings, logs and history)."
}
