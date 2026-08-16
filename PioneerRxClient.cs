using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Checks the PioneerRx enterprise API to see whether a prescription's last billing
/// method was "California Medicaid" (or any other value in AppSettings.CaliforniaMedicaidPayMethod).
///
/// Three-step chain per lookup:
///   1. RxIDSearch    — Rx number   → rxID
///   2. GetRx         — rxID        → personID (and keeps rxID for the profile filter)
///   3. GetPatientProfile — personID → full profile; filter by rxID, read lastPayMethod
///
/// Auth: every request requires three headers —
///   prx-api-key   : static key from AppSettings (loaded via .env)
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
            BaseAddress = new Uri(AppSettings.PioneerRxBaseUrl),
            Timeout     = TimeSpan.FromSeconds(30)
        };

        Http.DefaultRequestHeaders.Add("Accept",      "application/json");
        Http.DefaultRequestHeaders.Add("prx-api-key", AppSettings.PioneerRxApiKey);
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

    /// <summary>
    /// Full chain: Rx number → rxID → rxTransactionIDLatestComplete → acquisitionCost/totalPricePaid.
    /// Returns a <see cref="RxMarginResult"/> only when (totalPricePaid - acquisitionCost)
    /// exceeds <see cref="AppSettings.MarginFeeThreshold"/>.
    /// </summary>
    public static async Task<RxMarginResult?> CheckMarginAsync(string rxNumber)
    {
        // Step 1: Rx number → rxID
        var rxId = await GetRxIdAsync(rxNumber);
        if (rxId == null)
        {
            Logger.Log($"PioneerRx margin check — no rxID returned for Rx {rxNumber}");
            return null;
        }

        // Step 2: rxID → rxTransactionIDLatestComplete
        var rxTransactionId = await GetLatestCompleteTransactionIdAsync(rxId);
        if (rxTransactionId == null)
        {
            Logger.Log($"PioneerRx margin check — no rxTransactionIDLatestComplete for rxID {rxId} (Rx {rxNumber})");
            return null;
        }

        // Step 3: rxTransactionID → acquisitionCost / totalPricePaid
        return await GetRxTransactionMarginAsync(rxTransactionId, rxNumber);
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

    /// <summary>
    /// Margin check for a bag-derived rxTransactionID, where (unlike CheckMarginAsync)
    /// we don't already know the Rx number. Resolves a friendly Rx number for display/
    /// logging via a secondary GetRx(RxID) call; falls back to the raw rxTransactionID
    /// if that resolution fails for any reason.
    /// </summary>
    public static async Task<RxMarginResult?> CheckMarginByTransactionIdAsync(string rxTransactionId)
    {
        var body = BuildRequestBody("GetRxTransaction", ("RxTransactionID", rxTransactionId));
        var json = await PostAsync(body);
        if (json == null) return null;

        var transaction = JObject.Parse(json)["results"]?["rxTransaction"]?.FirstOrDefault();
        if (transaction == null)
        {
            Logger.Log($"PioneerRx GetRxTransaction — no rxTransaction entry for rxTransactionID {rxTransactionId}");
            return null;
        }

        var rxId             = transaction["rxID"]?.Value<string>();
        var acquisitionCost  = transaction["acquisitionCost"]?.Value<decimal>() ?? 0m;
        var totalPricePaid   = transaction["totalPricePaid"]?.Value<decimal>()  ?? 0m;

        var rxNumber = rxId != null ? await GetRxNumberAsync(rxId) : null;
        var label    = rxNumber ?? rxTransactionId;

        return EvaluateMargin(label, acquisitionCost, totalPricePaid);
    }

    private static async Task<string?> GetRxNumberAsync(string rxId)
    {
        var body = BuildRequestBody("GetRx", ("RxID", rxId));
        var json = await PostAsync(body);
        if (json == null) return null;

        return JObject.Parse(json)["results"]?["rx"]?
            .FirstOrDefault()?["rxNumber"]?.Value<string>();
    }

    private static async Task<string?> GetLatestCompleteTransactionIdAsync(string rxId)
    {
        var body = BuildRequestBody("GetRx", ("RxID", rxId));
        var json = await PostAsync(body);
        if (json == null) return null;

        var rxTransactionId = JObject.Parse(json)["results"]?["rx"]?
            .FirstOrDefault()?["rxTransactionIDLatestComplete"]?.Value<string>();

        Logger.Log($"PioneerRx GetRx — rxID {rxId} → rxTransactionIDLatestComplete {rxTransactionId ?? "(none)"}");
        return rxTransactionId;
    }

    private static async Task<RxMarginResult?> GetRxTransactionMarginAsync(string rxTransactionId, string rxNumber)
    {
        var body = BuildRequestBody("GetRxTransaction", ("RxTransactionID", rxTransactionId));
        var json = await PostAsync(body);
        if (json == null) return null;

        var transaction = JObject.Parse(json)["results"]?["rxTransaction"]?.FirstOrDefault();
        if (transaction == null)
        {
            Logger.Log($"PioneerRx GetRxTransaction — no rxTransaction entry for Rx {rxNumber}");
            return null;
        }

        var acquisitionCost = transaction["acquisitionCost"]?.Value<decimal>() ?? 0m;
        var totalPricePaid  = transaction["totalPricePaid"]?.Value<decimal>()  ?? 0m;

        return EvaluateMargin(rxNumber, acquisitionCost, totalPricePaid);
    }

    /// <summary>
    /// Shared margin decision logic for both the single-Rx and bag-derived lookup paths.
    ///
    /// margin = totalPricePaid - acquisitionCost can be positive (we collected more than
    /// the drug cost — overpaid, e.g. by a third party) or negative (we collected less
    /// than the drug cost — underpaid/a loss, e.g. an insurance reimbursement that didn't
    /// cover acquisition). Either direction can be a real dollar mismatch that needs a
    /// balancing line item in the sale, so we compare/inject on the ABSOLUTE VALUE of the
    /// margin, not the signed value — a $11.29 underpayment triggers the fee exactly like
    /// a $11.29 overpayment would.
    /// </summary>
    private static RxMarginResult? EvaluateMargin(string label, decimal acquisitionCost, decimal totalPricePaid)
    {
        var margin    = totalPricePaid - acquisitionCost;
        var absMargin = Math.Abs(margin);
        var direction = margin >= 0 ? "overpaid (collected more than acquisition cost)"
                                     : "underpaid (collected less than acquisition cost)";

        Logger.Log($"PioneerRx — {label}: acquisitionCost={acquisitionCost:0.00}, totalPricePaid={totalPricePaid:0.00}, " +
                   $"margin={margin:0.00} [{direction}], |margin|={absMargin:0.00}, threshold=${AppSettings.MarginFeeThreshold:0.00}");

        if (absMargin <= AppSettings.MarginFeeThreshold)
        {
            Logger.Log($"{label} — |margin| ${absMargin:0.00} at or below threshold ${AppSettings.MarginFeeThreshold:0.00} — no fee line needed");
            return null;
        }

        Logger.Log($"{label} — |margin| ${absMargin:0.00} exceeds threshold ${AppSettings.MarginFeeThreshold:0.00} — fee amount to inject: ${absMargin:0.00}");

        return new RxMarginResult
        {
            RxNumber        = label,
            AcquisitionCost = acquisitionCost,
            TotalPricePaid  = totalPricePaid,
            Margin          = margin,
            FeeAmount       = absMargin
        };
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

        if (!lastPayMethod.Contains(AppSettings.CaliforniaMedicaidPayMethod, StringComparison.OrdinalIgnoreCase))
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
            new { Name = "RequestedByEmployeeID", Value = AppSettings.PioneerRxEmployeeId }
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
        var salted = timestamp + AppSettings.PioneerRxSharedSecret;
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

public class RxMarginResult
{
    public string  RxNumber        { get; set; } = "";
    public decimal AcquisitionCost { get; set; }
    public decimal TotalPricePaid  { get; set; }

    /// <summary>Signed value: totalPricePaid - acquisitionCost. Positive = overpaid, negative = underpaid.</summary>
    public decimal Margin          { get; set; }

    /// <summary>Absolute value of Margin — this is the actual dollar amount injected as the fee line.</summary>
    public decimal FeeAmount       { get; set; }

    public bool IsOverpaid => Margin >= 0;
}
