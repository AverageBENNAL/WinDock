using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinDock;

/// <summary>The dock itself: a full-width transparent strip along the bottom of the primary screen.</summary>
public class DockWindow : Window
{
    enum Kind { App, Launchpad, Search, Trash, Separator }

    class Item
    {
        public Kind Kind;
        public string Id = "", Name = "", Target = "", IconPath = "";
        public bool Pinned;
        public List<IntPtr> Windows = new();
        public HashSet<uint> ProcessIds = new();
        public Image? Image;
        public Ellipse? Dot;
        public Rectangle? Line;
        public double Left, Size;
        public DateTime Bounce = DateTime.MinValue;
    }

    const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    const double BottomGap = 8;
    const double SeparatorWidth = 12;
    const double BounceSeconds = 1.5;

    readonly DockManager _manager;
    readonly Settings _s;
    readonly List<Item> _items = new();
    readonly Canvas _canvas = new();
    readonly Border _bar = new() { BorderThickness = new Thickness(1) };
    readonly Rectangle _hit = new() { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 12 };
    readonly Border _label;
    readonly TranslateTransform _slide = new();

    // The system controls: a second, smaller pill to the right of the dock on the main display.
    readonly Border _status = new() { BorderThickness = new Thickness(1) };
    readonly StackPanel _statusPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _network = Glyph(), _volume = Glyph(), _battery = Glyph(), _power = Glyph();
    readonly TextBlock _clock = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly bool _isPrimary;
    readonly Rectangle _hitStatus = new() { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    bool _showStatus, _startOpen, _statusPressed, _statusDragging;
    double _statusLeft, _statusWidth, _statusPressX, _statusGrab, _statusDragLeft;
    IntPtr _lastForeground;
    readonly DispatcherTimer _timer;
    static readonly ImageSource LaunchpadIcon = MakeLaunchpadIcon();
    static readonly ImageSource SearchIcon = MakeSearchIcon();

    IntPtr _hwnd;
    double _dpi = 1;
    double _gap, _pad, _barHeight, _baseBottom;
    double _barLeft, _barWidth;
    double _amount, _mouseX, _hideOffset;
    DateTime _lastActive = DateTime.Now;
    bool _dirty = true, _menuOpen, _fullscreen, _appBar, _monitorEmpty;
    bool? _trashFull;
    int _tick;
    Item? _hovered, _pressed;
    double _pressX;
    bool _dragging;
    Native.RECT _screen;

    /// <summary>The monitor this dock lives on.</summary>
    public IntPtr Monitor { get; }

    static TextBlock Glyph() => new()
    {
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        VerticalAlignment = VerticalAlignment.Center
    };

    public DockWindow(DockManager manager, IntPtr monitor, bool isPrimary)
    {
        _manager = manager;
        _s = manager.Settings;
        Monitor = monitor;
        _isPrimary = isPrimary;

        foreach (var cell in new[] { _network, _volume, _battery, _clock, _power })
            _statusPanel.Children.Add(cell);
        _power.Text = "";
        _power.Tag = "Power";
        _status.Child = _statusPanel;
        MouseWheel += OnWheel;

        Title = "WinDock";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x24)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 3, 9, 4),
            Child = _labelText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        _canvas.RenderTransform = _slide;
        Content = _canvas;

