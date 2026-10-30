using System.Diagnostics;

namespace RxBarcodeListener;

/// <summary>
/// Task names used by the install script, and the "restart me after I exit" helper.
/// The app runs without administrator rights, so relaunching is just starting the exe
/// again as the same user - no UAC prompt and no scheduled task needed.
/// </summary>
static class Launcher
{
    /// <summary>Runs at logon for every user and auto-restarts after crashes.</summary>
    public const string LogonTaskName = "RxBarcodeListener";

    /// <summary>Old on-demand task from earlier versions; the install script removes it.</summary>
    public const string LegacyStartTaskName = "RxBarcodeListener-Start";

    /// <summary>
    /// Spawns a hidden helper that waits for this process to exit, then starts the exe
    /// again as the current user.
    /// </summary>
    public static void ScheduleRelaunchAfterCurrentProcessExit()
    {
        var pid = Environment.ProcessId;
        var exe = Environment.ProcessPath ?? Installer.InstallExe;
        var scriptPath = Path.Combine(Path.GetTempPath(), "RxBarcodeListener_relaunch.ps1");
        var script =
            $"$appPid = {pid}\r\n" +
            "while (Get-Process -Id $appPid -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }\r\n" +
            $"Start-Process -FilePath '{exe.Replace("'", "''")}'\r\n" +
            $"Remove-Item -LiteralPath '{scriptPath.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue\r\n";
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo(
            "powershell.exe",
            $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
    }
}
