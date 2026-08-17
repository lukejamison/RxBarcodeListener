using System.Reflection;

namespace RxBarcodeListener;

/// <summary>
/// Self-update from the internal Linux update server (HTTP or optional SMB share).
/// GitHub is source code only — releases are NOT published there.
/// </summary>
public static class Updater
{
    private const string ExeName     = "RxBarcodeListener.exe";
    private const string VersionFile = "version.txt";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static Updater()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "RxBarcodeListener-Updater");
    }

    private enum UpdateSource { Http, Share }

    public class UpdateInfo
    {
        private readonly UpdateSource _source;

        public Version Version       { get; }
        public string  VersionString { get; }
        public string? DownloadUrl   { get; }

        private UpdateInfo(Version version, string versionString, UpdateSource source, string? downloadUrl)
        {
            Version       = version;
            VersionString = versionString;
            _source       = source;
            DownloadUrl   = downloadUrl;
        }

        public static UpdateInfo FromHttp(Version version, string versionString, string downloadUrl) =>
            new(version, versionString, UpdateSource.Http, downloadUrl);

        public static UpdateInfo FromShare(Version version, string versionString) =>
            new(version, versionString, UpdateSource.Share, downloadUrl: null);

        public bool IsFromHttp => _source == UpdateSource.Http;
    }

    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        if (!string.IsNullOrWhiteSpace(Config.UpdateBaseUrl))
        {
            try
            {
                var info = await CheckHttpUpdateAsync().ConfigureAwait(false);
                if (info != null) return info;
            }
            catch (Exception ex)
            {
                Logger.Log($"Updater: HTTP check failed — {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(Config.UpdateSharePath))
        {
            try
            {
                return await CheckShareUpdateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Log($"Updater: share check failed — {ex.Message}");
            }
        }

        return null;
    }

    public static async void InstallUpdate(UpdateInfo info)
    {
        try
        {
            var stagedPath = await StageUpdateAsync(info).ConfigureAwait(true);
            LaunchUpdaterScriptAndExit(stagedPath);
        }
        catch (Exception ex)
        {
            Logger.LogError("Updater: failed to install update", ex);
            MessageBox.Show(
                $"Update failed:\n\n{ex.Message}\n\nThe app will keep running on the current version.",
                "RxBarcodeListener — Update Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static async Task<UpdateInfo?> CheckHttpUpdateAsync()
    {
        var baseUrl = Config.UpdateBaseUrl.TrimEnd('/');
        var versionUrl = $"{baseUrl}/{VersionFile}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var remoteVersionStr = (await Http.GetStringAsync(versionUrl, cts.Token).ConfigureAwait(false)).Trim();

        if (!Version.TryParse(remoteVersionStr, out var remoteVersion))
        {
            Logger.Log($"Updater: could not parse HTTP version '{remoteVersionStr}' from {versionUrl}");
            return null;
        }

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        Logger.Log($"Updater: local={localVersion}, server={remoteVersion} ({versionUrl})");

        if (remoteVersion <= localVersion)
            return null;

        return UpdateInfo.FromHttp(remoteVersion, remoteVersionStr, $"{baseUrl}/{ExeName}");
    }

    private static async Task<UpdateInfo?> CheckShareUpdateAsync()
    {
        var versionFilePath = Path.Combine(Config.UpdateSharePath, VersionFile);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var remoteVersionStr = (await Task.Run(
            () => File.ReadAllText(versionFilePath), cts.Token).ConfigureAwait(false)).Trim();

        if (!Version.TryParse(remoteVersionStr, out var remoteVersion))
        {
            Logger.Log($"Updater: could not parse share version '{remoteVersionStr}'");
            return null;
        }

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        Logger.Log($"Updater: local={localVersion}, share={remoteVersion}");

        return remoteVersion > localVersion ? UpdateInfo.FromShare(remoteVersion, remoteVersionStr) : null;
    }

    private static async Task<string> StageUpdateAsync(UpdateInfo info)
    {
        var stagedPath = Path.Combine(Path.GetTempPath(), "RxBarcodeListener_staged.exe");

        if (info.IsFromHttp)
        {
            Logger.Log($"Updater: downloading v{info.VersionString} from {info.DownloadUrl}");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var bytes = await Http.GetByteArrayAsync(info.DownloadUrl!, cts.Token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(stagedPath, bytes, cts.Token).ConfigureAwait(false);
        }
        else
        {
            var shareExe = Path.Combine(Config.UpdateSharePath, ExeName);
            Logger.Log($"Updater: copying v{info.VersionString} from share {shareExe}");
            await Task.Run(() => File.Copy(shareExe, stagedPath, overwrite: true)).ConfigureAwait(false);
        }

        return stagedPath;
    }

    private static void LaunchUpdaterScriptAndExit(string stagedPath)
    {
        var exePath = Environment.ProcessPath!;
        var pid = Environment.ProcessId;

        var scriptLines = new[]
        {
            "# RxBarcodeListener auto-updater",
            $"$appPid = {pid}",
            $"$dest   = '{exePath.Replace("'", "''")}'",
            $"$staged = '{stagedPath.Replace("'", "''")}'",
            "$waited = 0",
            "while ((Get-Process -Id $appPid -ErrorAction SilentlyContinue) -and $waited -lt 10) {",
            "    Start-Sleep -Seconds 1",
            "    $waited++",
            "}",
            "Copy-Item -Path $staged -Destination $dest -Force",
            "Remove-Item -Path $staged -Force -ErrorAction SilentlyContinue",
            "Start-Process -FilePath $dest",
        };

        var scriptPath = Path.Combine(Path.GetTempPath(), "RxBarcodeListener_updater.ps1");
        File.WriteAllText(scriptPath, string.Join(Environment.NewLine, scriptLines));

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
        });

        Logger.Log("Updater: launched updater script, exiting for update");

        // Route through TrayApp.Shutdown() (hides the tray icon, uninstalls the keyboard
        // hook) instead of calling Application.Exit() directly — otherwise the tray icon
        // is left behind as a "ghost" in the notification area until the user hovers over it.
        if (TrayApp.Current != null) TrayApp.Current.Shutdown();
        else Application.Exit();
    }
}
