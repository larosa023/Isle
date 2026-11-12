using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Services;

/// <summary>
/// Synced lyrics for one now-playing card, from LRCLIB (lrclib.net — free, no account). The player
/// reports the song with <see cref="Track"/> and the position with <see cref="Clock"/>; the current
/// line goes on the card, changing exactly on time between the player's own updates.
/// Only the song's title, artist, album and length are sent, and only while the option is on.
/// </summary>
public sealed partial class Lyrics : IDisposable
{
    readonly record struct Line(double Time, string Text);

    static AppSettings S => AppSettings.Current;
    const string Note = "♪"; // ♪ — before the first line and in instrumental breaks

    // Shared by every player: title+artist -> lines (empty = looked up, none found).
    static readonly Dictionary<string, Line[]> Cache = new();
    static readonly Queue<string> CacheOrder = new();

    readonly DispatcherTimer _next;
    Activity? _card;
    string? _key;
    string _title = "", _artist = "", _album = "";
    double _duration;
    Line[]? _lines;
    CancellationTokenSource? _load;

    double _position;
    DateTime _clockAt;
    bool _playing, _hasClock;

    public Lyrics()
    {
        _next = new DispatcherTimer(DispatcherPriority.Normal);
        _next.Tick += (_, _) => { _next.Stop(); Show(); };
    }

    /// <summary>The song on this card. Cheap to call repeatedly; only a new song starts a lookup.</summary>
    public void Track(Activity card, string title, string? artist, string? album, double durationSeconds)
    {
        if (!S.Lyrics)
        {
            Clear();
            return;
        }
        if (!ReferenceEquals(card, _card))
        {
            Detach();
            _card = card;
        }

        artist ??= "";
        var key = (title.Trim() + "\n" + artist.Trim()).ToLowerInvariant();
        if (key == _key)
        {
            if (durationSeconds > 0) _duration = durationSeconds;
            Show();
            return;
        }

        _key = key;
        _title = title.Trim();
        _artist = artist.Trim();
        _album = (album ?? "").Trim();
        _duration = durationSeconds;
        _lines = null;
        _hasClock = false;
        Show();

        _load?.Cancel();
        _load = new CancellationTokenSource();
        _ = LoadAsync(key, _load.Token);
    }

    /// <summary>Where the song is now (seconds), and whether it's moving.</summary>
    public void Clock(double positionSeconds, bool playing)
    {
        _position = positionSeconds;
        _clockAt = DateTime.UtcNow;
        _playing = playing;
        _hasClock = true;
        Show();
    }

