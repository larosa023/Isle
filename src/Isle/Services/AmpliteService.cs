using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Isle.Core;

namespace Isle.Services;

/// <summary>
/// Amplite (larosa023/Amplite) integration through its local API on 127.0.0.1:43821 — the same one
/// Mantel and the Stream Deck plugin use. Shows the song with cover and live progress, and controls
/// play/pause, skip, like and EQ presets. Nothing to set up in Amplite.
/// </summary>
public sealed class AmpliteService : IModule
{
    const string Base = "http://127.0.0.1:43821";
    public const string ActivityId = "amplite";
    public const string AppKey = "media:amplite";

    sealed record Preset(string Id, string Name);

    sealed record NowState(
        bool Playing, string? Uri, string Title, string Artist, string Album, string? Image,
        double DurationMs, double PositionMs, long At, string? Accent,
        bool EqAvailable, bool EqOn, string EqPreset, List<Preset> Presets, string? Version);

    /// <summary>Amplite is running and answering.</summary>
    public static bool IsConnected { get; private set; }

    /// <summary>Amplite has a song loaded (playing or paused).</summary>
    public static bool HasTrack { get; private set; }

    public static bool IsPlaying { get; private set; }
    public static string? Version { get; private set; }

    /// <summary>Connection or playback state changed.</summary>
    public static event Action? StateChanged;

    static AppSettings S => AppSettings.Current;

    readonly ActivityHub _hub;
    readonly CancellationTokenSource _cts = new();
    NowState? _now;
    DateTime _receivedAt;
    string? _imageUrl;
    string _actionsKey = "";
    System.Windows.Threading.DispatcherTimer? _tick;

    public AmpliteService(ActivityHub hub) => _hub = hub;

    public void Start()
    {
        AppRegistry.Seen(AppKey, "Amplite", "media");
        _tick = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _tick.Tick += (_, _) => RenderProgress();
        _tick.Start();
        _ = LoopAsync(_cts.Token);
    }

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NowState? state = null;
            bool reachable;
            try
            {
                state = await FetchAsync(ct).ConfigureAwait(false);
                reachable = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                reachable = false; // Amplite isn't running
            }

            await Ui.InvokeAsync(() => Apply(reachable, state)).ConfigureAwait(false);

            var delay = !reachable ? TimeSpan.FromSeconds(8)
                : state?.Playing == true ? TimeSpan.FromSeconds(1.5)
                : state?.Uri != null ? TimeSpan.FromSeconds(3)
                : TimeSpan.FromSeconds(5);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    static async Task<NowState?> FetchAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/api/now");
        using var resp = await Http.Local.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var r = doc.RootElement;

        var track = r.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object ? t : (JsonElement?)null;
        var presets = new List<Preset>();
        bool eqAvailable = false, eqOn = false;
        string eqPreset = "custom";
        if (r.TryGetProperty("eq", out var eq) && eq.ValueKind == JsonValueKind.Object)
        {
            eqAvailable = Bool(eq, "available");
            eqOn = Bool(eq, "on");
            eqPreset = Str(eq, "preset") ?? "custom";
            if (eq.TryGetProperty("presets", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                    if (Str(p, "id") is string id) presets.Add(new Preset(id, Str(p, "name") ?? id));
        }

        return new NowState(
            Playing: Bool(r, "playing"),
            Uri: track is JsonElement tr ? Str(tr, "uri") : null,
            Title: track is JsonElement t1 ? Str(t1, "title") ?? "" : "",
            Artist: track is JsonElement t2 ? Str(t2, "artist") ?? "" : "",
            Album: track is JsonElement t3 ? Str(t3, "album") ?? "" : "",
            Image: track is JsonElement t4 ? Str(t4, "image") : null,
            DurationMs: track is JsonElement t5 ? Num(t5, "duration") : 0,
            PositionMs: Num(r, "position"),
            At: (long)Num(r, "at"),
            Accent: Str(r, "accent"),
            EqAvailable: eqAvailable, EqOn: eqOn, EqPreset: eqPreset, Presets: presets,
            Version: Str(r, "version"));
    }

    void Apply(bool reachable, NowState? state)
    {
        bool wasConnected = IsConnected, hadTrack = HasTrack, wasPlaying = IsPlaying;
        IsConnected = reachable && state != null;
        Version = state?.Version;
        HasTrack = IsConnected && state!.Uri != null && state.Title.Length > 0;
        IsPlaying = HasTrack && state!.Playing;

        if (!HasTrack)
        {
            _now = null;
            _imageUrl = null;
            _hub.Remove(ActivityId);
        }
        else
        {
            var s = state!;
            bool newTrack = _now?.Uri != s.Uri;
            _now = s;
            _receivedAt = DateTime.UtcNow;
            Render(s, newTrack);
        }

        if (wasConnected != IsConnected || hadTrack != HasTrack || wasPlaying != IsPlaying) StateChanged?.Invoke();
    }

    void Render(NowState s, bool newTrack)
    {
        var a = _hub.GetOrAdd(ActivityId, ActivityKind.Media, x =>
        {
            x.Glyph = Glyphs.Music;
            x.AppKey = AppKey;
            x.OnScroll = d => Command(d > 0 ? "vol:+4" : "vol:-4");
            x.Seek = SeekTo;
        });

        a.Title = s.Title;
        a.Subtitle = s.Artist.Length > 0 ? s.Artist : s.Album;
        var presetName = s.Presets.Find(p => p.Id == s.EqPreset)?.Name ?? "Custom";
        a.Source = S.AmpliteShowEqInTitle && s.EqAvailable && s.EqOn ? $"Amplite · EQ {presetName}" : "Amplite";
        a.IsPlaying = s.Playing;
        int priority = s.Playing ? 52 : 16; // just above other players
        if (a.Priority != priority)
        {
            a.Priority = priority;
            _hub.Reorder();
        }

        if (!ThemeManager.TryParseColor(s.Accent, out var amp)) amp = Color.FromRgb(0x1D, 0xB9, 0x54);
        if (a.Image == null) a.Accent = amp;

        if (s.Image != _imageUrl)
        {
            _imageUrl = s.Image;
            _ = LoadCoverAsync(a, s.Image, amp);
        }

        BuildActions(a, s);
        RenderProgress();

        if (newTrack && s.Playing && S.MediaPeekOnTrackChange) _hub.Peek(a);
    }

    async Task LoadCoverAsync(Activity a, string? url, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            a.Image = null;
            a.Accent = fallback;
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var bytes = await Http.Client.GetByteArrayAsync(url, cts.Token);
            var (img, color) = await Task.Run(() =>
            {
                var bmp = Images.Decode(bytes);
                return (bmp, bmp == null ? null : Images.Dominant(bmp));
            });
            if (_imageUrl != url) return; // song changed meanwhile
            a.Image = img;
            a.Accent = color ?? fallback;
        }
        catch (Exception ex)
        {
            Log.Error("amplite cover", ex);
        }
    }

