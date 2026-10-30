namespace RxBarcodeListener;

/// <summary>
/// Where the installed copy lives. Installation itself (copying the exe, the logon task,
/// shortcuts) is done once, as administrator, by Install-RxBarcodeListener.ps1 - the app
/// no longer installs or re-registers itself, because it now runs without administrator
/// rights for the daily (standard) Windows user.
///
/// Install location is machine-wide (C:\ProgramData\RxBarcodeListener), NOT inside any one
/// user's profile, so the same copy works for every Windows user on the PC. Logs and
/// settings still go to each user's own %LOCALAPPDATA%.
/// </summary>
static class Installer
{
    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RxBarcodeListener");

    public static readonly string InstallExe = Path.Combine(InstallDir, "RxBarcodeListener.exe");

    private static string CurrentExe =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? "";

    public static bool IsInstalled =>
        string.Equals(CurrentExe, InstallExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Logs where the app is running from. A copy running from anywhere else (for example a
    /// dev build) works normally but won't have the logon task or shortcuts pointing at it.
    /// </summary>
    public static void LogInstallState()
    {
        Logger.Log(IsInstalled
            ? $"Running from the installed location: {InstallExe}"
            : $"Running from {CurrentExe} (not the installed location {InstallExe})");
    }
}
