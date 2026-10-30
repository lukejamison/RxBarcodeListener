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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static Updater()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "RxBarcodeListener-Updater");
        Http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
        };
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
                Logger.Log($"Updater: HTTP check failed - {ex.Message}");
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
                Logger.Log($"Updater: share check failed - {ex.Message}");
            }
        }

        return null;
    }

    public static async void InstallUpdate(UpdateInfo info)
    {
        try
        {
            var stagedPath = await StageUpdateAsync(info).ConfigureAwait(true);
            LaunchUpdaterScriptAndExit(stagedPath, info.VersionString);
        }
        catch (Exception ex)
        {
            Logger.LogError("Updater: failed to install update", ex);
            MessageBox.Show(
                $"Update failed:\n\n{ex.Message}\n\nThe app will keep running on the current version.",
                "RxBarcodeListener - Update Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static async Task<UpdateInfo?> CheckHttpUpdateAsync()
    {
        var baseUrl = Config.UpdateBaseUrl.TrimEnd('/');
        // Cache-bust so a stale version.txt behind a proxy doesn't leave POS machines outdated.
        var versionUrl = $"{baseUrl}/{VersionFile}?t={DateTime.UtcNow.Ticks}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var remoteVersionStr = (await Http.GetStringAsync(versionUrl, cts.Token).ConfigureAwait(false)).Trim();

        if (!Version.TryParse(remoteVersionStr, out var remoteVersion))
        {
            Logger.Log($"Updater: could not parse HTTP version '{remoteVersionStr}' from {versionUrl}");
            return null;
        }

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        Logger.Log($"Updater: local={localVersion}, server={remoteVersion} ({baseUrl}/{VersionFile})");

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
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (info.IsFromHttp)
                {
                    Logger.Log($"Updater: downloading v{info.VersionString} (attempt {attempt}) from {info.DownloadUrl}");
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                    var bytes = await Http.GetByteArrayAsync(info.DownloadUrl!, cts.Token).ConfigureAwait(false);
                    if (bytes.Length < 1024 * 1024)
                        throw new InvalidOperationException($"Download too small ({bytes.Length} bytes) - likely an error page.");
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
            catch (Exception ex)
            {
                lastError = ex;
                Logger.LogError($"Updater: stage attempt {attempt} failed", ex);
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException("Update staging failed.");
    }

    private static void LaunchUpdaterScriptAndExit(string stagedPath, string expectedVersion)
    {
        var exePath = Environment.ProcessPath!;
        var pid = Environment.ProcessId;
        var scriptLines = new[]
        {
            "# RxBarcodeListener auto-updater",
            "$ErrorActionPreference = 'Stop'",
            $"$appPid = {pid}",
            $"$dest   = '{exePath.Replace("'", "''")}'",
            $"$staged = '{stagedPath.Replace("'", "''")}'",
            $"$expectedVersion = '{expectedVersion.Replace("'", "''")}'",
            "$waited = 0",
            "while ((Get-Process -Id $appPid -ErrorAction SilentlyContinue) -and $waited -lt 90) {",
            "    Start-Sleep -Seconds 1",
            "    $waited++",
            "}",
            // The exe is shared by every Windows user on this PC. If another user's session
            // still has it running, it can't be overwritten - but Windows does allow renaming
            // a running exe, so move it aside first and drop the new one in its place.
            "Get-ChildItem -Path \"$dest.old-*\" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue",
            "for ($copyTry = 1; $copyTry -le 5; $copyTry++) {",
            "    try {",
            "        try {",
            "            Copy-Item -Path $staged -Destination $dest -Force",
            "        } catch {",
            "            Move-Item -LiteralPath $dest -Destination \"$dest.old-$([DateTime]::UtcNow.Ticks)\" -Force",
            "            Copy-Item -Path $staged -Destination $dest -Force",
            "        }",
            "        break",
            "    } catch {",
            "        if ($copyTry -eq 5) { throw }",
            "        Start-Sleep -Seconds 2",
            "    }",
            "}",
            "Remove-Item -Path $staged -Force -ErrorAction SilentlyContinue",
            "$ver = (Get-Item -LiteralPath $dest).VersionInfo.ProductVersion -replace '\\+.*$',''",
            "if ($ver -ne $expectedVersion) {",
            "    Write-Error \"After update, exe reports v$ver but expected v$expectedVersion\"",
            "    exit 1",
            "}",
            // Relaunch as the same user. The app needs no elevation, so no UAC prompt.
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

        if (TrayApp.Current != null) TrayApp.Current.Shutdown(TrayApp.ShutdownReason.Update);
        else Environment.Exit(0);
    }
}
