using System;
using System.Runtime.InteropServices;

namespace CoreShift.Services;

/// <summary>
/// Releases pages from the process working set when the widget goes idle. WPF keeps a
/// large working set of (mostly shared) runtime pages mapped in; trimming hands them
/// back to the OS. Windows transparently faults the few that are needed back in on the
/// next show — imperceptible for a tray app that is idle almost all the time.
/// </summary>
public static class MemoryUtil
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr handle, IntPtr min, IntPtr max);

    /// <summary>Trims the current process working set to its minimum.</summary>
    public static void TrimWorkingSet()
    {
        try
        {
            // (SIZE_T)-1 for both bounds is the documented "trim as much as possible" sentinel.
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // Best-effort: never let a memory hint break the app.
        }
    }
}
