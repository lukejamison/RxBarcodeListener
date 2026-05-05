using Microsoft.Win32;

namespace RxBarcodeListener;

/// <summary>
/// ApplicationContext subclass that manages the system tray icon and application lifecycle.
/// No main window — the app lives entirely in the system tray.
///
/// Thread marshalling note:
/// The barcode is detected on a Task.Run() thread pool thread. Toast windows must be
/// created on the UI (STA) thread. We keep a hidden Control (_uiInvoker) whose HWND
/// is created on the UI thread during construction, giving us a valid Invoke() target
/// for the lifetime of the application.
/// </summary>
public class TrayApp : ApplicationContext
{
    private NotifyIcon _trayIcon = null!;
    private KeyboardHook _hook = null!;
    private BarcodeProcessor _processor = null!;

    // Hidden control used solely to marshal calls back to the UI thread.
    // Must be created on the UI thread (it is — TrayApp() is called before Application.Run).
    private readonly Control _uiInvoker;

    public TrayApp()
    {
        // Create the invoke helper and force its Win32 handle to exist now,
        // while we are still on the UI thread.
        _uiInvoker = new Control();
        _ = _uiInvoker.Handle; // Accessing Handle forces HWND creation

        Logger.Initialize();
        Logger.Log("RxBarcodeListener starting up");

        InitializeTrayIcon();
        InitializeHook();

        // When the machine wakes from sleep, Windows invalidates low-level keyboard hooks.
        // We catch PowerModes.Resume and reinstall the hook automatically.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _trayIcon.ShowBalloonTip(
            timeout:  3000,
            tipTitle: "RxBarcodeListener",
            tipText:  "Running \u2014 listening for barcode scans.",
            tipIcon:  ToolTipIcon.Info
        );

        // Check for updates in the background — never blocks startup
        _ = CheckForUpdateAsync();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon    = LoadAppIcon(),
            Text    = "RxBarcodeListener — Running",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };
    }

    private static Icon LoadAppIcon()
    {
        // Load the icon embedded in the assembly at build time.
        // Falls back to the default application icon if something goes wrong.
        try
        {
            var stream = typeof(TrayApp).Assembly
                .GetManifestResourceStream("RxBarcodeListener.app.ico");
            if (stream != null)
                return new Icon(stream);
        }
        catch (Exception ex)
        {
            Logger.LogError("Could not load embedded app icon", ex);
        }
        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("RxBarcodeListener", null, null).Enabled = false; // non-clickable title
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reload Hook",    null, (s, e) => ReloadHook());
        menu.Items.Add("Open Log File",  null, (s, e) => Logger.OpenLogFile());
        menu.Items.Add(new ToolStripSeparator());

        // Test Mode — bypasses the PioneerRx window filter
        var testItem = new ToolStripMenuItem("Test Mode (bypass window filter)")
        {
            CheckOnClick = true,
            Checked = false
        };
        testItem.CheckedChanged += (s, e) =>
        {
            _processor.TestModeEnabled = testItem.Checked;
            Logger.Log($"Test mode {(testItem.Checked ? "ENABLED" : "disabled")}");
            _trayIcon.Text = testItem.Checked
                ? "RxBarcodeListener — TEST MODE"
                : "RxBarcodeListener — Running";
        };
        menu.Items.Add(testItem);

        // Debug Logging — logs every VK code received by the hook to the log file
        var debugItem = new ToolStripMenuItem("Debug Logging (log all keystrokes)")
        {
            CheckOnClick = true,
            Checked = false
        };
        debugItem.CheckedChanged += (s, e) =>
        {
            _hook.DebugLogging = debugItem.Checked;
            Logger.Log($"Debug logging {(debugItem.Checked ? "ENABLED" : "disabled")}");
        };
        menu.Items.Add(debugItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ExitApp());

        return menu;
    }

    private void InitializeHook()
    {
        _processor = new BarcodeProcessor(OnBarcodeDetected);
        _hook      = new KeyboardHook(_processor.ProcessKey);
        _hook.Install();
        Logger.Log("Keyboard hook installed");
    }

    private void ReloadHook()
    {
        Logger.Log("Manual hook reload triggered from tray menu");
        _hook?.Uninstall();
        InitializeHook();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Logger.Log("System resumed from sleep — reinitializing keyboard hook");
            ReloadHook();
        }
    }

    /// <summary>
    /// Called from a Task.Run() thread (not the UI thread).
    /// All UI work must be marshalled via _uiInvoker.Invoke().
    /// </summary>
    private void OnBarcodeDetected(string rxNumber)
    {
        Logger.Log($"Barcode matched — Rx: {rxNumber}");

        try
        {
            // Run the async lookup synchronously on the thread-pool thread
            var result = NimbleRxClient.LookupAsync(rxNumber).GetAwaiter().GetResult();

            if (result == null)
            {
                Logger.Log($"Rx {rxNumber} — no paid NimbleRx order found");
                return;
            }

            Logger.Log($"Rx {rxNumber} — paid order found for {result.PatientName}, due {result.DueByDate}");

            // Marshal to the UI thread for toast creation
            _uiInvoker.Invoke(() => ToastWindow.ShowToast(result));
        }
        catch (Exception ex)
        {
            Logger.LogError($"NimbleRx lookup failed for Rx {rxNumber}", ex);
        }
    }

    /// <summary>
    /// Checks the network share for a newer version. If found, adds an update
    /// item to the top of the tray menu and shows a balloon tip.
    /// Runs entirely in the background; marshals UI changes to the UI thread.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        // Short delay so the hook and UI are fully settled before hitting the share
        await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);

        var newVersion = await Updater.CheckForUpdateAsync().ConfigureAwait(false);
        if (newVersion == null) return;

        _uiInvoker.Invoke(() =>
        {
            var updateItem = new ToolStripMenuItem($"Install Update (v{newVersion})")
            {
                ForeColor = Color.FromArgb(0, 160, 120),
                Font      = new Font(SystemFonts.MenuFont!, FontStyle.Bold)
            };
            updateItem.Click += (_, _) => Updater.InstallUpdate();

            var menu = _trayIcon.ContextMenuStrip!;
            menu.Items.Insert(0, new ToolStripSeparator());
            menu.Items.Insert(0, updateItem);

            _trayIcon.ShowBalloonTip(
                timeout:  8000,
                tipTitle: "Update Available",
                tipText:  $"RxBarcodeListener v{newVersion} is ready. Right-click the tray icon to install.",
                tipIcon:  ToolTipIcon.Info
            );
        });
    }

    private void ExitApp()
    {
        Logger.Log("Application exiting");
        _hook?.Uninstall();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _trayIcon.Visible = false;
        _uiInvoker.Dispose();
        Application.Exit();
    }
}
