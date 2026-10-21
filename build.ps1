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

    & $dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -o $output `
        -m:1 `
        -p:UseSharedCompilation=false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Write-Host "publish done: $output"
}
