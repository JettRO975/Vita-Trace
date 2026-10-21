param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [string]$DataDir,
    [ValidateSet('demo', 'real')]
    [string]$Mode = 'demo',
    [int]$TimeoutSeconds = 25,
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Exe not found: $ExePath"
}
if (-not $DataDir) {
    $DataDir = Join-Path $env:TEMP 'VitaTrace-Acceptance'
}

$previousDataDir = $env:VITATRACE_DATA_DIR
$env:VITATRACE_DATA_DIR = $DataDir
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $script:failures.Add($message) | Out-Null
    Write-Host "FAIL: $message" -ForegroundColor Red
}

function Add-Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

$appArgs = @()
if ($Mode -eq 'demo') {
    $appArgs += '--demo'
}

$startParams = @{
    FilePath = $ExePath
    PassThru = $true
}
if ($appArgs.Count -gt 0) {
    $startParams['ArgumentList'] = $appArgs
}

$process = $null
try {
    if (Test-Path -LiteralPath $DataDir) {
        Remove-Item -LiteralPath $DataDir -Recurse -Force
    }

    $process = Start-Process @startParams
    $windowSeen = $false
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) {
            break
        }
        if ($process.MainWindowTitle -eq 'Vita Trace') {
            $windowSeen = $true
            break
        }
    }

    if ($process.HasExited) {
        Add-Failure "process exited early with code $($process.ExitCode)"
    }
    else {
        Add-Pass 'process is running'
    }

    if ($windowSeen) {
        Add-Pass 'main window is visible'
    }
    else {
        Add-Failure 'main window was not visible before timeout'
    }

    $logName = if ($Mode -eq 'demo') { 'demo.log' } else { 'app.log' }
    $logPath = Join-Path $DataDir "logs\$logName"
    if (Test-Path -LiteralPath $logPath) {
        Add-Pass "log exists: $logPath"
        $log = Get-Content -LiteralPath $logPath -Encoding UTF8
        $errors = $log | Where-Object { $_ -match '\[ERROR\]' }
        if ($errors) {
            Add-Failure "log contains $($errors.Count) ERROR line(s)"
            $errors | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
        }
        else {
            Add-Pass 'log contains no ERROR lines'
        }
    }
    else {
        Add-Failure "log missing: $logPath"
    }

    if ($Mode -eq 'demo') {
        $historyPath = Join-Path $DataDir 'history.json'
        if (Test-Path -LiteralPath $historyPath) {
            Add-Pass 'demo history file exists (should be absent)'
            Add-Failure 'demo mode created history.json; demo must not write real history'
        }
        else {
            Add-Pass 'demo mode did not create real history.json'
        }
    }

    # Single-instance check: the second process must exit by itself.
    $second = Start-Process @startParams
    Start-Sleep -Seconds 3
    if ($second.HasExited) {
        Add-Pass 'second instance exited instead of opening a duplicate window'
    }
    else {
        Add-Failure 'second instance is still running; single-instance guard may be broken'
        Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue
    }

    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    $env:VITATRACE_DATA_DIR = $previousDataDir

    if (-not $KeepData -and (Test-Path -LiteralPath $DataDir)) {
        Remove-Item -LiteralPath $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'RESULT: PASS' -ForegroundColor Green
    exit 0
}

Write-Host "RESULT: FAIL ($($failures.Count) issue(s))" -ForegroundColor Red
exit 1
