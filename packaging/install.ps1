# Per-user install for Vita Trace. No administrator rights required.
$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\VitaTrace'
$exe = Join-Path $target 'VitaTrace.exe'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Vita Trace.lnk'

if (-not (Test-Path (Join-Path $source 'VitaTrace.exe'))) {
    throw 'VitaTrace.exe was not found next to install.ps1. Extract the whole zip first.'
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $target -Force

$shortcutDir = Split-Path -Parent $shortcutPath
New-Item -ItemType Directory -Path $shortcutDir -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Vita Trace - wireless peripheral battery monitor'
$shortcut.Save()

Write-Host "Installed to: $target"
Write-Host "Start Menu shortcut: $shortcutPath"
Write-Host 'Run VitaTrace.exe, or use the Start Menu shortcut.'
Write-Host 'Uninstall with uninstall.ps1 in the same folder.'
