using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Isle.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Isle.Services;

/// <summary>
/// Small system moments: a volume HUD, battery and charging, Caps/Num Lock, screenshots and
/// (opt-in) clipboard copies. Each piece can be turned off in Settings → System.
/// </summary>
public sealed class SystemService : IModule
{
    const string VolumeId = "sys:volume", BatteryId = "sys:battery", LockId = "sys:lock", ClipId = "sys:clipboard";
    static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    static readonly Color Amber = Color.FromRgb(0xFF, 0x9F, 0x0A);
    static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);

    static AppSettings S => AppSettings.Current;

    /// <summary>Raised by the island window when the clipboard changes.</summary>
    public static event Action? ClipboardUpdated;
    public static void RaiseClipboard() => ClipboardUpdated?.Invoke();

    readonly ActivityHub _hub;
    readonly DispatcherTimer _keys, _battery;
    FileSystemWatcher? _shots;
    bool _audio, _caps, _num, _keysPrimed, _lowWarned;
    Forms.PowerLineStatus _power = Forms.PowerLineStatus.Unknown;
    DateTime _ignoreVolumeUntil = DateTime.UtcNow.AddSeconds(3); // startup readings aren't changes

    public SystemService(ActivityHub hub)
    {
        _hub = hub;
        _keys = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _keys.Tick += (_, _) => CheckKeys();
        _battery = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
        _battery.Tick += (_, _) => CheckBattery(false);
    }

    public void Start()
    {
        if (S.VolumeHud)
        {
            CoreAudio.Acquire();
            CoreAudio.Changed += OnVolume;
            _audio = true;
        }
        if (S.LockKeys) _keys.Start();
        if (S.BatteryAlerts && HasBattery())
        {
            _power = Forms.SystemInformation.PowerStatus.PowerLineStatus;
            SystemEvents.PowerModeChanged += OnPowerMode;
            _battery.Start();
            CheckBattery(false);
        }
        if (S.Screenshots) WatchScreenshots();
        if (S.ClipboardFlash) ClipboardUpdated += OnClipboard;
    }

    // ───────────────────────────── volume ─────────────────────────────

    void OnVolume(float level, bool muted)
    {
        if (DateTime.UtcNow < _ignoreVolumeUntil) return;
        ShowVolume(level, muted);
    }

    /// <summary>Puts the volume pill up (also used by scrolling on the music card).</summary>
    public void ShowVolume(float level, bool muted)
    {
        var a = _hub.GetOrAdd(VolumeId, ActivityKind.System, x =>
        {
            x.AppKey = "system:volume";
            x.Source = "Volume";
            x.Priority = 99;
            x.IsTransient = true;
            x.Accent = Blue;
            x.OnScroll = d => CoreAudio.Step(d * 2);
            x.Seek = f => CoreAudio.Set((float)f);
            x.Actions = new[]
            {
                new IslandAction("", "Mute", CoreAudio.ToggleMute),
                new IslandAction("", null, () => CoreAudio.Step(-5)),
                new IslandAction("", null, () => CoreAudio.Step(5), primary: true),
            };
        });
        int pct = (int)Math.Round(level * 100);
        a.Glyph = muted || pct == 0 ? "" : pct < 34 ? "" : pct < 67 ? "" : "";
        a.Title = muted ? "Muted" : "Volume";
        a.Trailing = muted ? "Off" : $"{pct}%";
        a.Badge = muted ? "Muted" : $"{pct}%";
        a.Progress = muted ? 0 : level;
        a.ProgressLeft = "0";
        a.ProgressRight = "100";
        a.SetTtl(TimeSpan.FromSeconds(1.8));
        _hub.Reorder();
    }

    // ───────────────────────────── lock keys ─────────────────────────────

    void CheckKeys()
    {
        bool caps = (Native.GetKeyState(0x14) & 1) != 0;
        bool num = (Native.GetKeyState(0x90) & 1) != 0;
        if (!_keysPrimed)
        {
            _keysPrimed = true;
            (_caps, _num) = (caps, num);
            return;
        }
        if (caps != _caps) ShowLock("Caps Lock", caps);
        else if (num != _num) ShowLock("Num Lock", num);
        (_caps, _num) = (caps, num);
    }

    void ShowLock(string name, bool on)
    {
        var a = _hub.GetOrAdd(LockId, ActivityKind.System, x =>
        {
            x.AppKey = "system:lockkeys";
            x.Priority = 99;
            x.IsTransient = true;
        });
        a.Title = name;
        a.Glyph = "";
        a.Accent = on ? Green : Color.FromRgb(0x8E, 0x8E, 0x93);
        a.Trailing = on ? "On" : "Off";
        a.Badge = on ? "On" : "Off";
        a.SetTtl(TimeSpan.FromSeconds(1.4));
        _hub.Reorder();
    }

    // ───────────────────────────── battery ─────────────────────────────

    static bool HasBattery()
    {
        var st = Forms.SystemInformation.PowerStatus.BatteryChargeStatus;
        return !st.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) && !st.HasFlag(Forms.BatteryChargeStatus.Unknown);
    }

    void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) Ui.Post(() => CheckBattery(true));
    }

    void CheckBattery(bool fromEvent)
    {
        var ps = Forms.SystemInformation.PowerStatus;
        int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
        var line = ps.PowerLineStatus;

        if (line != _power && _power != Forms.PowerLineStatus.Unknown)
        {
            bool charging = line == Forms.PowerLineStatus.Online;
            var a = new Activity(BatteryId, ActivityKind.System)
            {
                AppKey = "system:battery",
                Title = charging ? "Charging" : "On battery",
                Subtitle = charging ? null : RemainingText(ps),
                Glyph = charging ? "" : BatteryGlyph(pct),
                Accent = charging ? Green : pct <= S.BatteryLowPercent ? Red : Color.FromRgb(0x8E, 0x8E, 0x93),
                Trailing = $"{pct}%",
                Badge = $"{pct}%",
                Progress = pct / 100.0,
                Priority = 88,
            };
            a.SetTtl(TimeSpan.FromSeconds(4));
            _hub.Add(a);
            _hub.Peek(a);
            if (charging) _lowWarned = false;
        }
        _power = line;

        if (line != Forms.PowerLineStatus.Online && pct <= S.BatteryLowPercent && !_lowWarned)
        {
            _lowWarned = true;
            var a = new Activity(BatteryId, ActivityKind.System)
            {
                AppKey = "system:battery",
                Title = pct <= 10 ? "Battery very low" : "Battery low",
                Subtitle = RemainingText(ps) ?? "Plug in soon",
                Glyph = BatteryGlyph(pct),
                Accent = Red,
                Trailing = $"{pct}%",
                Badge = $"{pct}%",
                Progress = pct / 100.0,
                Priority = 92,
                IsLive = true,
            };
            a.Actions = new[] { new IslandAction(Glyphs.Check, "OK", () => _hub.Remove(BatteryId), primary: true) };
            a.SetTtl(TimeSpan.FromMinutes(2));
            _hub.Add(a);
            _hub.Peek(a);
        }
    }

    static string? RemainingText(Forms.PowerStatus ps)
    {
        int secs = ps.BatteryLifeRemaining;
        return secs > 0 ? $"About {Fmt.Human(TimeSpan.FromSeconds(secs))} left" : null;
    }

    static string BatteryGlyph(int pct) => char.ConvertFromUtf32(0xE850 + Math.Clamp(pct / 10, 0, 9));

    // ───────────────────────────── screenshots ─────────────────────────────

    void WatchScreenshots()
    {
        var folder = Native.ScreenshotsFolder();
        try
        {
            Directory.CreateDirectory(folder);
            _shots = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false,
            };
            _shots.Created += (_, e) =>
            {
                var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg") _ = ShowScreenshotAsync(e.FullPath);
            };
            _shots.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Info("screenshots folder: " + ex.Message);
        }
    }

    async Task ShowScreenshotAsync(string path)
    {
        // The file is still being written when it appears; wait until it can be read.
        byte[]? bytes = null;
        for (int i = 0; i < 20 && bytes == null; i++)
        {
            await Task.Delay(150).ConfigureAwait(false);
            try { bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return; }
        }
        if (bytes == null) return;
        var thumb = Images.Decode(bytes, 160);

        await Ui.InvokeAsync(() =>
        {
            var id = "sys:shot:" + Path.GetFileName(path);
            var a = new Activity(id, ActivityKind.System)
            {
                AppKey = "system:screenshots",
                Source = "Screenshot saved",
                Title = Path.GetFileNameWithoutExtension(path),
                Subtitle = "Saved to Pictures › Screenshots",
                Glyph = "",
                Image = thumb,
                Accent = Blue,
                Priority = 80,
                OnOpen = () => Ui.OpenUrl(path),
            };
            a.Actions = new[]
            {
                new IslandAction(Glyphs.Open, "Open", () => { Ui.OpenUrl(path); _hub.Remove(id); }, primary: true),
                new IslandAction("", "Copy", () => { CopyImage(path); _hub.Remove(id); }),
                new IslandAction(Glyphs.Folder, null, () => { Ui.ShowInFolder(path); _hub.Remove(id); }),
            };
            a.SetTtl(TimeSpan.FromSeconds(8));
            _hub.Add(a);
            _hub.Peek(a);
        }).ConfigureAwait(false);
    }

    static void CopyImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            Clipboard.SetImage(bmp);
        }
        catch (Exception ex)
        {
            Log.Error("copy screenshot", ex);
        }
    }

    // ───────────────────────────── clipboard ─────────────────────────────

    void OnClipboard()
    {
        string title = "Copied", subtitle = "";
        string glyph = "";
        try
        {
            if (Clipboard.ContainsImage()) { title = "Copied image"; glyph = ""; }
            else if (Clipboard.ContainsFileDropList())
            {
                var n = Clipboard.GetFileDropList().Count;
                title = n == 1 ? "Copied 1 file" : $"Copied {n} files";
                glyph = "";
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                title = "Copied";
                if (S.ClipboardShowText)
                {
                    text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    subtitle = text.Length > 80 ? text[..80] + "…" : text;
                }
                else
                {
                    subtitle = $"{text.Length} characters";
                }
            }
            else return;
        }
        catch
        {
            return; // another app is holding the clipboard
        }

        var a = _hub.GetOrAdd(ClipId, ActivityKind.System, x =>
        {
            x.AppKey = "system:clipboard";
            x.Priority = 97;
            x.IsTransient = true;
            x.Accent = Blue;
        });
        a.Title = title;
        a.Glyph = glyph;
        a.Subtitle = subtitle.Length > 0 ? subtitle : null;
        a.Trailing = S.ClipboardShowText && subtitle.Length > 0 ? subtitle : null;
        a.SetTtl(TimeSpan.FromSeconds(1.6));
        _hub.Reorder();
    }

    public void Dispose()
    {
        if (_audio)
        {
            CoreAudio.Changed -= OnVolume;
            CoreAudio.Release();
        }
        _keys.Stop();
        _battery.Stop();
        SystemEvents.PowerModeChanged -= OnPowerMode;
        ClipboardUpdated -= OnClipboard;
        if (_shots != null)
        {
            _shots.EnableRaisingEvents = false;
            _shots.Dispose();
        }
        _hub.RemoveWhere(a => a.Kind == ActivityKind.System);
    }
}
