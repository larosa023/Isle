using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Isle.Services;

/// <summary>
/// Now playing for any app that talks to Windows' media controls (SMTC): Spotify, Apple Music,
/// browsers, Media Player, and so on. Album art, a live progress bar and prev/play/next.
/// </summary>
public sealed class MediaService : IModule
{
    const string Id = "media";

    readonly ActivityHub _hub;
    static AppSettings S => AppSettings.Current;
    readonly DispatcherTimer _tick;

    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _trackKey;
    int _propsVersion;
    bool _disposed, _playing, _canPrev, _canNext;
    string _actionsKey = "";
    DateTime? _pausedSince;
    TimeSpan _position, _duration;
    DateTimeOffset _updatedAt, _snapshotAt;

    public MediaService(ActivityHub hub, AppSettings settings)
    {
        _hub = hub;
                _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Tick();
    }

    public async void Start()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _mgr.CurrentSessionChanged += OnCurrentSessionChanged;
            _mgr.SessionsChanged += OnSessionsChanged;
            AppRegistry.Changed += OnRulesChanged;
            AmpliteService.StateChanged += OnRulesChanged;
            Attach(Choose());
            _tick.Start();
        }
        catch (Exception ex)
        {
            Log.Error("media start", ex);
        }
    }

    void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager m, CurrentSessionChangedEventArgs e) => Reattach(m);
    void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager m, SessionsChangedEventArgs e) => Reattach(m);

    void Reattach(GlobalSystemMediaTransportControlsSessionManager m) => Ui.Post(Reselect);

    void OnRulesChanged() => Ui.Post(Reselect);

    void Reselect()
    {
        if (_disposed || _mgr == null) return;
        try
        {
            var next = Choose();
            if (next?.SourceAppUserModelId != _session?.SourceAppUserModelId || (next == null) != (_session == null))
                Attach(next);
        }
        catch (Exception ex)
        {
            Log.Error("media reselect", ex);
        }
    }

    static string KeyFor(string? aumid) => "media:" + AppName(aumid).ToLowerInvariant().Replace(' ', '-');

    /// <summary>
    /// Picks which app's media to show: skips apps the user turned off, and Amplite's own sessions
    /// (the Amplite card covers them) — plus Spotify while Amplite is playing, since that's usually
    /// Amplite's fallback player. Prefers whatever is playing.
    /// </summary>
    GlobalSystemMediaTransportControlsSession? Choose()
    {
        if (_mgr == null) return null;
        var sessions = _mgr.GetSessions();
        var current = _mgr.GetCurrentSession();
        var allowed = new List<(GlobalSystemMediaTransportControlsSession s, bool playing)>();

        foreach (var s in sessions)
        {
            string aumid;
            try { aumid = s.SourceAppUserModelId ?? ""; } catch { continue; }
            var id = aumid.ToLowerInvariant();
            bool amplite = id.Contains("amplite");
            if (amplite && S.AmpliteEnabled && AmpliteService.IsConnected) continue;
            if (!amplite) AppRegistry.Seen(KeyFor(aumid), AppName(aumid), "media");
            if (!AppRegistry.IsEnabled(KeyFor(aumid))) continue;
            if (id.Contains("spotify") && S.AmplitePreferOverSpotify && AmpliteService.HasTrack) continue;

            bool playing = false;
            try { playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; } catch { }
            allowed.Add((s, playing));
        }

        string? currentId = null;
        try { currentId = current?.SourceAppUserModelId; } catch { }
        var cur = allowed.Find(x => x.s.SourceAppUserModelId == currentId);
        if (cur.s != null && cur.playing) return cur.s;
        var anyPlaying = allowed.Find(x => x.playing);
        if (anyPlaying.s != null) return anyPlaying.s;
        if (cur.s != null) return cur.s;
        // Keep showing the paused session we already had, if it's still allowed.
        var kept = allowed.Find(x => x.s.SourceAppUserModelId == _session?.SourceAppUserModelId);
        if (kept.s != null) return kept.s;
        return allowed.Count > 0 ? allowed[0].s : null;
    }

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        string? oldApp = _session?.SourceAppUserModelId;
        Detach();
        _session = session;

        if (session == null)
        {
            _hub.Remove(Id);
            _trackKey = null;
            return;
        }

        if (session.SourceAppUserModelId != oldApp) _trackKey = null;
        session.MediaPropertiesChanged += OnPropsChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;

        _ = RefreshPropsAsync();
        RefreshPlayback();
        RefreshTimeline();
    }

    void Detach()
    {
        if (_session == null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnPropsChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch { }
        _session = null;
    }

    bool IsCurrent(GlobalSystemMediaTransportControlsSession s) =>
        _session != null && s.SourceAppUserModelId == _session.SourceAppUserModelId;

    void OnPropsChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        Ui.Post(() => { if (!_disposed && IsCurrent(s)) _ = RefreshPropsAsync(); });

    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        Ui.Post(() => { if (!_disposed && IsCurrent(s)) RefreshPlayback(); });

    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        Ui.Post(() => { if (!_disposed && IsCurrent(s)) RefreshTimeline(); });

    async Task RefreshPropsAsync()
    {
        var session = _session;
        if (session == null) return;
        int version = ++_propsVersion;

        GlobalSystemMediaTransportControlsSessionMediaProperties props;
        try { props = await session.TryGetMediaPropertiesAsync(); }
        catch (Exception ex) { Log.Error("media props", ex); return; }
        if (_disposed || version != _propsVersion || !ReferenceEquals(session, _session)) return;

        var title = props.Title ?? "";
        if (title.Length == 0) return; // apps briefly report empty metadata between tracks
        var artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist : props.Artist;

        var a = _hub.GetOrAdd(Id, ActivityKind.Media, x =>
        {
            x.Glyph = Glyphs.Music;
            x.Priority = 15;
        });
        a.Title = title;
        a.Subtitle = string.IsNullOrWhiteSpace(artist) ? props.AlbumTitle : artist;
        a.Source = AppName(session.SourceAppUserModelId);
        a.AppKey = KeyFor(session.SourceAppUserModelId);
        a.OnOpen = null;

        var key = title + "\n" + artist;
        bool newTrack = key != _trackKey;
        if (newTrack || a.Image == null)
        {
            _trackKey = key;
            var (image, accent) = await LoadArtAsync(props.Thumbnail);
            if (_disposed || version != _propsVersion) return;
            a.Image = image;
            a.Accent = accent;
            if (newTrack && _playing && S.MediaPeekOnTrackChange) _hub.Peek(a);
        }

        ApplyState();
    }

    static async Task<(ImageSource?, Color?)> LoadArtAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb == null) return (null, null);
        try
        {
            byte[] bytes;
            using (var ras = await thumb.OpenReadAsync())
            using (var stream = ras.AsStreamForRead())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            return await Task.Run(() =>
            {
                var bmp = Images.Decode(bytes);
                return bmp == null ? ((ImageSource?)null, (Color?)null) : ((ImageSource?)bmp, Images.Dominant(bmp));
            });
        }
        catch (Exception ex)
        {
            Log.Error("media art", ex);
            return (null, null);
        }
    }

    void RefreshPlayback()
    {
        var session = _session;
        if (session == null) return;

        GlobalSystemMediaTransportControlsSessionPlaybackInfo info;
        try { info = session.GetPlaybackInfo(); }
        catch { return; }

        var status = info.PlaybackStatus;
        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
        {
            _hub.Remove(Id);
            _trackKey = null;
            return;
        }

        _playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        _pausedSince = _playing ? null : _pausedSince ?? DateTime.UtcNow;
        _canPrev = info.Controls.IsPreviousEnabled;
        _canNext = info.Controls.IsNextEnabled;
        // Timeline position stops advancing at the moment of the state change.
        _snapshotAt = DateTimeOffset.Now;

        if (_hub.Get(Id) == null)
        {
            if (_playing) _ = RefreshPropsAsync(); // came back after being hidden while paused
            return;
        }
        ApplyState();
        Tick();
    }

    void ApplyState()
    {
        var a = _hub.Get(Id);
        if (a == null) return;

        a.IsPlaying = _playing;
        int priority = _playing ? 50 : 15;
        if (a.Priority != priority)
        {
            a.Priority = priority;
            _hub.Reorder();
        }

        var key = $"{_playing}{_canPrev}{_canNext}";
        if (key == _actionsKey && a.HasActions) return;
        _actionsKey = key;

        var actions = new List<IslandAction>();
        if (_canPrev) actions.Add(new IslandAction(Glyphs.Previous, null, () => Control(s => s.TrySkipPreviousAsync())));
        actions.Add(new IslandAction(_playing ? Glyphs.Pause : Glyphs.Play, null, () => Control(s => s.TryTogglePlayPauseAsync()), primary: true));
        if (_canNext) actions.Add(new IslandAction(Glyphs.Next, null, () => Control(s => s.TrySkipNextAsync())));
        a.Actions = actions;
    }

    void Control(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command)
    {
        var s = _session;
        if (s == null) return;
        try { _ = command(s); }
        catch (Exception ex) { Log.Error("media control", ex); }
    }

    void RefreshTimeline()
    {
        var session = _session;
        if (session == null) return;
        try
        {
            var t = session.GetTimelineProperties();
            _position = t.Position;
            _duration = t.EndTime - t.StartTime;
            _updatedAt = t.LastUpdatedTime;
            _snapshotAt = DateTimeOffset.Now;
        }
        catch
        {
            return;
        }
        Tick();
    }

    int _ticks;

    void Tick()
    {
        if (++_ticks % 3 == 0) Reselect(); // a background app may have started playing
        var a = _hub.Get(Id);
        if (a == null) return;

        if (!_playing && _pausedSince is DateTime since && S.MediaHidePausedAfterMinutes > 0
            && DateTime.UtcNow - since > TimeSpan.FromMinutes(S.MediaHidePausedAfterMinutes))
        {
            _hub.Remove(Id);
            _trackKey = null;
            return;
        }

        if (_duration.TotalSeconds < 1)
        {
            a.Progress = null;
            return;
        }

        var pos = _position;
        if (_playing)
        {
            var elapsed = DateTimeOffset.Now - _updatedAt;
            if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromHours(6)) elapsed = DateTimeOffset.Now - _snapshotAt;
            pos += elapsed;
        }
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (pos > _duration) pos = _duration;

        a.Progress = pos.TotalSeconds / _duration.TotalSeconds;
        a.ProgressLeft = Fmt.Clock(pos);
        a.ProgressRight = "-" + Fmt.Clock(_duration - pos);
    }

    static string AppName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "Media";
        var id = aumid.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("applemusic")) return "Apple Music";
        if (id.Contains("zunemusic") || id.Contains("mediaplayer")) return "Media Player";
        if (id.Contains("zunevideo")) return "Movies & TV";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("msedge") || id.Contains("microsoftedge")) return "Edge";
        if (id.Contains("firefox") || id == "308046b0af4a39cb") return "Firefox";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("tidal")) return "TIDAL";
        if (id.Contains("amazon")) return "Amazon Music";
        if (id.Contains("youtube")) return "YouTube Music";
        if (id.Contains("amplite")) return "Amplite";

        var name = aumid;
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name;
    }

    public void Dispose()
    {
        _disposed = true;
        _tick.Stop();
        AppRegistry.Changed -= OnRulesChanged;
        AmpliteService.StateChanged -= OnRulesChanged;
        Detach();
        if (_mgr != null)
        {
            try
            {
                _mgr.CurrentSessionChanged -= OnCurrentSessionChanged;
                _mgr.SessionsChanged -= OnSessionsChanged;
            }
            catch { }
        }
        _hub.Remove(Id);
    }
}
