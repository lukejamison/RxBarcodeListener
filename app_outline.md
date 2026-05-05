# RxBarcodeListener — Cursor Build Instructions
### C# WinForms System Tray App for NimbleRx Barcode Detection

---

## What We Are Building

A lightweight Windows system tray application written in C# (.NET 8) that:

1. Runs silently in the background as a system tray icon
2. Listens globally for barcode scanner input using a low-level Windows keyboard hook
3. Matches barcodes against a pattern used by PioneerRx prescription labels
4. Only activates when a specific PioneerRx screen is the active window
5. Calls the NimbleRx API to check if a paid delivery order exists for that Rx number
6. Shows a custom toast-style notification if a paid order is found, alerting staff not to charge the patient
7. Includes a clickable deep link in the toast to open the task directly in the NimbleRx Chrome app
8. Recovers automatically after the computer wakes from sleep
9. Logs errors to Sentry and a local rolling log file

This replaces a previous AutoHotkey script that had reliability issues after sleep/wake cycles
because Windows resets low-level input hooks and AHK does not automatically recover them.

---

## Why C# WinForms (Not WPF, Not Electron)

- **WinForms** is the lightest possible native Windows UI framework — minimal resource usage,
  fast startup, no XAML overhead
- **System tray app** pattern means zero visible windows unless something happens —
  it just runs quietly and shows a toast when triggered
- **Global keyboard hook** via `SetWindowsHookEx` is well-supported in C# with P/Invoke
  and can be reregistered on sleep/wake events
- **Single `.exe` output** — no AutoHotkey runtime required on the workstation
- **.NET 8** is the current LTS version, ships as a self-contained executable so no runtime
  install is needed on the target machine

---

## Project Setup

### 1. Create the project

```bash
dotnet new winforms -n RxBarcodeListener
cd RxBarcodeListener
```

### 2. Install NuGet packages

```bash
dotnet add package Sentry                          # Error tracking
dotnet add package Microsoft.Extensions.Logging    # Structured logging
dotnet add package Newtonsoft.Json                 # JSON parsing
```

### 3. Project structure

```
RxBarcodeListener/
├── Program.cs                  # Entry point, SentrySdk init, Application.Run
├── TrayApp.cs                  # ApplicationContext subclass, tray icon, lifecycle
├── KeyboardHook.cs             # Low-level WH_KEYBOARD_LL hook via P/Invoke
├── BarcodeProcessor.cs         # Buffer logic, regex matching, window detection
├── NimbleRxClient.cs           # HttpClient wrapper for NimbleRx API
├── ToastWindow.cs              # Custom WinForms floating toast notification
├── Config.cs                   # All configuration constants in one place
├── Logger.cs                   # Rolling file logger + Sentry bridge
└── RxBarcodeListener.csproj
```

---

## Config.cs

All configuration lives here. No values should be hardcoded anywhere else.

```csharp
namespace RxBarcodeListener;

public static class Config
{
    // NimbleRx API
    public const string NimbleRxBearerToken = "eyJhb..."; // Replace with full token
    public const string NimbleRxBaseUrl = "https://api-prod.nimblerx.com";

    // NimbleRx deep link — URL to open a task in the Chrome app
    // Format: replace {taskId} with the actual task ID from the API response
    // Example task ID format: Tas-NnEmpDAIS6o
    // TODO: Confirm the exact URL pattern from the NimbleRx Chrome app address bar
    public const string NimbleRxTaskUrlTemplate = "https://admin.nimblerx.com/tasks/{taskId}";

    // PioneerRx window filter — fire only when these strings appear in the active window title
    public static readonly string[] PioneerRxScreens =
    [
        "Point of Sale",
        "Create Bag"
    ];

    // Barcode pattern
    // Matches: X or C + 7 digit Rx number + 2 digit refill = 10 chars total
    // Capture group 1 = the 7-digit Rx number
    public const string BarcodePattern = @"(?i)[XC](\d{7})\d{2}";

    // How long (ms) to wait between keystrokes before resetting the buffer
    // Scanners send all characters in <50ms; humans type much slower
    // This is what distinguishes a barcode scan from someone typing
    public const int BufferTimeoutMs = 150;

    // Sentry DSN for error reporting — get from sentry.io project settings
    public const string SentryDsn = "https://YOUR_KEY@oXXXXXX.ingest.sentry.io/XXXXXXX";

    // Toast display duration in milliseconds
    public const int ToastDurationMs = 12000;

    // Log file location
    public static readonly string LogFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RxBarcodeListener", "log.txt");
}
```