    void BuildActions(Activity a, NowState s)
    {
        var key = $"{s.Playing}|{S.AmpliteShowLike}|{S.AmpliteShowEq}|{s.EqAvailable}|{s.EqOn}|{s.EqPreset}|{s.Presets.Count}";
        if (key == _actionsKey && a.HasActions) return;
        _actionsKey = key;

        var list = new List<IslandAction>();
        if (S.AmpliteShowLike) list.Add(new IslandAction("", null, () => Command("like")));
        list.Add(new IslandAction(Glyphs.Previous, null, () => Command("prev")));
        list.Add(new IslandAction(s.Playing ? Glyphs.Pause : Glyphs.Play, null, () => Command("toggle"), primary: true));
        list.Add(new IslandAction(Glyphs.Next, null, () => Command("next")));

        if (S.AmpliteShowEq && s.EqAvailable)
        {
            var menu = new List<IslandMenuItem> { new("Equalizer off", !s.EqOn, () => Command("eq:off")) };
            foreach (var p in s.Presets)
            {
                var id = p.Id;
                menu.Add(new IslandMenuItem(p.Name, s.EqOn && s.EqPreset == id, () => Command("eq:" + id)));
            }
            list.Add(new IslandAction("", "EQ", menu));
        }
        a.Actions = list;
    }

    void SeekTo(double fraction)
    {
        var s = _now;
        if (s == null || s.DurationMs < 1000) return;
        double pos = s.PositionMs + (s.Playing ? (DateTime.UtcNow - _receivedAt).TotalMilliseconds : 0);
        long delta = (long)Math.Round(Math.Clamp(fraction, 0, 1) * s.DurationMs - pos);
        if (Math.Abs(delta) < 500) return;
        delta = Math.Clamp(delta, -9_999_999, 9_999_999);
        Command(delta >= 0 ? $"seek:+{delta}" : $"seek:{delta}");
        // reflect it immediately; the next poll confirms
        _now = s with { PositionMs = s.PositionMs + delta };
        RenderProgress();
    }

    void RenderProgress()
    {
        var s = _now;
        var a = _hub.Get(ActivityId);
        if (s == null || a == null) return;
        if (s.DurationMs < 1000) { a.Progress = null; return; }

        double pos = s.PositionMs;
        if (s.Playing) pos += (DateTime.UtcNow - _receivedAt).TotalMilliseconds;
        pos = Math.Clamp(pos, 0, s.DurationMs);
        a.Progress = pos / s.DurationMs;
        a.ProgressLeft = Fmt.Clock(TimeSpan.FromMilliseconds(pos));
        a.ProgressRight = "-" + Fmt.Clock(TimeSpan.FromMilliseconds(s.DurationMs - pos));
    }

    /// <summary>Sends a command: toggle, next, prev, like, vol:+10, seek:-10000, eq:on|off|&lt;preset&gt;.</summary>
    public static async void Command(string cmd)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/api/cmd")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { cmd }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-amplite", "1");
            using var resp = await Http.Local.SendAsync(req);
            if (!resp.IsSuccessStatusCode) Log.Info($"amplite command {cmd}: {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Info($"amplite command {cmd} failed: {ex.Message}");
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    public void Dispose()
    {
        _cts.Cancel();
        _tick?.Stop();
        _hub.Remove(ActivityId);
        bool changed = IsConnected || HasTrack;
        IsConnected = HasTrack = IsPlaying = false;
        if (changed) StateChanged?.Invoke();
    }
}
