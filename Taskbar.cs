using System.Runtime.InteropServices;

namespace WinDock;

/// <summary>Controls the real Windows taskbar: its auto-hide setting and whether it is shown at all.</summary>
internal static class Taskbar
{
    static long _stuckSince, _lastReset;

    static IntPtr Primary => Native.FindWindow("Shell_TrayWnd", null);

    static Native.APPBARDATA Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(),
        hWnd = Primary
    };

    public static bool GetAutoHide()
    {
        var data = Data();
        return ((long)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref data) & Native.ABS_AUTOHIDE) != 0;
    }

    public static void SetAutoHide(bool on)
    {
        var data = Data();
        data.lParam = new IntPtr(on ? Native.ABS_AUTOHIDE : 0);
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref data);
    }

    /// <summary>
    /// Hides or shows the taskbar windows on every monitor. Hiding is meant to be called repeatedly:
    /// a taskbar is only hidden once it has slid away, and one stuck in the popped-up position is reset.
    /// </summary>
    public static void SetHidden(bool hidden)
    {
        bool poppedUp = Apply(Primary, hidden);
        IntPtr secondary = IntPtr.Zero;
        while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            poppedUp |= Apply(secondary, hidden);
        if (!hidden) return;

        long now = Environment.TickCount64;
        if (!poppedUp) { _stuckSince = 0; return; }
        if (_stuckSince == 0) { _stuckSince = now; return; }
        if (now - _stuckSince < 4000 || now - _lastReset < 30000 || ShellPanelOpen()) return;

        // Windows treats a taskbar that stays popped up as occupying the screen edge, so snapped
        // windows stop short of it. Re-arming auto-hide makes it slide away again.
        _lastReset = now;
        _stuckSince = 0;
        SetAutoHide(false);
        Thread.Sleep(300);
        SetAutoHide(true);
    }

    // Returns true when the taskbar should be hidden but is currently popped up on screen.
    static bool Apply(IntPtr hWnd, bool hidden)
    {
        if (hWnd == IntPtr.Zero) return false;
        bool visible = Native.IsWindowVisible(hWnd);
        if (!hidden)
        {
            if (!visible) Native.ShowWindow(hWnd, Native.SW_SHOW);
            return false;
        }

        var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetWindowRect(hWnd, out var rect)
            || !Native.GetMonitorInfo(Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST), ref info))
            return false;

        if (rect.Top < info.rcMonitor.Bottom - 8) return true;   // still up: hiding it now would leave it stuck up
        if (visible) Native.ShowWindow(hWnd, Native.SW_HIDE);
        return false;
    }

    // The taskbar is legitimately up while Start or Search is open.
    static bool ShellPanelOpen() => Native.ClassName(Native.GetForegroundWindow()) == "Windows.UI.Core.CoreWindow";
}
