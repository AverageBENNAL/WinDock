using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace WinDock;

public class LaunchpadEntry : INotifyPropertyChanged
{
    ImageSource? _icon;

    public required AppEntry App { get; init; }
    public string Name => App.Name;

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Full-screen grid of every installed app, with search.</summary>
public partial class LaunchpadWindow : Window
{
    readonly DockManager _manager;
    readonly ObservableCollection<LaunchpadEntry> _entries = new();
    bool _loaded;

    public LaunchpadWindow(DockManager manager)
    {
        _manager = manager;
        InitializeComponent();

        var view = CollectionViewSource.GetDefaultView(_entries);
        view.Filter = o => Search.Text.Length == 0
            || ((LaunchpadEntry)o).Name.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase);
        Apps.ItemsSource = view;

        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        MouseLeftButtonDown += (_, _) => Hide();   // a click on empty space; buttons and the search box swallow theirs

        AppCatalog.InstalledLoaded += () => Dispatcher.BeginInvoke(Populate);
    }

    /// <summary>Opens over the given screen (the one whose dock was clicked).</summary>
    public void Open(Rect screen)
    {
        Left = screen.Left;
        Top = screen.Top;
        Width = screen.Width;
        Height = screen.Height;
        Search.Text = "";
        Populate();
        Show();
        Activate();
        Search.Focus();
    }

    void Populate()
    {
        if (_loaded || AppCatalog.Installed.Count == 0) return;
        _loaded = true;
        foreach (var app in AppCatalog.Installed)
        {
            var entry = new LaunchpadEntry { App = app };
            _entries.Add(entry);
            StaWorker.Post(() =>
            {
                var icon = ShellIcons.Load(app.IconPath, 96);
                Dispatcher.BeginInvoke(() => entry.Icon = icon);
            });
        }
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CollectionViewSource.GetDefaultView(_entries).Refresh();
    }

    void App_Click(object sender, RoutedEventArgs e)
    {
        var entry = (LaunchpadEntry)((Button)sender).DataContext;
        Hide();
        AppCatalog.Launch(entry.App.Target);
    }

    void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        var entry = (LaunchpadEntry)((Button)sender).DataContext;
        var menu = new ContextMenu();
        var keep = new MenuItem { Header = "Pin to Dock" };
        keep.Click += (_, _) => _manager.Pin(entry.App);
        menu.Items.Add(keep);
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