        MouseLeftButtonDown += OnLeftDown;
        MouseLeftButtonUp += OnLeftClick;
        MouseRightButtonUp += OnRightClick;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick(), Dispatcher);
        _timer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;

        // Never take focus and stay out of Alt+Tab.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

        ApplySettings();
        if (_isPrimary) ListenForAttention();
        _lastActive = DateTime.Now.AddSeconds(4);   // show off for a moment before auto-hiding
        _timer.Start();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Re-reads every setting and rebuilds the dock. Called live from the settings window.</summary>
    public void ApplySettings()
    {
        double size = _s.IconSize;
        _gap = size * 0.10;
        _pad = size * 0.16;
        _barHeight = size + 2 * _pad;

        var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Monitor, ref info);
        _screen = info.rcMonitor;

        // Sit on the screen edge when the taskbar is out of the way, otherwise just above it.
        _baseBottom = (_s.HideTaskbar ? info.rcMonitor.Bottom : info.rcWork.Bottom) / _dpi;

        Width = (_screen.Right - _screen.Left) / _dpi;
        Height = BottomGap + _pad + size * Math.Max(_s.Magnification, 1) + size * 0.5 + 44;
        Left = _screen.Left / _dpi;
        Top = _baseBottom - Height;

        Color fill = _s.DarkTheme ? Color.FromRgb(0x1E, 0x1E, 0x22) : Color.FromRgb(0xF4, 0xF4, 0xF8);
        _bar.Background = new SolidColorBrush(fill) { Opacity = _s.BarOpacity };
        _bar.BorderBrush = new SolidColorBrush(Color.FromArgb(_s.DarkTheme ? (byte)0x40 : (byte)0x90, 255, 255, 255));
        _bar.CornerRadius = new CornerRadius(Math.Min(_s.CornerRadius, _barHeight / 2));
        _bar.Height = _barHeight;

        _showStatus = _isPrimary && _s.ShowStatus;
        _status.Background = _bar.Background;
        _status.BorderBrush = _bar.BorderBrush;
        _status.CornerRadius = _bar.CornerRadius;
        _status.Height = _barHeight;
        _status.Padding = new Thickness(size * 0.14, 0, size * 0.14, 0);
        var ink = new SolidColorBrush(_s.DarkTheme ? Color.FromRgb(0xF2, 0xF2, 0xF7) : Color.FromRgb(0x1C, 0x1C, 0x1E));
        foreach (TextBlock cell in _statusPanel.Children)
        {
            cell.Foreground = ink;
            cell.FontSize = cell == _clock ? size * 0.27 : size * 0.36;
            cell.Margin = new Thickness(size * 0.13, 0, size * 0.13, 0);
        }
        if (_showStatus) UpdateStatus(network: true);

        Topmost = _s.Mode != DockMode.BehindWindows;
        Native.SetWindowLongPtr(_hwnd, Native.GWLP_HWNDPARENT,
            _s.Mode == DockMode.BehindWindows ? Native.FindWindow("Progman", null) : IntPtr.Zero);
        if (_s.Mode == DockMode.BehindWindows) SendToBottom();

        SetAppBar(_s.Mode == DockMode.AlwaysVisible && _s.HideTaskbar);

        BuildItems();
        RefreshRunning(force: true);
    }

    // In "always visible" mode, reserve the strip so maximized windows stop above the dock.
    void SetAppBar(bool on)
    {
        var data = new Native.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };
        if (!on)
        {
            if (_appBar) Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
            _appBar = false;
            return;
        }
        if (!_appBar)
        {
            data.uCallbackMessage = 0x8000 + 1;
            Native.SHAppBarMessage(Native.ABM_NEW, ref data);
            _appBar = true;
        }
        int strip = (int)Math.Round((_barHeight + BottomGap * 2) * _dpi);
        data.uEdge = Native.ABE_BOTTOM;
        data.rc = new Native.RECT { Left = _screen.Left, Top = _screen.Bottom - strip, Right = _screen.Right, Bottom = _screen.Bottom };
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref data);
        data.rc.Top = data.rc.Bottom - strip;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref data);
    }

    // ---- attention: Windows tells shell-hook windows when an app flashes for attention ----

    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);

    uint _shellMessage;

    void ListenForAttention()
    {
        _shellMessage = RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int HSHELL_FLASH = 0x8006;
            if ((uint)message == _shellMessage && wParam.ToInt64() == HSHELL_FLASH) _manager.Attention(lParam);
            return IntPtr.Zero;
        });
    }

    /// <summary>Bounces the icon of the app that owns <paramref name="window"/>, at most every few seconds.</summary>
    public void Attention(IntPtr window)
    {
        var item = _items.FirstOrDefault(i => i.Kind == Kind.App && i.Windows.Contains(window));
        if (item == null || (DateTime.Now - item.Bounce).TotalSeconds < 6) return;
        item.Bounce = DateTime.Now;
    }

    void SendToBottom() =>
        Native.SetWindowPos(_hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    // ------------------------------------------------------------------- items

    void BuildItems()
    {
        _items.Clear();
        if (_s.ShowLaunchpad)
            _items.Add(new Item { Kind = Kind.Launchpad, Name = "Launchpad" });
        if (_s.ShowSearch)
            _items.Add(new Item { Kind = Kind.Search, Name = "Search" });
        foreach (var pin in _s.Pinned)
            _items.Add(new Item { Kind = Kind.App, Id = pin.Id, Name = pin.Name, Target = pin.Target, IconPath = pin.Icon, Pinned = true });
        if (_s.ShowTrash)
        {
            _items.Add(new Item { Kind = Kind.Separator });
            _items.Add(new Item { Kind = Kind.Trash, Name = "Recycle Bin" });
        }
    }

    void RefreshRunning(bool force = false)
    {
        var running = AppCatalog.GetRunning();
        bool changed = force;

        foreach (var item in _items.Where(i => i.Kind == Kind.App))
        {
            var match = running.FirstOrDefault(r => r.Entry.Id == item.Id);
            var windows = match?.Windows ?? new List<IntPtr>();
            if (windows.Count > 0 != item.Windows.Count > 0) changed = true;
            item.Windows = windows;
            item.ProcessIds = match?.ProcessIds ?? new HashSet<uint>();
        }

        foreach (var app in running)
        {
            if (_items.Any(i => i.Kind == Kind.App && i.Id == app.Entry.Id)) continue;
            int index = _items.FindIndex(i => i.Kind == Kind.Separator);
            var item = new Item
            {
                Kind = Kind.App, Id = app.Entry.Id, Name = app.Entry.Name,
                Target = app.Entry.Target, IconPath = app.Entry.IconPath, Windows = app.Windows, ProcessIds = app.ProcessIds
            };
            _items.Insert(index < 0 ? _items.Count : index, item);
            changed = true;
        }

        if (_items.RemoveAll(i => i.Kind == Kind.App && !i.Pinned && i.Windows.Count == 0) > 0) changed = true;

        if (changed) RebuildVisuals();
    }

    void RebuildVisuals()
    {
        _canvas.Children.Clear();
        _canvas.Children.Add(_hit);
        _canvas.Children.Add(_bar);
        if (_showStatus)
        {
            _canvas.Children.Add(_hitStatus);
            _canvas.Children.Add(_status);
        }

        var dotBrush = new SolidColorBrush(_s.DarkTheme ? Color.FromArgb(0xE0, 255, 255, 255) : Color.FromArgb(0xC0, 0, 0, 0));
        foreach (var item in _items)
        {
            if (item.Kind == Kind.Separator)
            {
                item.Line = new Rectangle { Width = 1, Fill = dotBrush, Opacity = 0.35, IsHitTestVisible = false };
                _canvas.Children.Add(item.Line);
                continue;
            }

            item.Image = new Image { Source = IconFor(item), Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(item.Image, BitmapScalingMode.HighQuality);
            _canvas.Children.Add(item.Image);

            item.Dot = new Ellipse { Width = 4, Height = 4, Fill = dotBrush, IsHitTestVisible = false };
            _canvas.Children.Add(item.Dot);
        }

        _canvas.Children.Add(_label);
        _dirty = true;
    }

    ImageSource? IconFor(Item item) => item.Kind switch
    {
        Kind.Launchpad => LaunchpadIcon,
        Kind.Search => SearchIcon,
        Kind.Trash => ShellIcons.Load(RecycleBin, 256),
        _ => ShellIcons.Get(item.IconPath, 256) ?? ShellIcons.Get(item.Target, 256)
    };

    static ImageSource MakeLaunchpadIcon()
    {
        Color[] colors =
        {
            Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xFF, 0xBD, 0x2E), Color.FromRgb(0x28, 0xC8, 0x40),
            Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xFF, 0x37, 0x5F),
            Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0x30, 0xD1, 0x58)
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var back = new LinearGradientBrush(Color.FromRgb(0x9A, 0x9A, 0xA2), Color.FromRgb(0x4A, 0x4A, 0x52), 90);
            dc.DrawRoundedRectangle(back, null, new Rect(16, 16, 224, 224), 52, 52);
            for (int i = 0; i < 9; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(colors[i]), null,
                    new Rect(56 + i % 3 * 52, 56 + i / 3 * 52, 40, 40), 10, 10);
        }
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    static ImageSource MakeSearchIcon()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var back = new LinearGradientBrush(Color.FromRgb(0x3A, 0x8D, 0xFF), Color.FromRgb(0x0A, 0x55, 0xD6), 90);
            dc.DrawRoundedRectangle(back, null, new Rect(16, 16, 224, 224), 52, 52);
            var pen = new Pen(Brushes.White, 18) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawEllipse(null, pen, new Point(116, 116), 46, 46);
            dc.DrawLine(pen, new Point(152, 152), new Point(190, 190));
        }
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // -------------------------------------------------------------- animation

    void Tick()
    {
        _tick++;
        Native.GetCursorPos(out var point);
        double cursorX = point.X / _dpi - Left;
        double cursorY = point.Y / _dpi - Top;
        var now = DateTime.Now;

        if (_tick % 60 == 0) EverySecond();

        // Show / hide.
        double hiddenOffset = _barHeight + BottomGap + 12;
        double zoneTop = Height - BottomGap - _barHeight - 4 - (_amount > 0.01 ? _s.IconSize * (_s.Magnification - 1) : 0);
        bool overBar = cursorX >= _barLeft - 10 && cursorX <= _barLeft + _barWidth + 10;
        bool overStatus = _showStatus && cursorX >= _statusLeft - 10 && cursorX <= _statusLeft + _statusWidth + 10;
        bool inZone = _hideOffset < hiddenOffset * 0.5 && (overBar || overStatus)
            && cursorY >= zoneTop && cursorY <= Height + 2;

        bool shown = true;
        if (_s.Mode == DockMode.AutoHide)
        {
            int edge = (int)Math.Round(_baseBottom * _dpi);
            bool atEdge = point.Y >= edge - 3 && point.Y <= edge
                && point.X >= _screen.Left && point.X < _screen.Right;
            if (atEdge && _hideOffset > 0) _fullscreen = IsForegroundFullscreen() || (_s.AvoidWindows && IsCovered());
            if (_tick % 6 == 0) CheckStartMenu();
            if (inZone || _menuOpen || _monitorEmpty || _startOpen || (atEdge && !_fullscreen)) _lastActive = now;
            shown = (now - _lastActive).TotalMilliseconds < 500;
        }
        else if (_s.Mode == DockMode.AlwaysVisible)
        {
            shown = !_fullscreen;
        }

        double offsetTarget = shown ? 0 : hiddenOffset;
        if (Math.Abs(offsetTarget - _hideOffset) > 0.5)
        {
            _hideOffset += (offsetTarget - _hideOffset) * 0.22;
            _slide.Y = _hideOffset;
        }
        else if (_hideOffset != offsetTarget)
        {
            _hideOffset = offsetTarget;
            _slide.Y = _hideOffset;
        }

        // Dragging a pinned icon: it follows the mouse and trades places with neighbours it passes.
        if (_pressed != null)
        {
            if (!_dragging && Math.Abs(cursorX - _pressX) > 8) _dragging = true;
            int index = _items.IndexOf(_pressed);
            if (index < 0)
            {
                _pressed = null;
                _dragging = false;
            }
            else if (_dragging)
            {
                inZone = true;
                _lastActive = now;
                if (index > 0 && CanReorder(_items[index - 1]) && cursorX < Middle(_items[index - 1]))
                    (_items[index], _items[index - 1]) = (_items[index - 1], _items[index]);
                else if (index < _items.Count - 1 && CanReorder(_items[index + 1]) && cursorX > Middle(_items[index + 1]))
                    (_items[index], _items[index + 1]) = (_items[index + 1], _items[index]);
                _dirty = true;
            }
        }

        // Dragging the system controls: they slide along the bottom of the screen with the mouse.
        if (_statusPressed)
        {
            if (!_statusDragging && Math.Abs(cursorX - _statusPressX) > 8) _statusDragging = true;
            if (_statusDragging)
            {
                _statusDragLeft = Math.Clamp(cursorX - _statusGrab, 8, Math.Max(8, Width - _statusWidth - 8));
                inZone = true;
                _lastActive = now;
                _dirty = true;
            }
        }

        // Magnification follows the mouse while it is over the dock.
        if (inZone && !_menuOpen && Math.Abs(cursorX - _mouseX) > 0.01)
        {
            _mouseX = cursorX;
            _dirty = true;
        }
        double amountTarget = _menuOpen ? _amount : inZone ? 1 : 0;
        if (Math.Abs(amountTarget - _amount) > 0.002)
        {
            _amount += (amountTarget - _amount) * 0.28;
            _dirty = true;
        }
        else if (_amount != amountTarget)
        {
            _amount = amountTarget;
            _dirty = true;
        }

        bool bouncing = _items.Any(i => (now - i.Bounce).TotalSeconds < BounceSeconds);
        if (_dirty || bouncing)
        {
            _dirty = false;
            Layout(now);
        }
    }

    void EverySecond()
    {
        RefreshRunning();
        if (_showStatus) UpdateStatus(network: _tick % 300 == 0);

        // Icons can fail to load in the first moments after sign-in; keep trying until they appear.
        foreach (var item in _items)
            if (item.Kind == Kind.App && item.Image is { Source: null } image) image.Source = IconFor(item);
        if (_s.Mode == DockMode.BehindWindows) SendToBottom();
        if (_s.Mode == DockMode.AlwaysVisible) _fullscreen = IsForegroundFullscreen();

        // With nothing open on this monitor there is nothing to get in the way of, so stay up.
        _monitorEmpty = _s.ShowWhenEmpty && !AppCatalog.GetRunning().Any(app => app.Windows.Any(w =>
            !Native.IsIconic(w) && Native.MonitorFromWindow(w, Native.MONITOR_DEFAULTTONEAREST) == Monitor));

        var trash = _items.FirstOrDefault(i => i.Kind == Kind.Trash);
        if (trash?.Image != null)
        {
            var info = new Native.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<Native.SHQUERYRBINFO>() };
            bool full = Native.SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
            if (_trashFull != full)
            {
                _trashFull = full;
                trash.Image.Source = IconFor(trash);
            }
        }
    }

    // Pressing the Windows key opens Start; bring the dock up with it, as the taskbar would be.
    void CheckStartMenu()
    {
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground == _lastForeground) return;
        _lastForeground = foreground;
        bool wasOpen = _startOpen;
        _startOpen = false;
        string name = "";
        Native.GetWindowThreadProcessId(foreground, out uint pid);
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        _startOpen = name is "StartMenuExperienceHost" or "SearchHost";

        // Start sits in front of everything, so a click meant for the dock can land on Start and just
        // close it. Keep the dock up for a moment afterwards so the next click reaches it.
        if (wasOpen && !_startOpen) _lastActive = DateTime.Now.AddSeconds(3);

        // Start and Search open low enough to cover the top of the dock; lift them clear. They are
        // still sliding into place when they take focus, so check a few times.
        if (_startOpen && Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST) == Monitor)
        {
            int tries = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (_, _) =>
            {
                if (++tries >= 6 || Native.GetForegroundWindow() != foreground) timer.Stop();
                foreach (IntPtr panel in ShellPanels()) LiftAboveDock(panel);
            };
            timer.Start();
        }
    }

    // The Start and Search panels showing on this monitor. Start's own window is not the one that
    // holds focus (the search box is), and these windows only turn up in a direct window search.
    IEnumerable<IntPtr> ShellPanels()
    {
        IntPtr window = IntPtr.Zero;
        while ((window = Native.FindWindowEx(IntPtr.Zero, window, "Windows.UI.Core.CoreWindow", null)) != IntPtr.Zero)
        {
            if (!Native.IsWindowVisible(window)) continue;
            if (Native.DwmGetWindowAttribute(window, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (Native.MonitorFromWindow(window, Native.MONITOR_DEFAULTTONEAREST) != Monitor) continue;
            Native.GetWindowThreadProcessId(window, out uint pid);
            string name = "";
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            if (name is "StartMenuExperienceHost" or "SearchHost") yield return window;
        }
    }

    // Moves a window up just far enough that it no longer overlaps the dock.
    void LiftAboveDock(IntPtr window)
    {
        if (!Native.GetWindowRect(window, out var rect)) return;
        int dockTop = _screen.Bottom - (int)((BottomGap + _barHeight + 8) * _dpi);
        // These windows keep whatever position they are given, so place the bottom edge exactly on
        // the dock's top each time rather than only nudging upward. Start's window is as tall as the
        // screen with its panel at the bottom, so its top is allowed to go off-screen.
        if (Math.Abs(rect.Bottom - dockTop) <= 1 || rect.Bottom < _screen.Bottom - (_screen.Bottom - _screen.Top) / 3) return;
        LiftedPanels.TryAdd(window, rect.Top);
        int y = rect.Top - (rect.Bottom - dockTop);
        Native.SetWindowPos(window, IntPtr.Zero, rect.Left, y, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
    }

    // Where each Start/Search window sat before WinDock moved it, so it can be put back on exit.
    static readonly Dictionary<IntPtr, int> LiftedPanels = new();

    static void RestoreLiftedPanels()
    {
        foreach (var (window, top) in LiftedPanels)
            if (Native.GetWindowRect(window, out var rect))
                Native.SetWindowPos(window, IntPtr.Zero, rect.Left, top, 0, 0, 0x0001 | 0x0004 | 0x0010);
        LiftedPanels.Clear();
    }

    // Refreshes the system controls. Each cell's Tag is its hover label. The network scan is the
    // only slow part, so it runs less often.
    void UpdateStatus(bool network)
    {
        string before = _clock.Text + _volume.Text + _battery.Text + _network.Text;

        _clock.Text = DateTime.Now.ToString("t");
        _clock.Tag = DateTime.Now.ToString("D");

        if (SystemStatus.TryGetVolume(out float level, out bool muted))
        {
            _volume.Text = muted ? "" : level < 0.01 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
            _volume.Tag = muted ? "Muted" : $"Volume {level * 100:0}% (scroll to change)";
        }
        else
        {
            _volume.Text = "";
            _volume.Tag = "No sound device";
        }

        if (SystemStatus.TryGetBattery(out int percent, out bool charging))
        {
            _battery.Visibility = Visibility.Visible;
            _battery.Text = ((char)((charging ? 0xEBAB : 0xEBA0) + Math.Clamp(percent / 10, 0, 10))).ToString();
            _battery.Tag = $"Battery {percent}%" + (charging ? ", charging" : "");
        }
        else
        {
            _battery.Visibility = Visibility.Collapsed;
        }

        if (network || _network.Text.Length == 0)
        {
            var kind = SystemStatus.GetNetwork(out string name);
            _network.Text = kind == SystemStatus.Network.WiFi ? "" : kind == SystemStatus.Network.Wired ? "" : "";
            _network.Tag = kind == SystemStatus.Network.None ? "Not connected" : name;
        }

        if (before != _clock.Text + _volume.Text + _battery.Text + _network.Text) _dirty = true;
    }

    // The system-controls cell under a window x coordinate, if any.
    TextBlock? StatusCellAt(double x)
    {
        if (!_showStatus) return null;
        double origin = Canvas.GetLeft(_status);
        foreach (TextBlock cell in _statusPanel.Children)
        {
            if (cell.Visibility != Visibility.Visible) continue;
            double left = origin + cell.TranslatePoint(new Point(0, 0), _status).X - cell.Margin.Left;
            if (x >= left && x < left + cell.ActualWidth + cell.Margin.Left + cell.Margin.Right) return cell;
        }
        return null;
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (StatusCellAt(e.GetPosition(this).X) == _volume)
            Native.PressKey(e.Delta > 0 ? (byte)0xAF : (byte)0xAE);   // the volume up / down keys
    }

    void OnStatusClick(TextBlock cell)
    {
        if (cell == _power)
        {
            var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
            // Ask the taskbar to open Start (WM_SYSCOMMAND / SC_TASKLIST); a simulated Windows key is unreliable.
            Add(menu, "Start Menu", () => Native.PostMessage(Native.FindWindow("Shell_TrayWnd", null), 0x0112, new IntPtr(0xF130), IntPtr.Zero));
            menu.Items.Add(new Separator());
            Add(menu, "Lock", SystemStatus.Lock);
            Add(menu, "Sleep", SystemStatus.Sleep);
            Add(menu, "Sign Out", SystemStatus.SignOut);
            Add(menu, "Restart", SystemStatus.Restart);
            Add(menu, "Shut Down", SystemStatus.ShutDown);
            Show(menu);
        }
        else if (cell == _clock)
        {
            Native.PressWinKey(0x4E);   // Win+N: notifications and calendar
        }
        else
        {
            Native.PressWinKey(0x41);   // Win+A: quick settings (Wi-Fi, Bluetooth, sound, brightness)
        }
    }

    // A true fullscreen app (game, video) exactly covers the screen; a maximized window overhangs it.
    bool IsForegroundFullscreen()
    {
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd) return false;
        Native.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        if (Native.ClassName(foreground) is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Native.GetWindowRect(foreground, out var rect)) return false;
        return rect.Left == _screen.Left && rect.Top == _screen.Top
            && rect.Right == _screen.Right && rect.Bottom == _screen.Bottom;
    }

    // True when any open window overlaps where the bar would appear (a windowed game, a maximized app).
    bool IsCovered()
    {
        double left = (Left + _barLeft) * _dpi, right = left + _barWidth * _dpi;
        double bottom = _baseBottom * _dpi, top = bottom - (BottomGap + _barHeight) * _dpi;
        return AppCatalog.GetRunning().Any(app => app.Windows.Any(w =>
            !Native.IsIconic(w) && Native.GetWindowRect(w, out var r)
            && r.Left < right && r.Right > left && r.Top < bottom && r.Bottom > top));
    }

    void Layout(DateTime now)
    {
        double size = _s.IconSize;
        double range = size * 2.6;

        double baseTotal = _items.Sum(i => (i.Kind == Kind.Separator ? SeparatorWidth : size) + _gap);
        double baseLeft = (Width - baseTotal) / 2;
        double u = _mouseX - baseLeft;   // mouse position in unmagnified dock coordinates

        // Pass 1: sizes, and where the point under the mouse lands once icons have grown.
        double basePos = 0, total = 0, anchor = double.NaN;
        foreach (var item in _items)
        {
            double baseWidth = (item.Kind == Kind.Separator ? SeparatorWidth : size) + _gap;
            double scale = 1;
            if (item.Kind != Kind.Separator && _amount > 0)
            {
                double distance = Math.Abs(u - (basePos + baseWidth / 2));
                if (distance < range)
                {
                    double falloff = Math.Cos(distance / range * Math.PI / 2);
                    scale = 1 + (_s.Magnification - 1) * _amount * falloff * falloff;
                }
            }
            item.Size = item.Kind == Kind.Separator ? SeparatorWidth : size * scale;
            double width = item.Size + _gap;
            if (double.IsNaN(anchor) && u >= basePos && u < basePos + baseWidth)
                anchor = total + (u - basePos) / baseWidth * width;
            basePos += baseWidth;
            total += width;
        }
        if (double.IsNaN(anchor)) anchor = u < 0 ? u : total + (u - baseTotal);

        // Pass 2: keep that point under the mouse so the dock doesn't swim.
        double x = _mouseX - anchor;
        double floor = Height - BottomGap - _pad;

        _barLeft = x - _pad * 0.6;
        _barWidth = total + _pad * 1.2;
        _bar.Width = _barWidth;
        Canvas.SetLeft(_bar, _barLeft);
        Canvas.SetTop(_bar, Height - BottomGap - _barHeight);

        _hovered = null;
        foreach (var item in _items)
        {
            item.Left = x;
            if (item.Kind == Kind.Separator)
            {
                item.Line!.Height = size * 0.8;
                Canvas.SetLeft(item.Line, x + (item.Size + _gap) / 2);
                Canvas.SetTop(item.Line, floor - size * 0.9);
            }
            else
            {
                double lift = 0;
                double t = (now - item.Bounce).TotalSeconds;
                if (t < BounceSeconds)
                    lift = Math.Abs(Math.Sin(t * Math.PI * 2.2)) * size * 0.45 * (1 - t / BounceSeconds);

                bool dragged = _dragging && item == _pressed;
                if (dragged) lift += size * 0.2;
                item.Image!.Opacity = dragged ? 0.8 : 1;
                item.Image.Width = item.Image.Height = item.Size;
                Canvas.SetLeft(item.Image, x + _gap / 2);
                Canvas.SetTop(item.Image, floor - item.Size - lift);

                item.Dot!.Visibility = item.Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden;
                Canvas.SetLeft(item.Dot, x + (item.Size + _gap) / 2 - 2);
                Canvas.SetTop(item.Dot, floor + (_pad - 4) / 2);

                if (_amount > 0.5 && _mouseX >= x && _mouseX < x + item.Size + _gap) _hovered = item;
            }
            x += item.Size + _gap;
        }

        double zoneHeight = BottomGap + _barHeight + 4 + (_amount > 0.01 ? size * (_s.Magnification - 1) : 0);

        if (_showStatus)
        {
            _status.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _statusWidth = _status.DesiredSize.Width;
            double afterDock = _barLeft + _barWidth + 10, beforeDock = _barLeft - 10 - _statusWidth;
            if (_statusDragging)
            {
                _statusLeft = _statusDragLeft;
            }
            else if (_s.StatusAnchor == StatusAnchor.Free)
            {
                // Wherever it was dropped, but never under the dock (which widens as it magnifies).
                _statusLeft = Math.Clamp(_s.StatusX, 8, Math.Max(8, Width - _statusWidth - 8));
                if (_statusLeft < afterDock && _statusLeft > beforeDock)
                    _statusLeft = _statusLeft + _statusWidth / 2 > _barLeft + _barWidth / 2 ? afterDock : beforeDock;
            }
            else
            {
                _statusLeft = _s.StatusAnchor == StatusAnchor.DockLeft ? beforeDock : afterDock;
            }
            Canvas.SetLeft(_status, _statusLeft);
            Canvas.SetTop(_status, Height - BottomGap - _barHeight);

            _hitStatus.Width = _statusWidth + 20;
            _hitStatus.Height = zoneHeight;
            Canvas.SetLeft(_hitStatus, _statusLeft - 10);
            Canvas.SetTop(_hitStatus, Height - zoneHeight);
        }

        _hit.Width = _barWidth + 20;
        _hit.Height = zoneHeight;
        Canvas.SetLeft(_hit, _barLeft - 10);
        Canvas.SetTop(_hit, Height - zoneHeight);

        // Name label: over the hovered icon, or over the hovered system control.
        string? text = _hovered?.Name;
        double center = _hovered == null ? 0 : _hovered.Left + (_hovered.Size + _gap) / 2;
        double above = _hovered?.Size ?? size;
        if (_hovered == null && _amount > 0.5 && StatusCellAt(_mouseX) is { Tag: string tip } cell)
        {
            text = tip;
            center = Canvas.GetLeft(_status) + cell.TranslatePoint(new Point(cell.ActualWidth / 2, 0), _status).X;
        }

        if (text != null && !_menuOpen && !_dragging && !_statusDragging)
        {
            _labelText.Text = text;
            _label.Visibility = Visibility.Visible;
            _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = Math.Clamp(center - _label.DesiredSize.Width / 2, 8, Math.Max(8, Width - _label.DesiredSize.Width - 8));
            Canvas.SetLeft(_label, left);
            Canvas.SetTop(_label, floor - above - _label.DesiredSize.Height - 10);
        }
        else
        {
            _label.Visibility = Visibility.Collapsed;
        }
    }

    Item? ItemAt(double x) =>
        _items.FirstOrDefault(i => i.Kind != Kind.Separator && x >= i.Left && x < i.Left + i.Size + _gap);

    // ------------------------------------------------------------------ clicks

    // Dropped next to either end of the dock, the system controls snap on and travel with it;
    // anywhere else they stay where they were put.
    void DropStatus()
    {
        double left = _statusDragLeft;
        double afterDock = _barLeft + _barWidth + 10, beforeDock = _barLeft - 10 - _statusWidth;
        if (Math.Abs(left - afterDock) < 40) _s.StatusAnchor = StatusAnchor.DockRight;
        else if (Math.Abs(left - beforeDock) < 40) _s.StatusAnchor = StatusAnchor.DockLeft;
        else if (left < afterDock && left > beforeDock)
            _s.StatusAnchor = left + _statusWidth / 2 > _barLeft + _barWidth / 2 ? StatusAnchor.DockRight : StatusAnchor.DockLeft;
        else
        {
            _s.StatusAnchor = StatusAnchor.Free;
            _s.StatusX = left;
        }
        _s.Save();
    }

    static bool CanReorder(Item item) => item.Kind == Kind.App && item.Pinned;

    double Middle(Item item) => item.Left + (item.Size + _gap) / 2;

    void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        double x = e.GetPosition(this).X;
        var item = ItemAt(x);
        if (item == null)
        {
            if (_showStatus && x >= _statusLeft && x <= _statusLeft + _statusWidth)
            {
                _statusPressed = true;
                _statusPressX = x;
                _statusGrab = x - _statusLeft;
                CaptureMouse();
            }
            return;
        }
        if (!CanReorder(item)) return;
        _pressed = item;
        _pressX = x;
        CaptureMouse();
    }

    void OnLeftClick(object sender, MouseButtonEventArgs e)
    {
        bool wasDrag = _dragging, wasStatusDrag = _statusDragging;
        _pressed = null;
        _dragging = _statusPressed = _statusDragging = false;
        ReleaseMouseCapture();
        _dirty = true;
        if (wasStatusDrag)
        {
            DropStatus();
            return;
        }
        if (wasDrag)
        {
            _manager.Reorder(_items.Where(CanReorder).Select(i => i.Id).ToList());
            return;
        }

        var item = ItemAt(e.GetPosition(this).X);
        if (item == null)
        {
            if (StatusCellAt(e.GetPosition(this).X) is { } cell) OnStatusClick(cell);
            return;
        }

        switch (item.Kind)
        {
            case Kind.Launchpad:
                _manager.ToggleLaunchpad(new Rect(Left, _screen.Top / _dpi, Width, (_screen.Bottom - _screen.Top) / _dpi));
                break;
            case Kind.Search:
                Native.PressWinKey(0x53);   // Win+S opens Windows Search
                BringSearchHere();
                break;
            case Kind.Trash:
                AppCatalog.Launch("shell:RecycleBinFolder");
                break;
            case Kind.App when item.Windows.Count == 0:
                item.Bounce = DateTime.Now;
                AppCatalog.Launch(item.Target);
                break;
            case Kind.App:
                IntPtr foreground = Native.GetForegroundWindow();
                if (!item.Windows.Contains(foreground)) BringToFront(item.Windows[0]);
                else if (item.Windows.Count > 1) BringToFront(item.Windows[^1]);   // cycle through its windows
                else Native.ShowWindow(foreground, Native.SW_MINIMIZE);
                break;
        }
    }

    // Windows always opens Search on the main display; once it appears, move it to this dock's monitor.
    void BringSearchHere()
    {
        int tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            if (++tries > 40) { timer.Stop(); return; }
            IntPtr search = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(search, out uint pid);
            try
            {
                if (System.Diagnostics.Process.GetProcessById((int)pid).ProcessName != "SearchHost") return;
            }
            catch { return; }

            timer.Stop();
            if (Native.MonitorFromWindow(search, Native.MONITOR_DEFAULTTONEAREST) == Monitor)
            {
                LiftAboveDock(search);
                return;
            }
            if (!Native.GetWindowRect(search, out var rect)) return;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            int x = Math.Max(_screen.Left, _screen.Left + (_screen.Right - _screen.Left - width) / 2);
            int y = Math.Max(_screen.Top, _screen.Bottom - height - (int)((BottomGap + _barHeight + 8) * _dpi));
            Native.SetWindowPos(search, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
        };
        timer.Start();
    }

    static void BringToFront(IntPtr hWnd)
    {
        if (Native.IsIconic(hWnd)) Native.ShowWindow(hWnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hWnd);
    }

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.GetPosition(this).X);
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };

        if (item?.Kind == Kind.App)
        {
            Add(menu, item.Windows.Count > 0 ? "New Window" : "Open", () =>
            {
                item.Bounce = DateTime.Now;
                AppCatalog.Launch(item.Target);
            });
            Add(menu, item.Pinned ? "Unpin from Dock" : "Pin to Dock", () =>
            {
                if (item.Pinned) _manager.Unpin(item.Id);
                else _manager.Pin(new AppEntry(item.Id, item.Name, item.Target, item.IconPath));
            });
            if (item.Windows.Count > 0)
                Add(menu, "Quit", () =>
                {
                    foreach (var window in item.Windows)
                        Native.PostMessage(window, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                });
            if (item.Windows.Count > 0)
                Add(menu, "End Task", () =>
                {
                    // Force-close, for an app that is frozen; unsaved work in it is lost.
                    foreach (uint pid in item.ProcessIds)
                    {
                        try { System.Diagnostics.Process.GetProcessById((int)pid).Kill(); } catch { }
                    }
                });
            menu.Items.Add(new Separator());
        }
        else if (item?.Kind == Kind.Trash)
        {
            Add(menu, "Open", () => AppCatalog.Launch("shell:RecycleBinFolder"));
            Add(menu, "Empty Recycle Bin...", () => Native.SHEmptyRecycleBin(_hwnd, null, 0));   // Windows asks to confirm
            menu.Items.Add(new Separator());
        }

        Add(menu, "Dock Settings...", _manager.OpenSettings);
        Add(menu, _s.HideTaskbar ? "Show Windows Taskbar" : "Hide Windows Taskbar", _manager.ToggleTaskbar);
        Add(menu, "Turn Off WinDock", () => Application.Current.Shutdown());

        Show(menu);
    }

    void Show(ContextMenu menu)
    {
        _menuOpen = true;
        Menus.Show(this, _hwnd, menu, () => { _menuOpen = false; _dirty = true; });
    }

    static void Add(ContextMenu menu, string header, Action action)
    {
        var entry = new MenuItem { Header = header };
        entry.Click += (_, _) => action();
        menu.Items.Add(entry);
    }

    /// <summary>Stops this dock and releases any screen space it reserved.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        SetAppBar(false);
        RestoreLiftedPanels();
    }
}
