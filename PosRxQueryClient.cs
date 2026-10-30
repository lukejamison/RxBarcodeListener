using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Asks PioneerRx POS RxQuery whether the patient for a scanned Rx number has other
/// prescriptions already ready (Will-Call / ready to sell).
///
/// lookupType 2 = Rx number only (the 7 digits the barcode listener already extracted).
/// returnType 2 = all ready prescriptions for that patient, not linked patients.
/// The scanned Rx is excluded from the count so the toast only means "more are waiting."
/// </summary>
public static class PosRxQueryClient
{
    private static readonly HttpClient Http;

    static PosRxQueryClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        Http = new HttpClient(handler)
        {
            BaseAddress = new Uri(Config.PioneerRxBaseUrl),
            Timeout     = TimeSpan.FromSeconds(15)
        };
    }

    /// <summary>
    /// Returns a result only when this patient has at least one other ready Rx.
    /// Returns null when nothing else is ready, or when the lookup fails.
    /// </summary>
    public static async Task<ReadyRxAlert?> GetOtherReadyAsync(string rxNumber)
    {
        var body = JsonConvert.SerializeObject(new
        {
            lookupType  = 2,
            lookupValue = rxNumber,
            returnType  = 2,
            version     = 1
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/POS/RxQuery");
        request.Headers.TryAddWithoutValidation("Vendor-Key", Config.PosVendorKey);
        request.Headers.TryAddWithoutValidation("Data-Exchange-ID", Config.PosDataExchangeId);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($"RxQuery returned {(int)response.StatusCode} for Rx {rxNumber}: {json}");
            return null;
        }

        return ParseOtherReady(json, rxNumber);
    }

    private static ReadyRxAlert? ParseOtherReady(string json, string scannedRxNumber)
    {
        var root = JObject.Parse(json);
        var patients = Child(root, "patients") as JArray;
        if (patients == null || patients.Count == 0)
        {
            Logger.Log($"RxQuery — Rx {scannedRxNumber}: no patients in response");
            return null;
        }

        var searchedId = Child(root, "patientIDSearched")?.Value<string>();
        var patient = patients.FirstOrDefault(p =>
                !string.IsNullOrEmpty(searchedId) &&
                string.Equals(Child(p, "patientID")?.Value<string>(), searchedId, StringComparison.OrdinalIgnoreCase))
            ?? patients.First();

        var patientName = FormatName(patient);
        var otherReady = new HashSet<int>();

        var rxs = Child(patient, "rxs") as JArray;
        if (rxs != null)
        {
            foreach (var rx in rxs)
            {
                if (!IsReady(rx)) continue;

                var number = Child(rx, "rxNumber")?.Value<int?>();
                if (number == null) continue;
                if (IsScannedRx(number.Value, scannedRxNumber)) continue;

                otherReady.Add(number.Value);
            }
        }

        Logger.Log($"RxQuery — Rx {scannedRxNumber}: patient '{patientName}', {otherReady.Count} other ready Rx(s)");

        if (otherReady.Count == 0)
            return null;

        return new ReadyRxAlert
        {
            PatientName     = patientName,
            OtherReadyCount = otherReady.Count
        };
    }

    private static bool IsReady(JToken rx)
    {
        var ready = Child(rx, "readyToBeSold")?.Value<int?>();
        return ready == null || ready == 1;
    }

    private static bool IsScannedRx(int rxNumber, string scannedRxNumber) =>
        int.TryParse(scannedRxNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scanned)
        && scanned == rxNumber;

    private static string FormatName(JToken patient)
    {
        var first = Child(patient, "firstName")?.Value<string>()?.Trim() ?? "";
        var last  = Child(patient, "lastName")?.Value<string>()?.Trim() ?? "";
        var name  = $"{first} {last}".Trim();
        return string.IsNullOrEmpty(name) ? "This patient" : name;
    }

    private static JToken? Child(JToken token, string name)
    {
        if (token is not JObject obj) return null;
        return obj.Properties()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }
}

public class ReadyRxAlert
{
    public string PatientName     { get; set; } = "";
    public int    OtherReadyCount { get; set; }
}
