using System;
using System.Collections.Generic;
using System.ComponentModel;
using Process = System.Diagnostics.Process;
using System.Linq;
using System.Runtime.InteropServices;
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
using Isle.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Isle;

/// <summary>
/// The island. A fixed-size, transparent, click-through, topmost tool window; only the pill (and the
/// companion bubble) are hit-testable. Size changes are spring animations on the pill, never on the
/// window, which keeps them smooth and flicker-free. Everything about it is driven by settings.
/// </summary>
public partial class MainWindow : Window
{
    const double SidePad = 24;
    const double BubbleRoom = 110;  // space kept beside the compact pill for the companion bubble
    const double RadiusCap = 32;    // big cards get soft corners, small states stay full pills
    const int HotkeyId = 0x1515, HideHotkeyId = 0x1516;

    readonly ActivityHub _hub;
    readonly DispatcherTimer _hoverTimer, _leaveTimer, _peekTimer, _guardTimer, _hideTimer;
    AppSettings _s;
    IntPtr _hwnd;
    Activity? _bound, _bubbleItem;
    bool _hover, _sticky, _menuOpen, _peeking, _expanded;
    bool _fullscreenHidden, _userHidden, _appHidden, _overMaximized;
    bool _hotkeyRegistered, _hideHotkeyRegistered, _clipboardListening, _accentBorder;
    double _glowTarget;
    int _guardTicks;
    (double w, double h, double o) _target = (-1, -1, -1);
    int _fps = 60;
    string? _screenName;
    uint _lastFgPid;
    string _lastFgName = "";
    DateTime _lastChange = DateTime.UtcNow, _lastTrim = DateTime.MinValue;

