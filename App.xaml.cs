using System.Windows;

namespace WinDock;

public partial class App : Application
{
    Mutex? _mutex;
    DockManager? _manager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Write($"start: {Environment.ProcessPath} {string.Join(' ', e.Args)}");
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Log.Write("FATAL: " + error.ExceptionObject);

        try
        {
            Start(e);
        }
        catch (Exception ex)
        {
            Log.Write("startup failed: " + ex);
            throw;
        }
    }

    void Start(StartupEventArgs e)
    {
        bool quiet = e.Args.Contains("--quiet");
        if (e.Args.Contains("--uninstall"))
        {
            Installer.Uninstall(quiet, purge: e.Args.Contains("--purge"));
            Shutdown();
            return;
        }
        if (Installer.IsSetup)
        {
            Installer.Install(quiet);
            Shutdown();
            return;
        }

        // "WinDock.exe --welcome" just previews the first-run window.
        if (e.Args.Contains("--welcome"))
        {
            new WelcomeWindow().ShowDialog();
            Shutdown();
            return;
        }

        // Launching WinDock while it is already running opens its settings instead.
        var openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "WinDock_OpenSettings");
        _mutex = new Mutex(true, "WinDock_SingleInstance", out bool created);
        if (!created)
        {
            if (e.Args.Contains(WindowsIntegration.StartupArgument))
            {
                Log.Write("already running (second sign-in entry): exiting quietly");
                Shutdown();
                return;
            }
            Log.Write("already running: asking that copy to open settings, then exiting");
            Native.AllowSetForegroundWindow(-1);   // let the running copy bring its window to the front
            openSettings.Set();
            Shutdown();
            return;
        }

        AppCatalog.LoadInstalledAsync();

        var settings = Settings.Load();
        if (!settings.Seeded)
        {
            // First run: explain what is about to happen before touching the taskbar.
            var welcome = new WelcomeWindow();
            welcome.ShowDialog();
            if (!welcome.Accepted)
            {
                Log.Write("welcome window closed without Get started: exiting");
                Shutdown();
                return;
            }
            settings.DockEnabled = welcome.DockChosen;
            settings.IconButton = welcome.IconButtonChosen;
            settings.HideTaskbar = welcome.HideTaskbarChosen;
            if (welcome.StartWithWindowsChosen) WindowsIntegration.Enabled = true;

            // Start the dock with everything pinned to the taskbar (Store apps need the installed list).
            AppCatalog.InstalledReady.Wait(8000);
            settings.Pinned = AppCatalog.SeedFromTaskbar();
            settings.Seeded = true;
            settings.Save();
        }

        // Keep the shortcut and the sign-in entry pointing at this copy, in case it was moved or reinstalled.
        WindowsIntegration.EnsureStartMenuShortcut();
        if (WindowsIntegration.Enabled) WindowsIntegration.Enabled = true;

        _manager = new DockManager(settings);

        // An error in one click or one timer tick shouldn't take the whole dock down: note it and carry on.
        DispatcherUnhandledException += (_, error) =>
        {
            Log.Write("error (continuing): " + error.Exception);
            error.Handled = true;
        };
        // Whatever happens, give the real taskbar back.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _manager.Cleanup();
        SessionEnding += (_, reason) =>
        {
            Log.Write("Windows session ending: " + reason.ReasonSessionEnding);
            _manager.Cleanup();
        };

        _manager.Apply();
        Log.Write($"running: dock={settings.DockEnabled} iconButton={settings.IconButton} pins={settings.Pinned.Count} seededFromFile={settings.Seeded}");

        new Thread(() =>
        {
            while (openSettings.WaitOne())
                Dispatcher.BeginInvoke(_manager.OpenSettings);
        }) { IsBackground = true }.Start();

        // The installer and uninstaller signal this to make WinDock exit cleanly.
        var quit = Installer.CreateQuitEvent();
        new Thread(() =>
        {
            quit.WaitOne();
            Log.Write("asked to quit by the installer/uninstaller");
            Dispatcher.BeginInvoke(() => Shutdown());
        }) { IsBackground = true }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write("exit, code " + e.ApplicationExitCode);
        _manager?.Cleanup();
        base.OnExit(e);
    }
}
