using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Wraps the NimbleRx task search API.
///
/// Endpoint: GET /v2/tasks/search?statuses=Q&amp;query={rxNumber}
/// Auth: Bearer token in Authorization header
///
/// Returns null if no paid delivery order exists for this Rx number.
/// Throws on network/HTTP errors so the caller can capture them via Sentry.
/// </summary>
public static class NimbleRxClient
{
    // Single shared HttpClient — never create per-request instances (socket exhaustion)
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Config.NimbleRxBaseUrl),
        Timeout = TimeSpan.FromSeconds(10)
    };

    static NimbleRxClient()
    {
        Http.DefaultRequestHeaders.Add("Authorization", $"Bearer {Config.NimbleRxBearerToken}");
        Http.DefaultRequestHeaders.Add("Accept", "application/json");
        Http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        Http.DefaultRequestHeaders.Add("Origin", "https://admin.nimblerx.com");
    }

    public static async Task<NimbleRxResult?> LookupAsync(string rxNumber)
    {
        var url = $"/v2/tasks/search?statuses=Q&query={Uri.EscapeDataString(rxNumber)}";
        var response = await Http.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($"NimbleRx API returned {(int)response.StatusCode} for Rx {rxNumber}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var array = JArray.Parse(json);

        if (!array.Any()) return null;

        // Find the first task where isPaid = true
        // Must use Value<bool?>() — Value<bool>() throws if the field is JSON null
        var paidTask = array.FirstOrDefault(t => t["isPaid"]?.Value<bool?>() == true);
        if (paidTask == null) return null;

        var firstName = paidTask["patientName"]?["firstName"]?.Value<string>() ?? "";
        var lastName  = paidTask["patientName"]?["lastName"]?.Value<string>()  ?? "";
        var dueByDate = paidTask["dueByDate"]?.Value<DateTime?>() ?? null;
        var taskId    = paidTask["id"]?.Value<string>() ?? "";

        return new NimbleRxResult
        {
            RxNumber    = rxNumber,
            PatientName = $"{firstName} {lastName}".Trim(),
            DueByDate   = dueByDate,
            TaskId      = taskId,
            TaskUrl     = Config.NimbleRxTaskUrlTemplate.Replace("{taskId}", taskId)
        };
    }
}

public class NimbleRxResult
{
    public string    RxNumber    { get; set; } = "";
    public string    PatientName { get; set; } = "";
    public DateTime? DueByDate   { get; set; }
    public string    TaskId      { get; set; } = "";
    public string    TaskUrl     { get; set; } = "";

    /// <summary>
    /// Human-readable due time relative to now.
    /// Examples: "Due in 2 hours", "Due in 3 days", "Overdue"
    /// </summary>
    public string DueLabel
    {
        get
        {
            if (DueByDate == null) return "";
            var diff = DueByDate.Value.ToUniversalTime() - DateTime.UtcNow;

            if (diff.TotalMinutes <= 0) return "Overdue";
            if (diff.TotalMinutes < 60)
            {
                var mins = (int)diff.TotalMinutes;
                return $"Due in {mins} {(mins == 1 ? "minute" : "minutes")}";
            }
            if (diff.TotalHours < 24)
            {
                var hrs = (int)diff.TotalHours;
                return $"Due in {hrs} {(hrs == 1 ? "hour" : "hours")}";
            }
            var days = (int)diff.TotalDays;
            return $"Due in {days} {(days == 1 ? "day" : "days")}";
        }
    }

    /// <summary>
    /// Red if overdue, orange if due today, green otherwise.
    /// </summary>
    public Color DueLabelColor
    {
        get
        {
            if (DueByDate == null) return Color.Gray;
            var diff = DueByDate.Value.ToUniversalTime() - DateTime.UtcNow;
            if (diff.TotalMinutes <= 0) return Color.FromArgb(255, 68, 68);   // Red
            if (diff.TotalHours   < 24) return Color.FromArgb(240, 165, 0);   // Orange
            return Color.FromArgb(0, 200, 150);                                 // Green
        }
    }
}
