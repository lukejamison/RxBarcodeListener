using System.Globalization;
using System.Runtime.InteropServices;

namespace RxBarcodeListener;

/// <summary>
/// Simulates keyboard input via SendInput to type text into whatever field
/// currently has focus. Used to auto-inject the fixed margin-fee UPC and the
/// computed price into PioneerRx's Point of Sale screen.
///
/// PioneerRx's POS flow: scanning/typing a UPC followed by Enter adds the item
/// and moves focus straight to the price field, so a second value + Tab sets
/// the price and confirms the line.
///
/// Because the API lookups this runs after are async, the currently-focused
/// window can change before injection happens (e.g. the cashier clicks away).
/// Callers should capture the foreground window at scan time via
/// <see cref="CaptureForegroundWindow"/> and pass it into <see cref="InjectFeeLine"/>,
/// which restores focus to it immediately before typing.
/// </summary>
public static class InputInjector
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;

    // PioneerRx's POS UI needs a moment to react to each keystroke/Enter before it's ready
    // for the next one — without these pauses, characters land while the UI is still mid-
    // transition (adding the item, moving focus to the price field, etc.) and get dropped
    // or produce a glitchy/garbled result. This runs on a background thread, so Thread.Sleep
    // here doesn't block the UI.
    private const int InterCharDelayMs = 15;   // between each typed character
    private const int PreEnterDelayMs  = 80;   // after typing a field, before pressing Enter
    private const int PostEnterDelayMs = 350;  // after Enter, before typing into the next field

    // Guards every injection end-to-end (focus restore through the final Enter) so that
    // two scans processed concurrently on different Task.Run threads (e.g. several Rxs in
    // one Will Call bag, or two scans seconds apart) can never interleave their keystrokes
    // into each other's fields.
    private static readonly object InjectionLock = new();

    public static IntPtr CaptureForegroundWindow() => GetForegroundWindow();

    /// <summary>
    /// Restores focus to <paramref name="targetWindow"/> (captured earlier via
    /// <see cref="CaptureForegroundWindow"/>, before the async API lookups ran) and then
    /// types <paramref name="upc"/> + Enter (adds the fee item and focuses the price field),
    /// followed by <paramref name="amount"/> + Tab + Enter (Tab commits the price field/moves
    /// focus off it, and the trailing Enter confirms it — PioneerRx's price field wasn't
    /// reliably accepting the value with either key alone).
    /// </summary>
    public static void InjectFeeLine(IntPtr targetWindow, string upc, decimal amount)
    {
        lock (InjectionLock)
        {
            if (targetWindow != IntPtr.Zero)
                SetForegroundWindow(targetWindow);

            SendText(upc);
            Thread.Sleep(PreEnterDelayMs);
            SendEnter();
            Thread.Sleep(PostEnterDelayMs);

            SendText(amount.ToString("0.00", CultureInfo.InvariantCulture));
            Thread.Sleep(PreEnterDelayMs);
            SendTab();
            Thread.Sleep(PreEnterDelayMs);
            SendEnter();
        }
    }

    private static void SendText(string text)
    {
        foreach (var c in text)
        {
            SendInputEvents(
                CreateUnicodeKeyInput(c, keyUp: false),
                CreateUnicodeKeyInput(c, keyUp: true));
            Thread.Sleep(InterCharDelayMs);
        }
    }

    private static void SendEnter()
    {
        SendInputEvents(
            CreateVkKeyInput(VK_RETURN, keyUp: false),
            CreateVkKeyInput(VK_RETURN, keyUp: true));
    }

    private static void SendTab()
    {
        SendInputEvents(
            CreateVkKeyInput(VK_TAB, keyUp: false),
            CreateVkKeyInput(VK_TAB, keyUp: true));
    }

    private static void SendInputEvents(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        if (sent != inputs.Length)
        {
            Logger.LogError("InputInjector: SendInput failed",
                new InvalidOperationException(
                    $"SendInput sent {sent}/{inputs.Length} events (Win32 error: {Marshal.GetLastWin32Error()})"));
        }
    }

    private static INPUT CreateUnicodeKeyInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (uint)(keyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static INPUT CreateVkKeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = (uint)(keyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // The real Win32 INPUT.u union also includes MOUSEINPUT and HARDWAREINPUT. Even though
    // we only ever send keyboard events, the union's marshaled size must match the size of
    // its LARGEST member (MOUSEINPUT, which is bigger than KEYBDINPUT) or Marshal.SizeOf<INPUT>()
    // comes out too small on x64 — SendInput then rejects every call with ERROR_INVALID_PARAMETER
    // and silently types nothing, with no exception thrown on our side.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
