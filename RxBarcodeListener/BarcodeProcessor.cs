using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RxBarcodeListener;

/// <summary>
/// Receives individual keystrokes from the keyboard hook and determines
/// whether they constitute a barcode scan.
///
/// Barcode scanners send all characters in rapid succession (typically &lt;50ms).
/// Humans type much slower. The buffer timeout (BufferTimeoutMs) distinguishes
/// between the two — if a gap exceeds that threshold the buffer resets so normal
/// typing can never accidentally match the barcode pattern.
/// </summary>
public class BarcodeProcessor
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private static readonly Regex BarcodeRegex =
        new(Config.BarcodePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// When true, skip the PioneerRx window check so any window works.
    /// Toggle via tray menu during testing.
    /// </summary>
    public bool TestModeEnabled { get; set; } = false;

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

        // Reset buffer if there has been too long a gap since the last keystroke
        if ((now - _lastKeyTime).TotalMilliseconds > Config.BufferTimeoutMs)
            _buffer = "";

        _lastKeyTime = now;

        if (c == '\r')
        {
            // Enter = barcode terminator — evaluate and reset
            CheckBuffer();
            _buffer = "";
            return;
        }

        _buffer += c;

        // Also check mid-buffer for scanners that do not send a trailing Enter
        CheckBuffer();

        // Prevent unbounded growth — keep the most recent 20 chars
        if (_buffer.Length > 50)
            _buffer = _buffer[^20..];
    }

    private void CheckBuffer()
    {
        var match = BarcodeRegex.Match(_buffer);
        if (!match.Success) return;

        var rxNumber = match.Groups[1].Value;
        _buffer = "";

        var windowTitle = GetForegroundWindowTitle();
        var pioneerActive = TestModeEnabled || Config.PioneerRxScreens.Any(screen =>
            windowTitle.Contains(screen, StringComparison.OrdinalIgnoreCase));

        if (!pioneerActive)
        {
            Logger.Log($"Barcode matched (Rx {rxNumber}) but PioneerRx not active — window: \"{windowTitle}\"");
            return;
        }

        Logger.Log($"Barcode matched (Rx {rxNumber}) — {(TestModeEnabled ? "TEST MODE" : "PioneerRx window active")}");

        // Dispatch off the hook callback thread — never do HTTP/UI work here
        Task.Run(() => _onBarcodeDetected(rxNumber));
    }

    private static string GetForegroundWindowTitle()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }
}
