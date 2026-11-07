using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;
using Isle.Services;
using Forms = System.Windows.Forms;

namespace Isle;

/// <summary>
/// Settings, built in code from small building blocks (cards with a title, description and a
/// control). Every change applies live after a short debounce; there is no Save button.
/// </summary>
public partial class SettingsWindow : Window
{
    sealed record PageDef(string Glyph, string Title, Action Build);

    readonly AppSettings _s;
    readonly DispatcherTimer _apply;
    readonly List<PageDef> _pages;
    PageDef? _current;
    TextBlock? _ampliteStatus;
    readonly List<Action> _afterApply = new();
    static string? _lastPage;

    static readonly Brush CardFill = Frozen(Color.FromArgb(0x14, 0x80, 0x80, 0x80));
    static readonly Brush CardStroke = Frozen(Color.FromArgb(0x26, 0x80, 0x80, 0x80));

    public SettingsWindow(AppSettings current, string? page = null)
    {
        InitializeComponent();
        ThemeMode = ThemeMode.System; // Fluent styling, follows Windows light/dark
        _s = current.Clone();
        VersionText.Text = $"Version {AppInfo.VersionString}";

        _apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _apply.Tick += (_, _) =>
        {
            _apply.Stop();
            App.Instance.ApplySettings(_s.Clone());
            foreach (var a in _afterApply) a();
        };

        _pages = new List<PageDef>
        {
            new("", "General", BuildGeneral),
            new("", "Island", BuildIsland),
            new("", "Appearance", BuildAppearance),
            new("", "Behavior & motion", BuildBehavior),
            new("", "Activities", BuildActivities),
            new("", "Apps", BuildApps),
            new("", "Amplite", BuildAmplite),
            new("", "Live sports", BuildSports),
            new("", "Integrations", BuildIntegrations),
            new("", "About & updates", BuildAbout),
        };

        foreach (var p in _pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6) };
            row.Children.Add(new TextBlock { Text = p.Glyph, FontFamily = Icons, FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = p.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            Nav.Items.Add(new ListBoxItem { Content = row, Tag = p });
        }

        AppRegistry.Detected += OnAppsDetected;
        AmpliteService.StateChanged += OnAmpliteChanged;
        FullcourtLink.Changed += OnFullcourtChanged;
        Closed += (_, _) =>
        {
            AppRegistry.Detected -= OnAppsDetected;
            AmpliteService.StateChanged -= OnAmpliteChanged;
            FullcourtLink.Changed -= OnFullcourtChanged;
            if (_apply.IsEnabled) { _apply.Stop(); App.Instance.ApplySettings(_s.Clone()); }
        };

        Nav.SelectedIndex = Math.Max(0, _pages.FindIndex(p => string.Equals(p.Title, page ?? _lastPage, StringComparison.OrdinalIgnoreCase)));
    }

    FontFamily Icons => (FontFamily)FindResource("Icons");

    void Changed()
    {
        _apply.Stop();
        _apply.Start();
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((Nav.SelectedItem as ListBoxItem)?.Tag is PageDef p) Show(p);
    }

