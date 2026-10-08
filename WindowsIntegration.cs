using System.IO;
using Microsoft.Win32;

namespace WinDock;

/// <summary>Per-user integration with Windows: start at sign-in and the Start menu shortcut.</summary>
internal static class WindowsIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Passed when Windows starts WinDock at sign-in, so a second launch stays silent.</summary>
    public const string StartupArgument = "--startup";

    static string StartupShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WinDock.lnk");

    /// <summary>
    /// Start at sign-in. Registered two ways, a Startup-folder shortcut and the registry Run entry,
    /// because Windows has been seen to skip the Run entry on its own. Whichever fires second finds
    /// WinDock already running and exits quietly.
    /// </summary>
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("WinDock") != null || File.Exists(StartupShortcutPath);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
            {
                key.SetValue("WinDock", $"\"{Environment.ProcessPath}\" {StartupArgument}");
                try
                {
                    dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                    dynamic shortcut = wsh.CreateShortcut(StartupShortcutPath);
                    shortcut.TargetPath = Environment.ProcessPath;
                    shortcut.Arguments = StartupArgument;
                    shortcut.Description = "Starts WinDock when you sign in";
                    shortcut.Save();
                }
                catch (Exception ex) { Log.Write("could not create the Startup shortcut: " + ex.Message); }
            }
            else
            {
                key.DeleteValue("WinDock", false);
                try { File.Delete(StartupShortcutPath); } catch { }
            }
        }
    }

    static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WinDock.lnk");

    public static void RemoveStartMenuShortcut()
    {
        try { File.Delete(ShortcutPath); } catch { }
    }

    /// <summary>Makes sure the Start menu entry exists and points at this copy of WinDock.</summary>
    public static void EnsureStartMenuShortcut()
    {
        try
        {
            string path = ShortcutPath;
            dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = wsh.CreateShortcut(path);
            if (string.Equals((string)shortcut.TargetPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
            shortcut.TargetPath = Environment.ProcessPath;
            shortcut.Description = "Mac-style dock (open again for settings)";
            shortcut.Save();
        }
        catch { }
    }
}
