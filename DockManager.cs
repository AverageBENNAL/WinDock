using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace WinDock;

/// <summary>Owns one dock per monitor plus everything they share: settings, taskbar, Launchpad, the editor.</summary>
public class DockManager
{
    readonly List<DockWindow> _docks = new();
    LaunchpadWindow? _launchpad;
    SettingsWindow? _settingsWindow;
    IconToggleWindow? _iconToggle;
    bool _cleanedUp;

    public Settings Settings { get; }

    public DockManager(Settings settings)
    {
        Settings = settings;
        SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Apply);
    }

    /// <summary>Applies the current settings everywhere. Called at startup and live from the editor.</summary>
    public void Apply()
    {
        if (_cleanedUp) return;
        ApplyTaskbar();

        if (Settings.IconButton && _iconToggle == null)
        {
            _iconToggle = new IconToggleWindow(this);
            _iconToggle.Show();
        }
        else if (!Settings.IconButton && _iconToggle != null)
        {
            _iconToggle.Close();
            _iconToggle = null;
        }

        var monitors = Monitors();
        if (!Settings.DockEnabled) monitors.Clear();
        else if (!Settings.AllMonitors) monitors = monitors.Take(1).ToList();

        if (monitors.SequenceEqual(_docks.Select(d => d.Monitor)))
        {
            foreach (var dock in _docks) dock.ApplySettings();
            return;
        }

        foreach (var dock in _docks) { dock.Shutdown(); dock.Close(); }
        _docks.Clear();
        foreach (var monitor in monitors)
        {
            var dock = new DockWindow(this, monitor, isPrimary: monitor == monitors[0]);
            _docks.Add(dock);
            dock.Show();
        }
    }

    // Monitor handles, primary first.
    static List<IntPtr> Monitors()
    {
        var list = new List<IntPtr>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(monitor, ref info);
            if ((info.dwFlags & 1) != 0) list.Insert(0, monitor);
            else list.Add(monitor);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    void ApplyTaskbar()
    {
        // Without the dock, the taskbar is the only way to get around, so leave it alone.
        if (!Settings.DockEnabled || !Settings.HideTaskbar) { RestoreTaskbar(); return; }
        if (Settings.TaskbarWasAutoHide == null)
        {
            Settings.TaskbarWasAutoHide = Taskbar.GetAutoHide();
            Settings.Save();
        }
        Taskbar.SetAutoHide(true);
        Taskbar.SetHidden(true);
    }

    void RestoreTaskbar()
    {
        if (Settings.TaskbarWasAutoHide is not bool wasAutoHide) return;
        Taskbar.SetHidden(false);
        Taskbar.SetAutoHide(wasAutoHide);
        Settings.TaskbarWasAutoHide = null;
        Settings.Save();
    }

    public void ToggleTaskbar()
    {
        Settings.HideTaskbar = !Settings.HideTaskbar;
        Changed();
    }

    public void Pin(AppEntry app)
    {
        if (Settings.Pinned.Any(p => p.Id == app.Id)) return;
        Settings.Pinned.Add(new PinnedApp { Id = app.Id, Name = app.Name, Target = app.Target, Icon = app.IconPath });
        Changed();
    }

    /// <summary>Adds any Windows taskbar pins the dock doesn't already have.</summary>
    public void ImportTaskbarPins()
    {
        var missing = AppCatalog.SeedFromTaskbar().Where(app => Settings.Pinned.All(p => p.Id != app.Id)).ToList();
        if (missing.Count == 0) return;
        Settings.Pinned.AddRange(missing);
        Changed();
    }

    public void Unpin(string id)
    {
        Settings.Pinned.RemoveAll(p => p.Id == id);
        Changed();
    }

    /// <summary>Saves a new left-to-right order for the pinned apps (ids in dock order).</summary>
    public void Reorder(IEnumerable<string> ids)
    {
        var ordered = ids.Select(id => Settings.Pinned.FirstOrDefault(p => p.Id == id)).OfType<PinnedApp>().ToList();
        if (ordered.Count != Settings.Pinned.Count) return;
        Settings.Pinned = ordered;
        Changed();
    }

    void Changed()
    {
        Settings.Save();
        Apply();
        _settingsWindow?.Reload();
    }

    public void ToggleLaunchpad(Rect screen)
    {
        _launchpad ??= new LaunchpadWindow(this);
        if (_launchpad.IsVisible) _launchpad.Hide();
        else _launchpad.Open(screen);
    }

    public void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;    // make sure it surfaces above whatever is in front
        _settingsWindow.Topmost = false;
    }

    /// <summary>Puts the system back the way it was. Safe to call more than once.</summary>
    public void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        foreach (var dock in _docks) dock.Shutdown();
        RestoreTaskbar();
        Settings.Save();
    }
}
