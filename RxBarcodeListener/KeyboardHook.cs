using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RxBarcodeListener;

/// <summary>
/// Installs a WH_KEYBOARD_LL (low-level keyboard) hook that intercepts all
/// keystrokes system-wide before they reach any application.
///
/// Key design decisions:
/// - The hook callback must return quickly — heavy work (HTTP calls, UI) must
///   be dispatched off the hook thread via Task.Run()
/// - We only pass alphanumeric characters and Enter to the processor
/// - _proc is stored as a field (not a local) to prevent garbage collection;
///   if it were collected, the hook would silently stop firing
/// </summary>
public class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Must be a field — if stored as a local it can be GC'd and the hook silently dies
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private readonly Action<char> _onKey;

    public KeyboardHook(Action<char> onKey)
    {
        _onKey = onKey;
        _proc = HookCallback;
    }

    public void Install()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(curModule.ModuleName!), 0);

        if (_hookId == IntPtr.Zero)
            Logger.LogError("Failed to install keyboard hook",
                new InvalidOperationException($"SetWindowsHookEx returned NULL (Win32 error: {Marshal.GetLastWin32Error()})"));
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    /// <summary>
    /// When true, logs every captured VK code and character to help diagnose
    /// whether the hook is receiving scanner input and what form it takes.
    /// Toggle via tray menu.
    /// </summary>
    public bool DebugLogging { get; set; } = false;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            char? c = VkToChar(vkCode);

            if (DebugLogging)
                Logger.Log($"[DEBUG] VK={vkCode} -> char={(c.HasValue ? c.Value.ToString() : "null")}");

            if (c.HasValue)
                _onKey(c.Value);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static char? VkToChar(int vk)
    {
        // Top-row 0–9 keys (VK 48–57)
        if (vk >= 48 && vk <= 57)
            return (char)vk;

        // Numpad 0–9 keys (VK 96–105) — many barcode scanners send these
        if (vk >= 96 && vk <= 105)
            return (char)('0' + (vk - 96));

        // A–Z keys (VK 65–90) — uppercase; barcode regex is case-insensitive
        if (vk >= 65 && vk <= 90)
            return (char)vk;

        // Enter key (VK 13) and numpad Enter (VK 13 same code) — barcode terminator
        if (vk == 13)
            return '\r';

        return null;
    }
}
