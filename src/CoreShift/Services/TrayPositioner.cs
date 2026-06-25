using System.Windows;

namespace CoreShift.Services;

/// <summary>
/// Computes the on-screen geometry so the widget always floats in the bottom-right
/// corner, just above the Windows clock/calendar, regardless of resolution or taskbar size.
/// </summary>
public static class TrayPositioner
{
    // The card inside the window has a 16px margin; nudge so the visible card sits ~12px from the edges.
    private const double EdgeNudge = 4;

    public static void PlaceBottomRight(Window window)
    {
        // WorkArea excludes the taskbar and is already in device-independent units.
        Rect workArea = SystemParameters.WorkArea;

        window.Left = workArea.Right - window.Width + EdgeNudge;
        window.Top = workArea.Bottom - window.Height + EdgeNudge;
    }
}
