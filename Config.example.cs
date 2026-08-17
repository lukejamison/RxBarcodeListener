namespace RxBarcodeListener;

/// <summary>
/// Copy this file to <c>Config.cs</c> (gitignored) and fill in real values.
/// Secrets are compiled into the exe — never commit Config.cs to GitHub.
/// Rebuild and run Publish-RxBarcodeListener.ps1 to deploy to the Linux update server.
/// </summary>
public static class Config
{
    // NimbleRx API
    public const string NimbleRxBearerToken = "REPLACE_ME";
    public const string NimbleRxBaseUrl = "https://api-prod.nimblerx.com";
    public const string NimbleRxTaskUrlTemplate =
        "https://admin.nimblerx.com/admin/pharmacyDashboard/{taskId}";

    // PioneerRx Enterprise API
    public const string PioneerRxBaseUrl = "https://172.18.129.10";
    public const string PioneerRxApiKey = "REPLACE_ME";
    public const string PioneerRxSharedSecret = "REPLACE_ME";
    public const string PioneerRxEmployeeId = "REPLACE_ME";
    public const string CaliforniaMedicaidPayMethod = "California Medicaid";

    public const string MarginFeeUpc = "490000002324";
    public const decimal MarginFeeThreshold = 3.00m;

    public static readonly string[] PioneerRxScreens =
    [
        "Point of Sale",
        "Create Bag"
    ];

    public const string BarcodePattern = @"(?i)[XC](\d{7})\d{2}";
    public const string BagBarcodePattern = @"(?i)B[0-9A-Z]{8}";

    // Will Call / Inventory mobile API (bag lookup)
    public const string WillCallBaseUrl = "https://172.18.129.10:42856";
    public const string WillCallLocationId = "85aad4a6-2277-413f-a8d8-078493614999";
    public const string WillCallAuthLocationId = "ccf12dfe-9312-4d7a-bca0-10987d49eaba";
    public const string WillCallApplicationId = "02239bfd-deae-44de-89bc-581f26d7d6ac";
    public const string WillCallPersonId = "REPLACE_ME";
    public const string WillCallEncryptedToken = "REPLACE_ME";
    public const string WillCallMobileDeviceId = "05bfbca2-256d-47ed-8ff6-4883a7949a42";
    public const string WillCallApplicationVersion = "6.1.3";
    public const string WillCallDeviceName = "iPhone";
    public const string WillCallDeviceModel = "iPhone17,2";
    public const string WillCallDeviceSystemName = "iPhone17,2";
    public const string WillCallDeviceSystemVersion = "26.4.2";

    public const int BufferTimeoutMs = 500;
    public const string SentryDsn = "REPLACE_ME";
    public const int ToastDurationMs = 12000;

    public static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RxBarcodeListener",
        "log.txt");

    // Auto-update — exe hosted on internal Linux server (not GitHub Releases)
    // HTTP: nginx serves /dev/bpapps/RxBarcodeListener/ on the LAN
    public const string UpdateBaseUrl = "http://172.18.129.154/bpapps/RxBarcodeListener";
    // Optional SMB fallback if you expose the folder as a Windows share:
    public const string UpdateSharePath = "";
}