---

## Program.cs

Entry point. Initializes Sentry, sets up the application, and starts the tray app.

```csharp
using Sentry;
using RxBarcodeListener;

// Must be STA thread for WinForms
[STAThread]
static void Main()
{
    // Initialize Sentry before anything else so startup errors are captured
    using var _ = SentrySdk.Init(options =>
    {
        options.Dsn = Config.SentryDsn;
        options.Environment = "production";
        options.TracesSampleRate = 0;        // No performance tracing needed
        options.AutoSessionTracking = false;
    });

    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

    // Run as ApplicationContext (no main form — tray only)
    Application.Run(new TrayApp());
}
```

---

## TrayApp.cs

The core of the application. Manages the tray icon, wires everything together,
and handles sleep/wake recovery.

```csharp
using Microsoft.Win32;
using RxBarcodeListener;

namespace RxBarcodeListener;

/// <summary>
/// ApplicationContext subclass that manages the system tray icon and application lifecycle.
/// No main window — the app lives entirely in the system tray.
/// </summary>
public class TrayApp : ApplicationContext
{
    private NotifyIcon _trayIcon = null!;
    private KeyboardHook _hook = null!;
    private BarcodeProcessor _processor = null!;

    public TrayApp()
    {
        Logger.Initialize();
        Logger.Log("RxBarcodeListener starting up");

        InitializeTrayIcon();
        InitializeHook();

        // Listen for Windows sleep/wake events
        // When the machine wakes from sleep, Windows resets low-level keyboard hooks.
        // We catch the resume event and reinitialize the hook automatically.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            // Use a built-in system icon as placeholder — replace with custom .ico if desired
            Icon = SystemIcons.Application,
            Text = "RxBarcodeListener — Running",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("RxBarcodeListener", null, null).Enabled = false; // Title label
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reload Hook", null, (s, e) => ReloadHook());
        menu.Items.Add("Open Log File", null, (s, e) => Logger.OpenLogFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ExitApp());

        return menu;
    }

    private void InitializeHook()
    {
        _processor = new BarcodeProcessor(OnBarcodeDetected);
        _hook = new KeyboardHook(_processor.ProcessKey);
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
            // Machine just woke from sleep — reinitialize the hook
            Logger.Log("System resumed from sleep — reinitializing keyboard hook");
            ReloadHook();
        }
    }

    private async void OnBarcodeDetected(string rxNumber)
    {
        Logger.Log($"Barcode matched — Rx: {rxNumber}");

        try
        {
            var result = await NimbleRxClient.LookupAsync(rxNumber);

            if (result == null)
            {
                Logger.Log($"Rx {rxNumber} — no paid NimbleRx order found");
                return;
            }

            Logger.Log($"Rx {rxNumber} — paid order found for {result.PatientName}, due {result.DueByDate}");

            // Toast must be shown on the UI thread
            _trayIcon.GetCurrentParent()?.Invoke(() => ShowToast(result));
        }
        catch (Exception ex)
        {
            Logger.LogError($"NimbleRx lookup failed for Rx {rxNumber}", ex);
        }
    }

    private static void ShowToast(NimbleRxResult result)
    {
        var toast = new ToastWindow(result);
        toast.Show();
    }

    private void ExitApp()
    {
        Logger.Log("Application exiting");
        _hook?.Uninstall();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _trayIcon.Visible = false;
        Application.Exit();
    }
}
```

