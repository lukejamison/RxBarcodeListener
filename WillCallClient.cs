using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Resolves a Will Call bag barcode into rxTransactionIDs via the Inventory mobile API.
/// Uses a static SecurityToken from .env (same shape as the Inventory app curl).
/// </summary>
public static class WillCallClient
{
    private static readonly HttpClient Http;
    private static readonly Lazy<JObject> SecurityToken = new(BuildSecurityToken);

    static WillCallClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        Http = new HttpClient(handler)
        {
            BaseAddress = new Uri(Config.WillCallBaseUrl),
            Timeout     = TimeSpan.FromSeconds(15)
        };
    }

    public static Task<List<string>?> GetRxTransactionIdsInBagAsync(string bagBarcode) =>
        AddItemAsync(bagBarcode);

    private static async Task<List<string>?> AddItemAsync(string itemOrBinValue)
    {
        var token = SecurityToken.Value;

        var body = new JObject
        {
            ["SecurityToken"]          = token,
            ["ItemOrBinValue"]         = itemOrBinValue,
            ["OtherItemsIDs"]          = JValue.CreateNull(),
            ["ForceBin"]               = false,
            ["RefreshLinkedItemsOnly"] = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/inventory/will-call/add-item");
        request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        AddCommonHeaders(request);

        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($"WillCall add-item returned {(int)response.StatusCode} for '{itemOrBinValue}': {json}");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                Logger.Log("WillCall token rejected — update WillCallEncryptedToken in .env / .Environment");
            return null;
        }

        var ids = ParseRxTransactionIdsInBag(json);

        if (ids.Count == 0)
            Logger.Log($"WillCall add-item — '{itemOrBinValue}' returned no rxTransactionIDsInBag. Raw response: {json}");
        else
            Logger.Log($"WillCall add-item — '{itemOrBinValue}' → {ids.Count} rxTransactionID(s): {string.Join(", ", ids)}");

        return ids;
    }

    private static List<string> ParseRxTransactionIdsInBag(string json)
    {
        var root = JObject.Parse(json);
        var bagIdsToken =
            root.SelectToken("scannedItem.rxTransactionIDsInBag")
         ?? root.SelectToken("scannedItem.RxTransactionIDsInBag")
         ?? root.SelectToken("rxTransactionIDsInBag")
         ?? root.SelectToken("RxTransactionIDsInBag");

        if (bagIdsToken == null)
            return [];

        IEnumerable<JToken> entries = bagIdsToken.Type switch
        {
            JTokenType.Array  => bagIdsToken.Children(),
            JTokenType.String => [bagIdsToken],
            _                 => []
        };

        return entries
            .Select(t => t.Type == JTokenType.String ? t.Value<string>() : t.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JObject BuildSecurityToken() => new()
    {
        ["PersonID"]       = Config.WillCallPersonId,
        ["EncryptedToken"] = Config.WillCallEncryptedToken,
        ["LocationID"]     = Config.WillCallLocationId,
        ["MobileDeviceApplication"] = new JObject
        {
            ["MobileDevice"]    = new JObject(),
            ["ApplicationID"] = Config.WillCallApplicationId
        }
    };

    private static void AddCommonHeaders(HttpRequestMessage request)
    {
        TryAddHeader(request, "portal-authLocationID", Config.WillCallAuthLocationId);
        TryAddHeader(request, "portal-applicationID", Config.WillCallApplicationId);
        TryAddHeader(request, "portal-mobileDeviceID", Config.WillCallMobileDeviceId);
        TryAddHeader(request, "portal-deviceUniqueIdentifier", Config.WillCallMobileDeviceId);
        TryAddHeader(request, "portal-personID", Config.WillCallPersonId);
        TryAddHeader(request, "portal-securityToken", Config.WillCallEncryptedToken);
        TryAddHeader(request, "portal-platformType", "0");
        TryAddHeader(request, "portal-applicationVersion", Config.WillCallApplicationVersion);
        TryAddHeader(request, "portal-screenScale", "1");
        TryAddHeader(request, "portal-deviceFamily", "0");
        TryAddHeader(request, "portal-deviceName", Config.WillCallDeviceName);
        TryAddHeader(request, "portal-deviceModel", Config.WillCallDeviceModel);
        TryAddHeader(request, "portal-deviceSystemName", Config.WillCallDeviceSystemName);
        TryAddHeader(request, "portal-deviceSystemVersion", Config.WillCallDeviceSystemVersion);
        TryAddHeader(request, "Accept", "application/json");
    }

    private static void TryAddHeader(HttpRequestMessage request, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || request.Headers.Contains(name)) return;
        request.Headers.TryAddWithoutValidation(name, value);
    }
}
