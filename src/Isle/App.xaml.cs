using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        ThemeMode = ThemeMode.System; // Windows 11 (Fluent) menus and dialogs, following light/dark
        Settings = AppSettings.Load();
        AppSettings.Current = Settings;
        ThemeManager.Apply(Settings);
        Visualizer.AnimationsEnabled = Settings.AnimatedVisualizer;
        if (Settings.StartWithWindows) StartupRegistration.Set(true); // keep the path fresh if the exe moved
        UpdateService.CleanupOldBinaries();

        Hub = new ActivityHub
        {
            Filter = a => Settings.Kind(a.Kind).Enabled && AppRegistry.IsEnabled(a.AppKey),
            RankOf = a => a.Priority + Settings.Kind(a.Kind).Boost + AppRegistry.Boost(a.AppKey),
            PeekAllowed = ShouldPeek,
        };
        AppRegistry.Changed += () => Hub.Reorder();
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

    bool ShouldPeek(Activity a)
    {
        var s = Settings;
        if (!s.PeekOnNewActivity || !s.Kind(a.Kind).Peek || !AppRegistry.CanPeek(a.AppKey)) return false;
        bool urgent = a.Kind == ActivityKind.Call || (a.Kind == ActivityKind.Timer && a.Priority >= 95);
        bool quiet = s.DoNotDisturb || s.InQuietHours(DateTime.Now);
        return !quiet || (urgent && s.AlertUrgentWhenQuiet);
    }

    void StartModules()
    {
        var s = Settings;
        if (s.Kind("media").Enabled) _modules.Add(new MediaService(Hub, s));
        if (s.Kind("media").Enabled && s.AmpliteEnabled) _modules.Add(new AmpliteService(Hub));
        if (s.Kind("download").Enabled) _modules.Add(new DownloadService(Hub, s));
        if (s.Kind("call").Enabled) _modules.Add(new CallService(Hub, s));
        if (s.Kind("notification").Enabled) _modules.Add(new NotificationService(Hub, s));
        if (s.Kind("sports").Enabled) _modules.Add(new SportsService(Hub, s));
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

    /// <summary>Applies settings live. Modules only restart when something they depend on changed.</summary>
    public void ApplySettings(AppSettings s)
    {
        var old = Settings;
        bool restart = old.ModuleSignature() != s.ModuleSignature();
        bool startupChanged = s.StartWithWindows != old.StartWithWindows;
        Settings = s;
        AppSettings.Current = s;
        s.Save();

        ThemeManager.Apply(s);
        foreach (var a in Hub.All) a.RefreshTheme();
        Visualizer.AnimationsEnabled = s.AnimatedVisualizer;
        _island?.ApplySettings(s);
        Updates.UpdateSettings(s);

        if (restart)
        {
            StopModules();
            StartModules();
        }
        Hub.Reorder();

        if (startupChanged) StartupRegistration.Set(s.StartWithWindows);
        SettingsApplied?.Invoke();
    }

    public event Action? SettingsApplied;

    /// <summary>Puts sample cards on the island so settings changes can be previewed.</summary>
    public void ShowDemo(string which)
    {
        switch (which)
        {
            case "timer":
                Timers.Start(TimeSpan.FromSeconds(15), "Demo timer");
                break;
            case "notification":
            {
                var n = new Activity("demo:notification", ActivityKind.Custom)
                {
                    Source = "Isle",
                    Title = "This is a notification",
                    Subtitle = "It peeks for a few seconds, then tucks away.",
                    Glyph = Glyphs.Bell,
                    Priority = 90,
                };
                n.SetTtl(TimeSpan.FromSeconds(Math.Max(4, Settings.PeekSeconds + 1.5)));
                Hub.Add(n);
                Hub.ForcePeek(n);
                break;
            }
            default:
            {
                var m = new Activity("demo:media", ActivityKind.Custom)
                {
                    Source = "Preview",
                    Title = "Midnight City",
                    Subtitle = "M83",
                    Glyph = Glyphs.Music,
                    Accent = System.Windows.Media.Color.FromRgb(0xBF, 0x5A, 0xF2),
                    Progress = 0.42,
                    ProgressLeft = "1:46",
                    ProgressRight = "-2:17",
                    Trailing = "Preview",
                    Priority = 89,
                };
                m.Actions = new[]
                {
                    new IslandAction(Glyphs.Previous, null, () => { }),
                    new IslandAction(Glyphs.Pause, null, () => { }, primary: true),
                    new IslandAction(Glyphs.Next, null, () => { }),
                    new IslandAction(Glyphs.Close, "Close preview", () => Hub.Remove("demo:media")),
                };
                m.SetTtl(TimeSpan.FromSeconds(30));
                Hub.Add(m);
                Hub.ForcePeek(m);
                break;
            }
        }
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

    public (bool open, bool hide) HotkeysActive => _island?.HotkeysActive ?? (false, false);

    /// <summary>While Settings records a new shortcut, the current ones must not fire.</summary>
    public void SuspendHotkeys(bool suspend) => _island?.SuspendHotkeys(suspend);

    /// <summary>The menu shared by the tray icon and right-clicking the island.</summary>
    public ContextMenu BuildMenu(Activity? current, bool includeHeader = false)
    {
        var menu = new ContextMenu();
        if (includeHeader)
        {
            menu.Items.Add(new MenuItem { Header = $"Isle {AppInfo.VersionString}", IsEnabled = false });
            menu.Items.Add(new Separator());
        }

        var timers = new MenuItem { Header = "New timer" };
        foreach (var minutes in new[] { 1, 3, 5, 10, 15, 25, 45, 60 })
        {
            var m = minutes;
            timers.Items.Add(Item(m == 60 ? "1 hour" : $"{m} min", () => Timers.Start(TimeSpan.FromMinutes(m))));
        }
        menu.Items.Add(timers);

        if (current is { AppKey: string key })
        {
            var name = AppRegistry.Get(key)?.Name ?? current.Source ?? "this app";
            menu.Items.Add(Item($"Don't show {name}", () =>
            {
                var rule = AppRegistry.Get(key) ?? AppRegistry.Seen(key, name, key.Split(':')[0]);
                rule.Enabled = false;
                AppRegistry.Update(rule);
            }));
        }

        var dnd = new MenuItem { Header = "Do not disturb", IsCheckable = true, IsChecked = Settings.DoNotDisturb };
        dnd.Click += (_, _) =>
        {
            var s = Settings.Clone();
            s.DoNotDisturb = !s.DoNotDisturb;
            ApplySettings(s);
        };
        menu.Items.Add(dnd);

        bool hidden = _island?.IsUserHidden == true;
        menu.Items.Add(Item(hidden ? "Show the island" : "Hide the island", ToggleIslandHidden, Shortcut(Settings.HideHotkey)));
        if (!hidden) menu.Items.Add(Item("Hide for 1 hour", () => _island?.SetUserHidden(true, TimeSpan.FromHours(1))));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", ShowSettings));
        menu.Items.Add(Item("Check for updates", () => _ = Updates.CheckAsync(manual: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Isle", Quit));
        return menu;

        static MenuItem Item(string header, Action action, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => action();
            return mi;
        }

        static string? Shortcut(string? hotkey) => string.IsNullOrWhiteSpace(hotkey) ? null : hotkey;
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
        AppRegistry.SaveNow();
        _tray?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