---

## KeyboardHook.cs

Low-level global keyboard hook using Windows P/Invoke.
This captures all keystrokes system-wide regardless of which app has focus.

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RxBarcodeListener;

/// <summary>
/// Installs a WH_KEYBOARD_LL (low-level keyboard) hook that intercepts all
/// keystrokes system-wide before they reach any application.
///
/// Key design decisions:
/// - The hook callback must return quickly — heavy work (HTTP calls, UI) must
///   be dispatched off the hook thread
/// - We only pass printable ASCII characters and Enter to the processor
/// - The hook is stored as a GC root (static field) to prevent garbage collection
/// </summary>
public class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Store delegate as field to prevent GC collection — if this gets collected
    // the hook will silently stop working
    private LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private readonly Action<char> _onKey;

    public KeyboardHook(Action<char> onKey)
    {
        _onKey = onKey;
        _proc = HookCallback; // Assign to field, not local variable
    }

    public void Install()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(curModule.ModuleName!), 0);
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);

            // Convert virtual key code to char
            // We only care about alphanumeric and Enter
            char? c = VkToChar(vkCode);
            if (c.HasValue)
                _onKey(c.Value);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static char? VkToChar(int vk)
    {
        // 0-9 keys (VK 48-57)
        if (vk >= 48 && vk <= 57)
            return (char)vk;

        // A-Z keys (VK 65-90) — return uppercase
        // The barcode pattern uses case-insensitive matching so this is fine
        if (vk >= 65 && vk <= 90)
            return (char)vk;

        // Enter key (VK 13) — used as barcode terminator
        if (vk == 13)
            return '\r';

        return null;
    }
}
```

---

## BarcodeProcessor.cs

Buffers incoming keystrokes, detects scanner input vs human typing,
matches the barcode pattern, and checks the active window.

```csharp
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace RxBarcodeListener;

