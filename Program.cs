using Sentry;

namespace RxBarcodeListener;

static class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            AppSettings.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to load configuration:\n\n{ex.Message}\n\n" +
                "Make sure a .env file exists next to the exe or in the project root.\n" +
                "Copy .env.example to .env and fill in your values.",
                "RxBarcodeListener — Configuration Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // Initialize Sentry FIRST, before anything else — Logger.LogError() forwards to
        // Sentry, and SingleInstance/Installer below can both hit error paths during
        // startup. Any Sentry init done later would silently drop those early errors
        // (SentrySdk.CaptureException is a no-op before Init runs).
        using var _ = SentrySdk.Init(options =>
        {
            options.Dsn = AppSettings.SentryDsn;
            options.Environment = "production";
            options.TracesSampleRate = 0;        // No performance tracing needed
            options.AutoSessionTracking = false;
        });

        // Initialize logging next so SingleInstance/Installer actions below get recorded.
        Logger.Initialize();

        // Always terminate any other running copy before doing anything else — covers
        // manual re-launches during testing as well as installs/updates.
        SingleInstance.KillOtherInstances();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // If not running from the install location, offer to install and exit
        if (Installer.CheckAndInstall()) return;

        // Safety nets: by default an unhandled exception on ANY thread (including the
        // background Task.Run threads used for barcode lookups) silently kills the whole
        // process — no dialog, no log line. These handlers make sure every unhandled
        // exception is at least logged/reported before we go down, and keep the app
        // alive whenever the runtime allows it (UI-thread exceptions).
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

        // Run as ApplicationContext — no main window, tray only
        Application.Run(new TrayApp());
    }
}