    async Task LoadAsync(string key, CancellationToken ct)
    {
        try
        {
            // Skipping through songs shouldn't fire a lookup for each one; this also lets the
            // player report the song's length, which makes the match exact.
            await Task.Delay(700, ct);
            Line[]? lines;
            lock (Cache) Cache.TryGetValue(key, out lines);
            if (lines == null)
            {
                lines = await FetchAsync(_title, _artist, _album, _duration, ct) ?? Array.Empty<Line>();
                lock (Cache)
                {
                    Cache[key] = lines;
                    CacheOrder.Enqueue(key);
                    while (CacheOrder.Count > 60) Cache.Remove(CacheOrder.Dequeue());
                }
            }
            if (ct.IsCancellationRequested || key != _key) return;
            _lines = lines;
            Show();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"lyrics: {ex.Message}");
        }
    }

    void Show()
    {
        _next.Stop();
        if (_card == null) return;
        if (!S.Lyrics || _lines == null || _lines.Length == 0 || !_hasClock)
        {
            Put(null);
            return;
        }

        double now = _position + (_playing ? (DateTime.UtcNow - _clockAt).TotalSeconds : 0) + S.LyricsOffsetMs / 1000.0;
        int i = -1;
        int lo = 0, hi = _lines.Length - 1;
        while (lo <= hi) // last line that has started
        {
            int mid = (lo + hi) / 2;
            if (_lines[mid].Time <= now) { i = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        Put(i < 0 || _lines[i].Text.Length == 0 ? Note : _lines[i].Text);

        if (_playing && i + 1 < _lines.Length)
        {
            _next.Interval = TimeSpan.FromSeconds(Math.Clamp(_lines[i + 1].Time - now, 0.02, 60));
            _next.Start();
        }
    }

    void Put(string? line)
    {
        if (_card == null) return;
        _card.Lyric = line;
        _card.CompactText = S.LyricsInCompact && line != null && line != Note ? line : null;
    }

    void Detach()
    {
        if (_card == null) return;
        _card.Lyric = null;
        _card.CompactText = null;
        _card = null;
    }

    /// <summary>Takes the lyrics off the card (song gone, or the option turned off).</summary>
    public void Clear()
    {
        _load?.Cancel();
        _load = null;
        _next.Stop();
        Detach();
        _key = null;
        _lines = null;
        _hasClock = false;
    }

    public void Dispose() => Clear();

    // ───────────────────────────── LRCLIB ─────────────────────────────

    const string Api = "https://lrclib.net/api/";

    static async Task<Line[]?> FetchAsync(string title, string artist, string album, double duration, CancellationToken ct)
    {
        if (title.Length == 0) return null;

        // Browser tabs often report "Artist - Song (Official Video)" with the channel as the artist.
        var (cleanTitle, guessedArtist) = Clean(title);

        // 1. Exact match (needs the length).
        if (artist.Length > 0 && duration > 0)
        {
            var url = Api + "get?track_name=" + Esc(title) + "&artist_name=" + Esc(artist) +
                      (album.Length > 0 ? "&album_name=" + Esc(album) : "") +
                      "&duration=" + Math.Round(duration).ToString(CultureInfo.InvariantCulture);
            using var doc = await GetJsonAsync(url, ct);
            if (doc != null && doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True) return Array.Empty<Line>();
                var lines = Synced(doc.RootElement);
                if (lines != null) return lines;
            }
        }

        // 2. Search, then pick the closest length with synced lyrics.
        var queries = new List<string>();
        if (artist.Length > 0) queries.Add("track_name=" + Esc(cleanTitle) + "&artist_name=" + Esc(artist));
        if (guessedArtist != null) queries.Add("track_name=" + Esc(cleanTitle) + "&artist_name=" + Esc(guessedArtist));
        queries.Add("q=" + Esc((guessedArtist ?? artist) + " " + cleanTitle));

        foreach (var q in queries.Distinct())
        {
            using var doc = await GetJsonAsync(Api + "search?" + q, ct);
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            JsonElement? best = null;
            double bestGap = double.MaxValue;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (!r.TryGetProperty("syncedLyrics", out var sl) || sl.ValueKind != JsonValueKind.String) continue;
                double d = r.TryGetProperty("duration", out var dd) && dd.ValueKind == JsonValueKind.Number ? dd.GetDouble() : 0;
                double gap = duration > 0 && d > 0 ? Math.Abs(d - duration) : 0;
                if (duration > 0 && d > 0 && gap > 4) continue; // a different version of the song
                if (gap < bestGap) { bestGap = gap; best = r; }
                if (gap == 0) break;
            }
            if (best is JsonElement b && Synced(b) is Line[] found) return found;
        }
        return null;
    }

    static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Lrclib-Client", $"Isle {AppInfo.VersionString} (https://github.com/larosa023/Isle)");
        using var resp = await Http.Client.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode) return null; // 404 = not in LRCLIB
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
    }

    static Line[]? Synced(JsonElement e)
    {
        if (!e.TryGetProperty("syncedLyrics", out var sl) || sl.ValueKind != JsonValueKind.String) return null;
        var lines = Parse(sl.GetString() ?? "");
        return lines.Length > 0 ? lines : null;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex Stamp();

    /// <summary>"[01:02.34] text" (a line may carry several stamps) → time-ordered lines.</summary>
    static Line[] Parse(string lrc)
    {
        var list = new List<Line>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var stamps = Stamp().Matches(line);
            if (stamps.Count == 0) continue;
            var last = stamps[^1];
            var text = line[(last.Index + last.Length)..].Trim();
            foreach (Match m in stamps)
            {
                double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                list.Add(new Line(min * 60 + sec, text));
            }
        }
        return list.OrderBy(l => l.Time).ToArray();
    }

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(official|video|audio|lyric|visuali[sz]er|remaster|live|hd|4k|explicit|clean)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+-\s+(\d{4}\s+)?remaster(ed)?.*$|\s+(feat\.|ft\.).*$", RegexOptions.IgnoreCase)]
    private static partial Regex Tail();

    static (string title, string? artist) Clean(string title)
    {
        var t = Noise().Replace(title, "");
        t = Tail().Replace(t, "").Trim();
        string? artist = null;
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            artist = t[..dash].Trim();
            t = t[(dash + 3)..].Trim();
        }
        return (t.Length > 0 ? t : title, artist);
    }

    static string Esc(string s) => Uri.EscapeDataString(s);
}
