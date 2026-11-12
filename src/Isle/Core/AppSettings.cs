using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Isle.Core;

public sealed class CustomThemeColors
{
    public string Background { get; set; } = "#FF000000";
    public string Foreground { get; set; } = "#FFFFFFFF";
    public string Secondary { get; set; } = "#FF8E8E93";
    public string Border { get; set; } = "#00000000";
    public string Button { get; set; } = "#FF1C1C1E";
    public string ButtonHover { get; set; } = "#FF2C2C2E";
}

/// <summary>Per activity type: show it at all, let it peek, and nudge its priority.</summary>
public sealed class KindRule
{
    public bool Enabled { get; set; } = true;
    public bool Peek { get; set; } = true;
    public int Boost { get; set; }
}

/// <summary>Persisted to %AppData%\Isle\settings.json. Modules read <see cref="Current"/> live.</summary>
public sealed class AppSettings
{
    /// <summary>The settings in effect right now.</summary>
    public static AppSettings Current { get; set; } = new();

    // ── Appearance ──
    public string Theme { get; set; } = "midnight";           // midnight | graphite | glass | frost | custom
    public string Accent { get; set; } = "auto";               // "auto" (album art / team colors) or #RRGGBB
    public CustomThemeColors CustomTheme { get; set; } = new();
    public double Opacity { get; set; } = 1.0;
    public double BorderThickness { get; set; } = 1;
    public string FontFamily { get; set; } = "";               // empty = Segoe UI Variable
    public bool AnimatedVisualizer { get; set; } = true;
    public string VisualizerMode { get; set; } = "real";       // real (follows the sound) | animated | off
    public bool ArtworkGlow { get; set; } = true;              // soft album-art color behind the open music card
    public double GlowStrength { get; set; } = 0.35;
    public bool AccentBorder { get; set; }                     // tint the pill's outline with the activity's color

    // ── Island shape & placement ──
    public string Monitor { get; set; } = "";                  // device name, "" = main display, "follow" = active window's display
    public string Align { get; set; } = "center";              // left | center | right
    public double OffsetX { get; set; }
    public double TopOffset { get; set; } = 6;
    public double Scale { get; set; } = 1.0;
    public double IdleWidth { get; set; } = 120;
    public double IdleHeight { get; set; } = 30;
    public double CompactWidth { get; set; } = 300;            // the maximum when CompactAutoWidth is on
    public bool CompactAutoWidth { get; set; } = true;
    public double CompactHeight { get; set; } = 36;
    public double ExpandedWidth { get; set; } = 420;
    public double Roundness { get; set; } = 1.0;               // 0 = square-ish, 1 = full pill

    // What the pill shows
    public bool CompactShowArt { get; set; } = true;
    public bool CompactShowTitle { get; set; } = true;
    public bool CompactShowTrailing { get; set; } = true;
    public bool CompactShowProgress { get; set; } = true;
    public bool CompactShowLiveDot { get; set; } = true;
    public bool ExpandedShowSource { get; set; } = true;
    public bool ExpandedShowProgress { get; set; } = true;
    public bool ExpandedShowActions { get; set; } = true;
    public bool ExpandedShowCount { get; set; } = true;
    public bool IdleShowClock { get; set; }
    public bool HideWhenIdle { get; set; }
    public List<int> QuickTimers { get; set; } = new() { 1, 5, 10, 25 };
    public bool CompanionBubble { get; set; } = true;          // second live activity as a small bubble beside the pill
    public bool IdleShowWeather { get; set; } = true;
    public bool IdleShowQuickActions { get; set; } = true;     // stopwatch and focus buttons on the open idle island

    // ── Behavior & motion ──
    public bool HoverToExpand { get; set; } = true;
    public int HoverDelayMs { get; set; } = 200;
    public int CollapseDelayMs { get; set; } = 380;
    public bool ClickOpensActivity { get; set; } = true;
    public bool ScrollToSwitch { get; set; } = true;
    public bool PeekOnNewActivity { get; set; } = true;
    public string PeekStyle { get; set; } = "expand";          // expand | bump (pill pulses and shows it, without opening)
    public bool ScrollAdjustsVolume { get; set; } = true;      // scrolling an open music or volume card changes volume
    public bool ClickToSeek { get; set; } = true;
    public string MaximizedBehavior { get; set; } = "stay";    // stay | hideidle (idle pill steps aside over maximized windows)
    public List<string> HideForApps { get; set; } = new();     // process names, e.g. "obs64", "valorant"
    public double PeekSeconds { get; set; } = 3.5;
    public bool HideInFullscreen { get; set; } = true;
    public double AnimationSpeed { get; set; } = 1.0;
    public double Springiness { get; set; } = 0.22;
    public bool ReduceMotion { get; set; }
    public int FrameRate { get; set; }                          // 0 = match the display (e.g. 144 Hz), else a cap
    public bool TrimMemory { get; set; } = true;               // hand unused memory back to Windows while idle
    public string Hotkey { get; set; } = "Ctrl+Alt+I";
    public string HideHotkey { get; set; } = "Ctrl+Alt+H";
    public bool StartWithWindows { get; set; }

    // Do not disturb
    public bool DoNotDisturb { get; set; }
    public bool QuietHoursEnabled { get; set; }
    public string QuietFrom { get; set; } = "22:00";
    public string QuietTo { get; set; } = "07:00";
    public bool AlertUrgentWhenQuiet { get; set; } = true;     // calls and finished timers still peek

    // ── Activity types ──
    public Dictionary<string, KindRule> Kinds { get; set; } = new()
    {
        ["media"] = new(), ["timer"] = new(), ["download"] = new(), ["call"] = new(),
        ["notification"] = new() { Enabled = false }, ["sports"] = new(), ["update"] = new(), ["custom"] = new(),
        ["system"] = new(),
    };

