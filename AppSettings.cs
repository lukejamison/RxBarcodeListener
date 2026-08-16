namespace RxBarcodeListener;

/// <summary>
/// Application settings loaded from a <c>.env</c> file at startup (see <see cref="Load"/>).
/// All runtime code reads values here — never hardcode secrets in source files.
/// Copy <c>.env.example</c> to <c>.env</c> locally (gitignored).
/// </summary>
public static class AppSettings
{
    private static bool _loaded;

    public static string NimbleRxBearerToken { get; private set; } = "";
    public static string NimbleRxBaseUrl { get; private set; } = "";
    public static string NimbleRxTaskUrlTemplate { get; private set; } = "";

    public static string PioneerRxBaseUrl { get; private set; } = "";
    public static string PioneerRxApiKey { get; private set; } = "";
    public static string PioneerRxSharedSecret { get; private set; } = "";
    public static string PioneerRxEmployeeId { get; private set; } = "";
    public static string CaliforniaMedicaidPayMethod { get; private set; } = "";

    public static string MarginFeeUpc { get; private set; } = "";
    public static decimal MarginFeeThreshold { get; private set; }

    public static string[] PioneerRxScreens { get; private set; } = [];

    public static string BarcodePattern { get; private set; } = "";
    public static string BagBarcodePattern { get; private set; } = "";

    public static string WillCallBaseUrl { get; private set; } = "";
    public static string WillCallLocationId { get; private set; } = "";
    public static string WillCallSharedKey { get; private set; } = "";
    public static string WillCallApplicationId { get; private set; } = "";
    public static string WillCallServicePin { get; private set; } = "";

    public static int BufferTimeoutMs { get; private set; }
    public static string SentryDsn { get; private set; } = "";
    public static int ToastDurationMs { get; private set; }

    public static string LogFilePath { get; private set; } = "";

    public static string NetworkSharePath { get; private set; } = "";
    public static string GitHubRepoOwner { get; private set; } = "";
    public static string GitHubRepoName { get; private set; } = "";

    /// <summary>
    /// Must be called once at startup before any other code reads settings.
    /// Searches for <c>.env</c> next to the exe, in the install folder, then upward from cwd.
    /// </summary>
    public static void Load()
    {
        if (_loaded) return;

        var envPath = FindEnvFile();
        if (envPath == null)
            throw new InvalidOperationException(
                "No .env file found. Copy .env.example to .env next to the exe or in the project root.");

        var vars = ParseEnvFile(envPath);
        Apply(vars);
        _loaded = true;
    }

    private static void Apply(Dictionary<string, string> vars)
    {
        NimbleRxBearerToken       = Require(vars, "NimbleRxBearerToken");
        NimbleRxBaseUrl           = Require(vars, "NimbleRxBaseUrl");
        NimbleRxTaskUrlTemplate   = Require(vars, "NimbleRxTaskUrlTemplate");

        PioneerRxBaseUrl          = Require(vars, "PioneRxBaseUrl", "PioneerRxBaseUrl");
        PioneerRxApiKey           = Require(vars, "PioneRxApiKey", "PioneerRxApiKey");
        PioneerRxSharedSecret     = Require(vars, "PioneRxSharedSecret", "PioneerRxSharedSecret");
        PioneerRxEmployeeId       = Require(vars, "PioneRxEmployeeId", "PioneerRxEmployeeId");
        CaliforniaMedicaidPayMethod = Require(vars, "CaliforniaMedicaidPayMethod");

        MarginFeeUpc              = Require(vars, "MarginFeeUpc");
        MarginFeeThreshold        = ParseDecimal(Require(vars, "MarginFeeThreshold"), "MarginFeeThreshold");

        PioneerRxScreens          = Require(vars, "PioneRxScreens", "PioneerRxScreens")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        BarcodePattern            = Require(vars, "BarcodePattern");
        BagBarcodePattern         = Require(vars, "BagBarcodePattern");

        WillCallBaseUrl           = Require(vars, "WillCallBaseUrl");
        WillCallLocationId        = Require(vars, "WillCallLocationId");
        WillCallSharedKey         = Require(vars, "WillCallSharedKey");
        WillCallApplicationId     = Require(vars, "WillCallApplicationId");
        WillCallServicePin        = Require(vars, "WillCallServicePin");

        BufferTimeoutMs           = ParseInt(Require(vars, "BufferTimeoutMs"), "BufferTimeoutMs");
        SentryDsn                 = Require(vars, "SentryDsn");
        ToastDurationMs           = ParseInt(Require(vars, "ToastDurationMs"), "ToastDurationMs");

        var logPath = Optional(vars, "LogFilePath");
        LogFilePath = string.IsNullOrWhiteSpace(logPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RxBarcodeListener",
                "log.txt")
            : logPath;

        NetworkSharePath          = Optional(vars, "NetworkSharePath") ?? "";
        GitHubRepoOwner           = Require(vars, "GitHubRepoOwner");
        GitHubRepoName            = Require(vars, "GitHubRepoName");
    }

    private static string? FindEnvFile()
    {
        var overridePath = Environment.GetEnvironmentVariable("RXBARCODE_ENV_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var candidates = new List<string>();

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir))
            candidates.Add(Path.Combine(exeDir, ".env"));

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RxBarcodeListener",
            ".env"));

        var cwd = Directory.GetCurrentDirectory();
        candidates.Add(Path.Combine(cwd, ".env"));

        var dir = cwd;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            candidates.Add(Path.Combine(dir, ".env"));
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static Dictionary<string, string> ParseEnvFile(string path)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            vars[key] = value;
        }

        return vars;
    }

    private static string Require(Dictionary<string, string> vars, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (vars.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        throw new InvalidOperationException(
            $"Missing required .env key: {keys[0]}" +
            (keys.Length > 1 ? $" (also tried: {string.Join(", ", keys.Skip(1))})" : ""));
    }

    private static string? Optional(Dictionary<string, string> vars, string key) =>
        vars.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int ParseInt(string value, string key) =>
        int.TryParse(value, out var n)
            ? n
            : throw new InvalidOperationException($".env key '{key}' must be an integer, got '{value}'");

    private static decimal ParseDecimal(string value, string key) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new InvalidOperationException($".env key '{key}' must be a number, got '{value}'");
}
