using Sentry;

namespace RxBarcodeListener;

/// <summary>
/// Sends a periodic "I'm alive" check-in to a Sentry Crons monitor.
///
/// This is the answer to "how do I get notified if the app goes down" — Task Scheduler's
/// RestartCount/RestartInterval (see Installer.cs) keeps the PROCESS itself alive, but
/// nothing watches the watcher: if the PC is off, nobody's logged in, the network is down,
/// or the process is wedged, the app obviously can't report its own death from inside
/// itself. A dead-man's-switch works the other way around — Sentry expects a check-in
/// every <see cref="IntervalMinutes"/> minutes, and if one doesn't show up within
/// <see cref="MarginMinutes"/> minutes of the expected time, Sentry raises an alert on
/// its own schedule, independent of whether this process is even running anymore.
///
/// One-time setup required in the Sentry dashboard: Monitors → rxbarcodelistener-heartbeat
/// → Alerts → add a notification action (email/Slack/etc.) for "missed" and "error" check-ins.
/// The monitor itself is auto-created (upserted) by the first check-in below — no manual
/// monitor creation needed.
/// </summary>
public static class Heartbeat
{
    private const string MonitorSlug = "rxbarcodelistener-heartbeat";
    private const int IntervalMinutes = 5;
    private const int MarginMinutes   = 5; // grace period before Sentry considers a check-in "missed"

    private static System.Windows.Forms.Timer? _timer;

    public static void Start()
    {
        if (string.IsNullOrWhiteSpace(Config.SentryDsn) || Config.SentryDsn == "REPLACE_ME")
        {
            Logger.Log("Heartbeat: Sentry DSN not configured — heartbeat monitoring is DISABLED");
            return;
        }

        Logger.Log($"Heartbeat: starting Sentry Crons check-in every {IntervalMinutes} min " +
                   $"(monitor: {MonitorSlug})");

        SendCheckIn(); // immediate check-in so the monitor shows "up" right away instead of
                        // waiting a full interval after every app start

        _timer = new System.Windows.Forms.Timer { Interval = IntervalMinutes * 60 * 1000 };
        _timer.Tick += (_, _) => SendCheckIn();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private static void SendCheckIn()
    {
        try
        {
            // Heartbeat-style monitor: just report Ok on a fixed cadence (no InProgress/duration
            // tracking needed, since this isn't a discrete job with a start/end — it's "the app
            // is alive right now"). configureMonitorOptions upserts the schedule/margin every
            // call, which is safe/idempotent and keeps the dashboard config in sync with this code.
            SentrySdk.CaptureCheckIn(MonitorSlug, CheckInStatus.Ok, configureMonitorOptions: options =>
            {
                options.Interval(IntervalMinutes, SentryMonitorInterval.Minute);
                options.CheckInMargin = TimeSpan.FromMinutes(MarginMinutes);
            });
        }
        catch (Exception ex)
        {
            // Deliberately not fatal — a failed heartbeat send just means Sentry finds out
            // "late" (via the missed-checkin timeout) rather than immediately.
            Logger.LogError("Heartbeat: failed to send Sentry check-in", ex);
        }
    }
}
