using System.Reflection;

namespace RxBarcodeListener;

/// <summary>
/// Handles version checking and self-update from the network share defined in Config.
///
/// Update flow:
///   1. On startup, CheckForUpdateAsync() reads version.txt from the share.
///   2. If the share version is newer, the caller shows a balloon tip + tray menu item.
///   3. When the user clicks "Install Update", InstallUpdate() writes a short PowerShell
///      updater script to %TEMP%, launches it, then exits the app.
///   4. The updater script waits for this process to exit, copies the new exe from the
///      share over the installed exe, then restarts the app.
/// </summary>
public static class Updater
{
    private const string ExeName       = "RxBarcodeListener.exe";
    private const string VersionFile   = "version.txt";

    /// <summary>
    /// Compares the version.txt on the share against the running assembly version.
    /// Returns the remote version string if an update is available, null otherwise.
    /// Never throws — all errors are logged and swallowed.
    /// </summary>
    public static async Task<string?> CheckForUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(Config.NetworkSharePath))
            return null;

        try
        {
            var versionFilePath = Path.Combine(Config.NetworkSharePath, VersionFile);

            // Use Task.Run because File.ReadAllTextAsync on a UNC path can block
            // the thread pool on slow/unreachable shares; we want a short timeout.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var remoteVersionStr = await Task.Run(
                () => File.ReadAllText(versionFilePath), cts.Token).ConfigureAwait(false);

            if (!Version.TryParse(remoteVersionStr.Trim(), out var remoteVersion))
            {
                Logger.Log($"Updater: could not parse remote version '{remoteVersionStr.Trim()}'");
                return null;
            }

            var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            Logger.Log($"Updater: local={localVersion}, remote={remoteVersion}");

            return remoteVersion > localVersion ? remoteVersionStr.Trim() : null;
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Updater: version check timed out (share unreachable?)");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"Updater: version check failed — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes a PowerShell updater script to %TEMP%, launches it hidden, then exits
    /// the app so the script can overwrite the running exe.
    /// </summary>
    public static void InstallUpdate()
    {
        var exePath  = Environment.ProcessPath!;
        var shareExe = Path.Combine(Config.NetworkSharePath, ExeName);
        var pid      = Environment.ProcessId;

        // Build the PowerShell script line by line to avoid brace-escaping issues
        // with C# interpolated strings.
        var scriptLines = new[]
        {
            "# RxBarcodeListener auto-updater - generated at runtime, safe to delete",
            $"$appPid = {pid}",
            $"$dest = '{exePath.Replace("'", "''")}'",
            $"$src  = '{shareExe.Replace("'", "''")}'",
            "$waited = 0",
            "while ((Get-Process -Id $appPid -ErrorAction SilentlyContinue) -and $waited -lt 10) {",
            "    Start-Sleep -Seconds 1",
            "    $waited++",
            "}",
            "Copy-Item -Path $src -Destination $dest -Force",
            "Start-Process -FilePath $dest",
        };
        var script = string.Join(Environment.NewLine, scriptLines);

        var scriptPath = Path.Combine(Path.GetTempPath(), "RxBarcodeListener_updater.ps1");
        File.WriteAllText(scriptPath, script);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
        });

        Logger.Log("Updater: launched updater script, exiting for update");
        Application.Exit();
    }
}