    public event Action<bool>? UserHiddenChanged;
    public bool IsUserHidden => _userHidden;
    public bool IsExpanded => _expanded;

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
            if (_expanded)
            {
                if (!_hover && !_sticky && !_menuOpen) Collapse();
            }
            else if (_hub.Pinned != null)
            {
                _hub.Pinned = null; // a "bump" peek pinned it to the pill for a moment
            }
        });
        _guardTimer = Timer(1000, _ => Guard());
        _hideTimer = Timer(3_600_000, t => { t.Stop(); SetUserHidden(false); });

        _hub.Changed += Refresh;
        _hub.PeekRequested += OnPeek;
        WeatherService.Updated += OnWeather;

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
            WeatherService.Updated -= OnWeather;
            if (_hotkeyRegistered) Native.UnregisterHotKey(_hwnd, HotkeyId);
            if (_hideHotkeyRegistered) Native.UnregisterHotKey(_hwnd, HideHotkeyId);
            if (_clipboardListening) Native.RemoveClipboardFormatListener(_hwnd);
        };
    }

    bool Holding => _sticky || _peeking || _menuOpen;

    DispatcherTimer Timer(int ms, Action<DispatcherTimer> tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => tick(t);
        return t;
    }

    /// <summary>Every animation runs at the display's refresh rate (or the cap in settings).</summary>
    DoubleAnimation Anim(double to, double ms, IEasingFunction? ease = null, double? from = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms / Speed)) { EasingFunction = ease };
        if (from.HasValue) a.From = from;
        Timeline.SetDesiredFrameRate(a, _fps);
        return a;
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
        double bubble = s.CompanionBubble ? BubbleRoom * 2 : 0;
        double widest = Math.Max(ExpandedW, Math.Max(CompactW + bubble, IdleW));
        Width = (widest + SidePad * 2) * scale;
        Height = (top + 340) * scale;

        // Pill and bubble share a three-column stage. Centered: the pill stays dead center and the
        // bubble hangs off its right side. Left/right: the bubble sits on the inner side.
        var align = (s.Align ?? "center").ToLowerInvariant();
        Stage.Margin = new Thickness(align == "left" ? SidePad : 0, top, align == "right" ? SidePad : 0, 0);
        Col0.Width = align == "left" ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Col1.Width = GridLength.Auto;
        Col2.Width = align == "right" ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        switch (align)
        {
            case "left":
                Grid.SetColumn(Pill, 0); Grid.SetColumn(Bubble, 1);
                Bubble.HorizontalAlignment = HorizontalAlignment.Left; Bubble.Margin = new Thickness(8, 0, 0, 0);
                break;
            case "right":
                Grid.SetColumn(Pill, 2); Grid.SetColumn(Bubble, 1);
                Bubble.HorizontalAlignment = HorizontalAlignment.Right; Bubble.Margin = new Thickness(0, 0, 8, 0);
                break;
            default:
                Grid.SetColumn(Pill, 1); Grid.SetColumn(Bubble, 2);
                Bubble.HorizontalAlignment = HorizontalAlignment.Left; Bubble.Margin = new Thickness(8, 0, 0, 0);
                break;
        }
        Pill.HorizontalAlignment = HorizontalAlignment.Center;
        Bubble.Height = CompactH;
        Bubble.MinWidth = CompactH;
        Bubble.CornerRadius = new CornerRadius(CompactH / 2 * Roundness);
        Bubble.Padding = new Thickness(Math.Max(4, (CompactH - 22) / 2), 0, Math.Max(4, (CompactH - 22) / 2), 0);

        Root.Opacity = Math.Clamp(s.Opacity, 0.3, 1);
        double border = Math.Clamp(s.BorderThickness, 0, 4);
        if (s.AccentBorder && border < 1) border = 1.5;
        Pill.BorderThickness = new Thickness(border);
        Bubble.BorderThickness = new Thickness(border);
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
        BuildQuickActions();
        UpdateWeather();

        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.HoverDelayMs, 1, 3000));
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.CollapseDelayMs, 1, 5000));
        Visualizer.Mode = s.ReduceMotion && s.VisualizerMode == "animated" ? "off" : s.VisualizerMode;
        Visualizer.Refresh();

        UpdateClipboardListener();
        RegisterHotkey();
        _screenName = null;
        Position();
        _target = (-1, -1, -1);
        Refresh();
    }

    static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    void UpdateClipboardListener()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool want = _s.ClipboardFlash && _s.Kind("system").Enabled;
        if (want == _clipboardListening) return;
        if (want)
        {
            _clipboardListening = Native.AddClipboardFormatListener(_hwnd);
        }
        else
        {
            Native.RemoveClipboardFormatListener(_hwnd);
            _clipboardListening = false;
        }
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

    void BuildQuickActions()
    {
        QuickTimersPanel.Children.Clear();
        var icons = (FontFamily)FindResource("Icons");
        foreach (var minutes in (_s.QuickTimers ?? new()).Where(m => m > 0).Distinct().Take(5))
        {
            var b = new Button
            {
                Style = (Style)FindResource("IslandButton"),
                Content = minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} hr" : $"{minutes} min",
                Tag = minutes.ToString(),
                Margin = new Thickness(4, 4, 4, 0),
                ToolTip = $"Start a {minutes}-minute timer",
            };
            b.Click += OnQuickTimer;
            QuickTimersPanel.Children.Add(b);
        }
        if (!_s.IdleShowQuickActions) return;
        foreach (var (glyph, tip, action) in new (string, string, Action)[]
                 {
                     ("", "Stopwatch", () => App.Instance.Timers.ToggleStopwatch()),
                     ("", "Focus session", () => App.Instance.Timers.StartFocus()),
                 })
        {
            var b = new Button
            {
                Style = (Style)FindResource("IslandButton"),
                Content = new TextBlock { Text = glyph, FontFamily = icons, FontSize = 14 },
                Margin = new Thickness(4, 4, 4, 0),
                ToolTip = tip,
            };
            var act = action;
            b.Click += (_, e) => { e.Handled = true; _sticky = false; act(); };
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

        var screen = TargetScreen();
        _screenName = screen.DeviceName;
        _fps = _s.FrameRate > 0 ? Math.Clamp(_s.FrameRate, 20, 360) : Math.Clamp(Native.RefreshRate(screen.DeviceName), 30, 360);
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

    Forms.Screen TargetScreen()
    {
        var screens = Forms.Screen.AllScreens;
        var main = Forms.Screen.PrimaryScreen ?? screens[0];
        if (_s.Monitor == "follow")
        {
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != _hwnd && fg != Native.GetShellWindow() && fg != Native.GetDesktopWindow())
                return Forms.Screen.FromHandle(fg);
            return screens.FirstOrDefault(x => x.DeviceName == _screenName) ?? main;
        }
        return screens.FirstOrDefault(x => x.DeviceName == _s.Monitor) ?? main;
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Ui.Post(() => { _screenName = null; Position(); });

    // ───────────────────────────── hotkeys & messages ─────────────────────────────

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
        switch (msg)
        {
            case Native.WM_HOTKEY when wParam.ToInt32() == HideHotkeyId:
                handled = true;
                SetUserHidden(!_userHidden);
                break;
            case Native.WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                handled = true;
                ToggleExpanded();
                break;
            case Native.WM_CLIPBOARDUPDATE:
                SystemService.RaiseClipboard();
                break;
            case Native.WM_COPYDATA:
                // Another "Isle.exe --something" passing its command line to this one.
                try
                {
                    var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(lParam);
                    if (cds.dwData == new IntPtr(0x1515) && cds.cbData > 0 && cds.cbData < 64 * 1024)
                    {
                        var text = (Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) ?? "").TrimEnd('\0');
                        var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        Dispatcher.InvokeAsync(() => App.Instance.HandleCommand(args));
                        handled = true;
                        return new IntPtr(1);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("copydata", ex);
                }
                break;
        }
        return IntPtr.Zero;
    }

    // ───────────────────────────── state machine ─────────────────────────────

    public void ToggleExpanded()
    {
        if (_userHidden) SetUserHidden(false);
        if (_expanded) Collapse();
        else
        {
            _sticky = true;
            Expand();
        }
    }

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
        if (_fullscreenHidden || _userHidden || _appHidden) return;
        if (_hover || _sticky || _menuOpen || (_expanded && !_peeking)) return; // the user is already looking

        bool bump = _s.PeekStyle == "bump";
        _peeking = true;
        _hub.Pinned = a;
        if (bump && !_expanded) Bump();
        else
        {
            _expanded = true;
            Refresh();
        }
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_s.PeekSeconds, 1, 20));
        _peekTimer.Start();
    }

    /// <summary>A quick pulse of the pill — the "bump" peek style.</summary>
    void Bump()
    {
        if (_s.ReduceMotion) return;
        var up = Anim(1.07, 140, new CubicEase { EasingMode = EasingMode.EaseOut });
        up.AutoReverse = true;
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, up);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, up.Clone());
    }

    void Refresh()
    {
        _lastChange = DateTime.UtcNow;
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
            o = _s.HideWhenIdle || _overMaximized ? 0 : 1;
            if (_s.IdleShowClock && o > 0)
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

        UpdateAccentBorder(a);
        UpdateGlow(a);
        UpdateBubble(a);
        AnimateTo(w, h, o);
    }

    void UpdateAccentBorder(Activity? a)
    {
        if (_s.AccentBorder && a != null)
        {
            Pill.BorderBrush = a.AccentBrush;
            _accentBorder = true;
        }
        else if (_accentBorder)
        {
            Pill.SetResourceReference(Border.BorderBrushProperty, "Island.Border");
            _accentBorder = false;
        }
    }

    void UpdateGlow(Activity? a)
    {
        var src = _expanded && _s.ArtworkGlow && a != null ? a.GlowSource : null;
        if (src != null) GlowBrush.ImageSource = src;
        double target = src != null ? Math.Clamp(_s.GlowStrength, 0.05, 0.8) : 0;
        if (Math.Abs(target - _glowTarget) < 0.001) return;
        _glowTarget = target;
        GlowLayer.BeginAnimation(OpacityProperty, Anim(target, src != null ? 420 : 160, new CubicEase { EasingMode = EasingMode.EaseOut }));
    }

    void UpdateBubble(Activity? current)
    {
        Activity? second = null;
        if (_s.CompanionBubble && !_expanded && current != null)
            second = _hub.Items.FirstOrDefault(x => !ReferenceEquals(x, current) && !x.IsTransient);

        if (ReferenceEquals(second, _bubbleItem)) return;
        bool wasShown = _bubbleItem != null;
        _bubbleItem = second;

        if (second != null)
        {
            BubbleContent.DataContext = second;
            if (!wasShown)
            {
                Bubble.Visibility = Visibility.Visible;
                var ease = _s.ReduceMotion ? (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseOut } : new SpringEase { Damping = 0.7 };
                BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 480, ease, 0.4));
                BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 480, ease, 0.4));
                Bubble.BeginAnimation(OpacityProperty, Anim(1, 200, null, 0));
            }
        }
        else if (wasShown)
        {
            var fade = Anim(0, 140);
            fade.Completed += (_, _) =>
            {
                if (_bubbleItem != null) return;
                Bubble.Visibility = Visibility.Collapsed;
                BubbleContent.DataContext = null;
            };
            BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.5, 160));
            BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.5, 160));
            Bubble.BeginAnimation(OpacityProperty, fade);
        }
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
        double min = Math.Min(max, 150);
        CompactLayer.Width = double.NaN;
        CompactLayer.Measure(new Size(double.PositiveInfinity, CompactH));
        var m = CompactLayer.Margin;
        double want = Math.Ceiling(CompactLayer.DesiredSize.Width) + 2;
        if (want < m.Left + m.Right + 24) want = min;
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
        SeekArea.Cursor = a?.Seek != null && _s.ClickToSeek ? Cursors.Hand : null;
        if (swap) CardSwapFade(_expanded ? ExpandedLayer : CompactLayer);
        // Bindings and item containers settle during layout; re-measure once they have.
        Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Loaded);
    }

    /// <summary>A quick dip-and-return when the island switches to a different card.</summary>
    void CardSwapFade(FrameworkElement layer)
    {
        if (layer.Tag as string != "on" || _s.ReduceMotion) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var tt = new TranslateTransform(0, 4);
        layer.RenderTransform = tt;
        layer.BeginAnimation(OpacityProperty, Anim(1, 220, ease, 0.15));
        tt.BeginAnimation(TranslateTransform.YProperty, Anim(0, 220, ease, 4));
    }

    void OnActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_expanded)
        {
            if (_s.CompactAutoWidth && e.PropertyName is nameof(Activity.Title) or nameof(Activity.HasTrailing) or nameof(Activity.ShowVisualizer) or nameof(Activity.IsLive) or nameof(Activity.Trailing))
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
            case nameof(Activity.GlowSource):
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
        if (show)
        {
            if (state == "on") return;
            el.Tag = "on";
            el.Visibility = Visibility.Visible;
            el.IsHitTestVisible = true;
            el.RenderTransform = Transform.Identity;
            var fadeIn = Anim(1, 220, new CubicEase { EasingMode = EasingMode.EaseOut });
            fadeIn.BeginTime = TimeSpan.FromMilliseconds((delayIn ? 120 : 60) / Speed);
            el.BeginAnimation(OpacityProperty, fadeIn);
        }
        else
        {
            if (state != "on") return;
            el.Tag = "off";
            el.IsHitTestVisible = false;
            var fade = Anim(0, 90);
            fade.Completed += (_, _) => { if (el.Tag as string == "off") el.Visibility = Visibility.Hidden; };
            el.BeginAnimation(OpacityProperty, fade);
        }
    }

    void AnimateTo(double w, double h, double o)
    {
        if (_target == (w, h, o)) return;
        bool growing = w * h > Math.Max(0, _target.w) * Math.Max(0, _target.h);
        _target = (w, h, o);

        double ms = _s.ReduceMotion ? 160 : growing ? 560 : 420;
        double bounce = Math.Clamp(_s.Springiness, 0, 0.7);
        IEasingFunction ease = _s.ReduceMotion
            ? new CubicEase { EasingMode = EasingMode.EaseOut }
            // Opening gets the full spring; closing settles a little more firmly.
            : new SpringEase { Damping = growing ? 1 - bounce : Math.Min(1, 1.15 - bounce) };

        Pill.BeginAnimation(WidthProperty, Anim(w, ms, ease));
        Pill.BeginAnimation(HeightProperty, Anim(h, ms, ease));
        Pill.BeginAnimation(OpacityProperty, Anim(o, 220));
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        IdleTime.Text = now.ToString("h:mm tt");
        IdleDate.Text = now.ToString("dddd, MMMM d");
        var w = WeatherService.Current;
        IdleCompactLayer.Text = _s.IdleShowWeather && w != null
            ? $"{now:h:mm}  ·  {Math.Round(w.Temp)}°"
            : now.ToString("h:mm");
    }

    void OnWeather()
    {
        UpdateWeather();
        if (_hub.Current == null) Refresh();
    }

    void UpdateWeather()
    {
        var w = WeatherService.Current;
        bool show = _s.IdleShowWeather && w != null;
        WeatherPanel.Visibility = Vis(show);
        if (!show) return;
        WeatherGlyph.Text = w!.Glyph;
        WeatherTemp.Text = $"{Math.Round(w.Temp)}°";
        WeatherText.Text = $"{w.Text} · H {Math.Round(w.High)}° L {Math.Round(w.Low)}°";
        WeatherPanel.ToolTip = string.IsNullOrEmpty(w.Place) ? null : w.Place;
    }

    // ───────────────────────────── guard: visibility, placement, memory ─────────────────────────────

    void Guard()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool fs = _s.HideInFullscreen && Native.IsFullscreenActive(_hwnd);
        bool app = _s.HideForApps.Count > 0 && ForegroundIsHiddenApp();
        if (fs != _fullscreenHidden || app != _appHidden)
        {
            _fullscreenHidden = fs;
            _appHidden = app;
            if ((fs || app) && _expanded) Collapse();
            UpdateVisibility();
        }

        bool maxed = _s.MaximizedBehavior == "hideidle" && Native.IsForegroundMaximized(_hwnd);
        if (maxed != _overMaximized)
        {
            _overMaximized = maxed;
            if (_hub.Current == null && !_expanded) Refresh();
        }

        if (_s.Monitor == "follow" && !_expanded && TargetScreen().DeviceName != _screenName) Position();

        if (IsVisible)
        {
            // Some shell surfaces (taskbar, Start) can climb over topmost windows; reassert now and then.
            if (++_guardTicks % 3 == 0)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            if (_hub.Current == null && (_expanded || _s.IdleShowClock)) UpdateClock();
        }

        // Don't let a short-lived card (volume, caps lock…) vanish while you're hovering it.
        if (_expanded && _hover && _hub.Current is { IsTransient: true, ExpiresAt: not null } t)
            t.ExpiresAt = DateTime.UtcNow.AddSeconds(2);

        // After a quiet spell, hand unused memory back to Windows.
        var now = DateTime.UtcNow;
        if (_s.TrimMemory && !_expanded && now - _lastChange > TimeSpan.FromSeconds(30) && now - _lastTrim > TimeSpan.FromMinutes(5))
        {
            _lastTrim = now;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
            Native.TrimWorkingSet();
        }
    }

    bool ForegroundIsHiddenApp()
    {
        uint pid = Native.ForegroundProcessId();
        if (pid == 0) return false;
        if (pid != _lastFgPid)
        {
            _lastFgPid = pid;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                _lastFgName = p.ProcessName;
            }
            catch
            {
                _lastFgName = "";
            }
        }
        if (_lastFgName.Length == 0) return false;
        return _s.HideForApps.Any(n =>
        {
            var name = n.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return name.Equals(_lastFgName, StringComparison.OrdinalIgnoreCase);
        });
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
        bool hide = _userHidden || _fullscreenHidden || _appHidden;
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

    void Seek_Click(object sender, MouseButtonEventArgs e)
    {
        var a = _hub.Current;
        if (!_expanded || !_s.ClickToSeek || a?.Seek == null || SeekTrack.ActualWidth <= 0) return;
        e.Handled = true;
        double f = Math.Clamp(e.GetPosition(SeekTrack).X / SeekTrack.ActualWidth, 0, 1);
        try { a.Seek(f); } catch (Exception ex) { Log.Error("seek", ex); }
    }

    void Bubble_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_bubbleItem != null) _hub.Pinned = _bubbleItem;
    }

    void Pill_Wheel(object sender, MouseWheelEventArgs e)
    {
        var a = _hub.Current;
        if (_expanded && _s.ScrollAdjustsVolume && a?.OnScroll != null)
        {
            try { a.OnScroll(e.Delta > 0 ? 1 : -1); } catch (Exception ex) { Log.Error("scroll", ex); }
            e.Handled = true;
            return;
        }
        if (!_s.ScrollToSwitch || _hub.Items.Count < 2) return;
        _hub.Cycle(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    void Pill_RightClick(object sender, MouseButtonEventArgs e)
    {
        var target = ReferenceEquals(sender, Bubble) ? _bubbleItem : _hub.Current;
        OpenMenu(App.Instance.BuildMenu(target), (UIElement)sender);
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
