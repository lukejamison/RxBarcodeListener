using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Checks the PioneerRx enterprise API to see whether a prescription's last billing
/// method was "California Medicaid" (or any other value in Config.CaliforniaMedicaidPayMethod).
///
/// Three-step chain per lookup:
///   1. RxIDSearch    — Rx number   → rxID
///   2. GetRx         — rxID        → personID (and keeps rxID for the profile filter)
///   3. GetPatientProfile — personID → full profile; filter by rxID, read lastPayMethod
///
/// Auth: every request requires three headers —
///   prx-api-key   : static key from Config
///   prx-timestamp : yyyy-MM-ddTHH:mm:ss.ffffffZ  (UTC, microsecond precision)
///   prx-signature : Base64( SHA-512( UTF-16LE( timestamp + sharedSecret ) ) )
///
/// Returns null if the pay-method is NOT California Medicaid, or on any lookup failure.
/// Throws on unrecoverable network errors so the caller can capture them via Sentry.
/// </summary>
public static class PioneerRxClient
{
    // Single shared HttpClient with SSL validation bypassed for the internal self-signed cert.
    // Never create per-request instances (socket exhaustion).
    private static readonly HttpClient Http;

    static PioneerRxClient()
    {
        var handler = new HttpClientHandler
        {
            // The PioneerRx server runs on a private IP (172.18.129.10) with a self-signed
            // certificate — we must bypass validation or every request throws SslException.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        Http = new HttpClient(handler)
        {
            BaseAddress = new Uri(Config.PioneerRxBaseUrl),
            Timeout     = TimeSpan.FromSeconds(30)
        };

        Http.DefaultRequestHeaders.Add("Accept",      "application/json");
        Http.DefaultRequestHeaders.Add("prx-api-key", Config.PioneerRxApiKey);
    }

    /// <summary>
    /// Full chain: Rx number → rxID → personID → lastPayMethod check.
    /// Returns a <see cref="PioneerRxResult"/> only when lastPayMethod matches California Medicaid.
    /// </summary>
    public static async Task<PioneerRxResult?> LookupAsync(string rxNumber)
    {
        // Step 1: Rx number → rxID
        var rxId = await GetRxIdAsync(rxNumber);
        if (rxId == null)
        {
            Logger.Log($"PioneerRx — no rxID returned for Rx {rxNumber}");
            return null;
        }

        // Step 2: rxID → personID
        var personId = await GetPersonIdAsync(rxId);
        if (personId == null)
        {
            Logger.Log($"PioneerRx — no personID returned for rxID {rxId}");
            return null;
        }

        // Step 3: personID + rxID → lastPayMethod
        return await GetPatientProfileAsync(personId, rxId, rxNumber);
    }

    // -------------------------------------------------------------------------
    // Private chain steps
    // -------------------------------------------------------------------------

    private static async Task<string?> GetRxIdAsync(string rxNumber)
    {
        var body = BuildRequestBody("RxIDSearch", ("RxNumber", rxNumber));
        var json = await PostAsync(body);
        if (json == null) return null;

        var rxId = JObject.Parse(json)["results"]?["rxID"]?
            .FirstOrDefault()?["rxID"]?.Value<string>();

        Logger.Log($"PioneerRx RxIDSearch — Rx {rxNumber} → rxID {rxId ?? "(none)"}");
        return rxId;
    }

    private static async Task<string?> GetPersonIdAsync(string rxId)
    {
        var body = BuildRequestBody("GetRx", ("RxID", rxId));
        var json = await PostAsync(body);
        if (json == null) return null;

        var personId = JObject.Parse(json)["results"]?["rx"]?
            .FirstOrDefault()?["personID"]?.Value<string>();

        Logger.Log($"PioneerRx GetRx — rxID {rxId} → personID {personId ?? "(none)"}");
        return personId;
    }

    private static async Task<PioneerRxResult?> GetPatientProfileAsync(
        string personId, string rxId, string rxNumber)
    {
        var body = BuildRequestBody("GetPatientProfile", ("PersonID", personId));
        var json = await PostAsync(body);
        if (json == null) return null;

        var profiles = JObject.Parse(json)["results"]?["patientProfile"];
        if (profiles == null)
        {
            Logger.Log($"PioneerRx GetPatientProfile — no patientProfile array in response");
            return null;
        }

        // The profile lists all Rxs for this patient — find the one we scanned by rxID.
        var entry = profiles.FirstOrDefault(p =>
            string.Equals(p["rxID"]?.Value<string>(), rxId, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            Logger.Log($"PioneerRx GetPatientProfile — no entry matched rxID {rxId} for Rx {rxNumber}");
            return null;
        }

        var lastPayMethod = entry["lastPayMethod"]?.Value<string>() ?? "";
        var patientName   = entry["patientName"]?.Value<string>()   ?? "";

        Logger.Log($"PioneerRx — Rx {rxNumber}: lastPayMethod='{lastPayMethod}', patient='{patientName}'");

        if (!lastPayMethod.Contains(Config.CaliforniaMedicaidPayMethod, StringComparison.OrdinalIgnoreCase))
            return null;

        return new PioneerRxResult
        {
            RxNumber      = rxNumber,
            PatientName   = patientName,
            LastPayMethod = lastPayMethod
        };
    }

    // -------------------------------------------------------------------------
    // HTTP + auth helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// POST to /api/enterprise/method/process with per-request timestamp + signature headers.
    /// Returns the response body as a string, or null on non-success status codes.
    /// </summary>
    private static async Task<string?> PostAsync(string jsonBody)
    {
        var timestamp = FormatTimestamp();
        var signature = ComputeSignature(timestamp);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/enterprise/method/process");
        request.Headers.Add("prx-timestamp", timestamp);
        request.Headers.Add("prx-signature",  signature);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await Http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($"PioneerRx API returned {(int)response.StatusCode}");
            return null;
        }

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Builds the JSON body for a PioneerRx method call.
    /// RequestedByEmployeeID is always prepended automatically.
    /// </summary>
    private static string BuildRequestBody(string methodName, params (string Name, string Value)[] extraParams)
    {
        var paramList = new List<object>
        {
            new { Name = "RequestedByEmployeeID", Value = Config.PioneerRxEmployeeId }
        };

        foreach (var (name, value) in extraParams)
            paramList.Add(new { Name = name, Value = value });

        return JsonConvert.SerializeObject(new
        {
            MethodName          = methodName,
            Version             = 1.0,
            ParameterCollection = paramList
        });
    }

    /// <summary>
    /// Generates a UTC timestamp in PioneerRx format: yyyy-MM-ddTHH:mm:ss.ffffffZ
    /// The last 6 digits (ffffff) are microseconds; .NET DateTime has 100-ns ticks,
    /// so we expand milliseconds × 1000 to fill all 6 digits (matches the JS implementation).
    /// </summary>
    private static string FormatTimestamp()
    {
        var now    = DateTime.UtcNow;
        var micros = (now.Millisecond * 1000).ToString("D6");
        return $"{now:yyyy-MM-ddTHH:mm:ss}.{micros}Z";
    }

    /// <summary>
    /// signature = Base64( SHA-512( UTF-16LE( timestamp + sharedSecret ) ) )
    /// Matches the CryptoJS implementation in ShowIfThirdPartyIsTrue.md:
    ///   CryptoJS.enc.Utf16LE.parse(salted) → SHA512 → Base64
    /// Note: Encoding.Unicode in .NET is UTF-16 little-endian (no BOM via GetBytes).
    /// </summary>
    private static string ComputeSignature(string timestamp)
    {
        var salted = timestamp + Config.PioneerRxSharedSecret;
        var bytes  = Encoding.Unicode.GetBytes(salted); // UTF-16LE, no BOM
        var hash   = SHA512.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}

public class PioneerRxResult
{
    public string RxNumber      { get; set; } = "";
    public string PatientName   { get; set; } = "";
    public string LastPayMethod { get; set; } = "";
}
