#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Builds RxBarcodeListener, publishes a GitHub Release (primary auto-update source),
    and pushes it to the network share (fallback auto-update source).
    Bump <Version> in RxBarcodeListener.csproj before running to release a new version.
    Place a .env file in the install folder (%LOCALAPPDATA%\RxBarcodeListener\) on each
    workstation — it is NOT bundled in the published exe (secrets stay out of git).
    Run from an elevated PowerShell prompt: .\Publish-RxBarcodeListener.ps1

.NOTES
    GitHub publishing requires the `gh` CLI to be installed and authenticated
    (`gh auth login`). If `gh` is missing/unauthenticated, that step is skipped with a
    warning and the network-share push still happens — installed apps just won't see
    the update via GitHub until it's published there too.
#>

$ErrorActionPreference = "Stop"

$SharePath      = "\\172.18.129.75\RxBarcodeListener"
$GitHubRepo     = "lukejamison/RxBarcodeListener" # must match AppSettings GitHubRepoOwner/Name
$scriptDir      = Split-Path -Parent $MyInvocation.MyCommand.Path
$project        = Join-Path $scriptDir "RxBarcodeListener.csproj"
$publishDir     = "$env:LOCALAPPDATA\RxBarcodeListener-build\bin\Release\net8.0-windows\win-x64\publish"
$exeName        = "RxBarcodeListener.exe"

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

# --- Publish to GitHub Releases (primary auto-update source) ---
$tag = "v$version"
$ghAvailable = $null -ne (Get-Command gh -ErrorAction SilentlyContinue)
if ($ghAvailable) {
    Write-Host "Publishing GitHub release $tag ..."
    try {
        $ErrorActionPreference = "Continue"
        gh release view $tag --repo $GitHubRepo *> $null
        $releaseExists = $LASTEXITCODE -eq 0

        if ($releaseExists) {
            gh release upload $tag $exePath --repo $GitHubRepo --clobber
        } else {
            gh release create $tag $exePath --repo $GitHubRepo --title $tag --notes "Automated release from Publish-RxBarcodeListener.ps1"
        }

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "gh release publish failed (exit $LASTEXITCODE) - installed apps will fall back to the network share until this is fixed."
        } else {
            Write-Host "Published $exeName to GitHub release $tag"
        }
    } finally {
        $ErrorActionPreference = "Stop"
    }
} else {
    Write-Warning "GitHub CLI (gh) not found - skipping GitHub release. Run 'winget install GitHub.cli' then 'gh auth login' to enable it."
}

# --- Push to network share (fallback auto-update source) ---
if (-not (Test-Path $SharePath)) {
    Write-Warning "Cannot reach $SharePath - skipping network-share fallback push."
} else {
    Write-Host "Copying to $SharePath ..."
    Copy-Item -Path $exePath -Destination (Join-Path $SharePath $exeName) -Force
    Set-Content -Path (Join-Path $SharePath "version.txt") -Value $version -NoNewline
    Copy-Item -Path (Join-Path $scriptDir "Install-RxBarcodeListener.ps1") -Destination (Join-Path $SharePath "Install-RxBarcodeListener.ps1") -Force
    $localEnv = Join-Path $scriptDir ".env"
    if (Test-Path $localEnv) {
        Copy-Item -Path $localEnv -Destination (Join-Path $SharePath ".env") -Force
        Write-Host "Pushed .env to share (secrets — not in git)"
    } else {
        Write-Warning ".env not found in repo root — share deploy has no .env; copy one manually to the share."
    }
    Write-Host "Pushed $exeName, version.txt ($version), and Install-RxBarcodeListener.ps1 to share"
}

# Restart the app if it was running before
if ($running.Count -gt 0) {
    Write-Host "Restarting RxBarcodeListener..."
    Start-Process -FilePath $exePath
}

Write-Host ""
Write-Host "Done. Installed apps will check GitHub (or the share, as a fallback) and prompt to update within a few seconds of their next launch."
