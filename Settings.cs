using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDock;

public enum DockMode { AutoHide, AlwaysVisible, BehindWindows }

/// <summary>Where the system controls sit: against either end of the dock, or wherever they were dragged.</summary>
public enum StatusAnchor { DockRight, DockLeft, Free }

public class PinnedApp
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Icon { get; set; } = "";
}

public class Settings
{
    // The two features; either can be used on its own.
    public bool DockEnabled { get; set; } = true;
    public bool IconButton { get; set; } = true;
    public double? IconButtonLeft { get; set; }
    public double? IconButtonTop { get; set; }

    public double IconSize { get; set; } = 48;
    public double Magnification { get; set; } = 1.8;
    public double BarOpacity { get; set; } = 0.6;
    public double CornerRadius { get; set; } = 30;
    public bool DarkTheme { get; set; } = true;
    public DockMode Mode { get; set; } = DockMode.AutoHide;
    public bool AllMonitors { get; set; } = true;
    public bool ShowWhenEmpty { get; set; }
    public bool AvoidWindows { get; set; } = true;
    public bool HideTaskbar { get; set; } = true;
    public bool ShowLaunchpad { get; set; } = true;
    public bool ShowSearch { get; set; } = true;
    public bool ShowTrash { get; set; } = true;
    public bool ShowStatus { get; set; } = true;
    public StatusAnchor StatusAnchor { get; set; } = StatusAnchor.DockRight;
    public double StatusX { get; set; }
    public bool Seeded { get; set; }
    public List<PinnedApp> Pinned { get; set; } = new();

    // Exe names (lower case, e.g. "game.exe") the user marked as a game or as not a game, overriding
    // WinDock's own guess. A game keeps the dock from popping up over it.
    public List<string> Games { get; set; } = new();
    public List<string> NotGames { get; set; } = new();

    // The taskbar's own auto-hide state before WinDock took it over; null while WinDock isn't managing it.
    public bool? TaskbarWasAutoHide { get; set; }

    // Windows' "Show flashing on taskbar apps" setting before WinDock switched it off, to put back later.
    public int? TaskbarFlashingWas { get; set; }

    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock");

    static readonly string FilePath = Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Puts look and behaviour back to the defaults. Pinned apps are kept.</summary>
    public void ResetToDefaults()
    {
        var defaults = new Settings();
        DockEnabled = defaults.DockEnabled;
        IconButton = defaults.IconButton;
        IconSize = defaults.IconSize;
        Magnification = defaults.Magnification;
        BarOpacity = defaults.BarOpacity;
        CornerRadius = defaults.CornerRadius;
        DarkTheme = defaults.DarkTheme;
        Mode = defaults.Mode;
        AllMonitors = defaults.AllMonitors;
        ShowWhenEmpty = defaults.ShowWhenEmpty;
        AvoidWindows = defaults.AvoidWindows;
        HideTaskbar = defaults.HideTaskbar;
        ShowLaunchpad = defaults.ShowLaunchpad;
        ShowSearch = defaults.ShowSearch;
        ShowTrash = defaults.ShowTrash;
        ShowStatus = defaults.ShowStatus;
        StatusAnchor = defaults.StatusAnchor;
    }

    static readonly string BackupPath = FilePath + ".bak";

    /// <summary>
    /// Reads the saved settings. If the file is missing or damaged (say, the PC lost power mid-save),
    /// the previous good copy is used instead, so pins and preferences are not lost.
    /// </summary>
    public static Settings Load() => Read(FilePath) ?? Read(BackupPath) ?? new Settings();

    static Settings? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) : null;
        }
        catch { return null; }
    }

    /// <summary>Saves without ever leaving a half-written file: write a new file, then swap it in.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath);
            else File.Move(temp, FilePath);
        }
        catch { }
    }
}
