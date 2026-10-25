[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Prefer the project-local SDK. Fall back to the system dotnet when absent.
$dotnet = [System.IO.Path]::Combine($root, '.dotnet\dotnet.exe')
if (-not (Test-Path -Path $dotnet)) {
    $dotnet = 'dotnet'
}

$env:DOTNET_CLI_HOME = [System.IO.Path]::Combine($root, '.dotnet-home')
$env:NUGET_PACKAGES = [System.IO.Path]::Combine($root, '.nuget\packages')

$solution = [System.IO.Path]::Combine($root, 'VitaTrace.slnx')

Write-Host "dotnet  : $dotnet"
Write-Host "solution: $solution"
Write-Host "config  : $Configuration"

# The current sandbox blocks named pipes, so MSBuild multi-node and the compiler
# server fail. Normal developer machines can remove -m:1 -p:UseSharedCompilation=false.
& $dotnet build $solution -c $Configuration -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($Publish) {
    $output = [System.IO.Path]::Combine($root, 'artifacts\publish\win-x64')
    $project = [System.IO.Path]::Combine($root, 'src\VitaTrace.App\VitaTrace.App.csproj')
    Write-Host "publish : $output"

    # 不压缩单文件：压缩会在启动时把程序集解压到内存，实测常驻内存
    # 比未压缩版本高约 100 MB；用体积换内存，桌面工具更合适。
    & $dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -o $output `
        -m:1 `
        -p:UseSharedCompilation=false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Write-Host "publish done: $output"
}
