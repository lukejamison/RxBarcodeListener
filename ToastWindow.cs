namespace RxBarcodeListener;

/// <summary>
/// Custom toast notification window. Handles three alert types:
///
///   1. NimbleRx alert  — fires when a paid PO/DO order exists in NimbleRx.
///      Shown at the bottom-left corner (20px margin).
///
///   2. California Medicaid alert — fires when PioneerRx reports lastPayMethod
///      matches "California Medicaid". Shown directly above the NimbleRx slot
///      so both can be visible simultaneously without overlapping.
///
///   3. SOC fee alert — fires when a fee line was auto-injected because the fill was
///      underpaid (acquisitionCost - totalPricePaid) by more than Config.MarginFeeThreshold.
///      Never fires for a profitable/break-even fill. Shown at the bottom-right corner
///      so it never overlaps the other two.
///
/// Shared design:
/// - Dark background (#1a1a2e), always on top, no taskbar entry, no caption bar
/// - Auto-dismisses after Config.ToastDurationMs milliseconds
/// - Click anywhere to dismiss
/// </summary>
public class ToastWindow : Form
{
    private enum ToastKind { Nimble, ThirdParty, MarginFee }

    private readonly NimbleRxResult?   _result;
    private readonly PioneerRxResult?  _thirdPartyResult;
    private readonly RxMarginResult?   _marginResult;
    private System.Windows.Forms.Timer _dismissTimer = null!;

    // Separate trackers so each alert type manages its own lifecycle independently.
    private static ToastWindow? _current;             // NimbleRx alert
    private static ToastWindow? _currentThirdParty;   // California Medicaid alert
    private static ToastWindow? _currentMarginFee;    // Margin fee alert

    private const int NimbleToastHeight     = 265;
    private const int ThirdPartyToastHeight = 210;
    private const int MarginFeeToastHeight  = 210;

    private ToastWindow(NimbleRxResult result)
    {
        _result = result;
        SetupWindow(NimbleToastHeight, ToastKind.Nimble);
        BuildNimbleLayout();
        StartDismissTimer();
    }

    private ToastWindow(PioneerRxResult result)
    {
        _thirdPartyResult = result;
        SetupWindow(ThirdPartyToastHeight, ToastKind.ThirdParty);
        BuildThirdPartyLayout();
        StartDismissTimer();
    }

    private ToastWindow(RxMarginResult result)
    {
        _marginResult = result;
        SetupWindow(MarginFeeToastHeight, ToastKind.MarginFee);
        BuildMarginFeeLayout();
        StartDismissTimer();
    }

    /// <summary>
    /// Show a NimbleRx toast, closing any previous NimbleRx toast still on screen.
    /// Must be called on the UI thread.
    /// </summary>
    public static void ShowToast(NimbleRxResult result)
    {
        _current?.Close();
        var toast = new ToastWindow(result);
        _current = toast;
        toast.Show();
    }

    /// <summary>
    /// Show a California Medicaid toast, closing any previous third-party toast still on screen.
    /// Positioned above the NimbleRx slot so both can show simultaneously.
    /// Must be called on the UI thread.
    /// </summary>
    public static void ShowThirdPartyToast(PioneerRxResult result)
    {
        _currentThirdParty?.Close();
        var toast = new ToastWindow(result);
        _currentThirdParty = toast;
        toast.Show();
    }

    /// <summary>
    /// Show a margin fee toast, closing any previous margin fee toast still on screen.
    /// Positioned bottom-right so it never overlaps the NimbleRx/Medicaid slots.
    /// Must be called on the UI thread.
    /// </summary>
    public static void ShowMarginFeeToast(RxMarginResult result)
    {
        _currentMarginFee?.Close();
        var toast = new ToastWindow(result);
        _currentMarginFee = toast;
        toast.Show();
    }

    private void SetupWindow(int height, ToastKind kind)
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost         = true;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual; // Required — without this WinForms ignores Left/Top
        AutoScaleMode   = AutoScaleMode.Dpi;
        BackColor       = UiTheme.Background;

        Width  = 480;
        Height = height;

        // Use the monitor the cursor is currently on rather than always the primary display —
        // on a multi-monitor POS setup the cashier's active screen (where the mouse/POS
        // software is) isn't necessarily Windows' "primary" monitor, and a toast on an
        // unwatched screen is as good as no toast at all.
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;

        if (kind == ToastKind.MarginFee)
        {
            // Bottom-right corner — never overlaps the bottom-left NimbleRx/Medicaid stack.
            Left = screen.Right - Width - 20;
            Top  = screen.Bottom - height - 20;
        }
        else
        {
            Left = screen.Left + 20;

            // NimbleRx toast sits at the very bottom-left.
            // California Medicaid toast sits directly above that slot so they don't overlap.
            Top = kind == ToastKind.ThirdParty
                ? screen.Bottom - NimbleToastHeight - height - 30
                : screen.Bottom - height - 20;
        }

