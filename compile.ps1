#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$project = Join-Path $root 'src/K4-GOTV.csproj'
$publishOutput = Join-Path $root 'src/bin/K4-GOTV'
$compiledRoot = Join-Path $root 'compiled'
$pluginName = 'K4-GOTV'
$pluginTarget = Join-Path $compiledRoot "counterstrikesharp/plugins/$pluginName"

# Clean build and staging directories.
Remove-Item -Recurse -Force $publishOutput -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $compiledRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null

dotnet restore $project
dotnet publish $project -c Release --no-restore --nologo

if (-not (Test-Path $publishOutput)) {
    throw "Publish output not found at $publishOutput"
}

# Stage plugin files
Copy-Item -Path (Join-Path $publishOutput '*') -Destination $pluginTarget -Recurse -Force

# Keep only linux and Windows runtimes to mirror release packaging
$runtimeDir = Join-Path $pluginTarget 'runtimes'
if (Test-Path $runtimeDir) {
    $keep = @('linux-x64', 'win-x64')
    Get-ChildItem $runtimeDir -Directory | Where-Object { $keep -notcontains $_.Name } | Remove-Item -Recurse -Force
} else {
    Write-Host '[WARN] No runtimes directory found in build output.'
}

# Strip CSS API (already provided by server)
$cssApi = Join-Path $pluginTarget 'CounterStrikeSharp.API.dll'
if (Test-Path $cssApi) {
    Remove-Item $cssApi -Force
}

# Zip the staged CounterStrikeSharp folder for convenience.
$zipPath = Join-Path $compiledRoot "$pluginName.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $compiledRoot 'counterstrikesharp') -DestinationPath $zipPath

Write-Host "[OK] Build finished."
Write-Host " - Folder: $pluginTarget"
Write-Host " - Zip:    $zipPath"
