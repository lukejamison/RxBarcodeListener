namespace RxBarcodeListener;

public static class Config
{
    // NimbleRx API
    public const string NimbleRxBearerToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJOaW1ibGVQaGFybWFjeSIsImlhdCI6MTc2MjcxODkzNCwiZXhwIjoxNzk0MjU0OTM0LCJsdCI6IlBIQVJNQUNZX09XTkVSIiwiZWEiOiIxNzk0MjU0OTM0IiwicCI6IjEwMjQiLCJsIjoiTC1zbDl3YUFPZCIsInRkIjoiOEJhTnlnNXdodEU4MEkyVE5tUGVrV2dsSitjYmwrV2I1OFl6UitrbklqND0iLCJwaWQiOiJQLUNVMm0xQ0ZBVVZtdTVUOWxYNCIsImFwIjoiIn0.niTRV3VJkeD9spw0KiJYgmwaDmhFkwTsrsDNjxr0w94";
    public const string NimbleRxBaseUrl = "https://api-prod.nimblerx.com";

    // NimbleRx deep link — URL to open a task in the Chrome app
    // Replace {taskId} with the actual task ID from the API response
    // Confirm the exact URL pattern from the NimbleRx Chrome app address bar
    public const string NimbleRxTaskUrlTemplate = "https://admin.nimblerx.com/admin/pharmacyDashboard/{taskId}";
    // https://admin.nimblerx.com/admin/pharmacyDashboard/Tas-xoZ1k6ZLBz5

    // PioneerRx window filter — fire only when these strings appear in the active window title
    public static readonly string[] PioneerRxScreens =
    [
        "Point of Sale",
        "Create Bag"
    ];

    // Barcode pattern
    // Matches: X or C + 7-digit Rx number + 2-digit refill = 10 chars total
    // Capture group 1 = the 7-digit Rx number
    public const string BarcodePattern = @"(?i)[XC](\d{7})\d{2}";

    // How long (ms) to wait between keystrokes before resetting the buffer.
    // Scanners typically send all characters in <50ms but some are slower.
    // 500ms gives plenty of headroom while still blocking deliberate human typing.
    public const int BufferTimeoutMs = 500;

    // Sentry DSN for error reporting — get from sentry.io project settings
    public const string SentryDsn = "https://467dd622db768fdd13ae0aad5775dd2f@o355169.ingest.us.sentry.io/4511191175331840";

    // Toast display duration in milliseconds
    public const int ToastDurationMs = 12000;

    // Rolling log file location
    public static readonly string LogFilePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RxBarcodeListener",
            "log.txt");

    // Network share used for auto-updates and installs.
    // Set to your server's shared folder, e.g. @"\\YOURSERVER\RxBarcodeListener"
    // Leave empty to disable auto-update checks.
    public const string NetworkSharePath = @"\\172.18.129.75\RxBarcodeListener";
}
