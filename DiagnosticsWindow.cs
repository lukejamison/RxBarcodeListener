using System.Reflection;

namespace RxBarcodeListener;

/// <summary>
/// Simple diagnostics window for pharmacy staff / whoever's on shift — no dev tools or
/// SSH access needed to see what the app is doing or get unstuck.
///
/// Lets a user, without any technical knowledge:
///   - See the tail of the log file without opening Notepad
///   - Check for an update on demand (instead of waiting for the automatic check)
///   - Restart the app if something looks stuck
///   - Send a diagnostic report (log file attached) to the developer via Sentry, even
///     when nothing has actually crashed — "something looks off" is reportable too
///
/// Opened from the tray menu ("Diagnostics...") or by double-clicking the tray icon.
/// Singleton — reopening just focuses the existing window instead of stacking copies.
/// </summary>
public class DiagnosticsWindow : Form
{
    private static DiagnosticsWindow? _current;

    private TextBox _logBox = null!;
    private Label _statusLabel = null!;
    private Button _sendReportButton = null!;
    private ToolTip _toolTip = null!;

    // Local aliases keep the rest of this file's existing call sites unchanged
    // while sourcing the actual values from the single shared palette.
    private static readonly Color BackColorDark = UiTheme.Background;
    private static readonly Color PanelColor    = UiTheme.Panel;
    private static readonly Color AccentGreen   = UiTheme.AccentGreen;
    private static readonly Color AccentRed     = UiTheme.AccentRed;

    public static void ShowOrFocus()
    {
        if (_current is { IsDisposed: false })
        {
            _current.WindowState = FormWindowState.Normal;
            _current.Activate();
            _current.BringToFront();
            return;
        }

        _current = new DiagnosticsWindow();
        _current.Show();
    }

    private DiagnosticsWindow()
    {
        BuildLayout();
        RefreshLog();
    }

    private void BuildLayout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;

        Text = "RxBarcodeListener — Diagnostics";
        Icon = TrayApp.AppIcon;
        Width = 720;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = BackColorDark;
        ForeColor = Color.White;
        Font = UiTheme.BodyFont;
        // Wide enough to fit all 5 action buttons on one row even at minimum size —
        // otherwise FlowLayoutPanel would clip/hide buttons off the right edge.
        MinimumSize = new Size(760, 420);

        _toolTip = new ToolTip { AutoPopDelay = 8000, InitialDelay = 400, ReshowDelay = 200 };

        var titleLabel = new Label
        {
            Text = $"RxBarcodeListener v{version?.Major}.{version?.Minor}.{version?.Build}",
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(12, 8, 0, 0),
            Font = UiTheme.TitleFont,
            ForeColor = Color.White,
        };

        _statusLabel = new Label
        {
            Text = $"Update server: {Config.UpdateBaseUrl}",
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(12, 0, 0, 0),
            ForeColor = UiTheme.TextMuted,
        };

