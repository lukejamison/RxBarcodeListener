using Sentry;

namespace RxBarcodeListener;

/// <summary>
/// Simple rolling log file writer + Sentry error bridge.
/// Log file: %LOCALAPPDATA%\RxBarcodeListener\log.txt
/// Rolls over at 1 MB to prevent unbounded growth.
/// </summary>
public static class Logger
{
    private static string _logPath = "";
    private const long MaxLogBytes = 1_000_000; // 1 MB
    private static readonly object _logLock = new();

    public static void Initialize()
    {
        _logPath = Config.LogFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        Log("=== RxBarcodeListener started ===");
    }

    public static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            lock (_logLock)
            {
                RollIfNeeded();
                File.AppendAllText(_logPath, line);
            }
        }
        catch { /* Never crash on a logging failure */ }
    }

    public static void LogError(string message, Exception ex)
    {
        Log($"ERROR: {message} | {ex.Message}");

        SentrySdk.CaptureException(ex, scope =>
        {
            scope.SetExtra("message", message);
        });
    }

    public static void OpenLogFile()
    {
        if (File.Exists(_logPath))
            System.Diagnostics.Process.Start("notepad.exe", _logPath);
    }

    private static void RollIfNeeded()
    {
        if (!File.Exists(_logPath)) return;
        var info = new FileInfo(_logPath);
        if (info.Length > MaxLogBytes)
        {
            var backup = _logPath.Replace(".txt", $"-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.Move(_logPath, backup);
        }
    }
}