    // System
    public bool VolumeHud { get; set; } = true;
    public bool BatteryAlerts { get; set; } = true;
    public int BatteryLowPercent { get; set; } = 20;
    public bool LockKeys { get; set; } = true;
    public bool Screenshots { get; set; } = true;
    public bool ClipboardFlash { get; set; }
    public bool ClipboardShowText { get; set; }

    // Timers & focus
    public string TimerSound { get; set; } = "Alarm01";         // a Windows sound (C:\Windows\Media), a .wav path, "system" or "none"
    public int TimerSoundRepeats { get; set; } = 3;
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int FocusRounds { get; set; } = 4;
    public bool FocusAutoStartBreaks { get; set; } = true;
    public bool FocusDndDuringFocus { get; set; }

    // Weather (Open-Meteo; no account)
    public string WeatherLocation { get; set; } = "";
    public string WeatherPlace { get; set; } = "";
    public double WeatherLat { get; set; }
    public double WeatherLon { get; set; }
    public string WeatherUnits { get; set; } = "auto";         // auto | f | c

    // Now playing
    public bool MediaPeekOnTrackChange { get; set; }
    public int MediaHidePausedAfterMinutes { get; set; } = 10;
    public bool Lyrics { get; set; }                           // synced lyrics from LRCLIB on the open music card
    public bool LyricsInCompact { get; set; }                  // the current line replaces the song title in the pill
    public int LyricsOffsetMs { get; set; }                    // + shows lines earlier, - later

    // Discord voice (Discord's local RPC, with the user's own Discord application)
    public bool DiscordVoice { get; set; }
    public string DiscordClientId { get; set; } = "";
    public bool DiscordShowSpeaker { get; set; } = true;       // the person talking (avatar + name) in the pill
    public bool DiscordPeekOnJoin { get; set; } = true;

    // Amplite
    public bool AmpliteEnabled { get; set; } = true;
    public bool AmplitePreferOverSpotify { get; set; } = true;
    public bool AmpliteShowLike { get; set; } = true;
    public bool AmpliteShowEq { get; set; } = true;
    public bool AmpliteShowEqInTitle { get; set; } = true;

    // Downloads
    public string DownloadsFolder { get; set; } = "";

    // Calls
    public bool ShowAnyMicUse { get; set; } = true;

    // Sports
    public List<string> SportsLeagues { get; set; } = new() { "nba", "nfl" };
    public List<string> FavoriteTeams { get; set; } = new();
    public bool SportsShowAllLive { get; set; }
    public bool SportsPeekOnScore { get; set; } = true;
    public string SportsOpenUrl { get; set; } = "";            // used when SportsOpenIn = "custom"
    public string SportsOpenIn { get; set; } = "auto";         // auto | app | site | espn | custom
    public bool SportsUseFullcourt { get; set; } = true;       // follow favorites and tracked games from Fullcourt
    public string FullcourtSiteUrl { get; set; } = "https://larosa023.github.io/fullcourt/";

    // Local API
    public bool ApiEnabled { get; set; } = true;
    public int ApiPort { get; set; } = 47820;
    public List<string> AllowedOrigins { get; set; } = new();

    // Updates
    public bool AutoCheckUpdates { get; set; } = true;
    public string SkippedVersion { get; set; } = "";

    /// <summary>Bumped when a default changes in a way existing settings files should pick up.</summary>
    public int SettingsVersion { get; set; }

    void Migrate()
    {
        if (SettingsVersion < 2)
        {
            if (HoverDelayMs == 120) HoverDelayMs = 200; // 0.4: a hair longer so passing over it doesn't open it
            SettingsVersion = 2;
        }
        if (SettingsVersion < 3)
        {
            if (!AnimatedVisualizer) VisualizerMode = "off"; // 0.5: the on/off switch became a style choice
            SettingsVersion = 3;
        }
    }

    public KindRule Kind(ActivityKind kind) => Kind(KindKey(kind));

    public KindRule Kind(string key)
    {
        if (!Kinds.TryGetValue(key, out var rule))
        {
            rule = new KindRule { Enabled = key != "notification" };
            Kinds[key] = rule;
        }
        return rule;
    }

    public static string KindKey(ActivityKind kind) => kind.ToString().ToLowerInvariant();

    public bool InQuietHours(DateTime now)
    {
        if (!QuietHoursEnabled) return false;
        if (!TimeSpan.TryParse(QuietFrom, CultureInfo.InvariantCulture, out var from) ||
            !TimeSpan.TryParse(QuietTo, CultureInfo.InvariantCulture, out var to)) return false;
        var t = now.TimeOfDay;
        return from <= to ? t >= from && t < to : t >= from || t < to; // handles overnight ranges
    }

    /// <summary>Settings that require modules to be restarted when they change.</summary>
    public string ModuleSignature() => JsonSerializer.Serialize(new
    {
        media = Kind("media").Enabled, download = Kind("download").Enabled, call = Kind("call").Enabled,
        notification = Kind("notification").Enabled, sports = Kind("sports").Enabled, system = Kind("system").Enabled,
        DownloadsFolder, ApiEnabled, ApiPort, AmpliteEnabled, DiscordVoice, DiscordClientId,
        VolumeHud, BatteryAlerts, LockKeys, Screenshots, ClipboardFlash, WeatherLat, WeatherLon, WeatherUnits,
    });

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Options) ?? new AppSettings();
                loaded.Migrate();
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
        }
        var s = new AppSettings();
        s.Migrate();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
        }
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Options), Options) ?? new AppSettings();
}
