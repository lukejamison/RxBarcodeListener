using Sentry;

namespace RxBarcodeListener;

/// <summary>
/// Sends a periodic "I'm alive" check-in to Sentry Crons, but only during store hours.
///
/// This is the answer to "how do I get notified if the app goes down, on any of my
/// computers, during business hours" — Task Scheduler's RestartCount/RestartInterval (see
/// Installer.cs) keeps the PROCESS itself alive, but nothing watches the watcher: if the PC
/// is off, the network is down, or the process is wedged, the app can't report its own death
/// from inside itself. A dead-man's-switch works the other way around — Sentry expects a
/// check-in on a schedule, and if one doesn't show up, Sentry raises an alert on its own,
/// independent of whether this process is even running anymore.
///
/// Two things make this work across multiple pharmacy computers with a single Sentry project:
///
/// 1. Per-machine identity: <see cref="Program"/> sets the Sentry Environment to
///    <c>Environment.MachineName</c>. Sentry Crons tracks check-in status separately per
///    environment within a monitor, so each computer shows up as its own row — "PC-FRONT2
///    missed a check-in" instead of an ambiguous "someone, somewhere, is down".
///
/// 2. Business-hours-only schedules: a plain fixed-interval schedule expects a check-in
///    every N minutes forever, including 2 AM and Sundays before opening — which would mean
///    constant false "down" alerts overnight/on off days. Sentry Crons supports crontab
///    schedules, which only expect a check-in at times matching the cron expression, so
///    hours outside the pharmacy's open hours simply aren't monitored at all. Since a single
///    cron expression can't vary its hour range by day of week, this uses three separate
///    monitors — one per distinct hours-pattern (weekday / Saturday / Sunday) — each with a
///    fixed schedule that's never swapped out, so there's no ambiguity at day-of-week
///    boundaries about what's "expected next".
///
/// One-time setup required in the Sentry dashboard (can't be done via API/MCP — Sentry has no
/// tool to create alert rules programmatically): for each of the 3 monitor slugs below, open
/// Monitors → (slug) → Alerts → add a notification action (email/Slack/etc.) for "missed" and
/// "error" check-ins. The monitors themselves are auto-created (upserted) by the check-ins
/// below — no manual monitor creation needed.
/// </summary>
public static class Heartbeat
{
    private const int TickMinutes  = 5;  // how often we poll to see if a check-in is due
    private const int MarginMinutes = 10; // grace period before Sentry considers a check-in "missed"

    private static readonly string TimeZoneId = ResolveIanaTimeZoneId();

    private static System.Windows.Forms.Timer? _timer;

    public static void Start()
    {
        if (string.IsNullOrWhiteSpace(Config.SentryDsn) || Config.SentryDsn == "REPLACE_ME")
        {
            Logger.Log("Heartbeat: Sentry DSN not configured — heartbeat monitoring is DISABLED");
            return;
        }

        Logger.Log($"Heartbeat: starting business-hours Sentry Crons check-ins " +
                   $"(machine: {Environment.MachineName}, timezone: {TimeZoneId})");

        SendCheckInIfOpen(); // immediate check-in (if currently open) so the monitor shows
                              // "up" right away instead of waiting a full tick after app start

        _timer = new System.Windows.Forms.Timer { Interval = TickMinutes * 60 * 1000 };
        _timer.Tick += (_, _) => SendCheckInIfOpen();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Store hours. Edit here if hours change — everything else (cron expressions, the
    /// "are we open right now" gate) derives from these numbers.
    /// </summary>
    private static (string MonitorSlug, string Cron, int OpenHour, int CloseHour)? GetSchedule(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday or DayOfWeek.Friday =>
            ("rxbarcodelistener-heartbeat-weekday", "*/5 9-17 * * 1-5", 9, 18),   // Mon-Fri 9am-6pm
        DayOfWeek.Saturday =>
            ("rxbarcodelistener-heartbeat-saturday", "*/5 10-16 * * 6", 10, 17),  // Sat 10am-5pm
        DayOfWeek.Sunday =>
            ("rxbarcodelistener-heartbeat-sunday", "*/5 11-15 * * 0", 11, 16),    // Sun 11am-4pm
        _ => null
    };

    private static void SendCheckInIfOpen()
    {
        var now = DateTime.Now; // machine's own local clock — these are physical POS
                                 // computers sitting in the pharmacy, so local time IS store time
        var schedule = GetSchedule(now.DayOfWeek);
        if (schedule is not { } s) return;

        if (now.Hour < s.OpenHour || now.Hour >= s.CloseHour) return; // closed right now — no
                                                                       // check-in expected, so
                                                                       // don't send one

        try
        {
            // Heartbeat-style monitor: just report Ok on a fixed cadence (no InProgress/duration
            // tracking needed, since this isn't a discrete job with a start/end — it's "the app
            // is alive right now"). configureMonitorOptions upserts the schedule/margin every
            // call, which is safe/idempotent and keeps the dashboard config in sync with this code.
            SentrySdk.CaptureCheckIn(s.MonitorSlug, CheckInStatus.Ok, configureMonitorOptions: options =>
            {
                options.Interval(s.Cron);
                options.TimeZone = TimeZoneId;
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

    /// <summary>
    /// Sentry Crons wants an IANA timezone (e.g. "America/Los_Angeles"), but Windows reports
    /// its own IDs (e.g. "Pacific Standard Time"). Converting whatever timezone this specific
    /// POS computer is set to means store hours are always evaluated correctly without hardcoding
    /// a timezone here — matters if any of the 4 machines is ever moved or reconfigured.
    /// </summary>
    private static string ResolveIanaTimeZoneId()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : "UTC";
    }
}
