using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WinDock;

/// <summary>
/// WinDock installs itself: a copy of the exe named "...Setup..." acts as the installer, and
/// "WinDock.exe --uninstall" removes it. Everything is per-user, so no administrator rights are needed.
/// </summary>
internal static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinDock";
    const string QuitEvent = "WinDock_Quit";

    static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinDock");

    static string InstalledExe => Path.Combine(InstallDir, "WinDock.exe");

    /// <summary>True when this exe was started under a setup name, e.g. WinDock-Setup.exe.</summary>
    public static bool IsSetup =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Signalled to make a running WinDock exit cleanly (restoring the taskbar).</summary>
    public static EventWaitHandle CreateQuitEvent() => new(false, EventResetMode.AutoReset, QuitEvent);

    public static void Install(bool quiet)
    {
        if (!quiet && MessageBox.Show(
                "Install WinDock for your Windows account?\n\nIt goes in your own user folder, so no administrator rights are needed. You can remove it later from Settings > Apps > Installed apps.",
                "WinDock Setup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            StopRunning();
            Directory.CreateDirectory(InstallDir);
            File.Copy(Environment.ProcessPath!, InstalledExe, true);

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "WinDock");
                key.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
                key.SetValue("Publisher", "AverageBENNAL");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet --purge");
                key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            // The installed copy creates its own Start menu shortcut and shows the welcome window.
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show("WinDock could not be installed.\n\n" + ex.Message, "WinDock Setup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Removes WinDock. Settings and pinned apps are kept unless <paramref name="purge"/> is set (or the
    /// person says so when asked), so uninstalling and reinstalling doesn't lose them.
    /// </summary>
    public static void Uninstall(bool quiet, bool purge)
    {
        if (!quiet)
        {
            if (MessageBox.Show(
                    "Remove WinDock from this account?\n\nThe Windows taskbar will be put back the way it was.",
                    "Uninstall WinDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            purge = MessageBox.Show(
                "Also delete your WinDock settings and pinned apps?\n\nChoose No to keep them in case you install WinDock again.",
                "Uninstall WinDock", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        StopRunning();

        // If WinDock didn't get to exit cleanly, give the taskbar back ourselves.
        if (Settings.Load().TaskbarWasAutoHide is bool wasAutoHide)
        {
            Taskbar.SetHidden(false);
            Taskbar.SetAutoHide(wasAutoHide);
        }

        // Don't leave the desktop icons hidden with no button to bring them back.
        if (!DesktopIcons.Visible) DesktopIcons.Toggle();

        WindowsIntegration.Enabled = false;
        WindowsIntegration.RemoveStartMenuShortcut();
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (purge)
        {
            try { Directory.Delete(Settings.Folder, true); } catch { }
        }

        // Only ever delete the fixed install folder, and only when that is where we are running from.
        bool installed = string.Equals(Path.GetDirectoryName(Environment.ProcessPath), InstallDir, StringComparison.OrdinalIgnoreCase);
        if (installed)
        {
            // A running exe can't delete itself, so a short-lived command does it once we have exited.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });
        }

        if (!quiet)
            MessageBox.Show(installed
                    ? "WinDock has been removed."
                    : "WinDock has been removed. You can now delete the folder you ran it from.",
                "Uninstall WinDock", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Ask a running WinDock to exit; force it only if it doesn't respond.
    static void StopRunning()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(QuitEvent, out var quit)) quit.Set();
        }
        catch { }

        var deadline = DateTime.Now.AddSeconds(5);
        while (Others().Any() && DateTime.Now < deadline) Thread.Sleep(100);

        foreach (var process in Others())
        {
            try { process.Kill(); process.WaitForExit(3000); } catch { }
        }
    }

    static IEnumerable<Process> Others() =>
        Process.GetProcessesByName("WinDock").Where(p => p.Id != Environment.ProcessId);
}
