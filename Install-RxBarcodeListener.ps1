#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs RxBarcodeListener from the network share and registers it to auto-start at logon.

.DESCRIPTION
    Pulls RxBarcodeListener.exe from the configured network share, copies it to
    %LOCALAPPDATA%\RxBarcodeListener\, and creates a Task Scheduler task that launches
    it at logon for the current user with elevated privileges (no UAC prompt on startup).

.USAGE
    Right-click Install-RxBarcodeListener.ps1 → "Run with PowerShell"
    Or from an elevated prompt:
        .\Install-RxBarcodeListener.ps1

.NOTES
    Update $SharePath below to match your server's shared folder before deploying.
#>

$ErrorActionPreference = "Stop"

# --- CONFIGURE THIS ---
$SharePath = "\\172.18.129.75\RxBarcodeListener"
# ----------------------

$appName    = "RxBarcodeListener"
$exeName    = "$appName.exe"
$installDir = Join-Path $env:LOCALAPPDATA $appName
$exeDest    = Join-Path $installDir $exeName
$shareExe   = Join-Path $SharePath $exeName

if (-not (Test-Path $shareExe)) {
    Write-Error "Cannot reach $shareExe — make sure you are on the network and the share path is correct."
    exit 1
}

# --- Install ---
Write-Host "Installing from $SharePath ..."
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path $shareExe -Destination $exeDest -Force
Write-Host "Installed $exeName to $exeDest"

# --- Task Scheduler (elevated logon task, no UAC prompt) ---
$taskName = $appName

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Removed previous scheduled task"
}

$action    = New-ScheduledTaskAction  -Execute $exeDest
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings  = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
    -MultipleInstances IgnoreNew `
    -DisallowHardTerminate $false
$principal = New-ScheduledTaskPrincipal `
    -UserId   $env:USERNAME `
    -LogonType Interactive `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName  $taskName `
    -Action    $action `
    -Trigger   $trigger `
    -Settings  $settings `
    -Principal $principal `
    -Force | Out-Null

Write-Host "Scheduled task '$taskName' registered — will start elevated at logon"

# --- Launch it now ---
$running = Get-Process -Name $appName -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "$appName is already running (PID $($running.Id)) — skipping launch"
} else {
    Write-Host "Starting $appName ..."
    Start-Process -FilePath $exeDest
}

Write-Host ""
Write-Host "Done. $appName is installed and will start automatically at logon."
