using Sentry;

namespace RxBarcodeListener;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Initialize Sentry before anything else so startup errors are captured
        using var _ = SentrySdk.Init(options =>
        {
            options.Dsn = Config.SentryDsn;
            options.Environment = "production";
            options.TracesSampleRate = 0;        // No performance tracing needed
            options.AutoSessionTracking = false;
        });

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Run as ApplicationContext — no main window, tray only
        Application.Run(new TrayApp());
    }
}