/// <summary>
/// Receives individual keystrokes from the keyboard hook and determines
/// whether they constitute a barcode scan.
///
/// How barcode detection works:
/// Barcode scanners send all characters in rapid succession (typically < 50ms total).
/// Humans type much slower. We use a timeout (BufferTimeoutMs) to reset the buffer
/// if there is too long a gap between keystrokes — this prevents normal typing
/// from accidentally matching the barcode pattern.
/// </summary>
public class BarcodeProcessor
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    private static readonly Regex BarcodeRegex =
        new(Config.BarcodePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string _buffer = "";
    private DateTime _lastKeyTime = DateTime.MinValue;
    private readonly Action<string> _onBarcodeDetected;

    public BarcodeProcessor(Action<string> onBarcodeDetected)
    {
        _onBarcodeDetected = onBarcodeDetected;
    }

    public void ProcessKey(char c)
    {
        var now = DateTime.UtcNow;

        // Reset buffer if too much time has passed since last keystroke
        if ((now - _lastKeyTime).TotalMilliseconds > Config.BufferTimeoutMs)
            _buffer = "";

        _lastKeyTime = now;

        // Enter = end of barcode — check buffer and reset
        if (c == '\r')
        {
            CheckBuffer();
            _buffer = "";
            return;
        }

        _buffer += c;

        // Also check mid-buffer in case scanner doesn't send Enter
        CheckBuffer();

        // Prevent unbounded growth
        if (_buffer.Length > 50)
            _buffer = _buffer[^20..];
    }

    private void CheckBuffer()
    {
        var match = BarcodeRegex.Match(_buffer);
        if (!match.Success) return;

        var rxNumber = match.Groups[1].Value;
        _buffer = "";

        // Only proceed if PioneerRx is the active window
        if (!IsPioneerRxActive()) return;

        // Fire on thread pool — never do heavy work on the hook callback thread
        Task.Run(() => _onBarcodeDetected(rxNumber));
    }

    private static bool IsPioneerRxActive()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            var title = sb.ToString();

            return Config.PioneerRxScreens.Any(screen =>
                title.Contains(screen, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
```

---

## NimbleRxClient.cs

Handles the NimbleRx API call and parses the response.

```csharp
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RxBarcodeListener;

/// <summary>
/// Wraps the NimbleRx task search API.
///
/// Endpoint: GET /v2/tasks/search?statuses=Q&query={rxNumber}
/// Auth: Bearer token in Authorization header
///
/// Returns null if no paid order exists for this Rx number.
/// Throws on network/HTTP errors so the caller can log them.
/// </summary>
public static class NimbleRxClient
{
    // Single shared HttpClient instance — do not create per-request
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Config.NimbleRxBaseUrl),
        Timeout = TimeSpan.FromSeconds(10)
    };

    static NimbleRxClient()
    {
        Http.DefaultRequestHeaders.Add("Authorization", $"Bearer {Config.NimbleRxBearerToken}");
        Http.DefaultRequestHeaders.Add("Accept", "application/json");
        Http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        Http.DefaultRequestHeaders.Add("Origin", "https://admin.nimblerx.com");
    }

    public static async Task<NimbleRxResult?> LookupAsync(string rxNumber)
    {
        var url = $"/v2/tasks/search?statuses=Q&query={rxNumber}";
        var response = await Http.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            Logger.Log($"NimbleRx API returned {(int)response.StatusCode} for Rx {rxNumber}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var array = JArray.Parse(json);

        if (!array.Any()) return null;

        // Find first task where isPaid = true
        var paidTask = array.FirstOrDefault(t => t["isPaid"]?.Value<bool>() == true);
        if (paidTask == null) return null;

        var firstName = paidTask["patientName"]?["firstName"]?.Value<string>() ?? "";
        var lastName  = paidTask["patientName"]?["lastName"]?.Value<string>()  ?? "";
        var dueByDate = paidTask["dueByDate"]?.Value<DateTime?>() ?? null;
        var taskId    = paidTask["id"]?.Value<string>() ?? "";

        return new NimbleRxResult
        {
            RxNumber    = rxNumber,
            PatientName = $"{firstName} {lastName}".Trim(),
            DueByDate   = dueByDate,
            TaskId      = taskId,
            TaskUrl     = Config.NimbleRxTaskUrlTemplate.Replace("{taskId}", taskId)
        };
    }
}

public class NimbleRxResult
{
    public string RxNumber    { get; set; } = "";
    public string PatientName { get; set; } = "";
    public DateTime? DueByDate { get; set; }
    public string TaskId      { get; set; } = "";
    public string TaskUrl     { get; set; } = "";

    /// <summary>
    /// Human-readable due date relative to now.
    /// Examples: "Due in 2 hours", "Due in 3 days", "Overdue"
    /// </summary>
    public string DueLabel
    {
        get
        {
            if (DueByDate == null) return "";
            var diff = DueByDate.Value.ToUniversalTime() - DateTime.UtcNow;

            if (diff.TotalMinutes <= 0) return "Overdue";
            if (diff.TotalMinutes < 60)
            {
                var mins = (int)diff.TotalMinutes;
                return $"Due in {mins} {(mins == 1 ? "minute" : "minutes")}";
            }
            if (diff.TotalHours < 24)
            {
                var hrs = (int)diff.TotalHours;
                return $"Due in {hrs} {(hrs == 1 ? "hour" : "hours")}";
            }
            var days = (int)diff.TotalDays;
            return $"Due in {days} {(days == 1 ? "day" : "days")}";
        }
    }

    /// <summary>
    /// Color for the due label — red if overdue, orange if today, green otherwise
    /// </summary>
    public Color DueLabelColor
    {
        get
        {
            if (DueByDate == null) return Color.Gray;
            var diff = DueByDate.Value.ToUniversalTime() - DateTime.UtcNow;
            if (diff.TotalMinutes <= 0)  return Color.FromArgb(255, 68,  68);  // Red
            if (diff.TotalHours   < 24)  return Color.FromArgb(240, 165, 0);   // Orange
            return Color.FromArgb(0, 200, 150);                                 // Green
        }
    }
}
```

---

## ToastWindow.cs

Custom floating toast notification. Appears bottom-right, auto-dismisses,
and includes a clickable button to open the NimbleRx task in the Chrome app.

```csharp
namespace RxBarcodeListener;

/// <summary>
/// Custom toast notification window.
///
/// Design:
/// - Dark background (#1a1a2e) matching the previous AHK version
/// - Always on top, no taskbar entry, no caption bar
/// - Bottom-right corner with 20px margin
/// - Auto-dismisses after Config.ToastDurationMs
/// - "Open in NimbleRx" button launches the task URL in the default browser
///   (which will open in the Chrome app if NimbleRx is installed as a Chrome app)
/// - Click anywhere else to dismiss
///
/// Size: 480 x 230px
/// </summary>
public class ToastWindow : Form
{
    private readonly NimbleRxResult _result;
    private System.Windows.Forms.Timer _dismissTimer = null!;

    // Track active toast so we can close it when a new scan arrives
    private static ToastWindow? _current;

    public ToastWindow(NimbleRxResult result)
    {
        _result = result;
        SetupWindow();
        BuildLayout();
        StartDismissTimer();
    }

    public static void ShowToast(NimbleRxResult result)
    {
        // Close previous toast if still showing
        _current?.Close();

        var toast = new ToastWindow(result);
        _current = toast;
        toast.Show();
    }

    private void SetupWindow()
    {
        // Window chrome
        FormBorderStyle = FormBorderStyle.None;
        TopMost         = true;
        ShowInTaskbar   = false;
        BackColor       = Color.FromArgb(26, 26, 46); // #1a1a2e

        // Size
        Width  = 480;
        Height = 230;

        // Position: bottom-right corner above taskbar
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Left = screen.Right - Width - 20;
        Top  = screen.Bottom - Height - 20;

        // Click anywhere to dismiss
        Click += (_, _) => Close();
    }

    private void BuildLayout()
    {
        // Header — warning label
        AddLabel("⚠️  DO NOT CHARGE — NimbleRx Paid Order",
            x: 20, y: 18, width: 440, fontSize: 13, bold: true,
            color: Color.FromArgb(255, 80, 80));

        // Rx number
        AddLabel($"Rx #{_result.RxNumber}",
            x: 20, y: 50, width: 440, fontSize: 11,
            color: Color.FromArgb(170, 170, 170));

        // Patient name
        AddLabel(_result.PatientName,
            x: 20, y: 72, width: 440, fontSize: 20, bold: true,
            color: Color.White);

        // Instruction
        AddLabel("Cancel this sale and check the NimbleRx dashboard.",
            x: 20, y: 108, width: 440, fontSize: 12,
            color: Color.FromArgb(240, 165, 0));

        // Due label
        if (!string.IsNullOrEmpty(_result.DueLabel))
        {
            AddLabel($"🕐  {_result.DueLabel}",
                x: 20, y: 135, width: 440, fontSize: 12, bold: true,
                color: _result.DueLabelColor);
        }

        // Open in NimbleRx button
        if (!string.IsNullOrEmpty(_result.TaskUrl))
        {
            var btn = new Button
            {
                Text      = "Open in NimbleRx →",
                Left      = 20,
                Top       = 165,
                Width     = 200,
                Height    = 32,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(0, 150, 110),
                Cursor    = Cursors.Hand,
                Font      = new Font("Segoe UI", 10, FontStyle.Regular)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += (_, _) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = _result.TaskUrl,
                    UseShellExecute = true  // Opens in default browser / Chrome app
                });
                Close();
            };
            Controls.Add(btn);
        }

        // Dismiss hint
        AddLabel("Click anywhere to dismiss",
            x: 20, y: 205, width: 440, fontSize: 9,
            color: Color.FromArgb(80, 80, 80));
    }

    private void AddLabel(string text, int x, int y, int width,
        float fontSize, bool bold = false, Color color = default)
    {
        var label = new Label
        {
            Text      = text,
            Left      = x,
            Top       = y,
            Width     = width,
            AutoSize  = false,
            ForeColor = color == default ? Color.White : color,
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", fontSize,
                bold ? FontStyle.Bold : FontStyle.Regular),
            Click     += (_, _) => Close()
        };
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
        if (_current == this) _current = null;
        base.OnFormClosed(e);
    }
}
```

---

## Logger.cs

Rolling log file + Sentry bridge. All errors go to both.

```csharp
using Sentry;

