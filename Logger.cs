using Sentry;
using Sentry.Extensibility;

namespace RxBarcodeListener;

/// <summary>
/// Routes the Sentry SDK's own internal diagnostics (DSN parse errors, network failures
/// sending events, etc.) into our normal log file. Without this, SDK-level problems are
/// invisible on a tray app with no console — this is the only way to notice, from the
/// log file alone, that Sentry itself isn't working.
/// </summary>
internal class SentryLogger : IDiagnosticLogger
{
    public bool IsEnabled(SentryLevel level) => level >= SentryLevel.Warning;

    public void Log(SentryLevel logLevel, string message, Exception? exception = null, params object?[] args)
    {
        var formatted = args.Length > 0 ? string.Format(message, args) : message;
        Logger.Log($"[Sentry SDK/{logLevel}] {formatted}" + (exception != null ? $" | {exception.Message}" : ""));
    }
}

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

    /// <summary>
    /// Returns the last <paramref name="maxChars"/> characters of the log file for
    /// display in the Diagnostics window, or a placeholder if there's nothing yet.
    /// Never throws.
    /// </summary>
    public static string GetRecentLogText(int maxChars = 20_000)
    {
        try
        {
            lock (_logLock)
            {
                if (!File.Exists(_logPath)) return "(no log entries yet)";

                using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                return text.Length > maxChars ? text[^maxChars..] : text;
            }
        }
        catch (Exception ex)
        {
            return $"(failed to read log file: {ex.Message})";
        }
    }

    /// <summary>
    /// Sends a manual diagnostic report to Sentry with the full log file attached, so a
    /// pharmacy user can proactively report "something looks off" even when nothing has
    /// actually thrown an exception. Returns the Sentry event ID on success, or null if
    /// sending failed (e.g. Sentry unreachable) — never throws.
    /// </summary>
    public static string? SendDiagnosticReport(string? userNote)
    {
        try
        {
            Log($"Diagnostic report requested by user{(string.IsNullOrWhiteSpace(userNote) ? "" : $" — note: {userNote}")}");

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

            var id = SentrySdk.CaptureMessage("Manual diagnostic report", scope =>
            {
                scope.Level = SentryLevel.Info;
                scope.SetTag("report_type", "manual");
                scope.SetExtra("version", version?.ToString() ?? "unknown");
                scope.SetExtra("machine", Environment.MachineName);
                scope.SetExtra("user_note", string.IsNullOrWhiteSpace(userNote) ? "(none)" : userNote);

                if (File.Exists(_logPath))
                    scope.AddAttachment(_logPath);
            });

            // Manual reports are informational, not errors — flush immediately so the
            // event actually reaches Sentry before the user closes the dialog, instead
            // of waiting for the SDK's normal batched background flush.
            SentrySdk.Flush(TimeSpan.FromSeconds(5));

            Log($"Diagnostic report sent — Sentry event {id}");
            return id.ToString();
        }
        catch (Exception ex)
        {
            Log($"Diagnostic report failed to send — {ex.Message}");
            return null;
        }
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
