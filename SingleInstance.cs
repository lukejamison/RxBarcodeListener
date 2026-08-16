using System.Diagnostics;

namespace RxBarcodeListener;

/// <summary>
/// Ensures only one copy of RxBarcodeListener is ever running at a time. Any other
/// running instance (found by process name, regardless of which folder it was launched
/// from) is force-killed before this instance proceeds to install its keyboard hook and
/// tray icon.
///
/// This matters because the app can be started multiple ways — manually during
/// development/testing, via the Task Scheduler auto-restart, or a fresh install/update —
/// and two copies running simultaneously would both install a WH_KEYBOARD_LL hook and
/// fight over the same barcode input.
/// </summary>
public static class SingleInstance
{
    public static void KillOtherInstances()
    {
        var currentProcess = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(currentProcess.ProcessName)
            .Where(p => p.Id != currentProcess.Id)
            .ToList();

        if (others.Count == 0) return;

        Logger.Log($"SingleInstance: found {others.Count} other running instance(s) — terminating before starting");

        foreach (var other in others)
        {
            try
            {
                other.Kill();
                other.WaitForExit(5000);
                Logger.Log($"SingleInstance: terminated PID {other.Id}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"SingleInstance: failed to terminate PID {other.Id}", ex);
            }
            finally
            {
                other.Dispose();
            }
        }
    }
}
