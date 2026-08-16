using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Handles version checking and self-update.
///
/// Primary source: the GitHub Releases page for this repo (plain HTTPS, works from
/// anywhere with internet — no VPN or LAN access needed). Falls back to the network
/// share (AppSettings.NetworkSharePath) only if GitHub itself is unreachable.
///
/// Update flow:
///   1. On startup, CheckForUpdateAsync() asks the GitHub API for the latest release tag
///      and compares it against the running assembly version. If GitHub can't be reached
///      at all (offline, DNS failure, timeout), it falls back to reading version.txt from
///      the network share instead.
///   2. If a newer version is found, the caller shows a balloon tip + tray menu item.
///   3. When the user clicks "Install Update", InstallUpdate() downloads (GitHub) or
///      copies (share) the new exe to a local temp file, writes a short PowerShell
///      updater script to %TEMP%, launches it, then exits the app.
///   4. The updater script waits for this process to exit, copies the staged exe over
///      the installed exe, restarts the app, then deletes itself and the staged file.
/// </summary>
public static class Updater
{
    private const string ExeName     = "RxBarcodeListener.exe";
    private const string VersionFile = "version.txt";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static Updater()
    {
        // The GitHub API requires a User-Agent header on every request, and rejects
        // requests without one.
        Http.DefaultRequestHeaders.Add("User-Agent", "RxBarcodeListener-Updater");
        Http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    private enum UpdateSource { GitHub, Share }

    public class UpdateInfo
    {
        private readonly UpdateSource _source;

        public Version Version       { get; }
        public string  VersionString { get; }
        public string? DownloadUrl   { get; } // GitHub source only

        private UpdateInfo(Version version, string versionString, UpdateSource source, string? downloadUrl)
        {
            Version       = version;
            VersionString = versionString;
            _source       = source;
            DownloadUrl   = downloadUrl;
        }

        public static UpdateInfo FromGitHub(Version version, string versionString, string downloadUrl) =>
            new(version, versionString, UpdateSource.GitHub, downloadUrl);

        public static UpdateInfo FromShare(Version version, string versionString) =>
            new(version, versionString, UpdateSource.Share, downloadUrl: null);

        public bool IsFromGitHub => _source == UpdateSource.GitHub;
    }

    /// <summary>
    /// Checks GitHub Releases first; if that request fails outright (not just "no
    /// update"), falls back to the network share's version.txt.
    /// Never throws — all errors are logged and swallowed.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            return await CheckGitHubUpdateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"Updater: GitHub check failed, falling back to network share — {ex.Message}");
        }

        try
        {
            return await CheckShareUpdateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"Updater: network share check also failed — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads (GitHub) or copies (share) the update to a local temp file, then hands
    /// off to a PowerShell script that swaps it in and restarts the app. Never throws —
    /// failures are logged and shown to the user via a message box instead of crashing.
    /// </summary>
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

    // -------------------------------------------------------------------------

    private static async Task<UpdateInfo?> CheckGitHubUpdateAsync()
    {
        var url = $"https://api.github.com/repos/{AppSettings.GitHubRepoOwner}/{AppSettings.GitHubRepoName}/releases/latest";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var json = await Http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        var release = JObject.Parse(json);

        var tagName = release["tag_name"]?.Value<string>() ?? "";
        var versionStr = tagName.TrimStart('v', 'V');

        if (!Version.TryParse(versionStr, out var remoteVersion))
        {
            Logger.Log($"Updater: could not parse GitHub release tag '{tagName}'");
            return null;
        }

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        Logger.Log($"Updater: local={localVersion}, GitHub latest={remoteVersion}");

        if (remoteVersion <= localVersion)
            return null;

        var downloadUrl = release["assets"]?
            .FirstOrDefault(a => string.Equals(a["name"]?.Value<string>(), ExeName, StringComparison.OrdinalIgnoreCase))
            ?["browser_download_url"]?.Value<string>();

        if (string.IsNullOrEmpty(downloadUrl))
        {
            Logger.Log($"Updater: GitHub release '{tagName}' has no {ExeName} asset — skipping");
            return null;
        }

        return UpdateInfo.FromGitHub(remoteVersion, versionStr, downloadUrl);
    }

    private static async Task<UpdateInfo?> CheckShareUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(AppSettings.NetworkSharePath))
            return null;

        var versionFilePath = Path.Combine(AppSettings.NetworkSharePath, VersionFile);

        // Use Task.Run because File.ReadAllTextAsync on a UNC path can block the thread
        // pool on slow/unreachable shares; we want a short timeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var remoteVersionStr = (await Task.Run(
            () => File.ReadAllText(versionFilePath), cts.Token).ConfigureAwait(false)).Trim();

        if (!Version.TryParse(remoteVersionStr, out var remoteVersion))
        {
            Logger.Log($"Updater: could not parse remote version '{remoteVersionStr}' from share");
            return null;
        }

        var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        Logger.Log($"Updater: local={localVersion}, share={remoteVersion}");

        return remoteVersion > localVersion ? UpdateInfo.FromShare(remoteVersion, remoteVersionStr) : null;
    }

    /// <summary>
    /// Downloads or copies the new exe into %TEMP%, returning the staged file's path.
    /// </summary>
    private static async Task<string> StageUpdateAsync(UpdateInfo info)
    {
        var stagedPath = Path.Combine(Path.GetTempPath(), "RxBarcodeListener_staged.exe");

        if (info.IsFromGitHub)
        {
            Logger.Log($"Updater: downloading v{info.VersionString} from {info.DownloadUrl}");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var bytes = await Http.GetByteArrayAsync(info.DownloadUrl!, cts.Token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(stagedPath, bytes, cts.Token).ConfigureAwait(false);
        }
        else
        {
            var shareExe = Path.Combine(AppSettings.NetworkSharePath, ExeName);
            Logger.Log($"Updater: copying v{info.VersionString} from share {shareExe}");
            await Task.Run(() => File.Copy(shareExe, stagedPath, overwrite: true)).ConfigureAwait(false);
        }

        return stagedPath;
    }

    /// <summary>
    /// Writes a PowerShell updater script to %TEMP%, launches it hidden, then exits
    /// the app so the script can overwrite the running exe.
    /// </summary>
    private static void LaunchUpdaterScriptAndExit(string stagedPath)
    {
        var exePath = Environment.ProcessPath!;
        var pid = Environment.ProcessId;

        // Build the PowerShell script line by line to avoid brace-escaping issues
        // with C# interpolated strings.
        var scriptLines = new[]
        {
            "# RxBarcodeListener auto-updater - generated at runtime, safe to delete",
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
