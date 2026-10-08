using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinDock;

/// <summary>The dock editor. Every change is applied to the dock immediately.</summary>
public partial class SettingsWindow : Window
{
    record PinnedTile(string Id, string Name, ImageSource? Icon);

    readonly Settings _s;
    readonly DockManager _manager;
    bool _ready;

    public SettingsWindow(DockManager manager)
    {
        _s = manager.Settings;
        _manager = manager;
        InitializeComponent();
        Reload();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar that blends into the window.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x001E1C1C;   // COLORREF is 0x00BBGGRR
        Native.DwmSetWindowAttribute(hwnd, 20 /* USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, 35 /* CAPTION_COLOR */, ref caption, sizeof(int));
    }

    /// <summary>Refreshes the controls from the settings (the dock can change them too).</summary>
    public void Reload()
    {
        _ready = false;
        DockEnabled.IsChecked = _s.DockEnabled;
        IconButton.IsChecked = _s.IconButton;
        IconSize.Value = _s.IconSize;
        Magnification.Value = _s.Magnification;
        BarOpacity.Value = _s.BarOpacity;
        Corner.Value = _s.CornerRadius;
        DarkTheme.IsChecked = _s.DarkTheme;
        ModeAuto.IsChecked = _s.Mode == DockMode.AutoHide;
        ModeAlways.IsChecked = _s.Mode == DockMode.AlwaysVisible;
        ModeBehind.IsChecked = _s.Mode == DockMode.BehindWindows;
        AllMonitors.IsChecked = _s.AllMonitors;
        AvoidWindows.IsChecked = _s.AvoidWindows;
        ShowWhenEmpty.IsChecked = _s.ShowWhenEmpty;
        HideTaskbar.IsChecked = _s.HideTaskbar;
        ShowLaunchpad.IsChecked = _s.ShowLaunchpad;
        ShowSearch.IsChecked = _s.ShowSearch;
        ShowTrash.IsChecked = _s.ShowTrash;
        ShowStatus.IsChecked = _s.ShowStatus;
        StartWithWindows.IsChecked = WindowsIntegration.Enabled;

        Pinned.ItemsSource = _s.Pinned
            .Select(p => new PinnedTile(p.Id, p.Name, ShellIcons.Get(p.Icon, 256) ?? ShellIcons.Get(p.Target, 256)))
            .ToList();
        UpdateLabels();
        _ready = true;
    }

    void UpdateLabels()
    {
        IconSizeText.Text = $"{IconSize.Value:0} px";
        MagnificationText.Text = $"{Magnification.Value:0.0}×";
        BarOpacityText.Text = $"{BarOpacity.Value * 100:0}%";
        CornerText.Text = $"{Corner.Value:0}";
    }

    void Apply()
    {
        _s.Save();
        _manager.Apply();
    }

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _s.IconSize = Math.Round(IconSize.Value);
        _s.Magnification = Magnification.Value;
        _s.BarOpacity = BarOpacity.Value;
        _s.CornerRadius = Corner.Value;
        UpdateLabels();
        Apply();
    }

    void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.DockEnabled = DockEnabled.IsChecked == true;
        _s.IconButton = IconButton.IsChecked == true;
        _s.DarkTheme = DarkTheme.IsChecked == true;
        _s.AllMonitors = AllMonitors.IsChecked == true;
        _s.AvoidWindows = AvoidWindows.IsChecked == true;
        _s.ShowWhenEmpty = ShowWhenEmpty.IsChecked == true;
        _s.HideTaskbar = HideTaskbar.IsChecked == true;
        _s.ShowLaunchpad = ShowLaunchpad.IsChecked == true;
        _s.ShowSearch = ShowSearch.IsChecked == true;
        _s.ShowTrash = ShowTrash.IsChecked == true;
        _s.ShowStatus = ShowStatus.IsChecked == true;
        Apply();
    }

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _s.Mode = ModeAlways.IsChecked == true ? DockMode.AlwaysVisible
            : ModeBehind.IsChecked == true ? DockMode.BehindWindows
            : DockMode.AutoHide;
        Apply();
    }

    void Startup_Changed(object sender, RoutedEventArgs e) => WindowsIntegration.Enabled = StartWithWindows.IsChecked == true;

    void Remove_Click(object sender, RoutedEventArgs e) => _manager.Unpin((string)((Button)sender).Tag);

    void Import_Click(object sender, RoutedEventArgs e) => _manager.ImportTaskbarPins();

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _s.ResetToDefaults();
        Apply();
        Reload();
    }

    void TurnOff_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
