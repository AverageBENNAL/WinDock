using System.Windows;
using System.Windows.Interop;

namespace WinDock;

/// <summary>Shown once, before WinDock touches anything, so hiding the taskbar is never a surprise.</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow() => InitializeComponent();

    /// <summary>False until "Get started" is clicked; closing the window any other way changes nothing.</summary>
    public bool Accepted { get; private set; }

    public bool DockChosen => DockEnabled.IsChecked == true;
    public bool IconButtonChosen => IconButton.IsChecked == true;
    public bool HideTaskbarChosen => HideTaskbar.IsChecked == true;
    public bool StartWithWindowsChosen => StartWithWindows.IsChecked == true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x001E1C1C;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }
}
