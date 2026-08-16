using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Wraps PioneerRx's mobile Inventory app API ("Will Call" endpoints), used only to
/// resolve a scanned Will Call bag barcode into the rxTransactionIDs it contains.
///
/// This is a different auth scheme than <see cref="PioneerRxClient"/>'s Enterprise API:
///   1. POST /inventory/validate-pin — logs in with a dedicated service PIN, returns a
///      SecurityToken (cached and reused for every subsequent call).
///   2. POST /inventory/will-call/add-item — scans a bag/Rx barcode using that
///      SecurityToken; response includes rxTransactionIDsInBag.
///
/// NOTE: The exact field name/casing of the SecurityToken in the validate-pin response
/// has not been confirmed against a real captured response yet (only the add-item
/// request/response shapes were confirmed via Postman). ValidatePinAsync logs the full
/// raw response and tries the most likely field name variants — if login keeps failing,
/// check the log for the raw JSON and adjust the field lookups below.
/// </summary>
public static class WillCallClient
{
    private static readonly HttpClient Http;
    private static readonly object _tokenLock = new();
    private static JObject? _securityToken; // Cached after first successful login

    // Fixed identifier for our "virtual device" — generated once, not tied to a real phone.
    private const string MobileDeviceId = "8f5a4b3e-2a1f-4b7a-9e60-8b6d6f9c2a3d";

    static WillCallClient()
    {
        var handler = new HttpClientHandler
        {
            // Same self-signed cert as the Enterprise API server.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        Http = new HttpClient(handler)
        {
            BaseAddress = new Uri(AppSettings.WillCallBaseUrl),
            Timeout     = TimeSpan.FromSeconds(15)
        };
    }

    /// <summary>
    /// Scans the given Will Call bag barcode and returns the rxTransactionIDs bagged
    /// together under it, or null if the lookup failed.
    /// </summary>
    public static Task<List<string>?> GetRxTransactionIdsInBagAsync(string bagBarcode) =>
        AddItemAsync(bagBarcode, retryAfterReauth: true);

    private static async Task<List<string>?> AddItemAsync(string itemOrBinValue, bool retryAfterReauth)
    {
        JObject? token;
        lock (_tokenLock) token = _securityToken;

        if (token == null)
        {
            token = await ValidatePinAsync();
            if (token == null) return null;
        }

        var body = new JObject
        {
            ["SecurityToken"]          = token,
            ["ItemOrBinValue"]         = itemOrBinValue,
            ["OtherItemsIDs"]          = JValue.CreateNull(),
            ["ForceBin"]               = false,
            ["RefreshLinkedItemsOnly"] = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/inventory/will-call/add-item");
        request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        AddCommonHeaders(request, token);

        var response = await Http.SendAsync(request);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && retryAfterReauth)
        {
            Logger.Log("WillCall add-item returned 401 — re-authenticating and retrying once");
            lock (_tokenLock) _securityToken = null;
            return await AddItemAsync(itemOrBinValue, retryAfterReauth: false);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($"WillCall add-item returned {(int)response.StatusCode} for '{itemOrBinValue}'");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var ids = JObject.Parse(json)["scannedItem"]?["rxTransactionIDsInBag"]?
            .Select(t => t.Value<string>()!)
            .ToList();

        Logger.Log($"WillCall add-item — '{itemOrBinValue}' → {ids?.Count ?? 0} rxTransactionID(s) in bag");
        return ids;
    }

    private static async Task<JObject?> ValidatePinAsync()
    {
        var body = new
        {
            LocationID = AppSettings.WillCallLocationId,
            Pin        = AppSettings.WillCallServicePin,
            sharedKey  = AppSettings.WillCallSharedKey,
            MobileDeviceApplication = new
            {
                MobileDevice        = (object?)null,
                ApplicationID       = AppSettings.WillCallApplicationId,
                ApplicationVersion  = (string?)null
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/inventory/validate-pin");
        request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
        AddCommonHeaders(request, securityToken: null);

        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($"WillCall validate-pin returned {(int)response.StatusCode}: {json}");
            return null;
        }

        Logger.Log($"WillCall validate-pin raw response: {json}");

        var root = JObject.Parse(json);
        var token = root["SecurityToken"] as JObject
                 ?? root["securityToken"] as JObject
                 ?? root; // Some PioneerRx mobile endpoints return the token fields at the root

        var hasEncryptedToken = token["EncryptedToken"] != null || token["encryptedToken"] != null;
        if (!hasEncryptedToken)
        {
            Logger.LogError(
                "WillCall validate-pin — could not find EncryptedToken in response; field mapping " +
                "needs to be corrected once a real response is confirmed (see raw response in log above)",
                new InvalidOperationException(json));
            return null;
        }

        lock (_tokenLock) _securityToken = token;
        return token;
    }

    private static void AddCommonHeaders(HttpRequestMessage request, JObject? securityToken)
    {
        request.Headers.Add("portal-authLocationID", AppSettings.WillCallLocationId);
        request.Headers.Add("portal-applicationID", AppSettings.WillCallApplicationId);
        request.Headers.Add("portal-mobileDeviceID", MobileDeviceId);
        request.Headers.Add("portal-deviceUniqueIdentifier", MobileDeviceId);
        request.Headers.Add("portal-platformType", "0");
        request.Headers.Add("Accept", "application/json");

        var personId = securityToken?["PersonID"]?.Value<string>()
                    ?? securityToken?["personID"]?.Value<string>();
        var encryptedToken = securityToken?["EncryptedToken"]?.Value<string>()
                          ?? securityToken?["encryptedToken"]?.Value<string>();

        if (personId != null)
            request.Headers.Add("portal-personID", personId);
        if (encryptedToken != null)
            request.Headers.Add("portal-securityToken", encryptedToken);
    }
}
