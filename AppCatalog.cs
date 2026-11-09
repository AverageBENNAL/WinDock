using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinDock;

public record AppEntry(string Id, string Name, string Target, string IconPath);

public class RunningApp
{
    public required AppEntry Entry { get; init; }
    public List<IntPtr> Windows { get; } = new();
    public HashSet<uint> ProcessIds { get; } = new();
}

/// <summary>Knows what is installed, what is running, and how to launch things.</summary>
internal static class AppCatalog
{
    const string AppsFolder = "shell:AppsFolder\\";

    public static volatile List<AppEntry> Installed = new();
    public static event Action? InstalledLoaded;

    static readonly Dictionary<string, string> DescriptionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lists everything in the Start menu's "All apps" on a background thread.</summary>
    public static void LoadInstalledAsync()
    {
        StaWorker.Post(() =>
        {
            try
            {
                var list = new List<AppEntry>();
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    try
                    {
                        string name = item.Name;
                        string path = item.Path;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                        string? linkTarget = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                        string id = !string.IsNullOrEmpty(linkTarget) && File.Exists(linkTarget) ? linkTarget : path;
                        list.Add(new AppEntry(id.ToLowerInvariant(), name, AppsFolder + path, AppsFolder + path));
                    }
                    catch { }
                }
                Installed = list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            finally { InstalledReady.Set(); }
            InstalledLoaded?.Invoke();
        });
    }

    /// <summary>Set once the installed-apps list has been read (or has failed to).</summary>
    public static readonly ManualResetEventSlim InstalledReady = new();

    /// <summary>
    /// Everything pinned to the Windows taskbar, in taskbar order, with File Explorer first.
    /// Desktop apps are pinned as shortcut files; Store apps only appear in the taskbar's registry
    /// record, so those are matched against the installed-apps list (call after it has loaded).
    /// </summary>
    public static List<PinnedApp> SeedFromTaskbar()
    {
        // The registry record holds each pin's shortcut name or app id as UTF-16 text; where a
        // name sits in it gives the left-to-right order.
        byte[] record = Array.Empty<byte>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            record = key?.GetValue("Favorites") as byte[] ?? record;
        }
        catch { }
        string even = Encoding.Unicode.GetString(record);
        string odd = record.Length > 1 ? Encoding.Unicode.GetString(record, 1, record.Length - 1) : "";
        int Position(string text)
        {
            int a = even.IndexOf(text, StringComparison.OrdinalIgnoreCase), b = odd.IndexOf(text, StringComparison.OrdinalIgnoreCase);
            return Math.Min(a < 0 ? int.MaxValue : a * 2, b < 0 ? int.MaxValue : b * 2 + 1);
        }

        var found = new List<(int Position, PinnedApp App)>();
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        found.Add((-1, new PinnedApp { Id = explorer.ToLowerInvariant(), Name = "File Explorer", Target = explorer, Icon = explorer }));

        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(dir))
            {
                dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                foreach (string lnk in Directory.GetFiles(dir, "*.lnk"))
                {
                    string target = wsh.CreateShortcut(lnk).TargetPath;
                    if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                    found.Add((Position(Path.GetFileName(lnk)), new PinnedApp
                    {
                        Id = target.ToLowerInvariant(), Name = Path.GetFileNameWithoutExtension(lnk), Target = lnk, Icon = target
                    }));
                }
            }
        }
        catch { }

        foreach (var app in Installed)
        {
            string appId = app.Target[AppsFolder.Length..];
            if (!appId.Contains('!')) continue;   // only Store-style app ids
            int position = Position(appId);
            if (position != int.MaxValue)
                found.Add((position, new PinnedApp { Id = app.Id, Name = app.Name, Target = app.Target, Icon = app.IconPath }));
        }

        return found.OrderBy(f => f.Position).Select(f => f.App).DistinctBy(p => p.Id).ToList();
    }

    /// <summary>Apps that currently have a window you could Alt+Tab to, front-most first.</summary>
    public static List<RunningApp> GetRunning()
    {
        // Every monitor's dock asks; one scan serves them all.
        if (_running != null && Environment.TickCount64 - _runningAt < 400) return _running;
        var result = new List<RunningApp>();
        uint self = (uint)Environment.ProcessId;

        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd)) return true;
            if (Native.GetWindow(hWnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.GetWindowTextLength(hWnd) == 0) return true;
            if (Native.DwmGetWindowAttribute(hWnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            string cls = Native.ClassName(hWnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return true;

            Native.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == self) return true;

            // Store apps are hosted in a frame window; the real app is the child from another process.
            if (cls == "ApplicationFrameWindow")
            {
                uint inner = 0;
                Native.EnumChildWindows(hWnd, (child, lp) =>
                {
                    Native.GetWindowThreadProcessId(child, out uint childPid);
                    if (childPid == pid) return true;
                    inner = childPid;
                    return false;
                }, IntPtr.Zero);
                if (inner == 0) return true;
                pid = inner;
            }

            var entry = Describe(pid);
            if (entry == null) return true;
            var app = result.FirstOrDefault(r => r.Entry.Id == entry.Id);
            if (app == null) result.Add(app = new RunningApp { Entry = entry });
            app.Windows.Add(hWnd);
            app.ProcessIds.Add(pid);
            return true;
        }, IntPtr.Zero);

        _runningAt = Environment.TickCount64;
        return _running = result;
    }

    static List<RunningApp>? _running;
    static long _runningAt;

    /// <summary>Full path of a process's exe, or null if it can't be read.</summary>
    public static string? ExePath(uint pid)
    {
        IntPtr process = Native.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return Native.QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(process); }
    }

    /// <summary>Starts a program or shortcut with extra arguments and/or a shell verb such as "runas".</summary>
    public static void Start(string file, string arguments = "", string? verb = null)
    {
        try { Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, Verb = verb ?? "" }); }
        catch { }   // includes the user saying No to the admin prompt
    }

    static AppEntry? Describe(uint pid)
    {
        IntPtr process = Native.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!Native.QueryFullProcessImageName(process, 0, sb, ref size)) return null;
            string exe = sb.ToString();

            var idBuffer = new StringBuilder(260);
            uint idLength = (uint)idBuffer.Capacity;
            if (Native.GetApplicationUserModelId(process, ref idLength, idBuffer) == 0)
            {
                string target = AppsFolder + idBuffer;
                var installed = Installed.FirstOrDefault(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase));
                return installed ?? new AppEntry(idBuffer.ToString().ToLowerInvariant(), Describe(exe), target, target);
            }
            return new AppEntry(exe.ToLowerInvariant(), Describe(exe), exe, exe);
        }
        finally { Native.CloseHandle(process); }
    }

    static string Describe(string exe)
    {
        if (DescriptionCache.TryGetValue(exe, out var name)) return name;
        try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exe);
        return DescriptionCache[exe] = name!;
    }

    public static void Launch(string target)
    {
        try
        {
            var info = target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("explorer.exe", "\"" + target + "\"")
                : new ProcessStartInfo(target);
            info.UseShellExecute = true;
            Process.Start(info);
        }
        catch { }
    }
}
