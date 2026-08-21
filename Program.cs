using Sentry;

namespace RxBarcodeListener;

static class Program
{
    [STAThread]
    static void Main()
    {
        if (string.IsNullOrWhiteSpace(Config.NimbleRxBearerToken) ||
            Config.NimbleRxBearerToken == "REPLACE_ME" ||
            string.IsNullOrWhiteSpace(Config.PioneerRxApiKey) ||
            Config.PioneerRxApiKey == "REPLACE_ME")
        {
            MessageBox.Show(
                "Config.cs is missing real values.\n\n" +
                "Copy Config.example.cs to Config.cs, fill in your keys, then rebuild.",
                "RxBarcodeListener — Config Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Logger.Initialize();

        if (string.IsNullOrWhiteSpace(Config.SentryDsn) || Config.SentryDsn == "REPLACE_ME")
        {
            Logger.Log("Sentry: DSN not configured — error reporting is DISABLED");
        }
        else
        {
            Logger.Log($"Sentry: initializing (project host: {new Uri(Config.SentryDsn).Host})");
        }

        using var _ = SentrySdk.Init(options =>
        {
            options.Dsn = Config.SentryDsn;
            // Environment is deliberately the machine name rather than a fixed "production" —
            // this pharmacy runs the same build on several POS computers, and tagging errors
            // and Sentry Crons check-ins (see Heartbeat.cs) by machine is what lets a "down"
            // alert say WHICH computer is down instead of lumping all of them together.
            options.Environment = Environment.MachineName;
            options.TracesSampleRate = 0;
            options.AutoSessionTracking = false;
            options.Release = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            // Route the SDK's own internal diagnostics into our log file instead of the
            // console (which nobody sees on a tray app) — makes DSN/network problems
            // visible in the same log a user would send via "Send Report to Developer".
            options.Debug = true;
            options.DiagnosticLevel = SentryLevel.Warning;
            options.DiagnosticLogger = new SentryLogger();
        });

        Logger.Log("Sentry: SDK initialized");
        SingleInstance.KillOtherInstances();

        // Must be set before any window handles are created (and before EnableVisualStyles)
        // so labels/buttons/forms scale correctly on non-100% displays instead of blurring
        // or clipping — this app has no manifest-level DPI declaration, so it defaults to
        // system-DPI-aware without this call.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (Installer.CheckAndInstall()) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            Logger.LogError("Unhandled UI-thread exception (app kept running)", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.LogError(
                $"Unhandled exception on a non-UI thread — process will terminate (isTerminating={e.IsTerminating})",
                e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.LogError("Unobserved task exception (suppressed)", e.Exception);
            e.SetObserved();
        };

        Application.Run(new TrayApp());
    }
}
