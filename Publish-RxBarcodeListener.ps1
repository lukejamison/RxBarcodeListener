#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Builds RxBarcodeListener and pushes it to the network share.
    Bump <Version> in RxBarcodeListener.csproj before running to release a new version.
    Run from an elevated PowerShell prompt: .\Publish-RxBarcodeListener.ps1
#>

$ErrorActionPreference = "Stop"

$SharePath  = "\\172.18.129.75\RxBarcodeListener"
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$project    = Join-Path $scriptDir "RxBarcodeListener\RxBarcodeListener.csproj"
$publishDir = "$env:LOCALAPPDATA\RxBarcodeListener-build\bin\Release\net8.0-windows\win-x64\publish"
$exeName    = "RxBarcodeListener.exe"

# Stop all running instances (a running exe is locked and blocks the build)
$running = @(Get-Process -Name "RxBarcodeListener" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "Stopping $($running.Count) instance(s) of RxBarcodeListener..."
    $running | Stop-Process -Force
    Start-Sleep -Seconds 3
}

# Wait for the publish exe file lock to be released
$publishExe = Join-Path $publishDir $exeName
$waited = 0
while ((Test-Path $publishExe) -and $waited -lt 10) {
    try {
        $fs = [System.IO.File]::Open($publishExe, "Open", "ReadWrite", "None")
        $fs.Close()
        break
    } catch {
        Write-Host "Waiting for file lock to release..."
        Start-Sleep -Seconds 1
        $waited++
    }
}

# Build
Write-Host "Building release..."
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed - aborting."
    exit 1
}

# Read version from the built exe
$exePath = Join-Path $publishDir $exeName
$version = (Get-Item $exePath).VersionInfo.ProductVersion
if (-not $version) { $version = "1.0.0" }
$version = $version -replace "\+.*$", ""
Write-Host "Built version: $version"

# Verify share is reachable
if (-not (Test-Path $SharePath)) {
    Write-Error "Cannot reach $SharePath - make sure the server is on and the folder is shared."
    exit 1
}

# Push to share
Write-Host "Copying to $SharePath ..."
Copy-Item -Path $exePath -Destination (Join-Path $SharePath $exeName) -Force
Set-Content -Path (Join-Path $SharePath "version.txt") -Value $version -NoNewline
Write-Host "Pushed $exeName and version.txt ($version) to share"

# Restart the app if it was running before
if ($running.Count -gt 0) {
    Write-Host "Restarting RxBarcodeListener..."
    Start-Process -FilePath $exePath
}

Write-Host ""
Write-Host "Done. Installed apps will prompt to update on their next launch."
