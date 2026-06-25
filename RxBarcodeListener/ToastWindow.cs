namespace RxBarcodeListener;

/// <summary>
/// Custom toast notification window. Handles two alert types:
///
///   1. NimbleRx alert  — fires when a paid PO/DO order exists in NimbleRx.
///      Shown at the bottom-left corner (20px margin).
///
///   2. California Medicaid alert — fires when PioneerRx reports lastPayMethod
///      matches "California Medicaid". Shown directly above the NimbleRx slot
///      so both can be visible simultaneously without overlapping.
///
/// Shared design:
/// - Dark background (#1a1a2e), always on top, no taskbar entry, no caption bar
/// - Auto-dismisses after Config.ToastDurationMs milliseconds
/// - Click anywhere to dismiss
/// </summary>
public class ToastWindow : Form
{
    private readonly NimbleRxResult?   _result;
    private readonly PioneerRxResult?  _thirdPartyResult;
    private System.Windows.Forms.Timer _dismissTimer = null!;

    // Separate trackers so each alert type manages its own lifecycle independently.
    private static ToastWindow? _current;             // NimbleRx alert
    private static ToastWindow? _currentThirdParty;   // California Medicaid alert

    private const int NimbleToastHeight     = 265;
    private const int ThirdPartyToastHeight = 210;

    private ToastWindow(NimbleRxResult result)
    {
        _result = result;
        SetupWindow(NimbleToastHeight, isThirdParty: false);
        BuildNimbleLayout();
        StartDismissTimer();
    }

    private ToastWindow(PioneerRxResult result)
    {
        _thirdPartyResult = result;
        SetupWindow(ThirdPartyToastHeight, isThirdParty: true);
        BuildThirdPartyLayout();
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

    private void SetupWindow(int height, bool isThirdParty)
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost         = true;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual; // Required — without this WinForms ignores Left/Top
        BackColor       = Color.FromArgb(26, 26, 46); // #1a1a2e

        Width  = 480;
        Height = height;

        var screen = Screen.PrimaryScreen!.WorkingArea;
        Left = screen.Left + 20;

        // NimbleRx toast sits at the very bottom-left.
        // California Medicaid toast sits directly above that slot so they don't overlap.
        Top = isThirdParty
            ? screen.Bottom - NimbleToastHeight - height - 30
            : screen.Bottom - height - 20;

        Click += (_, _) => Close();
    }

    private void BuildNimbleLayout()
    {
        // Warning header
        AddLabel($"⚠️  DO NOT CHARGE — NimbleRx {_result!.TaskTypeLabel}",
            x: 20, y: 16, width: 440, height: 26, fontSize: 13, bold: true,
            color: Color.FromArgb(255, 80, 80));

        // Rx number
        AddLabel($"Rx #{_result.RxNumber}",
            x: 20, y: 46, width: 440, height: 20, fontSize: 11,
            color: Color.FromArgb(170, 170, 170));

        // Patient name — taller to handle long names that may wrap to 2 lines
        AddLabel(_result.PatientName,
            x: 20, y: 68, width: 440, height: 56, fontSize: 20, bold: true,
            color: Color.White);

        // Instruction
        AddLabel("Cancel this sale and check the NimbleRx dashboard.",
            x: 20, y: 126, width: 440, height: 22, fontSize: 12,
            color: Color.FromArgb(240, 165, 0));

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
            color: Color.FromArgb(80, 80, 80));
    }

    private void BuildThirdPartyLayout()
    {
        // Warning header
        AddLabel("⚠️  DO NOT CHARGE SERVICE FEE",
            x: 20, y: 16, width: 440, height: 26, fontSize: 13, bold: true,
            color: Color.FromArgb(255, 80, 80));

        // Pay method badge
        AddLabel($"Billed via: {_thirdPartyResult!.LastPayMethod}",
            x: 20, y: 46, width: 440, height: 20, fontSize: 11,
            color: Color.FromArgb(170, 170, 170));

        // Patient name
        AddLabel(_thirdPartyResult.PatientName,
            x: 20, y: 68, width: 440, height: 56, fontSize: 20, bold: true,
            color: Color.White);

        // Instruction
        AddLabel("No service fee or admin fee for this prescription.",
            x: 20, y: 126, width: 440, height: 22, fontSize: 12,
            color: Color.FromArgb(240, 165, 0));

        // Rx number
        AddLabel($"Rx #{_thirdPartyResult.RxNumber}",
            x: 20, y: 152, width: 440, height: 20, fontSize: 11,
            color: Color.FromArgb(170, 170, 170));

        // Dismiss hint
        AddLabel("Click anywhere to dismiss",
            x: 20, y: 182, width: 440, height: 18, fontSize: 9,
            color: Color.FromArgb(80, 80, 80));
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
        base.OnFormClosed(e);
    }
}
