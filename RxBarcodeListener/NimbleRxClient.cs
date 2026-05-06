using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Wraps the NimbleRx task search API.
///
/// Endpoint: GET /tasks/search?limit=100&amp;prescriptionNumber={rxNumber}
/// Auth: Bearer token in Authorization header
///
/// Response shape:
///   { buckets: [ { taskSummaries: [ { id, type, status, dueTs, completedTs, ... } ] } ] }
///
/// We alert only when a task matches ALL of:
///   1. type is "PO" (pharmacy order) or "DO" (delivery order) — not "F" (fill)
///   2. completedTs is null — the task has not been completed yet
///
/// Returns null if no matching active delivery task exists for this Rx number.
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
        var url = $"/tasks/search?limit=100&prescriptionNumber={Uri.EscapeDataString(rxNumber)}";
        var response = await Http.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($"NimbleRx API returned {(int)response.StatusCode} for Rx {rxNumber}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        Logger.Log($"NimbleRx response for Rx {rxNumber}: {json}");

        var root = JObject.Parse(json);

        // Flatten taskSummaries from all buckets into a single list
        var allTasks = root["buckets"]
            ?.SelectMany(b => b["taskSummaries"] ?? Enumerable.Empty<JToken>())
            .ToList() ?? [];

        Logger.Log($"Rx {rxNumber} — {allTasks.Count} total task(s) across all buckets");

        // Find the first active delivery/pickup task:
        //   type "PO" = pharmacy pickup order, "DO" = delivery order
        //   completedTs null = not yet completed
        var activeTask = allTasks.FirstOrDefault(t =>
        {
            var type        = t["type"]?.Value<string>() ?? "";
            var completedTs = t["completedTs"]?.Value<string>();
            return (type == "PO" || type == "DO") && string.IsNullOrEmpty(completedTs);
        });

        if (activeTask == null)
        {
            Logger.Log($"Rx {rxNumber} — no active PO/DO task found");
            return null;
        }

        var firstName = activeTask["patientFirstName"]?.Value<string>() ?? "";
        var lastName  = activeTask["patientLastName"]?.Value<string>()  ?? "";
        var dueTs     = activeTask["dueTs"]?.Value<DateTime?>();
        var taskId    = activeTask["id"]?.Value<string>() ?? "";
        var taskType  = activeTask["type"]?.Value<string>() ?? "";

        Logger.Log($"Rx {rxNumber} — active {taskType} task {taskId} for {firstName} {lastName}, due {dueTs}");

        return new NimbleRxResult
        {
            RxNumber    = rxNumber,
            PatientName = $"{firstName} {lastName}".Trim(),
            DueByDate   = dueTs,
            TaskId      = taskId,
            TaskType    = taskType,
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
    public string    TaskType    { get; set; } = "";   // "PO" = pickup, "DO" = delivery
    public string    TaskUrl     { get; set; } = "";

    public string TaskTypeLabel => TaskType switch
    {
        "DO" => "Delivery Order",
        "PO" => "Pickup Order",
        _    => TaskType
    };

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
