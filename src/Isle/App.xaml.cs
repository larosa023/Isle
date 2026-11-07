using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Isle.Controls;
using Isle.Core;
using Isle.Services;

namespace Isle;

public partial class App : Application
{
    public static App Instance => (App)Current;

    public AppSettings Settings { get; private set; } = new();
    public ActivityHub Hub { get; private set; } = null!;
    public TimerService Timers { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;

    readonly List<IModule> _modules = new();
    MainWindow? _island;
    TrayIcon? _tray;
    SettingsWindow? _settingsWindow;
    Mutex? _mutex;
    bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool justUpdated = e.Args.Contains("--updated");
        _mutex = new Mutex(false, @"Local\Isle.SingleInstance.larosa023");
        try
        {
            // After an update the old process may still be shutting down; give it a moment.
            _ownsMutex = _mutex.WaitOne(justUpdated ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) => { Log.Error("ui", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("fatal", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };

        Log.Info($"Isle {AppInfo.VersionString} starting");
        Settings = AppSettings.Load();
        ThemeManager.Apply(Settings);
        Visualizer.AnimationsEnabled = Settings.AnimatedVisualizer;
        if (Settings.StartWithWindows) StartupRegistration.Set(true); // keep the path fresh if the exe moved
        UpdateService.CleanupOldBinaries();

        Hub = new ActivityHub();
        Timers = new TimerService(Hub);
        Updates = new UpdateService(Hub, Settings);

        _island = new MainWindow(Hub, Settings);
        _island.UserHiddenChanged += hidden => _tray?.SetHidden(hidden);
        _island.Show();

        _tray = new TrayIcon(this);

        StartModules();
        Updates.Start();

        if (justUpdated)
        {
            var a = new Activity("isle:updated", ActivityKind.Update)
            {
                Title = $"Updated to Isle {AppInfo.VersionString}",
                Subtitle = "You're on the latest version.",
                Glyph = Glyphs.Check,
                Priority = 45,
            };
            a.SetTtl(TimeSpan.FromSeconds(8));
            Hub.Add(a);
            Dispatcher.InvokeAsync(() => Hub.Peek(a), DispatcherPriority.ApplicationIdle);
        }
    }

    void StartModules()
    {
        var s = Settings;
        if (s.MediaEnabled) _modules.Add(new MediaService(Hub, s));
        if (s.DownloadsEnabled) _modules.Add(new DownloadService(Hub, s));
        if (s.CallsEnabled) _modules.Add(new CallService(Hub, s));
        if (s.NotificationsEnabled) _modules.Add(new NotificationService(Hub, s));
        if (s.SportsEnabled) _modules.Add(new SportsService(Hub, s));
        if (s.ApiEnabled) _modules.Add(new IntegrationServer(Hub, s, Timers));

        foreach (var m in _modules)
        {
            try { m.Start(); }
            catch (Exception ex) { Log.Error("start " + m.GetType().Name, ex); }
        }
    }

    void StopModules()
    {
        foreach (var m in _modules)
        {
            try { m.Dispose(); }
            catch (Exception ex) { Log.Error("stop " + m.GetType().Name, ex); }
        }
        _modules.Clear();
    }

    public void ApplySettings(AppSettings s)
    {
        bool startupChanged = s.StartWithWindows != Settings.StartWithWindows;
        Settings = s;
        s.Save();

        ThemeManager.Apply(s);
        foreach (var a in Hub.Items) a.RefreshTheme();
        _island?.ApplySettings(s);
        Updates.UpdateSettings(s);

        StopModules();
        StartModules();

        if (startupChanged) StartupRegistration.Set(s.StartWithWindows);
    }

    public void ShowSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ToggleIslandHidden()
    {
        if (_island == null) return;
        _island.SetUserHidden(!_island.IsUserHidden);
    }

    public void Quit() => Shutdown();

    protected override void OnExit(ExitEventArgs e)
    {
        StopModules();
        _tray?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
