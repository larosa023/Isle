using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Isle.Controls;
using Isle.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Isle;

/// <summary>
/// The island. A fixed-size, transparent, click-through, topmost tool window; only the pill
/// itself is hit-testable. Size changes are spring animations on the pill, never on the window,
/// which keeps resizing smooth and flicker-free. Everything about it is driven by settings.
/// </summary>
public partial class MainWindow : Window
{
    const double SidePad = 24;
    const int HotkeyId = 0x1515, HideHotkeyId = 0x1516;

    readonly ActivityHub _hub;
    readonly DispatcherTimer _hoverTimer, _leaveTimer, _peekTimer, _guardTimer, _hideTimer;
    AppSettings _s;
    IntPtr _hwnd;
    Activity? _bound;
    bool _hover, _sticky, _menuOpen, _peeking, _expanded, _fullscreenHidden, _userHidden, _hotkeyRegistered, _hideHotkeyRegistered;
    (double w, double h, double o) _target = (-1, -1, -1);
    const double RadiusCap = 32; // big cards get soft corners, small states stay full pills

    public event Action<bool>? UserHiddenChanged;
    public bool IsUserHidden => _userHidden;

    public MainWindow(ActivityHub hub, AppSettings settings)
    {
        InitializeComponent();
        _hub = hub;
        _s = settings;

        _hoverTimer = Timer(200, t => { t.Stop(); if (_hover) Expand(); });
        _leaveTimer = Timer(380, t => { t.Stop(); if (!_hover && !Holding) Collapse(); });
        _peekTimer = Timer(3500, t =>
        {
            t.Stop();
            _peeking = false;
            if (!_hover && !_sticky && !_menuOpen) Collapse();
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
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        Loaded += (_, _) =>
        {
            ApplySettings(_s);
            _guardTimer.Start();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            if (_hotkeyRegistered) Native.UnregisterHotKey(_hwnd, HotkeyId);
            if (_hideHotkeyRegistered) Native.UnregisterHotKey(_hwnd, HideHotkeyId);
        };
    }

    bool Holding => _sticky || _peeking || _menuOpen;

    DispatcherTimer Timer(int ms, Action<DispatcherTimer> tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => tick(t);
        return t;
    }

    // ───────────────────────────── settings & placement ─────────────────────────────

    double IdleW => Math.Clamp(_s.IdleWidth, 40, 400);
    double IdleH => Math.Clamp(_s.IdleHeight, 8, 60);
    double CompactW => Math.Clamp(_s.CompactWidth, 120, 600);
    double CompactH => Math.Clamp(_s.CompactHeight, 24, 60);
    double ExpandedW => Math.Clamp(_s.ExpandedWidth, 300, 640);
    double Roundness => Math.Clamp(_s.Roundness, 0, 1);
    double Speed => _s.ReduceMotion ? 1 : Math.Clamp(_s.AnimationSpeed, 0.3, 3);

    public void ApplySettings(AppSettings s)
    {
        _s = s;
        double scale = Math.Clamp(s.Scale, 0.6, 2.0);
        Root.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);

        double top = Math.Clamp(s.TopOffset, 0, 120);
        double widest = Math.Max(ExpandedW, Math.Max(CompactW, IdleW));
        Width = (widest + SidePad * 2) * scale;
        Height = (top + 320) * scale;

        var align = (s.Align ?? "center").ToLowerInvariant();
        Pill.HorizontalAlignment = align switch
        {
            "left" => HorizontalAlignment.Left,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        Pill.Margin = new Thickness(align == "left" ? SidePad : 0, top, align == "right" ? SidePad : 0, 0);
        Root.Opacity = Math.Clamp(s.Opacity, 0.3, 1);
        Pill.BorderThickness = new Thickness(Math.Clamp(s.BorderThickness, 0, 4));
        FontFamily = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamily)
            ? "Segoe UI Variable Text, Segoe UI"
            : s.FontFamily.Trim() + ", Segoe UI");

        CompactArtHost.Visibility = Vis(s.CompactShowArt);
        CompactTitle.Visibility = Vis(s.CompactShowTitle);
        CompactProgressHost.Visibility = Vis(s.CompactShowProgress);
        CompactLiveHost.Visibility = Vis(s.CompactShowLiveDot);
        CompactTrailingHost.Visibility = Vis(s.CompactShowTrailing);
        SourceHost.Visibility = Vis(s.ExpandedShowSource);
        ProgressHost.Visibility = Vis(s.ExpandedShowProgress);
        ActionsHost.Visibility = Vis(s.ExpandedShowActions);
        PositionText.Visibility = Vis(s.ExpandedShowCount);
        BuildQuickTimers();

        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.HoverDelayMs, 1, 3000));
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.CollapseDelayMs, 1, 5000));
        Visualizer.AnimationsEnabled = s.AnimatedVisualizer && !s.ReduceMotion;

        RegisterHotkey();
        Position();
        _target = (-1, -1, -1);
        Refresh();
    }

    /// <summary>
    /// Corner radius tracks the pill's live size every frame (so it's always a proper pill shape,
    /// never wider corners than the height allows), and content is clipped to the same rounded shape.
    /// </summary>
    void Pill_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = Pill.ActualWidth, h = Pill.ActualHeight;
        double r = Math.Min(h / 2, RadiusCap) * Roundness;
        Pill.CornerRadius = new CornerRadius(r);
        double bt = Pill.BorderThickness.Left;
        double iw = Math.Max(0, w - bt * 2), ih = Math.Max(0, h - bt * 2), ir = Math.Max(0, r - bt);
        var clip = new RectangleGeometry(new Rect(0, 0, iw, ih), ir, ir);
        clip.Freeze();
        PillContent.Clip = clip;
    }

    static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    void BuildQuickTimers()
    {
        QuickTimersPanel.Children.Clear();
        QuickTimersPanel.Children.Add(new TextBlock
        {
            Text = Glyphs.Timer,
            FontFamily = (FontFamily)FindResource("Icons"),
            FontSize = 14,
            Style = (Style)FindResource("Secondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        foreach (var minutes in (_s.QuickTimers ?? new()).Where(m => m > 0).Distinct().Take(5))
        {
            var b = new Button
            {
                Style = (Style)FindResource("IslandButton"),
                Content = minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} hr" : $"{minutes} min",
                Tag = minutes.ToString(),
            };
            b.Click += OnQuickTimer;
            QuickTimersPanel.Children.Add(b);
        }
    }

    void Position()
    {
        if (_hwnd == IntPtr.Zero)
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
            return;
        }

        var screens = Forms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(x => x.DeviceName == _s.Monitor) ?? Forms.Screen.PrimaryScreen ?? screens[0];
        var b = screen.Bounds; // physical pixels

        // Step onto the target monitor first so WPF adopts its DPI and resizes itself,
        // then place it precisely using the window's real pixel size.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.Left + 1, b.Top + 1, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (!Native.GetWindowRect(_hwnd, out var r)) return;
        int w = r.Right - r.Left;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int offset = (int)Math.Round(_s.OffsetX * dpi);

        int x = (_s.Align ?? "center").ToLowerInvariant() switch
        {
            "left" => b.Left + offset,
            "right" => b.Right - w - offset,
            _ => b.Left + (b.Width - w) / 2 + offset,
        };
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, b.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Ui.Post(Position);

    // ───────────────────────────── hotkey ─────────────────────────────

    void RegisterHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        _hotkeyRegistered = Register(HotkeyId, _s.Hotkey, _hotkeyRegistered);
        _hideHotkeyRegistered = Register(HideHotkeyId, _s.HideHotkey, _hideHotkeyRegistered);
    }

    bool Register(int id, string? text, bool wasRegistered)
    {
        if (wasRegistered) Native.UnregisterHotKey(_hwnd, id);
        if (!TryParseHotkey(text, out var mods, out var vk)) return false;
        bool ok = Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk);
        if (!ok) Log.Info($"hotkey {text} is taken by another app");
        return ok;
    }

    public void SuspendHotkeys(bool suspend)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (suspend)
        {
            if (_hotkeyRegistered) Native.UnregisterHotKey(_hwnd, HotkeyId);
            if (_hideHotkeyRegistered) Native.UnregisterHotKey(_hwnd, HideHotkeyId);
            _hotkeyRegistered = _hideHotkeyRegistered = false;
        }
        else
        {
            RegisterHotkey();
        }
    }

    /// <summary>Whether each shortcut is registered (false = empty, invalid, or taken by another app).</summary>
    public (bool open, bool hide) HotkeysActive => (_hotkeyRegistered, _hideHotkeyRegistered);

    public static bool TryParseHotkey(string? text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": case "windows": mods |= Native.MOD_WIN; break;
                default:
                    Key key;
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, true, out key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HideHotkeyId)
        {
            handled = true;
            SetUserHidden(!_userHidden);
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (_userHidden) SetUserHidden(false);
            if (_expanded) Collapse();
            else
            {
                _sticky = true;
                Expand();
            }
        }
        return IntPtr.Zero;
    }

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
        if (_fullscreenHidden || _userHidden) return;
        if (_hover || _sticky || _menuOpen || (_expanded && !_peeking)) return; // the user is already looking

        _peeking = true;
        _expanded = true;
        _hub.Pinned = a;
        Refresh();
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_s.PeekSeconds, 1, 20));
        _peekTimer.Start();
    }

    void Refresh()
    {
        var a = _hub.Current;
        Bind(a);

        int count = _hub.Items.Count;
        PositionText.Text = count > 1 ? $"{_hub.CurrentIndex + 1} of {count}" : "";

        double w, h, o = 1;
        if (_expanded)
        {
            var layer = a == null ? IdleLayer : ExpandedLayer;
            if (a == null) UpdateClock();
            w = ExpandedW;
            FixWidth(layer, w);
            h = MeasureLayer(layer, w);
            ShowOnly(layer, delayIn: true);
        }
        else if (a == null)
        {
            w = IdleW;
            h = IdleH;
            o = _s.HideWhenIdle ? 0 : 1;
            if (_s.IdleShowClock && !_s.HideWhenIdle)
            {
                UpdateClock();
                ShowOnly(IdleCompactLayer, delayIn: false);
            }
            else
            {
                ShowOnly(null, delayIn: false);
            }
        }
        else
        {
            w = _s.CompactAutoWidth ? FitCompactWidth() : CompactW;
            h = CompactH;
            FixWidth(CompactLayer, w);
            ShowOnly(CompactLayer, delayIn: false);
        }

        AnimateTo(w, h, o);
    }

    /// <summary>
    /// Layers get the final width up front, so text and buttons don't reflow while the pill grows —
    /// the pill just reveals them (and clips them) as it opens.
    /// </summary>
    static void FixWidth(FrameworkElement layer, double pillWidth)
    {
        var m = layer.Margin;
        layer.Width = Math.Max(0, pillWidth - 2 - m.Left - m.Right);
    }

    /// <summary>Compact pill sized to its content, between a sensible minimum and the configured width.</summary>
    double FitCompactWidth()
    {
        double max = CompactW;
        double min = Math.Min(max, 170);
        CompactLayer.Width = double.NaN;
        CompactLayer.Measure(new Size(double.PositiveInfinity, CompactH));
        double want = Math.Ceiling(CompactLayer.DesiredSize.Width) + 2;
        // Round up to 8 px so small text changes don't nudge the width.
        want = Math.Ceiling(want / 8) * 8;
        return Math.Clamp(want, min, max);
    }

    void Bind(Activity? a)
    {
        if (ReferenceEquals(a, _bound)) return;
        if (_bound != null) _bound.PropertyChanged -= OnActivityChanged;
        _bound = a;
        if (a != null) a.PropertyChanged += OnActivityChanged;
        bool swap = CompactLayer.DataContext != null && a != null;
        CompactLayer.DataContext = a;
        ExpandedLayer.DataContext = a;
        if (swap) CardSwapFade(_expanded ? ExpandedLayer : CompactLayer);
        // Bindings and item containers settle during layout; re-measure once they have.
        Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Loaded);
    }

    /// <summary>A quick dip-and-return when the island switches to a different card.</summary>
    void CardSwapFade(FrameworkElement layer)
    {
        if (layer.Tag as string != "on" || _s.ReduceMotion) return;
        var ms = 220 / Speed;
        var fade = new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var tt = new TranslateTransform(0, 4);
        layer.RenderTransform = tt;
        layer.BeginAnimation(OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_expanded)
        {
            if (_s.CompactAutoWidth && e.PropertyName is nameof(Activity.Title) or nameof(Activity.HasTrailing) or nameof(Activity.ShowVisualizer) or nameof(Activity.IsLive))
                Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Loaded);
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(Activity.HasActions):
            case nameof(Activity.Actions):
            case nameof(Activity.HasProgress):
            case nameof(Activity.HasSubtitle):
            case nameof(Activity.Subtitle):
            case nameof(Activity.HasSource):
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
        SetLayer(IdleCompactLayer, ReferenceEquals(visible, IdleCompactLayer), delayIn);
    }

    void SetLayer(FrameworkElement el, bool show, bool delayIn)
    {
        var state = el.Tag as string;
        double k = 1 / Speed;
        if (show)
        {
            if (state == "on") return;
            el.Tag = "on";
            el.Visibility = Visibility.Visible;
            el.IsHitTestVisible = true;
            el.RenderTransform = Transform.Identity;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220 * k))
            {
                BeginTime = TimeSpan.FromMilliseconds((delayIn ? 120 : 60) * k),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            if (state != "on") return;
            el.Tag = "off";
            el.IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90 * k));
            fade.Completed += (_, _) => { if (el.Tag as string == "off") el.Visibility = Visibility.Hidden; };
            el.BeginAnimation(OpacityProperty, fade);
        }
    }

    void AnimateTo(double w, double h, double o)
    {
        if (_target == (w, h, o)) return;
        bool growing = w * h > Math.Max(0, _target.w) * Math.Max(0, _target.h);
        _target = (w, h, o);

        double baseMs = _s.ReduceMotion ? 160 : growing ? 560 : 420;
        var duration = TimeSpan.FromMilliseconds(baseMs / Speed);
        double bounce = Math.Clamp(_s.Springiness, 0, 0.7);
        IEasingFunction ease = _s.ReduceMotion
            ? new CubicEase { EasingMode = EasingMode.EaseOut }
            // Opening gets the full spring; closing settles a little more firmly.
            : new SpringEase { Damping = growing ? 1 - bounce : Math.Min(1, 1.15 - bounce) };

        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, duration) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, duration) { EasingFunction = ease });
        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(o, TimeSpan.FromMilliseconds(220 / Speed)));
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        IdleTime.Text = now.ToString("h:mm tt");
        IdleDate.Text = now.ToString("dddd, MMMM d");
        IdleCompactLayer.Text = now.ToString("h:mm");
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
            if (_hub.Current == null && (_expanded || _s.IdleShowClock)) UpdateClock();
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
        else if (!hide && !IsVisible)
        {
            Show();
            Position();
        }
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

        var open = _s.ClickOpensActivity ? _hub.Current?.OnOpen : null;
        if (open != null)
        {
            try { open(); } catch (Exception ex) { Log.Error("open activity", ex); }
            Collapse();
        }
        else if (_sticky || !_hover || !_s.HoverToExpand)
        {
            Collapse(); // clicked open earlier: a second click closes it
        }
    }

    void Pill_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!_s.ScrollToSwitch || _hub.Items.Count < 2) return;
        _hub.Cycle(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    void Pill_RightClick(object sender, MouseButtonEventArgs e)
    {
        OpenMenu(App.Instance.BuildMenu(_hub.Current), Pill);
        e.Handled = true;
    }

    void OpenMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        _menuOpen = true;
        _leaveTimer.Stop();
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!_hover) _leaveTimer.Start();
        };
        menu.IsOpen = true;
    }

    void OnActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not IslandAction action) return;

        if (action.Menu is { Count: > 0 } items)
        {
            var menu = new ContextMenu();
            foreach (var item in items)
            {
                var it = item;
                var mi = new MenuItem { Header = it.Label, IsChecked = it.Checked };
                mi.Click += (_, _) => it.Invoke();
                menu.Items.Add(mi);
            }
            OpenMenu(menu, (UIElement)sender);
            return;
        }

        try { action.Invoke(); }
        catch (Exception ex) { Log.Error("island action", ex); }
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
