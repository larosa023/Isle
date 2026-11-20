using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Controls;

/// <summary>
/// The sound panel that opens on the island: the speakers, every app playing sound (volume, mute,
/// live level), and the microphone (level, mute, live level) — plus switching the default
/// speakers and microphone. Everything updates live while it's open and stops when it closes.
/// </summary>
public sealed class MixerPanel : StackPanel
{
    const string Speaker = "", SpeakerOff = "", Mic = "", MicOff = "";
    const string Chevron = "", Close = "";

    /// <summary>Asks the island to show this menu (so the island stays open while it's up).</summary>
    public event Action<ContextMenu, UIElement>? MenuRequested;
    public event Action? CloseRequested;
    /// <summary>Rows came or went; the island re-measures.</summary>
    public event Action? LayoutChanged;

    readonly DispatcherTimer _meters, _refresh;
    readonly StackPanel _apps = new();
    readonly ScrollViewer _appsScroll;
    readonly TextBlock _noApps;
    readonly Button _outputChip, _micChip;
    readonly StackPanel _micSection = new();
    readonly Dictionary<string, Row> _appRows = new();
    Row? _master, _mic;
    bool _open;

    static AppSettings S => AppSettings.Current;

    public MixerPanel()
    {
        _meters = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _meters.Tick += (_, _) => UpdateMeters();
        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => Sync();

        // Header: title, then close.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Sound", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = IconButton(Close, "Close", () => CloseRequested?.Invoke(), 12);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        Children.Add(header);

        _outputChip = Chip(() => DeviceMenu(capture: false, _outputChip!));
        Children.Add(SectionHeader("Output", _outputChip));

        _noApps = new TextBlock { Text = "No apps are playing sound right now.", FontSize = 12, Margin = new Thickness(2, 6, 0, 4) };
        _noApps.SetResourceReference(TextBlock.ForegroundProperty, "Island.Secondary");
        _appsScroll = new ScrollViewer
        {
            Content = _apps,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 6 * Row.H,
            Focusable = false,
            PanningMode = PanningMode.VerticalOnly,
        };

        _micChip = Chip(() => DeviceMenu(capture: true, _micChip!));
        _micSection.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 10, 0, 8), Opacity = 0.6 });
        ((Border)_micSection.Children[0]).SetResourceReference(Border.BackgroundProperty, "Island.Track");
        _micSection.Children.Add(SectionHeader("Microphone", _micChip));
    }

    // ───────────────────────────── open / close ─────────────────────────────

    public void Open()
    {
        if (_open) return;
        _open = true;
        CoreAudio.Acquire();
        Sync();
        _meters.Start();
        _refresh.Start();
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        _meters.Stop();
        _refresh.Stop();
        foreach (var r in _appRows.Values) r.App = null;
        CoreAudio.ReleaseMixer();
        CoreAudio.Release();
    }

    // ───────────────────────────── building ─────────────────────────────

    void Sync()
    {
        CoreAudio.RefreshMixer();
        bool changed = false;

        // Speakers ("everything").
        if (_master == null)
        {
            _master = new Row(this, Speaker, "All sound")
            {
                Get = () => CoreAudio.Get().level,
                Set = v => CoreAudio.Set(v),
                GetMuted = () => CoreAudio.Get().muted,
                SetMuted = m => { if (CoreAudio.Get().muted != m) CoreAudio.ToggleMute(); },
                Peak = CoreAudio.Peak,
            };
            Children.Insert(Children.IndexOf(SectionOf(_outputChip)) + 1, _master);
            Children.Insert(Children.IndexOf(_master) + 1, _appsScroll);
            changed = true;
        }
        SetChip(_outputChip, CoreAudio.DefaultDeviceName(false) ?? "No speakers");

        // Apps — rows stay put (and keep any drag in progress); only their audio sessions are swapped.
        var apps = CoreAudio.MixerApps;
        var keys = new HashSet<string>(apps.Select(a => a.Key));
        foreach (var key in _appRows.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _apps.Children.Remove(_appRows[key]);
            _appRows.Remove(key);
            changed = true;
        }
        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            if (!_appRows.TryGetValue(app.Key, out var row))
            {
                row = new Row(this, app.IsSystem ? "" : Speaker, app.Name, app.Icon);
                row.Get = () => row.App?.Volume ?? 0;
                row.Set = v => row.App?.SetVolume((float)v);
                row.GetMuted = () => row.App?.Muted ?? false;
                row.SetMuted = m => row.App?.SetMuted(m);
                row.Peak = () => row.App?.Peak() ?? 0;
                _appRows[app.Key] = row;
                changed = true;
            }
            row.App = app;
            row.Opacity = app.Active ? 1 : 0.6;
            if (_apps.Children.IndexOf(row) != i)
            {
                _apps.Children.Remove(row);
                _apps.Children.Insert(Math.Min(i, _apps.Children.Count), row);
            }
        }
        bool none = apps.Count == 0;
        if (none && !_apps.Children.Contains(_noApps)) { _apps.Children.Add(_noApps); changed = true; }
        if (!none && _apps.Children.Contains(_noApps)) { _apps.Children.Remove(_noApps); changed = true; }

        // Microphone.
        bool hasMic = CoreAudio.HasMic;
        if (hasMic && !Children.Contains(_micSection))
        {
            Children.Add(_micSection);
            changed = true;
        }
        else if (!hasMic && Children.Contains(_micSection))
        {
            Children.Remove(_micSection);
            changed = true;
        }
        if (hasMic)
        {
            _mic ??= new Row(this, Mic, "Input level")
            {
                Get = () => CoreAudio.MicState().level,
                Set = v => CoreAudio.SetMicLevel((float)v),
                GetMuted = () => CoreAudio.MicState().muted,
                SetMuted = CoreAudio.SetDefaultMicMuted,
                Peak = CoreAudio.MicPeak,
                MutedGlyph = MicOff,
            };
            if (!_micSection.Children.Contains(_mic)) _micSection.Children.Add(_mic);
            SetChip(_micChip, CoreAudio.DefaultDeviceName(true) ?? "Microphone");
        }

        _master.Refresh();
        foreach (var r in _appRows.Values) r.Refresh();
        _mic?.Refresh();
        if (changed) LayoutChanged?.Invoke();
    }

    void UpdateMeters()
    {
        _master?.UpdateMeter();
        foreach (var r in _appRows.Values) r.UpdateMeter();
        if (_mic != null && Children.Contains(_micSection)) _mic.UpdateMeter();
    }

    void DeviceMenu(bool capture, UIElement anchor)
    {
        var menu = new ContextMenu();
        var devices = CoreAudio.Devices(capture);
        if (devices.Count == 0) menu.Items.Add(new MenuItem { Header = capture ? "No microphones" : "No speakers", IsEnabled = false });
        foreach (var d in devices)
        {
            var dev = d;
            var mi = new MenuItem { Header = dev.Name, IsCheckable = true, IsChecked = dev.IsDefault };
            mi.Click += (_, _) =>
            {
                if (!dev.IsDefault && !CoreAudio.SetDefaultDevice(dev.Id)) Log.Info("couldn't switch to " + dev.Name);
                Dispatcher.InvokeAsync(Sync, DispatcherPriority.Background);
            };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Windows sound settings…" };
        settings.Click += (_, _) => Ui.OpenUrl("ms-settings:sound");
        menu.Items.Add(settings);
        MenuRequested?.Invoke(menu, anchor);
    }

    // ───────────────────────────── pieces ─────────────────────────────

    static FrameworkElement SectionHeader(string title, Button chip)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var t = new TextBlock { Text = title, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Island.Secondary");
        g.Children.Add(t);
        Grid.SetColumn(chip, 1);
        g.Children.Add(chip);
        g.Tag = chip;
        return g;
    }

    UIElement SectionOf(Button chip) => Children.OfType<Grid>().First(g => ReferenceEquals(g.Tag, chip));

    Button Chip(Action click)
    {
        var text = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 230 };
        var chev = new TextBlock { Text = Chevron, FontSize = 9, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chev.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(text);
        sp.Children.Add(chev);
        var b = new Button { Content = sp, HorizontalAlignment = HorizontalAlignment.Right, Tag = text, ToolTip = "Switch device", Margin = new Thickness(0) };
        b.SetResourceReference(StyleProperty, "IslandButton");
        b.Click += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    static void SetChip(Button chip, string name)
    {
        if (chip.Tag is TextBlock t && t.Text != name) t.Text = name;
    }

    static Button IconButton(string glyph, string tip, Action click, double size = 14)
    {
        var t = new TextBlock { Text = glyph, FontSize = size };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        var b = new Button { Content = t, ToolTip = tip, Margin = new Thickness(0) };
        b.SetResourceReference(StyleProperty, "IslandButton");
        b.Click += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    /// <summary>One line: icon · name over a slider · percent · mute.</summary>
    sealed class Row : Grid
    {
        public const double H = 46;

        public Func<double> Get = () => 0;
        public Action<double> Set = _ => { };
        public Func<bool> GetMuted = () => false;
        public Action<bool> SetMuted = _ => { };
        public Func<float> Peak = () => 0;
        public string MutedGlyph = SpeakerOff;
        public MixerApp? App;

        readonly LevelSlider _slider = new();
        readonly TextBlock _pct = new() { FontSize = 12, Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBlock _muteIcon = new() { FontSize = 14 };
        readonly string _glyph;
        bool _muted;
        double _level;

        public Row(MixerPanel owner, string glyph, string name, ImageSource? icon = null)
        {
            _glyph = glyph;
            Height = H;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement art;
            if (icon != null)
            {
                art = new Border { Width = 26, Height = 26, Background = new ImageBrush(icon) { Stretch = Stretch.Uniform } };
                RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
            }
            else
            {
                var g = new TextBlock { Text = glyph, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                g.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
                var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Child = g };
                circle.SetResourceReference(Border.BackgroundProperty, "Island.Accent");
                art = circle;
            }
            art.Margin = new Thickness(0, 0, 12, 0);
            art.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(art);

            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            middle.Children.Add(new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(1, 0, 0, -1) });
            _slider.SetResourceReference(LevelSlider.FillBrushProperty, "Island.Accent");
            _slider.SetResourceReference(LevelSlider.TrackBrushProperty, "Island.Track");
            _slider.SetResourceReference(LevelSlider.ThumbBrushProperty, "Island.Foreground");
            _slider.Margin = new Thickness(-6, 0, 0, 0);
            _slider.ValueChanged += v =>
            {
                Set(v);
                _pct.Text = $"{Math.Round(v * 100)}%";
            };
            middle.Children.Add(_slider);
            SetColumn(middle, 1);
            Children.Add(middle);

            _pct.SetResourceReference(TextBlock.ForegroundProperty, "Island.Secondary");
            _pct.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
            SetColumn(_pct, 2);
            Children.Add(_pct);

            _muteIcon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            var mute = new Button { Content = _muteIcon, ToolTip = "Mute", Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            mute.SetResourceReference(StyleProperty, "IslandButton");
            mute.Click += (_, e) =>
            {
                e.Handled = true;
                SetMuted(!_muted);
                Refresh();
            };
            SetColumn(mute, 3);
            Children.Add(mute);
        }

        public void Refresh()
        {
            _muted = GetMuted();
            if (!_slider.Dragging) _slider.Value = Get();
            _slider.Dimmed = _muted;
            _pct.Text = _muted ? "Off" : $"{Math.Round(_slider.Value * 100)}%";
            var g = _muted ? MutedGlyph : _glyph == Mic ? Mic : Speaker;
            if (_muteIcon.Text != g) _muteIcon.Text = g;
        }

        public void UpdateMeter()
        {
            double target = _muted ? 0 : Peak();
            // Fast attack, gentle release — reads like a real meter instead of flickering.
            _level = target > _level ? target : Math.Max(target, _level - 0.06);
            if (Math.Abs(_slider.Level - _level) > 0.004) _slider.Level = _level;
        }
    }
}
