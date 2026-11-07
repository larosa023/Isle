using System;
using System.Collections.Generic;
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

/// <summary>Persisted to %AppData%\Isle\settings.json.</summary>
public sealed class AppSettings
{
    // Appearance
    public string Theme { get; set; } = "midnight";           // midnight | graphite | glass | frost | custom
    public string Accent { get; set; } = "auto";               // "auto" (album art / team colors) or a color like #0A84FF
    public CustomThemeColors CustomTheme { get; set; } = new();
    public double Scale { get; set; } = 1.0;
    public double TopOffset { get; set; } = 6;
    public bool HideWhenIdle { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public bool AnimatedVisualizer { get; set; } = true;

    // Behavior
    public bool HoverToExpand { get; set; } = true;
    public bool PeekOnNewActivity { get; set; } = true;
    public double PeekSeconds { get; set; } = 3.5;
    public bool StartWithWindows { get; set; }

    // Now playing
    public bool MediaEnabled { get; set; } = true;
    public bool MediaPeekOnTrackChange { get; set; }
    public int MediaHidePausedAfterMinutes { get; set; } = 10;

    // Downloads
    public bool DownloadsEnabled { get; set; } = true;
    public string DownloadsFolder { get; set; } = "";          // empty = the Windows Downloads folder

    // Calls / microphone
    public bool CallsEnabled { get; set; } = true;
    public bool ShowAnyMicUse { get; set; } = true;

    // Windows notifications (experimental)
    public bool NotificationsEnabled { get; set; }
    public List<string> MutedApps { get; set; } = new();

    // Sports
    public bool SportsEnabled { get; set; } = true;
    public List<string> SportsLeagues { get; set; } = new() { "nba", "nfl" };
    public List<string> FavoriteTeams { get; set; } = new(); // "NYK", or "nba:NYK" to pin a league
    public bool SportsShowAllLive { get; set; }
    public bool SportsPeekOnScore { get; set; } = true;
    public string SportsOpenUrl { get; set; } = "";            // e.g. your Fullcourt URL; empty = ESPN game page

    // Local integration API (Amplite, Savor, Fullcourt, scripts…)
    public bool ApiEnabled { get; set; } = true;
    public int ApiPort { get; set; } = 47820;
    public List<string> AllowedOrigins { get; set; } = new(); // websites allowed to call the API from a browser

    // Updates
    public bool AutoCheckUpdates { get; set; } = true;
    public string SkippedVersion { get; set; } = "";

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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
        }
        var s = new AppSettings();
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
