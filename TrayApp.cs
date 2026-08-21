using System.Reflection;
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
    // Set once the constructor finishes, so other components (DiagnosticsWindow, Updater)
    // that need to trigger a clean shutdown — not just Application.Exit() — can reach it
    // without threading a reference through every call site.
    public static TrayApp? Current { get; private set; }

    // Loaded once and shared by the tray icon AND every window that wants the app icon
    // (e.g. DiagnosticsWindow), instead of decoding the embedded .ico repeatedly.
    // Lives for the process lifetime; disposed in Shutdown().
    public static Icon AppIcon { get; } = LoadAppIcon();

    // Reused instead of allocating a new bold Font every time an update is found.
    private static readonly Font UpdateMenuFont = new(SystemFonts.MenuFont!, FontStyle.Bold);

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

        // Logger.Initialize() already ran in Program.Main() before this constructor — don't
        // call it again here (it would just write a second "=== started ===" header).
        Logger.Log("RxBarcodeListener starting up");
        Logger.Log($"Update server: {Config.UpdateBaseUrl}");

        InitializeTrayIcon();
        InitializeHook();
        Heartbeat.Start();
        // Fire-and-forget: shells out to powershell.exe, no need to block tray startup on it.
        _ = Task.Run(Installer.EnsureScheduledTaskUpToDate);
        Current = this;

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
            Icon    = AppIcon,
            Text    = "RxBarcodeListener — Running",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };
        // Double-click is the conventional way to open a tray app's main window —
        // saves a right-click for anyone who doesn't know the menu is there.
        _trayIcon.DoubleClick += (_, _) => DiagnosticsWindow.ShowOrFocus();
    }

    private static string GetTitleWithVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version != null
            ? $"RxBarcodeListener v{version.Major}.{version.Minor}.{version.Build}"
            : "RxBarcodeListener";
    }

    private static Icon LoadAppIcon()
    {
        // Load the icon embedded in the assembly at build time.
        // Falls back to the default application icon if something goes wrong.
        try
        {
            using var stream = typeof(TrayApp).Assembly
                .GetManifestResourceStream("RxBarcodeListener.app.ico");
            if (stream != null)
                return new Icon(stream); // Icon(Stream) copies the data eagerly — safe to dispose the stream right after.
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

        menu.Items.Add(GetTitleWithVersion(), null, null).Enabled = false; // non-clickable title
        menu.Items.Add(new ToolStripSeparator());

        // Auto SOC Add — opt-in toggle for the margin-fee auto-injection feature.
        // Persisted across restarts (unlike Test Mode/Debug Logging below).
        var socItem = new ToolStripMenuItem("Auto SOC Add")
        {
            CheckOnClick = true,
            Checked = Settings.AutoSocAddEnabled
        };
        socItem.CheckedChanged += (s, e) =>
        {
            Settings.AutoSocAddEnabled = socItem.Checked;
            Logger.Log($"Auto SOC Add {(socItem.Checked ? "ENABLED" : "disabled")} (tray menu)");
        };
        menu.Items.Add(socItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Diagnostics...", null, (s, e) => DiagnosticsWindow.ShowOrFocus());
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
        menu.Items.Add("Exit", null, (s, e) => Shutdown());

        return menu;
    }

    private void InitializeHook()
    {
        _processor = new BarcodeProcessor(OnBarcodeDetected, OnBagDetected);
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
    /// Both API lookups are fired in parallel to minimise total wait time.
    /// All UI work must be marshalled via _uiInvoker.Invoke().
    /// </summary>
    private void OnBarcodeDetected(string rxNumber)
    {
        Logger.Log($"Barcode matched — Rx: {rxNumber}");

        // Captured now, before any awaits — used to restore focus if the cashier
        // clicks elsewhere while the margin-check lookups are in flight.
        var focusedWindow = InputInjector.CaptureForegroundWindow();

        // Start all lookups immediately so they run concurrently. The margin check is
        // skipped entirely (no API call at all) when Auto SOC Add is turned off in the
        // tray menu — NimbleRx/California Medicaid checks are unrelated and always run.
        var nimbleTask  = NimbleRxClient.LookupAsync(rxNumber);
        var pioneerTask = PioneerRxClient.LookupAsync(rxNumber);
        var socEnabled  = Settings.AutoSocAddEnabled;
        var marginTask  = socEnabled ? PioneerRxClient.CheckMarginAsync(rxNumber) : Task.FromResult<RxMarginResult?>(null);

        // --- NimbleRx result ---
        NimbleRxResult? nimbleResult = null;
        try
        {
            nimbleResult = nimbleTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError($"NimbleRx lookup failed for Rx {rxNumber}", ex);
        }

        if (nimbleResult != null)
        {
            Logger.Log($"Rx {rxNumber} — paid NimbleRx order found for {nimbleResult.PatientName}, due {nimbleResult.DueByDate}");
            InvokeSafely(() => ToastWindow.ShowToast(nimbleResult));
        }
        else
        {
            Logger.Log($"Rx {rxNumber} — no paid NimbleRx order found");
        }

        // --- PioneerRx / California Medicaid result ---
        PioneerRxResult? pioneerResult = null;
        try
        {
            pioneerResult = pioneerTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError($"PioneerRx lookup failed for Rx {rxNumber}", ex);
        }

        if (pioneerResult != null)
        {
            Logger.Log($"Rx {rxNumber} — California Medicaid billing detected for {pioneerResult.PatientName}");
            InvokeSafely(() => ToastWindow.ShowThirdPartyToast(pioneerResult));
        }
        else
        {
            Logger.Log($"Rx {rxNumber} — no California Medicaid billing detected");
        }

        // --- Margin fee check (Auto SOC Add) ---
        if (!socEnabled)
        {
            Logger.Log($"Rx {rxNumber} — Auto SOC Add is disabled (tray menu), skipped margin check");
            return;
        }

        RxMarginResult? marginResult = null;
        try
        {
            marginResult = marginTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError($"PioneerRx margin check failed for Rx {rxNumber}", ex);
        }

        if (marginResult != null)
        {
            Logger.Log($"Rx {rxNumber} — injecting SOC fee line: UPC {Config.MarginFeeUpc} + ${marginResult.FeeAmount:0.00} " +
                       $"(sale was underpaid by that amount)");

            InputInjector.InjectFeeLine(focusedWindow, Config.MarginFeeUpc, marginResult.FeeAmount);
            InvokeSafely(() => ToastWindow.ShowMarginFeeToast(marginResult));
        }
        // (PioneerRxClient.EvaluateMargin already logs the acquisitionCost/totalPricePaid/margin
        // breakdown and the threshold decision, so no separate "no fee" log is needed here.)
    }

    /// <summary>
    /// Called from a Task.Run() thread when a Will Call bag barcode is scanned instead of
    /// an individual Rx label. Resolves every rxTransactionID bagged together via the
    /// mobile Inventory API, then runs the same margin check/fee injection as a normal
    /// Rx scan for each one. NimbleRx/California Medicaid checks are not run per-bag-item
    /// since those need an Rx number, which GetRxTransaction alone doesn't provide.
    /// </summary>
    private void OnBagDetected(string bagCode)
    {
        Logger.Log($"Bag matched — {bagCode}");

        if (!Settings.AutoSocAddEnabled)
        {
            Logger.Log($"Bag {bagCode} — Auto SOC Add is disabled (tray menu), skipping margin checks");
            return;
        }

        var focusedWindow = InputInjector.CaptureForegroundWindow();

        List<string>? rxTransactionIds;
        try
        {
            rxTransactionIds = WillCallClient.GetRxTransactionIdsInBagAsync(bagCode).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Will Call lookup failed for bag {bagCode}", ex);
            return;
        }

        if (rxTransactionIds == null || rxTransactionIds.Count == 0)
        {
            Logger.Log($"Bag {bagCode} — no rxTransactionIDs returned, nothing to check");
            return;
        }

        Logger.Log($"Bag {bagCode} — checking margin for {rxTransactionIds.Count} rxTransactionID(s)");

        var injectionCount = 0;
        foreach (var rxTransactionId in rxTransactionIds)
        {
            Logger.Log($"Bag {bagCode} — margin check for rxTransactionID {rxTransactionId}");

            RxMarginResult? marginResult;
            try
            {
                marginResult = PioneerRxClient.CheckMarginByTransactionIdAsync(rxTransactionId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Margin check failed for rxTransactionID {rxTransactionId} (bag {bagCode})", ex);
                continue;
            }

            if (marginResult == null)
            {
                // PioneerRxClient.EvaluateMargin already logged the full breakdown/decision.
                continue;
            }

            Logger.Log($"Bag {bagCode} — {marginResult.RxNumber}: injecting SOC fee line: UPC {Config.MarginFeeUpc} + " +
                       $"${marginResult.FeeAmount:0.00} (sale was underpaid by that amount)");

            InputInjector.InjectFeeLine(focusedWindow, Config.MarginFeeUpc, marginResult.FeeAmount);
            InvokeSafely(() => ToastWindow.ShowMarginFeeToast(marginResult));
            injectionCount++;

            // Give PioneerRx POS time to finish the previous line before the next injection.
            if (injectionCount < rxTransactionIds.Count)
                Thread.Sleep(600);
        }

        Logger.Log($"Bag {bagCode} — finished: checked {rxTransactionIds.Count} rxTransactionID(s), injected {injectionCount} fee line(s)");
    }

    /// <summary>
    /// Marshals an action to the UI thread and swallows/logs any exception it throws.
    /// Control.Invoke re-throws exceptions on the CALLING thread by default — since every
    /// caller here is a background Task.Run thread, an unhandled toast-rendering bug would
    /// otherwise silently kill the entire process.
    /// </summary>
    private void InvokeSafely(Action action)
    {
        try
        {
            _uiInvoker.Invoke(action);
        }
        catch (Exception ex)
        {
            Logger.LogError("Unhandled exception while showing toast (app kept running)", ex);
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

        var update = await Updater.CheckForUpdateAsync().ConfigureAwait(false);
        if (update == null) return;

        InvokeSafely(() =>
        {
            var updateItem = new ToolStripMenuItem($"Install Update (v{update.VersionString})")
            {
                ForeColor = UiTheme.AccentGreen,
                Font      = UpdateMenuFont
            };
            updateItem.Click += (_, _) =>
            {
                _trayIcon.ShowBalloonTip(
                    timeout:  4000,
                    tipTitle: "Updating…",
                    tipText:  $"Downloading v{update.VersionString} — the app will restart automatically when it's ready.",
                    tipIcon:  ToolTipIcon.Info
                );
                Updater.InstallUpdate(update);
            };

            var menu = _trayIcon.ContextMenuStrip!;
            menu.Items.Insert(0, new ToolStripSeparator());
            menu.Items.Insert(0, updateItem);

            _trayIcon.ShowBalloonTip(
                timeout:  8000,
                tipTitle: "Update Available",
                tipText:  $"RxBarcodeListener v{update.VersionString} is ready. Right-click the tray icon to install.",
                tipIcon:  ToolTipIcon.Info
            );
        });
    }

    /// <summary>
    /// Tears down the hook, tray icon, and shared app icon, then exits the message loop.
    /// This is the ONLY correct way to end the process — calling Application.Exit()
    /// directly (as the update installer and manual-restart flow used to) skips hiding
    /// the tray icon, which leaves a stale/ghost icon in the notification area until the
    /// user hovers over it. Public so DiagnosticsWindow and Updater can route through it
    /// via <see cref="Current"/> instead of duplicating the cleanup or bypassing it.
    /// </summary>
    public void Shutdown()
    {
        Logger.Log("Application exiting");
        Heartbeat.Stop();
        _hook?.Uninstall();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        if (!ReferenceEquals(AppIcon, SystemIcons.Application))
            AppIcon.Dispose();
        _uiInvoker.Dispose();
        Application.Exit();
    }
}
