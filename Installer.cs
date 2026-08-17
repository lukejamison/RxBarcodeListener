using System.Diagnostics;

namespace RxBarcodeListener;

/// <summary>
/// Handles first-run self-installation.
///
/// When the user downloads the exe (e.g. from a GitHub Release) and runs it from
/// Downloads or any location outside the install folder, this class detects that,
/// offers to install, copies the exe into place, registers the Task Scheduler task
/// so it starts elevated at every logon, and then relaunches from the install location.
///
/// On subsequent runs from the install folder this is a no-op.
/// </summary>
static class Installer
{
    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RxBarcodeListener");

    private static readonly string InstallExe = Path.Combine(InstallDir, "RxBarcodeListener.exe");

    private static string CurrentExe =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Cannot determine current exe path.");

    public static bool IsInstalled =>
        string.Equals(CurrentExe, InstallExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Call at startup. If the exe is not running from the install location, prompts the
    /// user to install and returns true (the caller should immediately return/exit).
    /// Returns false when already running from the install location — no action taken.
    /// </summary>
    public static bool CheckAndInstall()
    {
        if (IsInstalled) return false;

        var result = MessageBox.Show(
            "Install RxBarcodeListener?\n\n" +
            $"The app will be copied to:\n{InstallDir}\n\n" +
            "It will then start automatically at every logon (elevated, no UAC prompt).",
            "RxBarcodeListener \u2014 Install",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (result != DialogResult.Yes) return true;

        try
        {
            RunInstall();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Installation failed:\n\n{ex.Message}",
                "RxBarcodeListener \u2014 Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        return true;
    }

    // -------------------------------------------------------------------------

    private static void RunInstall()
    {
        Directory.CreateDirectory(InstallDir);

        // Stop any already-running installed instance so we can overwrite the exe
        foreach (var proc in Process.GetProcessesByName("RxBarcodeListener"))
        {
            if (proc.Id == Environment.ProcessId) continue;
            try { proc.Kill(); proc.WaitForExit(3000); } catch { /* best effort */ }
        }

        File.Copy(CurrentExe, InstallExe, overwrite: true);
        RegisterScheduledTask();

        // Launch from the install location
        Process.Start(new ProcessStartInfo
        {
            FileName        = InstallExe,
            UseShellExecute = true
        });

        MessageBox.Show(
            "RxBarcodeListener installed successfully.\n\n" +
            "It is now running in the system tray and will start automatically at every logon.",
            "RxBarcodeListener \u2014 Installed",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void RegisterScheduledTask()
    {
        // Uses PowerShell's Register-ScheduledTask (not schtasks.exe) because only the
        // Task Scheduler cmdlets expose RestartCount/RestartInterval. That setting makes
        // Windows itself relaunch the app - elevated, with no UAC prompt - if it ever
        // crashes or is closed mid-session, not just at the next logon.
        // RunLevel Highest + LogonType Interactive is what avoids the UAC prompt entirely;
        // the exe's requireAdministrator manifest needs this for the keyboard hook to
        // receive input when elevated apps (e.g. PioneerRx) are focused.
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var script = $$"""
            $action    = New-ScheduledTaskAction -Execute '{{InstallExe.Replace("'", "''")}}'
            $trigger   = New-ScheduledTaskTrigger -AtLogOn -User '{{user.Replace("'", "''")}}'
            $settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -MultipleInstances IgnoreNew -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
            $principal = New-ScheduledTaskPrincipal -UserId '{{user.Replace("'", "''")}}' -LogonType Interactive -RunLevel Highest
            Register-ScheduledTask -TaskName 'RxBarcodeListener' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
            """;

        var scriptPath = Path.Combine(Path.GetTempPath(), "RxBarcodeListener_register_task.ps1");
        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo("powershell.exe",
            $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        try { File.Delete(scriptPath); } catch { /* best effort */ }

        if (proc.ExitCode != 0)
            throw new Exception($"Register-ScheduledTask failed (exit {proc.ExitCode}): {stderr}");
    }
}
