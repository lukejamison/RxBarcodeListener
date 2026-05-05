#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes RxBarcodeListener and its scheduled task.
#>

$ErrorActionPreference = "Stop"

$appName    = "RxBarcodeListener"
$installDir = Join-Path $env:LOCALAPPDATA $appName

# Stop the process if running
$proc = Get-Process -Name $appName -ErrorAction SilentlyContinue
if ($proc) {
    Stop-Process -InputObject $proc -Force
    Write-Host "Stopped $appName (PID $($proc.Id))"
}

# Remove scheduled task
if (Get-ScheduledTask -TaskName $appName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $appName -Confirm:$false
    Write-Host "Removed scheduled task '$appName'"
}

# Remove install directory
if (Test-Path $installDir) {
    Remove-Item -Path $installDir -Recurse -Force
    Write-Host "Removed $installDir"
}

Write-Host ""
Write-Host "Done. $appName has been uninstalled."
