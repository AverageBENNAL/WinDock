using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinDock;

/// <summary>
/// Takes the real Windows taskbar out of the picture while the dock replaces it: the taskbar is set
/// to auto-hide (so it reserves no screen space) and its windows are hidden.
///
/// An auto-hide taskbar pops up on its own whenever an app asks for attention, and stays up until
/// that app is clicked. Hidden or not, a popped-up taskbar makes snapped windows stop short of the
/// screen edge. So Windows' "Show flashing on taskbar apps" setting is switched off for as long as
/// WinDock is in charge; the dock bounces the app's icon instead.
/// </summary>
internal static class Taskbar
{
    const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string FlashingValue = "TaskbarFlashing";

    /// <summary>Stored in place of the flashing setting when it had never been set.</summary>
    public const int FlashingNotSet = -1;

    static long _poppedSince, _lastRearm;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    static IntPtr Primary => Native.FindWindow("Shell_TrayWnd", null);

    static IEnumerable<IntPtr> All()
    {
        IntPtr primary = Primary;
        if (primary != IntPtr.Zero) yield return primary;
        IntPtr secondary = IntPtr.Zero;
        while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return secondary;
    }

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

    /// <summary>The "Show flashing on taskbar apps" setting: 1, 0, or <see cref="FlashingNotSet"/>.</summary>
    public static int GetFlashing()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        return key?.GetValue(FlashingValue) is int value ? value : FlashingNotSet;
    }

    public static void SetFlashing(int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey);
            if (value == FlashingNotSet) key.DeleteValue(FlashingValue, false);
            else key.SetValue(FlashingValue, value, RegistryValueKind.DWord);
            // Tell Explorer to re-read its taskbar settings now rather than at next sign-in.
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "TraySettings", 2, 2000, out _);
        }
        catch (Exception ex) { Log.Write("could not change the taskbar flashing setting: " + ex.Message); }
    }

    /// <summary>
    /// Keeps every taskbar window hidden. Call about once a second. If a taskbar has been sitting
    /// popped-up for a while with no reason to be, auto-hide is re-armed to make it slide away; that
    /// briefly changes the usable screen area, so it is done rarely.
    /// </summary>
    public static void KeepHidden()
    {
        bool popped = false;
        foreach (IntPtr taskbar in All())
        {
            if (Native.IsWindowVisible(taskbar)) Native.ShowWindow(taskbar, Native.SW_HIDE);

            var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetWindowRect(taskbar, out var rect)
                && Native.GetMonitorInfo(Native.MonitorFromWindow(taskbar, Native.MONITOR_DEFAULTTONEAREST), ref info)
                && rect.Top < info.rcMonitor.Bottom - 8)
                popped = true;
        }

        long now = Environment.TickCount64;
        if (!popped || ShellPanelOpen()) { _poppedSince = 0; return; }
        if (_poppedSince == 0) { _poppedSince = now; return; }
        if (now - _poppedSince < 10_000 || now - _lastRearm < 300_000) return;

        _lastRearm = now;
        _poppedSince = 0;
        Log.Write("taskbar was stuck popped up while hidden: re-arming auto-hide");
        SetAutoHide(false);
        Thread.Sleep(300);
        SetAutoHide(true);
    }

    /// <summary>Brings the taskbar back exactly as it was.</summary>
    public static void Restore(bool wasAutoHide, int flashingWas)
    {
        foreach (IntPtr taskbar in All())
            if (!Native.IsWindowVisible(taskbar)) Native.ShowWindow(taskbar, Native.SW_SHOW);
        SetAutoHide(wasAutoHide);
        SetFlashing(flashingWas);
    }

    // The taskbar is legitimately up while Start or Search is open.
    static bool ShellPanelOpen() => Native.ClassName(Native.GetForegroundWindow()) == "Windows.UI.Core.CoreWindow";
}
