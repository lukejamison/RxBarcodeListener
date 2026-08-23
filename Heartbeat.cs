using System.Net.Http.Json;

namespace RxBarcodeListener;

/// <summary>
/// Sends a periodic "I'm alive" ping to the internal heartbeat API, tagged with this
/// machine's name, so a dashboard/alert on that backend can tell which of the pharmacy's
/// several POS computers has gone quiet. Business-hours logic (when a missed heartbeat
/// should actually page someone) now lives server-side, not in this client — the backend
/// owns that decision since it's the one thing common to all machines.
///
/// Superseded the earlier Sentry Crons-based heartbeat: Sentry Crons expects a fixed
/// schedule per monitor (awkward for "9-6 weekdays, different hours Sat/Sun" without extra
/// monitors), and its MCP tooling can't create the alert-notification rules programmatically.
/// A small owned endpoint is simpler to reason about and to change.
/// </summary>
public static class Heartbeat
{
    private const int IntervalMinutes = 5;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static System.Windows.Forms.Timer? _timer;

    static Heartbeat()
    {
        Http.DefaultRequestHeaders.Add("User-Agent", "RxBarcodeListener-Heartbeat");
    }

    public static void Start()
    {
        if (string.IsNullOrWhiteSpace(Config.HeartbeatUrl) ||
            string.IsNullOrWhiteSpace(Config.HeartBeatAPIKEY) ||
            Config.HeartBeatAPIKEY == "REPLACE_ME")
        {
            Logger.Log("Heartbeat: HeartbeatUrl/HeartBeatAPIKEY not configured — heartbeat monitoring is DISABLED");
            return;
        }

        Logger.Log($"Heartbeat: starting check-in every {IntervalMinutes} min " +
                   $"(machine: {Environment.MachineName}, endpoint: {Config.HeartbeatUrl})");

        _ = SendCheckInAsync(); // immediate check-in so the dashboard shows "up" right away
                                 // instead of waiting a full interval after every app start

        _timer = new System.Windows.Forms.Timer { Interval = IntervalMinutes * 60 * 1000 };
        _timer.Tick += async (_, _) => await SendCheckInAsync();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private static async Task SendCheckInAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Config.HeartbeatUrl);
            request.Headers.Add("X-Api-Key", Config.HeartBeatAPIKEY);
            request.Content = JsonContent.Create(new { machine_id = Environment.MachineName });

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError(
                    $"Heartbeat: server returned {(int)response.StatusCode} {response.StatusCode}",
                    new Exception(await response.Content.ReadAsStringAsync()));
            }
        }
        catch (Exception ex)
        {
            // Deliberately not fatal — a failed heartbeat send just means the backend finds
            // out "late" (via its own missed-checkin timeout) rather than immediately.
            Logger.LogError("Heartbeat: failed to send check-in", ex);
        }
    }
}
