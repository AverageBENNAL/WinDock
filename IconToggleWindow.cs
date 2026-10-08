using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace WinDock;

/// <summary>Shows and hides the desktop icons, exactly as the desktop's own View > Show desktop icons does.</summary>
internal static class DesktopIcons
{
    const uint WM_COMMAND = 0x0111;
    const int ToggleCommand = 0x7402;

    public static IntPtr Desktop => Native.FindWindow("Progman", null);

    // The icon view lives under Progman, or under a WorkerW once the wallpaper has been re-hosted.
    static IntPtr View()
    {
        IntPtr view = Native.FindWindowEx(Desktop, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr worker = IntPtr.Zero;
        while (view == IntPtr.Zero)
        {
            worker = Native.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
            if (worker == IntPtr.Zero) break;
            view = Native.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        return view;
    }

    public static bool Visible
    {
        get
        {
            IntPtr list = Native.FindWindowEx(View(), IntPtr.Zero, "SysListView32", null);
            return list != IntPtr.Zero && Native.IsWindowVisible(list);
        }
    }

    public static void Toggle()
    {
        IntPtr view = View();
        if (view != IntPtr.Zero) Native.SendMessage(view, WM_COMMAND, new IntPtr(ToggleCommand), IntPtr.Zero);
    }
}

/// <summary>
/// A small floating button that lives on the desktop (behind your windows) and hides or shows the
/// desktop icons. Click to toggle, drag to move, right-click for the menu.
/// </summary>
public class IconToggleWindow : Window
{
    const double IdleOpacity = 0.55;

    readonly DockManager _manager;
    readonly Settings _s;
    readonly TextBlock _glyph = new()
    {
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 16,
        Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 255, 255, 255)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    readonly DispatcherTimer _timer;
    IntPtr _hwnd, _pinnedTo;

    public IconToggleWindow(DockManager manager)
    {
        _manager = manager;
        _s = manager.Settings;

        Title = "WinDock Desktop Icons";
        Width = Height = 64;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = IdleOpacity;
        Cursor = Cursors.Hand;

        Content = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x30), Color.FromArgb(0xE6, 0x14, 0x14, 0x17), 90),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0x55, 255, 255, 255), Color.FromArgb(0x14, 255, 255, 255), 90),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.45 },
            Child = _glyph
        };

        // Last saved spot if it is still on a screen, otherwise the top-left corner of the main display.
        var area = SystemParameters.WorkArea;
        Left = area.Left;
        Top = area.Top;
        if (_s.IconButtonLeft is double left && _s.IconButtonTop is double top
            && left >= SystemParameters.VirtualScreenLeft - 16
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 48
            && top >= SystemParameters.VirtualScreenTop - 16
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 48)
        {
            Left = left;
            Top = top;
        }

        MouseEnter += (_, _) => Fade(1);
        MouseLeave += (_, _) => Fade(IdleOpacity);
        MouseLeftButtonDown += OnLeftDown;
        MouseRightButtonUp += OnRightClick;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            // The desktop may not exist yet at sign-in and is recreated if Explorer restarts;
            // without a desktop to belong to, the button would end up underneath it.
            IntPtr desktop = DesktopIcons.Desktop;
            if (desktop != IntPtr.Zero && desktop != _pinnedTo) PinToDesktop();
            UpdateGlyph();   // icons can also be toggled from the desktop's own menu
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        PinToDesktop();
        UpdateGlyph();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    // Owned by the desktop (so it survives Win+D) and below every normal window.
    void PinToDesktop()
    {
        _pinnedTo = DesktopIcons.Desktop;
        Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT, _pinnedTo);
        SendToBottom();
    }

    void SendToBottom() =>
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    void UpdateGlyph()
    {
        bool visible = DesktopIcons.Visible;
        _glyph.Text = visible ? "" : "";   // eye / struck-through eye
        ToolTip = visible ? "Hide desktop icons" : "Show desktop icons";
    }

    void Fade(double to) =>
        BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));

    void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        double left = Left, top = Top;
        DragMove();   // returns when the mouse button is released
        if (Math.Abs(Left - left) < 3 && Math.Abs(Top - top) < 3)
        {
            DesktopIcons.Toggle();
            UpdateGlyph();
        }
        else
        {
            _s.IconButtonLeft = Left;
            _s.IconButtonTop = Top;
            _s.Save();
        }
        SendToBottom();
    }

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        Add(menu, DesktopIcons.Visible ? "Hide Desktop Icons" : "Show Desktop Icons", () =>
        {
            DesktopIcons.Toggle();
            UpdateGlyph();
        });
        menu.Items.Add(new Separator());
        Add(menu, "WinDock Settings...", _manager.OpenSettings);
        Add(menu, "Turn Off WinDock", () => Application.Current.Shutdown());

        Menus.Show(this, _hwnd, menu, SendToBottom);
    }

    static void Add(ContextMenu menu, string header, Action action)
    {
        var entry = new MenuItem { Header = header };
        entry.Click += (_, _) => action();
        menu.Items.Add(entry);
    }
}