        var logLabel = new Label
        {
            Text = "Recent log activity (newest at the bottom):",
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(12, 4, 0, 0),
            ForeColor = UiTheme.TextMuted,
        };

        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = UiTheme.InputBackground,
            ForeColor = Color.FromArgb(210, 210, 210),
            Font = UiTheme.MonoFont,
            BorderStyle = BorderStyle.None,
            WordWrap = false,
        };

        var buttonPanel = BuildButtonPanel();

        // Dock order matters: fill goes in last relative to the others visually, but
        // WinForms applies Dock in reverse-add order, so add Fill first, then the
        // top-docked strips, then the bottom-docked button panel last.
        Controls.Add(_logBox);
        Controls.Add(logLabel);
        Controls.Add(_statusLabel);
        Controls.Add(titleLabel);
        Controls.Add(buttonPanel);
    }

    private Panel BuildButtonPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            // Tall enough for the row to wrap onto a second line without clipping
            // buttons if the window is ever resized/DPI-scaled below the 5-button width.
            Height = 96,
            BackColor = PanelColor,
            Padding = new Padding(10),
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };

        var refreshButton = MakeButton("Refresh", UiTheme.ButtonNeutral);
        refreshButton.Click += (_, _) => RefreshLog();
        _toolTip.SetToolTip(refreshButton, "Reload the log view below with the latest activity");

        var openLogButton = MakeButton("Open Full Log", UiTheme.ButtonNeutral);
        openLogButton.Click += (_, _) => Logger.OpenLogFile();
        _toolTip.SetToolTip(openLogButton, "Open the complete log file in Notepad");

        var checkUpdateButton = MakeButton("Check for Updates", UiTheme.AccentBlue);
        checkUpdateButton.Click += async (_, _) => await CheckForUpdateNowAsync(checkUpdateButton).ConfigureAwait(true);
        _toolTip.SetToolTip(checkUpdateButton, "Check the update server now instead of waiting for the automatic check");

        var restartButton = MakeButton("Restart App", AccentRed);
        restartButton.Click += (_, _) => RestartApp();
        _toolTip.SetToolTip(restartButton, "Restart RxBarcodeListener if scanning seems stuck");

        _sendReportButton = MakeButton("Send Report to Developer", AccentGreen);
        _sendReportButton.Click += async (_, _) => await SendReportAsync().ConfigureAwait(true);
        _toolTip.SetToolTip(_sendReportButton, "Send the log file to the developer via Sentry, even if nothing has crashed");

        flow.Controls.Add(refreshButton);
        flow.Controls.Add(openLogButton);
        flow.Controls.Add(checkUpdateButton);
        flow.Controls.Add(restartButton);
        flow.Controls.Add(_sendReportButton);

        panel.Controls.Add(flow);
        return panel;
    }

    private static Button MakeButton(string text, Color backColor)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void RefreshLog()
    {
        _logBox.Text = Logger.GetRecentLogText();
        _logBox.SelectionStart = _logBox.Text.Length;
        _logBox.ScrollToCaret();
    }

    private async Task CheckForUpdateNowAsync(Button sourceButton)
    {
        sourceButton.Enabled = false;
        sourceButton.Text = "Checking…";

        Updater.UpdateInfo? update = null;
        try
        {
            update = await Updater.CheckForUpdateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.LogError("Diagnostics: manual update check failed", ex);
        }

        sourceButton.Enabled = true;
        sourceButton.Text = "Check for Updates";
        RefreshLog();

        if (update == null)
        {
            MessageBox.Show(this,
                "You're already running the latest version.",
                "RxBarcodeListener — Up to Date",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var result = MessageBox.Show(this,
            $"Version {update.VersionString} is available (you have v{Assembly.GetExecutingAssembly().GetName().Version}).\n\n" +
            "Install it now? The app will restart automatically once it's downloaded.",
            "RxBarcodeListener — Update Available",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
            Updater.InstallUpdate(update);
    }

    private void RestartApp()
    {
        var result = MessageBox.Show(this,
            "Restart RxBarcodeListener now? Barcode scanning will pause for a few seconds.",
            "RxBarcodeListener — Restart",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes) return;

        Logger.Log("Diagnostics: manual restart requested by user");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = Environment.ProcessPath!,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.LogError("Diagnostics: failed to relaunch for manual restart", ex);
            MessageBox.Show(this, $"Couldn't restart automatically:\n\n{ex.Message}",
                "RxBarcodeListener — Restart Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Route through TrayApp.Shutdown() (hides the tray icon, uninstalls the keyboard
        // hook) rather than calling Application.Exit() directly — otherwise the tray icon
        // is left behind as a "ghost" until the user hovers over the notification area.
        if (TrayApp.Current != null) TrayApp.Current.Shutdown();
        else Application.Exit();
    }

    private async Task SendReportAsync()
    {
        string? userNote;
        using (var promptForm = BuildNotePromptForm())
        {
            if (promptForm.ShowDialog(this) != DialogResult.OK) return;
            userNote = ((TextBox)promptForm.Tag!).Text.Trim();
        }

        _sendReportButton.Enabled = false;
        _sendReportButton.Text = "Sending…";

        // Logger.SendDiagnosticReport is a blocking call (network + file I/O). Running it
        // via Task.Run keeps the UI thread responsive without resorting to Application.DoEvents(),
        // which risks reentrancy (e.g. the user closing the window or double-clicking Send
        // mid-flight while a nested message pump is active).
        var eventId = await Task.Run(() => Logger.SendDiagnosticReport(userNote)).ConfigureAwait(true);

        _sendReportButton.Enabled = true;
        _sendReportButton.Text = "Send Report to Developer";
        RefreshLog();

        if (eventId != null)
        {
            MessageBox.Show(this,
                $"Report sent — thanks!\n\nReference: {eventId[..8]}",
                "RxBarcodeListener — Report Sent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this,
                "Couldn't send the report — check the internet connection and try again, " +
                "or just tell the developer directly what's happening.",
                "RxBarcodeListener — Send Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private Form BuildNotePromptForm()
    {
        var form = new Form
        {
            Text = "Send Report to Developer",
            Icon = TrayApp.AppIcon,
            Width = 440,
            Height = 220,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = BackColorDark,
            ForeColor = Color.White,
            Font = UiTheme.BodyFont,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var label = new Label
        {
            Text = "What's going on? (optional — the log file is attached automatically)",
            Left = 16, Top = 16, Width = 400, Height = 40,
        };

        var noteBox = new TextBox
        {
            Left = 16, Top = 60, Width = 392, Height = 70,
            Multiline = true,
            BackColor = UiTheme.InputBackground,
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var sendButton = MakeButton("Send", AccentGreen);
        sendButton.Left = 248; sendButton.Top = 145;
        sendButton.DialogResult = DialogResult.OK;

        var cancelButton = MakeButton("Cancel", UiTheme.ButtonNeutral);
        cancelButton.Left = 332; cancelButton.Top = 145;
        cancelButton.DialogResult = DialogResult.Cancel;

        form.Controls.Add(label);
        form.Controls.Add(noteBox);
        form.Controls.Add(sendButton);
        form.Controls.Add(cancelButton);
        form.AcceptButton = sendButton;
        form.CancelButton = cancelButton;
        form.Tag = noteBox;

        return form;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // ToolTip is a Component, not a Control — it isn't part of the Controls
        // collection, so Form.Dispose() won't clean it up automatically.
        _toolTip?.Dispose();
        if (_current == this) _current = null;
        base.OnFormClosed(e);
    }
}
