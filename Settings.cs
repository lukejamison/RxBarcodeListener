using Newtonsoft.Json;

namespace RxBarcodeListener;

/// <summary>
/// Small persisted user-toggleable settings, saved as JSON to
/// %LOCALAPPDATA%\RxBarcodeListener\settings.json so choices survive app restarts
/// and updates (unlike the tray menu's Test Mode/Debug Logging toggles, which are
/// meant to reset every launch).
/// </summary>
public static class Settings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RxBarcodeListener",
        "settings.json");

    private static readonly object Lock = new();

    private class SettingsData
    {
        // Opt-in: the auto margin-fee-line injection ("Auto SOC Add") starts OFF until a
        // user explicitly enables it from the tray menu.
        public bool AutoSocAddEnabled { get; set; } = false;
    }

    private static SettingsData _data = Load();

    /// <summary>
    /// When true, scanning an Rx (or Will Call bag) that triggers the margin-fee check will
    /// auto-inject the SOC adjustment line into PioneerRx's POS. When false, margin checks
    /// are skipped entirely — no API calls, no injection, no toast.
    /// </summary>
    public static bool AutoSocAddEnabled
    {
        get { lock (Lock) return _data.AutoSocAddEnabled; }
        set
        {
            lock (Lock) _data.AutoSocAddEnabled = value;
            Save();
        }
    }

    private static SettingsData Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(SettingsPath));
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Settings: failed to load settings.json — using defaults", ex);
        }

        return new SettingsData();
    }

    private static void Save()
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Settings: failed to save settings.json", ex);
        }
    }
}