    void Show(PageDef p)
    {
        _current = p;
        _lastPage = p.Title;
        _ampliteStatus = null;
        _afterApply.Clear();
        Page.Children.Clear();
        Page.Children.Add(new TextBlock { Text = p.Title, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        p.Build();
        Scroller.ScrollToTop();
    }

    void Rebuild()
    {
        if (_current == null) return;
        var offset = Scroller.VerticalOffset;
        Show(_current);
        Scroller.ScrollToVerticalOffset(offset);
    }

    void OnAppsDetected() => Dispatcher.InvokeAsync(() => { if (_current?.Title == "Apps") Rebuild(); });
    void OnFullcourtChanged() => Dispatcher.InvokeAsync(() => { if (_current?.Title == "Live sports") Rebuild(); });
    void OnAmpliteChanged() => Dispatcher.InvokeAsync(UpdateAmpliteStatus);

    // ───────────────────────────── building blocks ─────────────────────────────

    void Section(string title, string? hint = null)
    {
        Page.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        if (hint != null) Page.Children.Add(Hint(hint, new Thickness(0, 0, 0, 8)));
    }

    static TextBlock Hint(string text, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = 12.5,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0, 2, 0, 0),
    };

    /// <summary>A settings row: title + description on the left, a control on the right.</summary>
    Border Card(string title, string? description, UIElement? control, UIElement? below = null, double indent = 0)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description)) text.Children.Add(Hint(description));
        grid.Children.Add(text);

        if (control != null)
        {
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }

        UIElement content = grid;
        if (below != null)
        {
            var stack = new StackPanel();
            stack.Children.Add(grid);
            if (below is FrameworkElement b) b.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(below);
            content = stack;
        }

        var card = new Border
        {
            Background = CardFill,
            BorderBrush = CardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(indent, 0, 0, 4),
            Child = content,
        };
        Page.Children.Add(card);
        return card;
    }

    CheckBox Switch(Func<bool> get, Action<bool> set, Action? after = null)
    {
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get() };
        cb.Click += (_, _) =>
        {
            set(cb.IsChecked == true);
            Changed();
            after?.Invoke();
        };
        return cb;
    }

    void Toggle(string title, string? description, Func<bool> get, Action<bool> set, double indent = 0, Action? after = null) =>
        Card(title, description, Switch(get, set, after), indent: indent);

    FrameworkElement Slider(double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> format, double width = 240)
    {
        var value = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = width,
            Value = Math.Clamp(get(), min, max),
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.Text = format(slider.Value);
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            set(e.NewValue);
            Changed();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(value);
        return panel;
    }

    void SliderCard(string title, string? description, double min, double max, double step,
        Func<double> get, Action<double> set, Func<double, string> format, double indent = 0) =>
        Card(title, description, Slider(min, max, step, get, set, format), indent: indent);

    ComboBox Combo(IEnumerable<(string label, string value)> options, Func<string> get, Action<string> set, Action? after = null, double width = 240)
    {
        var box = new ComboBox { Width = width };
        foreach (var (label, value) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var current = get();
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, current, StringComparison.OrdinalIgnoreCase))
                           ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string v })
            {
                set(v);
                Changed();
                after?.Invoke();
            }
        };
        return box;
    }

    TextBox Text(Func<string> get, Action<string> set, double width = 240, string? tooltip = null)
    {
        var box = new TextBox { Text = get(), Width = width, ToolTip = tooltip };
        box.TextChanged += (_, _) =>
        {
            set(box.Text);
            Changed();
        };
        return box;
    }

    /// <summary>A color text box with a live swatch; only valid colors are applied.</summary>
    FrameworkElement ColorField(Func<string> get, Action<string> set)
    {
        var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), BorderBrush = CardStroke, Margin = new Thickness(0, 0, 10, 0) };
        var box = new TextBox { Text = get(), Width = 120 };
        void Paint()
        {
            bool ok = ThemeManager.TryParseColor(box.Text, out var col);
            swatch.Background = ok ? Frozen(col) : Brushes.Transparent;
            box.ToolTip = ok ? null : "Not a color yet — try #1C1C1E";
        }
        Paint();
        box.TextChanged += (_, _) =>
        {
            Paint();
            if (ThemeManager.TryParseColor(box.Text, out _)) { set(box.Text.Trim()); Changed(); }
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(swatch);
        sp.Children.Add(box);
        return sp;
    }

    /// <summary>Click, press the keys, done. Shows whether the shortcut actually works.</summary>
    FrameworkElement HotkeyRecorder(Func<string> get, Action<string> set, Func<bool> active)
    {
        var box = new TextBox { Width = 180, IsReadOnly = true, Cursor = Cursors.Hand, IsUndoEnabled = false, ToolTip = "Click, then press the keys" };
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        bool recording = false;

        string Shown() => string.IsNullOrWhiteSpace(get()) ? "Off" : get().Replace("+", " + ");
        void UpdateStatus()
        {
            if (recording) return;
            status.Text = string.IsNullOrWhiteSpace(get()) ? "" : active() ? "Working" : "Not available — another app is using it";
        }
        void Stop()
        {
            recording = false;
            App.Instance.SuspendHotkeys(false);
            box.Text = Shown();
            UpdateStatus();
        }

        box.Text = Shown();
        UpdateStatus();
        _afterApply.Add(UpdateStatus);

        box.GotKeyboardFocus += (_, _) =>
        {
            recording = true;
            App.Instance.SuspendHotkeys(true);
            box.Text = "Press the keys…";
            status.Text = "Recording";
        };
        box.LostKeyboardFocus += (_, _) => { if (recording) Stop(); };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
            if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                set("");
                Changed();
                Keyboard.ClearFocus();
                return;
            }

            var mods = Keyboard.Modifiers;
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                box.Text = parts.Count > 0 ? string.Join(" + ", parts) + " + …" : "Press the keys…";
                return;
            }
            if (parts.Count == 0)
            {
                box.Text = "Add Ctrl, Alt, Shift or Win";
                return;
            }

            parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
            var text = string.Join("+", parts);
            if (!MainWindow.TryParseHotkey(text, out uint _, out uint _))
            {
                box.Text = "That key can't be used";
                return;
            }
            set(text);
            Changed();
            Keyboard.ClearFocus();
        };

        var sp = new StackPanel();
        sp.Children.Add(box);
        sp.Children.Add(status);
        return sp;
    }

    static Button Btn(string label, Action click, string? glyph = null)
    {
        object content = label;
        if (glyph != null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Content = content, MinWidth = 96, Padding = new Thickness(12, 5, 12, 6) };
        b.Click += (_, _) => click();
        return b;
    }

    static StackPanel Row(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
        {
            if (i is FrameworkElement fe && sp.Children.Count > 0) fe.Margin = new Thickness(8, 0, 0, 0);
            sp.Children.Add(i);
        }
        return sp;
    }

    static string Px(double v) => $"{v:0} px";
    static string Ms(double v) => $"{v:0} ms";
    static string Pct(double v) => $"{v * 100:0}%";
    static string Times(double v) => $"{v:0.00}×";
    static string Secs(double v) => $"{v:0.0} s";

    static List<string> SplitList(string text) =>
        text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ───────────────────────────── pages ─────────────────────────────

    void BuildGeneral()
    {
        Section("Startup");
        Toggle("Start Isle when I sign in", "Adds Isle to your Windows startup apps.", () => _s.StartWithWindows, v => _s.StartWithWindows = v);

        Section("Do not disturb", "Quiet mode keeps the island from popping open. Cards still appear in the pill.");
        Toggle("Do not disturb", "Also in the island's right-click menu.", () => _s.DoNotDisturb, v => _s.DoNotDisturb = v);
        Toggle("Quiet hours", "Do not disturb automatically between these times.", () => _s.QuietHoursEnabled, v => _s.QuietHoursEnabled = v);
        Card("From", "24-hour time, e.g. 22:00", Text(() => _s.QuietFrom, v => _s.QuietFrom = v.Trim(), 90), indent: 24);
        Card("To", "e.g. 07:00", Text(() => _s.QuietTo, v => _s.QuietTo = v.Trim(), 90), indent: 24);
        Toggle("Still alert me for calls and finished timers", null, () => _s.AlertUrgentWhenQuiet, v => _s.AlertUrgentWhenQuiet = v);

        Section("Keyboard");
        Card("Open or close the island", null, HotkeyRecorder(() => _s.Hotkey, v => _s.Hotkey = v, () => App.Instance.HotkeysActive.open));
        Card("Hide or show the island", "Hides Isle completely; press again to bring it back.", HotkeyRecorder(() => _s.HideHotkey, v => _s.HideHotkey = v, () => App.Instance.HotkeysActive.hide));
        Page.Children.Add(Hint("Click a shortcut, then press the new keys. Backspace turns it off, Esc cancels.", new Thickness(0, 4, 0, 0)));

        Section("Try it", "Put sample cards on the island to see your settings in action.");
        Card("Preview", null, Row(
            Btn("Now playing", () => App.Instance.ShowDemo("media"), Glyphs.Music),
            Btn("Timer", () => App.Instance.ShowDemo("timer"), Glyphs.Timer),
            Btn("Notification", () => App.Instance.ShowDemo("notification"), Glyphs.Bell)));
    }

    void BuildIsland()
    {
        Section("Placement");
        var monitors = Forms.Screen.AllScreens
            .Select((sc, i) => ($"Display {i + 1} — {sc.Bounds.Width}×{sc.Bounds.Height}{(sc.Primary ? " (main)" : "")}", sc.DeviceName))
            .Prepend(("Main display", ""));
        Card("Display", "Which screen the island sits on.", Combo(monitors, () => _s.Monitor, v => _s.Monitor = v));
        Card("Position", null, Combo(new[] { ("Top left", "left"), ("Top center", "center"), ("Top right", "right") }, () => _s.Align, v => _s.Align = v));
        SliderCard("Horizontal offset", "Nudge it left or right (negative moves it left when centered).", -400, 400, 2, () => _s.OffsetX, v => _s.OffsetX = v, Px);
        SliderCard("Distance from top", null, 0, 80, 1, () => _s.TopOffset, v => _s.TopOffset = v, Px);
        SliderCard("Overall size", "Scales everything, text included.", 0.7, 1.6, 0.05, () => _s.Scale, v => _s.Scale = v, Times);

        Section("Shape");
        SliderCard("Idle pill width", "When nothing is happening.", 40, 300, 2, () => _s.IdleWidth, v => _s.IdleWidth = v, Px);
        SliderCard("Idle pill height", null, 8, 48, 1, () => _s.IdleHeight, v => _s.IdleHeight = v, Px);
        Toggle("Fit the compact pill to its content", "Short things like a timer get a shorter pill.", () => _s.CompactAutoWidth, v => _s.CompactAutoWidth = v);
        SliderCard(_s.CompactAutoWidth ? "Compact width (maximum)" : "Compact width", "When something is live but you're not hovering.", 160, 560, 2, () => _s.CompactWidth, v => _s.CompactWidth = v, Px);
        SliderCard("Compact height", null, 24, 56, 1, () => _s.CompactHeight, v => _s.CompactHeight = v, Px);
        SliderCard("Expanded width", null, 320, 620, 2, () => _s.ExpandedWidth, v => _s.ExpandedWidth = v, Px);
        SliderCard("Corner roundness", "100% is a full pill.", 0, 1, 0.05, () => _s.Roundness, v => _s.Roundness = v, Pct);

        Section("Compact pill shows");
        Toggle("Artwork or icon", null, () => _s.CompactShowArt, v => _s.CompactShowArt = v);
        Toggle("Title", null, () => _s.CompactShowTitle, v => _s.CompactShowTitle = v);
        Toggle("Visualizer, countdown or score", null, () => _s.CompactShowTrailing, v => _s.CompactShowTrailing = v);
        Toggle("Thin progress line", null, () => _s.CompactShowProgress, v => _s.CompactShowProgress = v);
        Toggle("Live dot", "For calls, live games and other live activities.", () => _s.CompactShowLiveDot, v => _s.CompactShowLiveDot = v);

        Section("Expanded island shows");
        Toggle("App name", null, () => _s.ExpandedShowSource, v => _s.ExpandedShowSource = v);
        Toggle("Progress bar and times", null, () => _s.ExpandedShowProgress, v => _s.ExpandedShowProgress = v);
        Toggle("Buttons", "Play/pause, skip, open, dismiss…", () => _s.ExpandedShowActions, v => _s.ExpandedShowActions = v);
        Toggle("\"2 of 3\" when several things are live", null, () => _s.ExpandedShowCount, v => _s.ExpandedShowCount = v);

        Section("When idle");
        Toggle("Hide the pill when nothing is happening", null, () => _s.HideWhenIdle, v => _s.HideWhenIdle = v);
        Toggle("Show the time in the idle pill", "Widen the idle pill a bit if the time doesn't fit.", () => _s.IdleShowClock, v => _s.IdleShowClock = v);
        Card("Quick timers", "Minutes, comma-separated (up to 5). Shown when you hover the idle island.",
            Text(() => string.Join(", ", _s.QuickTimers), v => _s.QuickTimers = SplitList(v).Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Take(5).ToList(), 160));
    }

    void BuildAppearance()
    {
        Section("Theme");
        Card("Theme", null, Combo(new[]
        {
            ("Midnight (true black)", "midnight"), ("Graphite", "graphite"), ("Glass", "glass"), ("Frost (light)", "frost"), ("Custom colors", "custom"),
        }, () => _s.Theme, v => _s.Theme = v, Rebuild));

        if (_s.Theme == "custom")
        {
            var c = _s.CustomTheme;
            Card("Background", "#RRGGBB, or #AARRGGBB for see-through", ColorField(() => c.Background, v => c.Background = v), indent: 24);
            Card("Text", null, ColorField(() => c.Foreground, v => c.Foreground = v), indent: 24);
            Card("Secondary text", null, ColorField(() => c.Secondary, v => c.Secondary = v), indent: 24);
            Card("Border", null, ColorField(() => c.Border, v => c.Border = v), indent: 24);
            Card("Buttons", null, ColorField(() => c.Button, v => c.Button = v), indent: 24);
            Card("Buttons on hover", null, ColorField(() => c.ButtonHover, v => c.ButtonHover = v), indent: 24);
        }

        Section("Accent");
        bool auto = !ThemeManager.TryParseColor(_s.Accent, out _);
        Toggle("Match the content", "Accent comes from album art, team colors and app colors.", () => auto, v => _s.Accent = v ? "auto" : "#0A84FF", after: Rebuild);
        if (!auto)
        {
            var swatches = new WrapPanel();
            foreach (var hex in new[] { "#0A84FF", "#30D158", "#FF9F0A", "#FF375F", "#BF5AF2", "#64D2FF", "#FFD60A", "#FFFFFF" })
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var sw = new Button
                {
                    Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0),
                    Background = Frozen(color), ToolTip = hex,
                    BorderThickness = new Thickness(string.Equals(_s.Accent, hex, StringComparison.OrdinalIgnoreCase) ? 3 : 1),
                };
                sw.Click += (_, _) => { _s.Accent = hex; Changed(); Rebuild(); };
                swatches.Children.Add(sw);
            }
            Card("Accent color", "Pick one, or type any color.", ColorField(() => _s.Accent, v => _s.Accent = v), swatches, indent: 24);
        }

        Section("Look");
        SliderCard("Opacity", null, 0.4, 1, 0.05, () => _s.Opacity, v => _s.Opacity = v, Pct);
        SliderCard("Border", "Outline thickness (the color comes from the theme).", 0, 3, 0.5, () => _s.BorderThickness, v => _s.BorderThickness = v, v => $"{v:0.#} px");
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).Select(n => (n, n)).Prepend(("Segoe UI Variable (default)", ""));
        Card("Font", null, Combo(fonts, () => _s.FontFamily, v => _s.FontFamily = v));
        Toggle("Animated music visualizer", "The bouncing bars. Off saves a little CPU.", () => _s.AnimatedVisualizer, v => _s.AnimatedVisualizer = v);
    }

    void BuildBehavior()
    {
        Section("Opening and closing");
        Toggle("Open on hover", "Off: click the pill to open it.", () => _s.HoverToExpand, v => _s.HoverToExpand = v);
        SliderCard("Hover delay", "How long to hover before it opens.", 0, 1000, 10, () => _s.HoverDelayMs, v => _s.HoverDelayMs = (int)v, Ms, 24);
        SliderCard("Close delay", "How long it stays open after the mouse leaves.", 0, 2000, 20, () => _s.CollapseDelayMs, v => _s.CollapseDelayMs = (int)v, Ms);
        Toggle("Clicking the open island opens the activity", "The game page, the downloaded file, the notification's link…", () => _s.ClickOpensActivity, v => _s.ClickOpensActivity = v);
        Toggle("Scroll on the island to switch activities", null, () => _s.ScrollToSwitch, v => _s.ScrollToSwitch = v);

        Section("Peek", "A peek is when the island opens by itself for a moment because something happened.");
        Toggle("Peek when something new happens", "Fine-tune per activity type and per app on the Activities and Apps pages.", () => _s.PeekOnNewActivity, v => _s.PeekOnNewActivity = v);
        SliderCard("Peek length", null, 1.5, 12, 0.5, () => _s.PeekSeconds, v => _s.PeekSeconds = v, Secs, 24);

        Section("Hiding");
        Toggle("Hide while a game or video is fullscreen", null, () => _s.HideInFullscreen, v => _s.HideInFullscreen = v);

        Section("Motion");
        SliderCard("Animation speed", null, 0.5, 2.5, 0.05, () => _s.AnimationSpeed, v => _s.AnimationSpeed = v, Times);
        SliderCard("Springiness", "How much it overshoots when opening. 0 = no bounce.", 0, 0.8, 0.02, () => _s.Springiness, v => _s.Springiness = v, Pct);
        Toggle("Reduce motion", "Quick fades instead of springs; the visualizer stops moving.", () => _s.ReduceMotion, v => _s.ReduceMotion = v);
    }

    void BuildActivities()
    {
        Page.Children.Add(Hint("Turn whole types of activity on or off, choose which may peek, and set priority — when several things are live, the highest priority gets the pill.", new Thickness(0, 0, 0, 6)));

        KindBlock("media", "Now playing", "Album art and controls for Spotify, browsers, Media Player, Amplite and any app that uses Windows media controls.", () =>
        {
            Toggle("Peek when the track changes", null, () => _s.MediaPeekOnTrackChange, v => _s.MediaPeekOnTrackChange = v, 24);
            SliderCard("Hide paused music after", "0 keeps it until the app closes.", 0, 120, 1, () => _s.MediaHidePausedAfterMinutes, v => _s.MediaHidePausedAfterMinutes = (int)v, v => v == 0 ? "Never" : $"{v:0} min", 24);
        });
        KindBlock("timer", "Timers", "Countdowns from the island, tray menu or your apps.");
        KindBlock("download", "Downloads", "Live progress for browser downloads.", () =>
        {
            Card("Folder", "Leave empty for your Windows Downloads folder.", Text(() => _s.DownloadsFolder, v => _s.DownloadsFolder = v.Trim(), 280), indent: 24);
        });
        KindBlock("call", "Calls & microphone", "Teams, Discord, Zoom, Phone Link, WhatsApp… detected from microphone use, with a call timer.", () =>
        {
            Toggle("Also show other apps using the microphone", "A privacy indicator for anything that isn't a calling app.", () => _s.ShowAnyMicUse, v => _s.ShowAnyMicUse = v, 24);
        });
        KindBlock("notification", "Windows notifications", "Experimental. Windows may refuse access to apps that aren't from the Store; your own apps can always notify through the local API.");
        KindBlock("sports", "Live sports", "Scores for your teams. Set leagues and teams on the Live sports page.");
        KindBlock("custom", "From your apps", "Activities that Amplite-style integrations, Savor, Fullcourt or scripts post through the local API.");
        KindBlock("update", "Isle updates", "The card that offers a new version of Isle.");
    }

    void KindBlock(string key, string title, string description, Action? extras = null)
    {
        var rule = _s.Kind(key);
        Section(title, description);
        Toggle("Show", null, () => rule.Enabled, v => rule.Enabled = v, after: Rebuild);
        if (!rule.Enabled) return;
        Toggle("Allow peeking", null, () => rule.Peek, v => rule.Peek = v);
        SliderCard("Priority", "Raise it to win the pill over other activities.", -50, 50, 5, () => rule.Boost, v => rule.Boost = (int)v, v => v == 0 ? "Normal" : v > 0 ? $"+{v:0}" : $"{v:0}");
        extras?.Invoke();
    }

    void BuildApps()
    {
        Page.Children.Add(Hint("Every app Isle has noticed, so you can choose exactly what reaches the island. Apps appear here the first time they play, call, notify or connect.", new Thickness(0, 0, 0, 6)));
        Page.Children.Add(Row(
            Btn("Show all again", () => { AppRegistry.ResetAll(); Rebuild(); }, "")));

        var all = AppRegistry.All.ToList();
        if (all.Count == 0)
        {
            Card("Nothing detected yet", "Play some music or start a call and come back.", null);
            return;
        }

        foreach (var group in all.GroupBy(r => r.Category).OrderBy(g => Array.IndexOf(AppRegistry.Categories.Keys.ToArray(), g.Key)))
        {
            Section(AppRegistry.Categories.TryGetValue(group.Key, out var label) ? label : group.Key);
            foreach (var rule in group.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                AppCard(rule);
        }
    }

    void AppCard(AppRule rule)
    {
        var r = rule;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };

        var peek = new CheckBox { Content = "Peek", IsChecked = r.Peek, IsEnabled = r.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        peek.Click += (_, _) => { r.Peek = peek.IsChecked == true; AppRegistry.Update(r); };

        var priority = new ComboBox { Width = 110, IsEnabled = r.Enabled, Margin = new Thickness(0, 0, 14, 0), ToolTip = "Priority" };
        foreach (var (lbl, val) in new[] { ("Low", -30), ("Normal", 0), ("High", 30), ("Highest", 60) })
            priority.Items.Add(new ComboBoxItem { Content = lbl, Tag = val });
        priority.SelectedItem = priority.Items.OfType<ComboBoxItem>().OrderBy(i => Math.Abs((int)i.Tag - r.Boost)).First();
        priority.SelectionChanged += (_, _) => { if (priority.SelectedItem is ComboBoxItem { Tag: int v }) { r.Boost = v; AppRegistry.Update(r); } };

        var show = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = r.Enabled };
        show.Click += (_, _) =>
        {
            r.Enabled = show.IsChecked == true;
            peek.IsEnabled = priority.IsEnabled = r.Enabled;
            AppRegistry.Update(r);
        };

        var forget = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = Icons },
            ToolTip = "Forget this app",
            Margin = new Thickness(14, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
        };
        forget.Click += (_, _) => { AppRegistry.Forget(r.Key); Rebuild(); };

        controls.Children.Add(peek);
        controls.Children.Add(priority);
        controls.Children.Add(show);
        controls.Children.Add(forget);

        var seen = r.LastSeen == default ? "" : $"Last seen {Ago(r.LastSeen)}";
        Card(r.Name, seen, controls);
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d < TimeSpan.FromMinutes(2)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} hr ago";
        return t.ToString("MMM d", CultureInfo.CurrentCulture);
    }

    void BuildAmplite()
    {
        Page.Children.Add(Hint("Isle talks to Amplite on this PC through Amplite's local API — the same one Mantel and the Stream Deck plugin use. Nothing to set up in Amplite.", new Thickness(0, 0, 0, 6)));

        _ampliteStatus = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Card("Status", null, _ampliteStatus);
        UpdateAmpliteStatus();

        Section("Integration");
        Toggle("Show Amplite on the island", "Cover, song, live progress and controls.", () => _s.AmpliteEnabled, v => _s.AmpliteEnabled = v);
        Toggle("Prefer Amplite over Spotify", "While Amplite has a song, hide the Spotify app's media card (Amplite sometimes plays through it in the background).", () => _s.AmplitePreferOverSpotify, v => _s.AmplitePreferOverSpotify = v);

        Section("Controls on the card");
        Toggle("Like button", "Adds or removes the song from your Liked Songs.", () => _s.AmpliteShowLike, v => _s.AmpliteShowLike = v);
        Toggle("EQ preset picker", "Switch Amplite's equalizer presets right from the island (when Amplite's engine is playing).", () => _s.AmpliteShowEq, v => _s.AmpliteShowEq = v);
        Toggle("Show the EQ preset next to \"Amplite\"", null, () => _s.AmpliteShowEqInTitle, v => _s.AmpliteShowEqInTitle = v);

        Section("Quick controls");
        Card("Send to Amplite", null, Row(
            Btn("Play / pause", () => AmpliteService.Command("toggle"), Glyphs.Play),
            Btn("Next", () => AmpliteService.Command("next"), Glyphs.Next),
            Btn("Vol −", () => AmpliteService.Command("vol:-10")),
            Btn("Vol +", () => AmpliteService.Command("vol:+10"))));
    }

    void UpdateAmpliteStatus()
    {
        if (_ampliteStatus == null) return;
        _ampliteStatus.Text = !AmpliteService.IsConnected
            ? (_s.AmpliteEnabled ? "Amplite isn't running." : "Integration is off.")
            : $"Connected to Amplite {AmpliteService.Version}{(AmpliteService.IsPlaying ? " — playing" : AmpliteService.HasTrack ? " — paused" : " — nothing playing")}";
    }

    void BuildSports()
    {
        Page.Children.Add(Hint("Live scores from ESPN — the same data Fullcourt uses. Turn the whole type on or off on the Activities page.", new Thickness(0, 0, 0, 6)));

        Section("Fullcourt");
        var app = FullcourtLink.AppPath;
        Card(app != null ? "Fullcourt for Windows is installed" : "Fullcourt for Windows isn't installed",
            app ?? "Games open on the Fullcourt website instead. Get the app from Fullcourt's GitHub Releases.",
            Row(Btn("Open Fullcourt", () => FullcourtLink.OpenInFullcourt(), Glyphs.Open),
                app == null ? Btn("Get the app", () => Ui.OpenUrl("https://github.com/larosa023/fullcourt/releases/latest"), Glyphs.Download) : new TextBlock()));
        Card("Open games in", "Auto uses the app when it's installed, otherwise the site. Leagues Fullcourt doesn't cover open on ESPN.",
            Combo(new[]
            {
                ("Auto (app, else site)", "auto"), ("Fullcourt app", "app"), ("Fullcourt website", "site"), ("ESPN", "espn"), ("A custom link", "custom"),
            }, () => _s.SportsOpenIn, v => _s.SportsOpenIn = v, Rebuild));
        if (_s.SportsOpenIn == "custom")
            Card("Custom link", null, Text(() => _s.SportsOpenUrl, v => _s.SportsOpenUrl = v.Trim(), 280), indent: 24);
        Card("Fullcourt website", "The GitHub Pages copy by default.", Text(() => _s.FullcourtSiteUrl, v => _s.FullcourtSiteUrl = v.Trim(), 280));

        Toggle("Follow Fullcourt", "Use the favorite teams you pick in Fullcourt, and track games you send with \"Show on Isle\".", () => _s.SportsUseFullcourt, v => _s.SportsUseFullcourt = v);

        var synced = FullcourtLink.Favorites.Count == 0
            ? "Nothing yet. In Fullcourt, make a team your team (turn on Settings → Isle on the website)."
            : string.Join(", ", FullcourtLink.Favorites.Select(f => $"{f.Name} ({f.League.ToUpperInvariant()})"));
        var trackedText = FullcourtLink.Tracked.Count == 0 ? "No games" : $"{FullcourtLink.Tracked.Count} game{(FullcourtLink.Tracked.Count == 1 ? "" : "s")}";
        Card("From Fullcourt", $"Your teams: {synced}\nTracking: {trackedText}" +
            (FullcourtLink.LastSync == default ? "" : $"\nLast synced {Ago(FullcourtLink.LastSync)}"),
            Btn("Clear", () => { FullcourtLink.ClearSynced(); Rebuild(); }), indent: 24);

        Section("Your teams in Isle");
        Card("Leagues", "nba, wnba, ncaam, ncaaw, nfl, ncaaf, mlb, nhl, mls, epl, laliga, ucl",
            Text(() => string.Join(", ", _s.SportsLeagues), v => _s.SportsLeagues = SplitList(v).Select(x => x.ToLowerInvariant()).ToList(), 280));
        Card("Favorite teams", "ESPN abbreviations like NY, BKN, NYG. Prefix a league if one is shared: nfl:NYJ. Added to the ones Fullcourt syncs.",
            Text(() => string.Join(", ", _s.FavoriteTeams), v => _s.FavoriteTeams = SplitList(v), 280));
        Toggle("Show every live game in those leagues", "Not just your favorites.", () => _s.SportsShowAllLive, v => _s.SportsShowAllLive = v);
        Toggle("Peek on score changes", "Never for basketball — it scores too often.", () => _s.SportsPeekOnScore, v => _s.SportsPeekOnScore = v);
    }

    void BuildIntegrations()
    {
        Page.Children.Add(Hint("A small API on this PC lets your apps (Savor, Fullcourt, scripts) put live activities on the island. Which apps may show is on the Apps page.", new Thickness(0, 0, 0, 6)));
        Section("Local API");
        Toggle("Enable the local API", "Listens on 127.0.0.1 only — never on your network.", () => _s.ApiEnabled, v => _s.ApiEnabled = v);
        Card("Port", null, Text(() => _s.ApiPort.ToString(), v => { if (int.TryParse(v, out var p) && p is > 1024 and < 65536) _s.ApiPort = p; }, 90));
        Card("Websites allowed to call it", "Desktop apps are always allowed. Sites need listing, e.g. https://fullcourt.netlify.app (localhost always works).",
            Text(() => string.Join(", ", _s.AllowedOrigins), v => _s.AllowedOrigins = SplitList(v).Select(o => o.TrimEnd('/')).ToList(), 280));

        Section("Try it");
        var sample = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Text = $"curl -X POST http://127.0.0.1:{_s.ApiPort}/v1/activities -H \"Content-Type: application/json\" " +
                   "-d \"{\\\"app\\\":\\\"Savor\\\",\\\"id\\\":\\\"dinner\\\",\\\"title\\\":\\\"Pasta is ready\\\",\\\"glyph\\\":\\\"check\\\",\\\"ttl\\\":30}\"",
        };
        Card("Example", "Full reference: docs/integration.md in the Isle repo.", null, sample);
        Card("Documentation", null, Btn("Open API docs", () => Ui.OpenUrl($"https://github.com/{UpdateService.Repo}/blob/main/docs/integration.md"), Glyphs.Open));
    }

    void BuildAbout()
    {
        Section("Updates");
        Toggle("Check for updates automatically", "Every 6 hours. Updates install with one click from the island.", () => _s.AutoCheckUpdates, v => _s.AutoCheckUpdates = v);
        Card($"Isle {AppInfo.VersionString}", UpdateService.AssetName == "Isle.exe" ? "Uses the .NET Desktop Runtime" : "Standalone build",
            Row(Btn("Check now", () => _ = App.Instance.Updates.CheckAsync(manual: true), Glyphs.Refresh),
                Btn("Release notes", () => Ui.OpenUrl($"https://github.com/{UpdateService.Repo}/releases"), Glyphs.Open)));

        Section("Files");
        Card("Settings and log", Paths.DataDir, Row(
            Btn("Open folder", () => Ui.OpenUrl(Paths.DataDir), Glyphs.Folder),
            Btn("Open log", () => Ui.OpenUrl(File.Exists(Paths.LogFile) ? Paths.LogFile : Paths.DataDir))));

        Section("Reset");
        Card("Restore default settings", "Keeps your app list; resets everything else.", Btn("Restore defaults", () =>
        {
            if (MessageBox.Show(this, "Reset all Isle settings to their defaults?", "Isle", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _apply.Stop();
            App.Instance.ApplySettings(new AppSettings());
            Close();
            App.Instance.ShowSettings();
        }));
    }
}