namespace RxBarcodeListener;

/// <summary>
/// Simple rolling log file writer + Sentry error bridge.
/// Log file location: %LOCALAPPDATA%\RxBarcodeListener\log.txt
/// Rolls over at 1MB to prevent unbounded growth.
/// </summary>
public static class Logger
{
    private static string _logPath = "";
    private const long MaxLogBytes = 1_000_000; // 1MB

    public static void Initialize()
    {
        _logPath = Config.LogFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        Log("=== RxBarcodeListener started ===");
    }

    public static void Log(string message)
    {
        try
        {
            RollIfNeeded();
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(_logPath, line);
        }
        catch { /* Never crash on logging */ }
    }

    public static void LogError(string message, Exception ex)
    {
        Log($"ERROR: {message} | {ex.Message}");

        // Send to Sentry
        SentrySdk.CaptureException(ex, scope =>
        {
            scope.SetExtra("message", message);
        });
    }

    public static void OpenLogFile()
    {
        if (File.Exists(_logPath))
            System.Diagnostics.Process.Start("notepad.exe", _logPath);
    }

    private static void RollIfNeeded()
    {
        if (!File.Exists(_logPath)) return;
        var info = new FileInfo(_logPath);
        if (info.Length > MaxLogBytes)
        {
            var backup = _logPath.Replace(".txt", $"-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.Move(_logPath, backup);
        }
    }
}
```

---

## Build & Deploy

### Development build
```bash
dotnet run
```

### Release build (self-contained single .exe)
```bash
dotnet publish -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin/Release/net8.0-windows/win-x64/publish/RxBarcodeListener.exe`

Copy this single `.exe` to the workstation — no .NET install required.

### Auto-start on Windows login

Add a shortcut to:
```
C:\Users\{username}\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\
```

Or use Task Scheduler for more control (e.g. run as admin, restart on failure).

---

## Things To Confirm Before Running

| Item | Status |
|---|---|
| NimbleRx Bearer token pasted into `Config.cs` | TODO |
| NimbleRx task URL pattern confirmed from Chrome app address bar | TODO — open a task and copy the URL |
| Sentry DSN pasted into `Config.cs` | TODO |
| PioneerRx screen titles verified (`Point of Sale`, `Create Bag`) | ✅ Confirmed |
| Tested on workstation with barcode scanner | TODO |

---

## Notes for Cursor

- All P/Invoke declarations must match the exact Windows API signatures shown — do not modify them
- The `_proc` delegate in `KeyboardHook.cs` **must** be stored as a field, not a local variable —
  if it gets garbage collected the hook silently stops working with no error
- `Task.Run()` in `BarcodeProcessor.CheckBuffer()` is required — never do HTTP calls or UI on the hook callback thread or Windows will kill the hook after ~500ms
- `HttpClient` is intentionally static/shared — creating per-request instances causes socket exhaustion
- The `ToastWindow` must be created and shown on the UI thread — `TrayApp` handles this with `Invoke()`
- `SystemEvents.PowerModeChanged` is the correct event for sleep/wake recovery — test by putting the machine to sleep and waking it, then scanning a barcode
- If the Bearer token expires, `NimbleRxClient` will log the 401 status code — token refresh is a future enhancement