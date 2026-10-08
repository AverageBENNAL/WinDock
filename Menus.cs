using System.Windows;
using System.Windows.Controls;

namespace WinDock;

internal static class Menus
{
    /// <summary>
    /// Opens a right-click menu from one of WinDock's never-focused windows. Such a window can't tell
    /// when you click elsewhere, so it takes focus for as long as the menu is open and closes the menu
    /// the moment it loses it.
    /// </summary>
    public static void Show(Window owner, IntPtr hwnd, ContextMenu menu, Action? closed = null)
    {
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style & ~Native.WS_EX_NOACTIVATE));
        owner.Activate();

        void OnDeactivated(object? sender, EventArgs e) => menu.IsOpen = false;
        owner.Deactivated += OnDeactivated;

        menu.Closed += (_, _) =>
        {
            owner.Deactivated -= OnDeactivated;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_NOACTIVATE));
            closed?.Invoke();
        };
        menu.IsOpen = true;
    }
}
