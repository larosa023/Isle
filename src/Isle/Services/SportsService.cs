using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Isle.Core;

namespace Isle.Services;

/// <summary>
/// Live scores for favorite teams from ESPN's public scoreboard feed. Polls every 20 s while a
/// favorite is playing, every minute right before tip-off, and every 5 minutes otherwise.
/// </summary>
public sealed class SportsService : IModule
{
    static readonly Dictionary<string, string> LeaguePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nba"] = "basketball/nba",
        ["wnba"] = "basketball/wnba",
        ["ncaam"] = "basketball/mens-college-basketball",
        ["ncaaw"] = "basketball/womens-college-basketball",
        ["nfl"] = "football/nfl",
        ["ncaaf"] = "football/college-football",
        ["mlb"] = "baseball/mlb",
        ["nhl"] = "hockey/nhl",
        ["mls"] = "soccer/usa.1",
        ["epl"] = "soccer/eng.1",
        ["laliga"] = "soccer/esp.1",
        ["ucl"] = "soccer/uefa.champions",
    };

    sealed record Favorite(string? League, string Abbr);

    sealed record Game(
        string Id, string League, string Sport, string State, string Detail,
        string Away, string Home, string AwayScore, string HomeScore,
        Color? Accent, string? Link, DateTime StartUtc, bool Tracked);

    sealed class Seen
    {
        public string Score = "";
        public bool WasLive;
        public bool FinalShown;
    }

    readonly ActivityHub _hub;
    static AppSettings S => AppSettings.Current;
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<string, Seen> _seen = new();

    public SportsService(ActivityHub hub, AppSettings settings)
    {
        _hub = hub;
            }

    TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<string, string> _actionKeys = new();

    public void Start()
    {
        FullcourtLink.Changed += Wake;
        _ = LoopAsync(_cts.Token);
    }

    /// <summary>Re-poll right away (Fullcourt synced a favorite or asked to track a game).</summary>
    void Wake() => _wake.TrySetResult();

    /// <summary>Your favorites from Settings plus, when enabled, the ones Fullcourt synced.</summary>
    static List<Favorite> AllFavorites()
    {
        var list = ParseFavorites(S.FavoriteTeams);
        if (S.SportsUseFullcourt)
            list.AddRange(FullcourtLink.Favorites.Select(f => new Favorite(f.League, f.Abbr)));
        return list;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var next = TimeSpan.FromMinutes(5);
            try
            {
                var favorites = AllFavorites();
                var tracked = S.SportsUseFullcourt ? FullcourtLink.Tracked.ToList() : new List<FullcourtLink.TrackedGame>();
                if (favorites.Count == 0 && tracked.Count == 0 && !S.SportsShowAllLive)
                {
                    next = TimeSpan.FromMinutes(10);
                    await Ui.InvokeAsync(() => Apply(new List<Game>())).ConfigureAwait(false);
                }
                else
                {
                    var games = new List<Game>();
                    var leagues = S.SportsLeagues.Select(l => l.Trim().ToLowerInvariant())
                        .Concat(favorites.Where(f => f.League != null).Select(f => f.League!))
                        .Concat(tracked.Select(t => t.League))
                        .Distinct();
                    foreach (var league in leagues)
                    {
                        if (!LeaguePaths.TryGetValue(league, out var path)) continue;
                        try { games.AddRange(await FetchAsync(league, path, favorites, ct).ConfigureAwait(false)); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { Log.Error("sports " + league, ex); }
                    }

                    await Ui.InvokeAsync(() => Apply(games)).ConfigureAwait(false);

                    var now = DateTime.UtcNow;
                    if (games.Any(g => g.State == "in")) next = TimeSpan.FromSeconds(20);
                    else if (games.Any(g => g.State == "pre" && g.StartUtc - now < TimeSpan.FromMinutes(30))) next = TimeSpan.FromMinutes(1);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("sports loop", ex);
                next = TimeSpan.FromMinutes(2);
            }

            try
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.WhenAny(Task.Delay(next, ct), _wake.Task).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { return; }
        }
    }

    static List<Favorite> ParseFavorites(IEnumerable<string> entries)
    {
        var list = new List<Favorite>();
        foreach (var raw in entries)
        {
            var e = raw.Trim();
            if (e.Length == 0) continue;
            int colon = e.IndexOf(':');
            list.Add(colon > 0
                ? new Favorite(e[..colon].Trim().ToLowerInvariant(), e[(colon + 1)..].Trim())
                : new Favorite(null, e));
        }
        return list;
    }

    async Task<List<Game>> FetchAsync(string league, string path, List<Favorite> favorites, CancellationToken ct)
    {
        // College scoreboards only list featured games unless asked for every Division I game.
        var query = path.Contains("college") ? "?groups=50&limit=400" : "";
        var json = await Http.GetStringAsync(
            $"https://site.api.espn.com/apis/site/v2/sports/{path}/scoreboard{query}", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        var result = new List<Game>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return result;

        string sport = path[..path.IndexOf('/')];
        foreach (var ev in events.EnumerateArray())
        {
            var id = Str(ev, "id");
            if (id == null) continue;
            var type = Prop(Prop(ev, "status"), "type");
            var state = Str(type, "state") ?? "pre";
            var detail = Str(type, "shortDetail") ?? Str(type, "detail") ?? "";

            var comp = First(Prop(ev, "competitions"));
            var competitors = Prop(comp, "competitors");
            if (competitors is not { ValueKind: JsonValueKind.Array } arr) continue;

            JsonElement? away = null, home = null;
            foreach (var c in arr.EnumerateArray())
            {
                if (Str(c, "homeAway") == "home") home = c; else away = c;
            }
            if (away == null || home == null) continue;

            string awayAbbr = Str(Prop(away, "team"), "abbreviation") ?? "AWAY";
            string homeAbbr = Str(Prop(home, "team"), "abbreviation") ?? "HOME";

            var fav = favorites.FirstOrDefault(f =>
                (f.League == null || f.League == league) &&
                (f.Abbr.Equals(awayAbbr, StringComparison.OrdinalIgnoreCase) || f.Abbr.Equals(homeAbbr, StringComparison.OrdinalIgnoreCase)));
            bool isTracked = S.SportsUseFullcourt && FullcourtLink.IsTracked(league, id);
            bool relevant = fav != null || isTracked || (S.SportsShowAllLive && state == "in");
            if (!relevant) continue;

            var favSide = fav != null && fav.Abbr.Equals(awayAbbr, StringComparison.OrdinalIgnoreCase) ? away : home;
            Color? accent = ThemeManager.TryParseColor("#" + (Str(Prop(favSide, "team"), "color") ?? ""), out var col) ? Brighten(col) : null;

            DateTime start = DateTime.TryParse(Str(ev, "date"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.UtcNow;

            string? link = null;
            if (Prop(ev, "links") is { ValueKind: JsonValueKind.Array } links)
                link = links.EnumerateArray().Select(l => Str(l, "href")).FirstOrDefault(h => h != null);

            result.Add(new Game(id, league, sport, state, detail, awayAbbr, homeAbbr,
                Str(away, "score") ?? "0", Str(home, "score") ?? "0", accent, link, start, isTracked));
        }
        return result;
    }

    void Apply(List<Game> games)
    {
        var keep = new HashSet<string>();
        var now = DateTime.UtcNow;

        foreach (var g in games)
        {
            var id = "sports:" + g.League + ":" + g.Id;
            if (!_seen.TryGetValue(id, out var seen)) _seen[id] = seen = new Seen();
            var score = $"{g.AwayScore}–{g.HomeScore}";

            if (g.State == "in")
            {
                keep.Add(id);
                var a = _hub.GetOrAdd(id, ActivityKind.Sports, x => Init(x, g));
                a.Title = $"{g.Away} {g.AwayScore}  –  {g.HomeScore} {g.Home}";
                a.Subtitle = g.Detail;
                a.Trailing = score;
                a.Badge = null;
                a.IsLive = true;
                if (a.Priority != 60) { a.Priority = 60; _hub.Reorder(); }

                bool changed = seen.Score.Length > 0 && seen.Score != score;
                bool newlyLive = !seen.WasLive;
                seen.Score = score;
                seen.WasLive = true;
                if (newlyLive || (changed && S.SportsPeekOnScore && g.Sport != "basketball")) _hub.Peek(a);
            }
            else if (g.State == "post")
            {
                if (seen.FinalShown || !(seen.WasLive || g.Tracked)) continue; // only finals we watched or tracked
                seen.FinalShown = true;
                keep.Add(id);
                var a = _hub.GetOrAdd(id, ActivityKind.Sports, x => Init(x, g));
                a.Title = $"{g.Away} {g.AwayScore}  –  {g.HomeScore} {g.Home}";
                a.Subtitle = g.Detail.Length > 0 ? g.Detail : "Final";
                a.Trailing = "Final";
                a.IsLive = false;
                a.Priority = 25;
                a.SetTtl(TimeSpan.FromMinutes(15));
                _hub.Reorder();
                _hub.Peek(a);
            }
            else if (g.State == "pre" && (g.Tracked || (g.StartUtc - now < TimeSpan.FromMinutes(15) && g.StartUtc - now > TimeSpan.FromMinutes(-30))))
            {
                keep.Add(id);
                var a = _hub.GetOrAdd(id, ActivityKind.Sports, x => Init(x, g));
                bool soon = g.StartUtc - now < TimeSpan.FromMinutes(15);
                a.Title = $"{g.Away} @ {g.Home}";
                a.Subtitle = (soon ? "Starting soon · " : g.Tracked ? "Tracking · " : "") + g.Detail;
                a.Trailing = g.StartUtc.ToLocalTime().ToString("h:mm");
                a.Priority = 10;
            }

            if (_hub.Get(id) is Activity card) Decorate(card, g);
        }

        // Tracked games that are over (and showed their final) can go.
        var finals = games.Where(g => g.State == "post").Select(g => (g.League, g.Id)).ToHashSet();
        FullcourtLink.PruneTracked(t => finals.Contains((t.League, t.Game)));

        // Drop live/pre-game cards that no longer apply; finals expire on their own.
        _hub.RemoveWhere(a => a.Kind == ActivityKind.Sports && !keep.Contains(a.Id) && a.ExpiresAt == null);
        foreach (var stale in _seen.Keys.Where(k => !games.Any(g => k == "sports:" + g.League + ":" + g.Id)).ToList())
            _seen.Remove(stale);
    }

    void Init(Activity a, Game g)
    {
        a.Glyph = Glyphs.Sports;
        a.Accent = g.Accent;
    }

    /// <summary>Source line, click target and buttons — refreshed every poll, buttons only when they change.</summary>
    void Decorate(Activity a, Game g)
    {
        a.Source = g.League.ToUpperInvariant() + (g.Tracked ? " · tracking from Fullcourt" : "");
        var open = FullcourtLink.OpenerFor(g.League, g.Id, g.Link);
        a.OnOpen = open;

        var label = FullcourtLink.OpenLabel(g.League);
        var key = $"{label}|{g.Tracked}|{open != null}|{g.Link != null}";
        if (_actionKeys.TryGetValue(a.Id, out var old) && old == key && a.HasActions) return;
        _actionKeys[a.Id] = key;

        var actions = new List<IslandAction>();
        if (open != null) actions.Add(new IslandAction(Glyphs.Open, label, open, primary: true));
        if (label == "Fullcourt" && g.Link != null) { var espn = g.Link; actions.Add(new IslandAction("", "ESPN", () => Ui.OpenUrl(espn))); }
        if (g.Tracked)
        {
            var (lg, id, cardId) = (g.League, g.Id, a.Id);
            actions.Add(new IslandAction(Glyphs.Close, "Stop tracking", () => { FullcourtLink.Track(lg, id, false); _hub.Remove(cardId); }));
        }
        a.Actions = actions;
    }

    static Color Brighten(Color c)
    {
        // Team colors are often navy/black; lift them so they read on a dark pill.
        double lum = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255;
        if (lum >= 0.45 || ThemeManager.IsLight) return c;
        double k = 0.45 / Math.Max(lum, 0.05);
        byte F(byte v) => (byte)Math.Min(255, v * k + 40);
        return Color.FromRgb(F(c.R), F(c.G), F(c.B));
    }

    static JsonElement? Prop(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v : null;

    static JsonElement? First(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? a[0] : null;

    static string? Str(JsonElement? e, string name)
    {
        var v = Prop(e, name);
        return v?.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.GetRawText(),
            _ => null,
        };
    }

    public void Dispose()
    {
        FullcourtLink.Changed -= Wake;
        _wake.TrySetResult();
        _cts.Cancel();
        _hub.RemoveWhere(a => a.Kind == ActivityKind.Sports);
    }
}
