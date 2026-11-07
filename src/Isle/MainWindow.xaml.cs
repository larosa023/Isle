using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Isle.Controls;
using Isle.Core;
using Microsoft.Win32;

namespace Isle;

/// <summary>
/// The island. A fixed-size, transparent, click-through, topmost tool window; only the pill
/// itself is hit-testable. Size changes are spring animations on the pill, never on the window,
/// which keeps resizing smooth and flicker-free.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly DependencyProperty PillRadiusProperty = DependencyProperty.Register(
        nameof(PillRadius), typeof(double), typeof(MainWindow),
        new PropertyMetadata(15.0, (d, e) => ((MainWindow)d).Pill.CornerRadius = new CornerRadius(Math.Max(0, (double)e.NewValue))));

    public double PillRadius
    {
        get => (double)GetValue(PillRadiusProperty);
        set => SetValue(PillRadiusProperty, value);
    }

    const double BaseWidth = 480, BaseHeight = 230;
    const double ExpandedWidth = 420, CompactWidth = 300, CompactHeight = 36, IdleWidth = 120, IdleHeight = 30;

    readonly ActivityHub _hub;
    readonly DispatcherTimer _hoverTimer, _leaveTimer, _peekTimer, _guardTimer, _hideTimer;
    AppSettings _s;
    IntPtr _hwnd;
    Activity? _bound;
    bool _hover, _sticky, _peeking, _expanded, _fullscreenHidden, _userHidden;
    (double w, double h, double r, double o) _target = (-1, -1, -1, -1);

    public event Action<bool>? UserHiddenChanged;
    public bool IsUserHidden => _userHidden;

    public MainWindow(ActivityHub hub, AppSettings settings)
    {
        InitializeComponent();
        _hub = hub;
        _s = settings;

        _hoverTimer = Timer(120, t => { t.Stop(); if (_hover) Expand(); });
        _leaveTimer = Timer(380, t => { t.Stop(); if (!_hover && !_sticky && !_peeking) Collapse(); });
        _peekTimer = Timer(3500, t =>
        {
            t.Stop();
            _peeking = false;
            if (!_hover && !_sticky) Collapse();
        });
        _guardTimer = Timer(2000, _ => Guard());
        _hideTimer = Timer(3_600_000, t => { t.Stop(); SetUserHidden(false); });

        _hub.Changed += Refresh;
        _hub.PeekRequested += OnPeek;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE; // no Alt+Tab, never steals focus
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
        Loaded += (_, _) =>
        {
            ApplySettings(_s);
            _guardTimer.Start();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
    }

    DispatcherTimer Timer(int ms, Action<DispatcherTimer> tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => tick(t);
        return t;
    }

    // ───────────────────────────── settings & placement ─────────────────────────────

    public void ApplySettings(AppSettings s)
    {
        _s = s;
        double scale = Math.Clamp(s.Scale, 0.7, 1.6);
        Root.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
        Pill.Margin = new Thickness(0, Math.Clamp(s.TopOffset, 0, 80), 0, 0);
        Visualizer.AnimationsEnabled = s.AnimatedVisualizer;
        Position();
        _target = (-1, -1, -1, -1);
        Refresh();
    }

    void Position()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Ui.Post(Position);

    // ───────────────────────────── state machine ─────────────────────────────

    void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        Refresh();
    }

    void Collapse()
    {
        _peekTimer.Stop();
        _expanded = false;
        _sticky = false;
        _peeking = false;
        if (_hub.Pinned != null) _hub.Pinned = null; // raises Changed → Refresh
        Refresh();
    }

    void OnPeek(Activity a)
    {
        if (!_s.PeekOnNewActivity || _fullscreenHidden || _userHidden) return;
        if (_hover || _sticky || (_expanded && !_peeking)) return; // the user is already looking

        _peeking = true;
        _expanded = true;
        _hub.Pinned = a;
        Refresh();
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_s.PeekSeconds, 1, 15));
        _peekTimer.Start();
    }

    void Refresh()
    {
        var a = _hub.Current;
        Bind(a);

        int count = _hub.Items.Count;
        PositionText.Text = count > 1 ? $"{_hub.CurrentIndex + 1} of {count}" : "";

        double w, h, r, o = 1;
        if (_expanded)
        {
            var layer = a == null ? IdleLayer : ExpandedLayer;
            if (a == null) UpdateClock();
            w = ExpandedWidth;
            h = MeasureLayer(layer, w);
            r = Math.Min(30, h / 2);
            ShowOnly(layer, delayIn: true);
        }
        else if (a == null)
        {
            w = IdleWidth;
            h = IdleHeight;
            r = IdleHeight / 2;
            o = _s.HideWhenIdle ? 0 : 1;
            ShowOnly(null, delayIn: false);
        }
        else
        {
            w = CompactWidth;
            h = CompactHeight;
            r = CompactHeight / 2;
            ShowOnly(CompactLayer, delayIn: false);
        }

        AnimateTo(w, h, r, o);
    }

    void Bind(Activity? a)
    {
        if (ReferenceEquals(a, _bound)) return;
        if (_bound != null) _bound.PropertyChanged -= OnActivityChanged;
        _bound = a;
        if (a != null) a.PropertyChanged += OnActivityChanged;
        CompactLayer.DataContext = a;
        ExpandedLayer.DataContext = a;
        // Bindings and item containers settle during layout; re-measure once they have.
        if (_expanded) Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Loaded);
    }

    void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_expanded) return;
        switch (e.PropertyName)
        {
            case nameof(Activity.HasActions):
            case nameof(Activity.Actions):
            case nameof(Activity.HasProgress):
            case nameof(Activity.HasSubtitle):
            case nameof(Activity.Subtitle):
                Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Loaded);
                break;
        }
    }

    static double MeasureLayer(FrameworkElement layer, double width)
    {
        layer.Measure(new Size(width - 2, double.PositiveInfinity));
        return Math.Ceiling(layer.DesiredSize.Height) + 2; // + pill border
    }

    void ShowOnly(FrameworkElement? visible, bool delayIn)
    {
        SetLayer(CompactLayer, ReferenceEquals(visible, CompactLayer), delayIn);
        SetLayer(ExpandedLayer, ReferenceEquals(visible, ExpandedLayer), delayIn);
        SetLayer(IdleLayer, ReferenceEquals(visible, IdleLayer), delayIn);
    }

    static void SetLayer(FrameworkElement el, bool show, bool delayIn)
    {
        var state = el.Tag as string;
        if (show)
        {
            if (state == "on") return;
            el.Tag = "on";
            el.Visibility = Visibility.Visible;
            el.IsHitTestVisible = true;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = delayIn ? TimeSpan.FromMilliseconds(110) : TimeSpan.FromMilliseconds(40),
            });
        }
        else
        {
            if (state != "on") return;
            el.Tag = "off";
            el.IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            fade.Completed += (_, _) => { if (el.Tag as string == "off") el.Visibility = Visibility.Hidden; };
            el.BeginAnimation(OpacityProperty, fade);
        }
    }

    void AnimateTo(double w, double h, double r, double o)
    {
        if (_target == (w, h, r, o)) return;
        bool growing = w * h > Math.Max(0, _target.w) * Math.Max(0, _target.h);
        _target = (w, h, r, o);

        var duration = TimeSpan.FromMilliseconds(growing ? 520 : 360);
        IEasingFunction ease = growing
            ? new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut } // a touch of spring
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, duration) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, duration) { EasingFunction = ease });
        BeginAnimation(PillRadiusProperty, new DoubleAnimation(r, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(o, TimeSpan.FromMilliseconds(220)));
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        IdleTime.Text = now.ToString("h:mm tt");
        IdleDate.Text = now.ToString("dddd, MMMM d");
    }

    // ───────────────────────────── visibility guard ─────────────────────────────

    void Guard()
    {
        bool fs = _s.HideInFullscreen && _hwnd != IntPtr.Zero && Native.IsFullscreenActive(_hwnd);
        if (fs != _fullscreenHidden)
        {
            _fullscreenHidden = fs;
            if (fs && _expanded) Collapse();
            UpdateVisibility();
        }

        if (IsVisible && _hwnd != IntPtr.Zero)
        {
            // Some shell surfaces (taskbar, Start) can climb over topmost windows; reassert.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            if (_expanded && _hub.Current == null) UpdateClock();
        }
    }

    public void SetUserHidden(bool hidden, TimeSpan? duration = null)
    {
        _userHidden = hidden;
        _hideTimer.Stop();
        if (hidden && duration is TimeSpan d)
        {
            _hideTimer.Interval = d;
            _hideTimer.Start();
        }
        if (hidden && _expanded) Collapse();
        UpdateVisibility();
        UserHiddenChanged?.Invoke(hidden);
    }

    void UpdateVisibility()
    {
        bool hide = _userHidden || _fullscreenHidden;
        if (hide && IsVisible) Hide();
        else if (!hide && !IsVisible) Show();
    }

    // ───────────────────────────── input ─────────────────────────────

    void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _hover = true;
        _leaveTimer.Stop();
        if (_s.HoverToExpand && !_expanded) _hoverTimer.Start();
    }

    void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _hover = false;
        _hoverTimer.Stop();
        _leaveTimer.Start();
    }

    void Pill_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_expanded)
        {
            _sticky = true;
            Expand();
            return;
        }

        var open = _hub.Current?.OnOpen;
        if (open != null)
        {
            try { open(); } catch (Exception ex) { Log.Error("open activity", ex); }
            Collapse();
        }
        else if (_sticky || !_hover)
        {
            Collapse(); // clicked open earlier: a second click closes it
        }
    }

    void Pill_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_hub.Items.Count < 2) return;
        _hub.Cycle(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    void Pill_RightClick(object sender, MouseButtonEventArgs e)
    {
        var app = App.Instance;
        var menu = new ContextMenu { PlacementTarget = Pill };

        var timers = new MenuItem { Header = "New timer" };
        foreach (var minutes in new[] { 1, 5, 10, 15, 25, 45, 60 })
        {
            var m = minutes;
            var item = new MenuItem { Header = $"{m} min" };
            item.Click += (_, _) => app.Timers.Start(TimeSpan.FromMinutes(m));
            timers.Items.Add(item);
        }
        menu.Items.Add(timers);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", app.ShowSettings));
        menu.Items.Add(Item("Check for updates", () => _ = app.Updates.CheckAsync(manual: true)));
        menu.Items.Add(Item("Hide for 1 hour", () => SetUserHidden(true, TimeSpan.FromHours(1))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Isle", app.Quit));

        menu.IsOpen = true;
        e.Handled = true;

        static MenuItem Item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            return mi;
        }
    }

    void OnActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is IslandAction action)
        {
            try { action.Invoke(); }
            catch (Exception ex) { Log.Error("island action", ex); }
        }
    }

    void OnQuickTimer(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var minutes))
        {
            _sticky = false;
            App.Instance.Timers.Start(TimeSpan.FromMinutes(minutes));
        }
    }
}
