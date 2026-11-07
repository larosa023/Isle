using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Isle.Core;
using Microsoft.Win32;

namespace Isle.Services;

/// <summary>
/// Everything Isle knows about Fullcourt (larosa023/fullcourt): where the Windows app is installed,
/// the favorite teams Fullcourt told us about, and the games you asked Isle to track from Fullcourt.
/// Synced state lives in %AppData%\Isle\fullcourt.json. UI thread only.
/// </summary>
public static class FullcourtLink
{
    /// <summary>Leagues the Fullcourt site and app cover.</summary>
    public static readonly string[] Leagues = { "nba", "wnba", "ncaam", "ncaaw" };

    /// <summary>Sites allowed to call Isle's API without being listed in settings.</summary>
    public static readonly string[] Origins =
    {
        "https://larosa023.github.io", "https://nbafullcourty.netlify.app", "https://fullcourt-46z.pages.dev",
        "https://tauri.localhost", "http://tauri.localhost",
    };

    public sealed class Favorite
    {
        public string League { get; set; } = "";
        public string Abbr { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class TrackedGame
    {
        public string League { get; set; } = "";
        public string Game { get; set; } = "";
        public DateTime Added { get; set; }
    }

    sealed class State
    {
        public List<Favorite> Favorites { get; set; } = new();
        public List<TrackedGame> Tracked { get; set; } = new();
        public DateTime LastSync { get; set; }
        public string From { get; set; } = "";
    }

    static readonly string File = Path.Combine(Paths.DataDir, "fullcourt.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    static State _state = Load();
    static string? _exe;
    static DateTime _exeCheckedAt;

    /// <summary>Favorites or tracked games changed.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<Favorite> Favorites => _state.Favorites;
    public static IReadOnlyList<TrackedGame> Tracked => _state.Tracked;
    public static DateTime LastSync => _state.LastSync;
    public static string LastSyncFrom => _state.From;

    public static bool Covers(string league) => Leagues.Contains(league, StringComparer.OrdinalIgnoreCase);

    // ───────────────────────── sync from Fullcourt ─────────────────────────

    /// <summary>Fullcourt reports its favorite team for one league (null = none).</summary>
    public static void SyncFavorite(string league, string? abbr, string? name, string from)
    {
        league = league.Trim().ToLowerInvariant();
        if (!Covers(league)) return;
        _state.Favorites.RemoveAll(f => f.League == league);
        if (!string.IsNullOrWhiteSpace(abbr))
            _state.Favorites.Add(new Favorite { League = league, Abbr = abbr.Trim().ToUpperInvariant(), Name = name?.Trim() ?? abbr.Trim() });
        _state.LastSync = DateTime.Now;
        _state.From = from;
        Save();
    }

    public static void Track(string league, string game, bool on)
    {
        league = league.Trim().ToLowerInvariant();
        if (!Covers(league) || string.IsNullOrWhiteSpace(game)) return;
        _state.Tracked.RemoveAll(t => t.League == league && t.Game == game);
        if (on) _state.Tracked.Add(new TrackedGame { League = league, Game = game.Trim(), Added = DateTime.Now });
        Save();
    }

    public static bool IsTracked(string league, string game) =>
        _state.Tracked.Any(t => t.League == league && t.Game == game);

    /// <summary>Called when a tracked game ends, or a day later regardless.</summary>
    public static void PruneTracked(Func<TrackedGame, bool> finished)
    {
        int n = _state.Tracked.RemoveAll(t => finished(t) || DateTime.Now - t.Added > TimeSpan.FromHours(30));
        if (n > 0) Save();
    }

    public static void ClearSynced()
    {
        _state = new State();
        Save();
    }

    // ───────────────────────── opening games ─────────────────────────

    /// <summary>Fullcourt for Windows, if installed.</summary>
    public static string? AppPath
    {
        get
        {
            if (DateTime.UtcNow - _exeCheckedAt < TimeSpan.FromMinutes(2)) return _exe;
            _exeCheckedAt = DateTime.UtcNow;
            _exe = FindApp();
            return _exe;
        }
    }

    static string? FindApp()
    {
        try
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var root = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is not string display || !display.Equals("Fullcourt", StringComparison.OrdinalIgnoreCase)) continue;
                    var icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                    if (icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(icon)) return icon;
                    if (k.GetValue("InstallLocation") is string dir)
                    {
                        dir = dir.Trim('"');
                        foreach (var exe in new[] { "Fullcourt.exe", "fullcourt.exe" })
                            if (System.IO.File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("fullcourt find", ex);
        }
        var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fullcourt", "Fullcourt.exe");
        return System.IO.File.Exists(guess) ? guess : null;
    }

    public static string SiteUrl(string? league = null, string? game = null)
    {
        var s = AppSettings.Current.FullcourtSiteUrl;
        var baseUrl = string.IsNullOrWhiteSpace(s) ? "https://larosa023.github.io/fullcourt/" : s.Trim();
        if (league == null && game == null) return baseUrl;
        var sep = baseUrl.Contains('?') ? "&" : "?";
        return baseUrl + sep + "league=" + Uri.EscapeDataString(league ?? "nba") + (game != null ? "&game=" + Uri.EscapeDataString(game) : "");
    }

    /// <summary>Where a sports card opens, following Settings → Live sports → Open games in.</summary>
    public static Action? OpenerFor(string league, string gameId, string? espnLink)
    {
        var mode = (AppSettings.Current.SportsOpenIn ?? "auto").ToLowerInvariant();
        bool covered = Covers(league);

        if (mode == "custom" && !string.IsNullOrWhiteSpace(AppSettings.Current.SportsOpenUrl))
        {
            var u = AppSettings.Current.SportsOpenUrl.Trim();
            return () => Ui.OpenUrl(u);
        }
        if (mode == "espn" || !covered)
            return espnLink == null ? null : () => Ui.OpenUrl(espnLink);
        if (mode == "site")
            return () => Ui.OpenUrl(SiteUrl(league, gameId));
        // auto / app: the app when it's installed, otherwise the site
        return () => OpenInFullcourt(league, gameId);
    }

    public static string OpenLabel(string league)
    {
        var mode = (AppSettings.Current.SportsOpenIn ?? "auto").ToLowerInvariant();
        if (mode == "custom") return "Open";
        if (mode == "espn" || !Covers(league)) return "ESPN";
        return "Fullcourt";
    }

    public static void OpenInFullcourt(string? league = null, string? gameId = null)
    {
        var exe = AppPath;
        if (exe != null && AppSettings.Current.SportsOpenIn is not "site")
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                if (league != null) { psi.ArgumentList.Add("--league"); psi.ArgumentList.Add(league); }
                if (gameId != null) { psi.ArgumentList.Add("--game"); psi.ArgumentList.Add(gameId); }
                Process.Start(psi);
                return;
            }
            catch (Exception ex)
            {
                Log.Error("open fullcourt app", ex);
            }
        }
        Ui.OpenUrl(SiteUrl(league, gameId));
    }

    // ───────────────────────── storage ─────────────────────────

    static State Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<State>(System.IO.File.ReadAllText(File), Json) ?? new State();
        }
        catch (Exception ex)
        {
            Log.Error("fullcourt load", ex);
        }
        return new State();
    }

    static void Save()
    {
        try
        {
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            System.IO.File.Move(tmp, File, true);
        }
        catch (Exception ex)
        {
            Log.Error("fullcourt save", ex);
        }
        Changed?.Invoke();
    }
}