        Click += (_, _) => Close();
    }

    private void BuildNimbleLayout()
    {
        // Warning header
        AddLabel($"⚠️  DO NOT CHARGE — NimbleRx {_result!.TaskTypeLabel}",
            x: 20, y: 16, width: 440, height: 26, fontSize: 13, bold: true,
            color: UiTheme.AccentDanger);

        // Rx number
        AddLabel($"Rx #{_result.RxNumber}",
            x: 20, y: 46, width: 440, height: 20, fontSize: 11,
            color: UiTheme.TextMuted);

        // Patient name — taller to handle long names that may wrap to 2 lines
        AddLabel(_result.PatientName,
            x: 20, y: 68, width: 440, height: 56, fontSize: 20, bold: true,
            color: Color.White);

        // Instruction
        AddLabel("Cancel this sale and check the NimbleRx dashboard.",
            x: 20, y: 126, width: 440, height: 22, fontSize: 12,
            color: UiTheme.AccentAmber);

        // Due label
        if (!string.IsNullOrEmpty(_result.DueLabel))
        {
            AddLabel($"🕐  {_result.DueLabel}",
                x: 20, y: 150, width: 440, height: 22, fontSize: 12, bold: true,
                color: _result.DueLabelColor);
        }

        // Open in NimbleRx button
        if (!string.IsNullOrEmpty(_result.TaskUrl))
        {
            var btn = new Button
            {
                Text      = "Open in NimbleRx →",
                Left      = 20,
                Top       = 182,
                Width     = 210,
                Height    = 34,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(0, 150, 110),
                Cursor    = Cursors.Hand,
                Font      = new Font("Segoe UI", 10, FontStyle.Regular)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += (_, _) =>
            {
                OpenInChromeApp(_result.TaskUrl);
                Close();
            };
            Controls.Add(btn);
        }

        // Dismiss hint
        AddLabel("Click anywhere to dismiss",
            x: 20, y: 238, width: 440, height: 18, fontSize: 9,
            color: UiTheme.TextDim);
    }

    private void BuildThirdPartyLayout()
    {
        // Warning header
        AddLabel("⚠️  DO NOT CHARGE SERVICE FEE",
            x: 20, y: 16, width: 440, height: 26, fontSize: 13, bold: true,
            color: UiTheme.AccentDanger);

        // Pay method badge
        AddLabel($"Billed via: {_thirdPartyResult!.LastPayMethod}",
            x: 20, y: 46, width: 440, height: 20, fontSize: 11,
            color: UiTheme.TextMuted);

        // Patient name
        AddLabel(_thirdPartyResult.PatientName,
            x: 20, y: 68, width: 440, height: 56, fontSize: 20, bold: true,
            color: Color.White);

        // Instruction
        AddLabel("No service fee or admin fee for this prescription.",
            x: 20, y: 126, width: 440, height: 22, fontSize: 12,
            color: UiTheme.AccentAmber);

        // Rx number
        AddLabel($"Rx #{_thirdPartyResult.RxNumber}",
            x: 20, y: 152, width: 440, height: 20, fontSize: 11,
            color: UiTheme.TextMuted);

        // Dismiss hint
        AddLabel("Click anywhere to dismiss",
            x: 20, y: 182, width: 440, height: 18, fontSize: 9,
            color: UiTheme.TextDim);
    }

    private void BuildMarginFeeLayout()
    {
        // A margin-fee toast only ever fires for an underpaid (shortfall) fill —
        // EvaluateMargin never returns a result for a profitable/break-even one.
        AddLabel("⚠️  SOC Fee Added",
            x: 20, y: 16, width: 440, height: 26, fontSize: 13, bold: true,
            color: Color.FromArgb(230, 90, 60));

        // Rx number
        AddLabel($"Rx #{_marginResult!.RxNumber}",
            x: 20, y: 46, width: 440, height: 20, fontSize: 11,
            color: UiTheme.TextMuted);

        // Fee amount — the big number (the shortfall dollar amount injected)
        AddLabel($"${_marginResult.FeeAmount:0.00}",
            x: 20, y: 68, width: 440, height: 56, fontSize: 28, bold: true,
            color: Color.White);

        // Breakdown — shows why this fired: acquisition cost exceeded what was paid
        AddLabel($"Acquisition ${_marginResult.AcquisitionCost:0.00} \u2212 Paid ${_marginResult.TotalPricePaid:0.00}",
            x: 20, y: 128, width: 440, height: 22, fontSize: 12,
            color: UiTheme.AccentAmber);

        // UPC that was injected
        AddLabel($"UPC {Config.MarginFeeUpc} scanned into sale",
            x: 20, y: 152, width: 440, height: 20, fontSize: 11,
            color: UiTheme.TextMuted);

        // Dismiss hint
        AddLabel("Click anywhere to dismiss",
            x: 20, y: 182, width: 440, height: 18, fontSize: 9,
            color: UiTheme.TextDim);
    }

    /// <summary>
    /// Opens the URL in the NimbleRx Chrome PWA by launching Chrome with --app.
    /// Falls back to the default browser if Chrome is not found.
    /// </summary>
    private static void OpenInChromeApp(string url)
    {
        var chromePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe"),
        };

        var chrome = chromePaths.FirstOrDefault(File.Exists);

        if (chrome != null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = chrome,
                Arguments       = $"--app={url}",
                UseShellExecute = false
            });
        }
        else
        {
            // Chrome not found — open in whatever the default browser is
            Logger.Log("Chrome not found — opening task URL in default browser");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true
            });
        }
    }

    private void AddLabel(string text, int x, int y, int width, int height,
        float fontSize, bool bold = false, Color color = default)
    {
        var label = new Label
        {
            Text      = text,
            Left      = x,
            Top       = y,
            Width     = width,
            Height    = height,
            AutoSize  = false,
            ForeColor = color == default ? Color.White : color,
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", fontSize,
                bold ? FontStyle.Bold : FontStyle.Regular)
        };
        label.Click += (_, _) => Close();
        Controls.Add(label);
    }

    private void StartDismissTimer()
    {
        _dismissTimer = new System.Windows.Forms.Timer { Interval = Config.ToastDurationMs };
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            Close();
        };
        _dismissTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _dismissTimer?.Stop();
        _dismissTimer?.Dispose();
        if (_current == this)             _current = null;
        if (_currentThirdParty == this)   _currentThirdParty = null;
        if (_currentMarginFee == this)    _currentMarginFee = null;
        base.OnFormClosed(e);
    }
}
