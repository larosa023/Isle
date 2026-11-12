using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Services;

/// <summary>
/// Discord voice on the island: the channel you're in, who's talking right now (their avatar and
/// name in the pill), and Mute / Deafen buttons that work on Discord itself. Talks to the Discord
/// app on this PC over its local RPC pipe, signed in with the user's own Discord application
/// (Discord only lets an app's owner use voice RPC). Nothing goes through a server except
/// Discord's own sign-in.
/// </summary>
public sealed class DiscordService : IModule
{
    public const string ActivityId = "discord:voice";
    const string AppKey = "mic:discord"; // same entry as the Discord call card on the Apps page
    static readonly string[] Scopes = { "rpc", "rpc.voice.read", "rpc.voice.write" };
    static readonly Color Blurple = Color.FromRgb(0x58, 0x65, 0xF2);
    static readonly Color Talking = Color.FromRgb(0x23, 0xA5, 0x5A);
    static readonly Color Red = Color.FromRgb(0xF2, 0x3F, 0x43);

    static AppSettings S => AppSettings.Current;

    // ── what Settings and the call card see ──

    /// <summary>In a voice channel right now (the plain "On a call" card for Discord steps aside).</summary>
    public static bool InVoice { get; private set; }
    static DateTime _leftAt;

    /// <summary>True while this card covers Discord, and for a moment after leaving, while the mic winds down.</summary>
    public static bool CoversDiscordCall => InVoice || DateTime.UtcNow - _leftAt < TimeSpan.FromSeconds(6);

    public static string Status { get; private set; } = "Off";
    public static bool SignedIn { get; private set; }
    public static event Action? StatusChanged;

    static bool _wantAuthorize;
    static DiscordService? _instance;

    /// <summary>Settings → Discord → Connect: show Discord's "Authorize" prompt on the next connection.</summary>
    public static void RequestSignIn()
    {
        _wantAuthorize = true;
        _instance?.Restart();
    }

    public static void SignOut()
    {
        DiscordTokens.Save(new DiscordTokens { ClientSecret = DiscordTokens.Load().ClientSecret });
        _instance?.Restart();
    }

    // ── state (UI thread) ──

    sealed class Member
    {
        public string Id = "", Name = "";
        public string? Avatar;
        public bool Muted, Deafened;
        public DateTime TalkingSince, StoppedAt;
        public bool Speaking;
    }

    readonly ActivityHub _hub;
    readonly CancellationTokenSource _cts = new();
    readonly DispatcherTimer _clock;
    readonly Dictionary<string, Member> _members = new();
    readonly Dictionary<string, ImageSource?> _avatars = new();
    CancellationTokenSource _wait = new();
    Rpc? _rpc;
    string? _channelId, _channelName, _guildId, _guildName, _guildIcon, _selfId;
    ImageSource? _guildImage;
    DateTime _joinedAt;
    bool _selfMute, _selfDeaf;
    string _actionsKey = "";

    public DiscordService(ActivityHub hub)
    {
        _hub = hub;
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Render();
    }

    public void Start()
    {
        _instance = this;
        AppRegistry.Seen(AppKey, "Discord", "mic");
        _ = LoopAsync(_cts.Token);
    }

    void Kick()
    {
        try { _wait.Cancel(); } catch { }
    }

    void Restart()
    {
        try { _rpc?.Dispose(); } catch { }
        Kick();
    }

    static void SetStatus(string text, bool signedIn)
    {
        if (Status == text && SignedIn == signedIn) return;
        Status = text;
        SignedIn = signedIn;
        Ui.Post(() => StatusChanged?.Invoke());
    }

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan retry = TimeSpan.FromSeconds(15);
            try
            {
                retry = await SessionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Info($"discord: {ex.Message}");
                SetStatus(ex is DiscordError de ? de.Message : "Lost the connection to Discord — retrying.", SignedIn);
            }
            finally
            {
                _rpc?.Dispose();
                _rpc = null;
                await Ui.InvokeAsync(LeaveChannel);
            }

            _wait = new CancellationTokenSource();
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, _wait.Token);
            try { await Task.Delay(retry, both.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // kicked: try now
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One connection to Discord, until it closes. Returns how long to wait before the next try.</summary>
    async Task<TimeSpan> SessionAsync(CancellationToken ct)
    {
        var clientId = S.DiscordClientId.Trim();
        if (clientId.Length == 0 || !clientId.All(char.IsDigit))
        {
            SetStatus("Add your Discord application ID below.", false);
            return TimeSpan.FromMinutes(5);
        }

        var rpc = await Rpc.ConnectAsync(clientId, ct);
        if (rpc == null)
        {
            SetStatus("Discord isn't running.", SignedIn);
            return TimeSpan.FromSeconds(20);
        }
        _rpc = rpc;
        rpc.Event += (evt, data) => Ui.Post(() => OnEvent(evt, data));
        _selfId = rpc.UserId;

        // Sign in: saved token → refreshed token → (only when asked) Discord's Authorize prompt.
        var tokens = DiscordTokens.Load();
        bool ok = false;
        if (tokens.AccessToken.Length > 0)
        {
            if (tokens.ExpiresAt > DateTime.UtcNow.AddMinutes(5)) ok = await TryAuthenticate(rpc, tokens.AccessToken);
            if (!ok && tokens.RefreshToken.Length > 0 && await Exchange(clientId, tokens, refresh: tokens.RefreshToken, code: null, ct) is DiscordTokens fresh)
            {
                tokens = fresh;
                ok = await TryAuthenticate(rpc, tokens.AccessToken);
            }
        }
        if (!ok)
        {
            if (!_wantAuthorize)
            {
                SetStatus("Not signed in. Press Connect, then click Authorize in Discord.", false);
                await rpc.Closed.WaitAsync(ct); // stay connected; Connect starts a new session
                return _wantAuthorize ? TimeSpan.Zero : TimeSpan.FromSeconds(2);
            }
            _wantAuthorize = false;
            if (tokens.ClientSecret.Length == 0) throw new DiscordError("Add your application's client secret below.");
            SetStatus("Waiting for you to click Authorize in Discord…", false);
            var auth = await rpc.CallAsync("AUTHORIZE", new { client_id = clientId, scopes = Scopes }, TimeSpan.FromMinutes(3));
            var code = Str(auth, "code") ?? throw new DiscordError("Discord didn't send a sign-in code.");
            tokens = await Exchange(clientId, tokens, refresh: null, code: code, ct)
                     ?? throw new DiscordError("Discord refused the sign-in. Check the client secret, and that http://localhost is a redirect in your app's OAuth2 settings.");
            if (!await TryAuthenticate(rpc, tokens.AccessToken)) throw new DiscordError("Discord refused the sign-in token.");
        }

        SetStatus($"Connected{(rpc.UserName != null ? " as " + rpc.UserName : "")}.", true);
        await rpc.CallAsync("SUBSCRIBE", new { }, evt: "VOICE_CHANNEL_SELECT");
        try { await rpc.CallAsync("SUBSCRIBE", new { }, evt: "VOICE_SETTINGS_UPDATE"); } catch (DiscordError) { }
        try
        {
            var vs = await rpc.CallAsync("GET_VOICE_SETTINGS", new { });
            await Ui.InvokeAsync(() => { _selfMute = Bool(vs, "mute"); _selfDeaf = Bool(vs, "deaf"); });
        }
        catch (DiscordError) { }

        var current = await rpc.CallAsync("GET_SELECTED_VOICE_CHANNEL", new { });
        if (current.ValueKind == JsonValueKind.Object) Ui.Post(() => { _ = JoinAsync(current); });

        await rpc.Closed.WaitAsync(ct);
        return TimeSpan.FromSeconds(5);
    }

    static async Task<bool> TryAuthenticate(Rpc rpc, string token)
    {
        try
        {
            var r = await rpc.CallAsync("AUTHENTICATE", new { access_token = token });
            if (r.TryGetProperty("user", out var u)) rpc.SetUser(u);
            return true;
        }
        catch (DiscordError)
        {
            return false;
        }
    }

    /// <summary>Trades a sign-in code (or a refresh token) for an access token. Needs the app's client secret.</summary>
    static async Task<DiscordTokens?> Exchange(string clientId, DiscordTokens saved, string? refresh, string? code, CancellationToken ct)
    {
        if (saved.ClientSecret.Length == 0) return null;
        foreach (var redirect in new[] { null, "http://localhost" })
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = saved.ClientSecret,
            };
            if (refresh != null) { form["grant_type"] = "refresh_token"; form["refresh_token"] = refresh; }
            else { form["grant_type"] = "authorization_code"; form["code"] = code!; }
            if (redirect != null) form["redirect_uri"] = redirect;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.Client.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form), cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info($"discord token: {(int)resp.StatusCode} {body}");
                if (redirect == null && body.Contains("redirect", StringComparison.OrdinalIgnoreCase)) continue;
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var tokens = new DiscordTokens
            {
                ClientSecret = saved.ClientSecret,
                AccessToken = Str(r, "access_token") ?? "",
                RefreshToken = Str(r, "refresh_token") ?? saved.RefreshToken,
                ExpiresAt = DateTime.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var e) && e.TryGetDouble(out var s) ? s : 604800),
            };
            if (tokens.AccessToken.Length == 0) return null;
            DiscordTokens.Save(tokens);
            return tokens;
        }
        return null;
    }

    // ───────────────────────────── events (UI thread) ─────────────────────────────

    void OnEvent(string evt, JsonElement data)
    {
        switch (evt)
        {
            case "VOICE_CHANNEL_SELECT":
                var id = Str(data, "channel_id");
                if (id == null) LeaveChannel();
                else if (id != _channelId) _ = JoinByIdAsync(id);
                break;
            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                Upsert(data);
                Render();
                break;
            case "VOICE_STATE_DELETE":
                if (data.TryGetProperty("user", out var u) && Str(u, "id") is string gone)
                {
                    _members.Remove(gone);
                    Render();
                }
                break;
            case "SPEAKING_START":
            case "SPEAKING_STOP":
                if (Str(data, "user_id") is string who && _members.TryGetValue(who, out var m))
                {
                    bool on = evt == "SPEAKING_START";
                    if (on && !m.Speaking) m.TalkingSince = DateTime.UtcNow;
                    if (!on) m.StoppedAt = DateTime.UtcNow;
                    m.Speaking = on;
                    Render();
                }
                break;
            case "VOICE_SETTINGS_UPDATE":
                _selfMute = Bool(data, "mute");
                _selfDeaf = Bool(data, "deaf");
                Render();
                break;
        }
    }

    async Task JoinByIdAsync(string channelId)
    {
        var rpc = _rpc;
        if (rpc == null) return;
        try { await JoinAsync(await rpc.CallAsync("GET_CHANNEL", new { channel_id = channelId })); }
        catch (Exception ex) { Log.Info($"discord channel: {ex.Message}"); }
    }

    async Task JoinAsync(JsonElement channel)
    {
        var rpc = _rpc;
        var id = Str(channel, "id");
        if (rpc == null || id == null) return;
        if (_channelId != null && _channelId != id) LeaveChannel();

        bool fresh = _channelId == null;
        _channelId = id;
        _channelName = Str(channel, "name");
        _guildId = Str(channel, "guild_id");
        if (fresh) _joinedAt = DateTime.UtcNow;
        _members.Clear();
        if (channel.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
            foreach (var st in states.EnumerateArray()) Upsert(st);

        InVoice = true;
        _clock.Start();
        Render(peek: fresh && S.DiscordPeekOnJoin);

        foreach (var evt in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" })
        {
            try { await rpc.CallAsync("SUBSCRIBE", new { channel_id = id }, evt: evt); }
            catch (Exception ex) { Log.Info($"discord subscribe {evt}: {ex.Message}"); }
        }

        if (_guildId != null && _guildId != _guildIconFor)
        {
            try
            {
                var g = await rpc.CallAsync("GET_GUILD", new { guild_id = _guildId });
                _guildName = Str(g, "name");
                _guildIcon = Str(g, "icon_url");
                _guildIconFor = _guildId;
                _guildImage = await LoadImageAsync(_guildIcon);
                Render();
            }
            catch (Exception ex) { Log.Info($"discord guild: {ex.Message}"); }
        }
        else if (_guildId == null)
        {
            _guildName = null;
            _guildImage = null;
            _guildIconFor = null;
        }
    }

    string? _guildIconFor;

    void LeaveChannel()
    {
        var old = _channelId;
        var rpc = _rpc;
        if (old != null && rpc != null)
            foreach (var evt in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP" })
                _ = rpc.CallAsync("UNSUBSCRIBE", new { channel_id = old }, evt: evt).ContinueWith(_ => { }, TaskScheduler.Default);

        _channelId = null;
        _members.Clear();
        _clock.Stop();
        if (InVoice) _leftAt = DateTime.UtcNow;
        InVoice = false;
        _actionsKey = "";
        _hub.Remove(ActivityId);
    }

    void Upsert(JsonElement st)
    {
        if (!st.TryGetProperty("user", out var u) || Str(u, "id") is not string id) return;
        if (!_members.TryGetValue(id, out var m)) _members[id] = m = new Member { Id = id };
        m.Name = Str(st, "nick") is { Length: > 0 } nick ? nick
               : Str(u, "global_name") is { Length: > 0 } g ? g
               : Str(u, "username") ?? "Someone";
        m.Avatar = Str(u, "avatar");
        if (st.TryGetProperty("voice_state", out var vs))
        {
            m.Muted = Bool(vs, "mute") || Bool(vs, "self_mute") || Bool(vs, "suppress");
            m.Deafened = Bool(vs, "deaf") || Bool(vs, "self_deaf");
        }
        if (id == _selfId)
        {
            _selfMute = m.Muted;
            _selfDeaf = m.Deafened;
        }
        if (!_avatars.ContainsKey(id)) _ = LoadAvatarAsync(m);
    }

    async Task LoadAvatarAsync(Member m)
    {
        _avatars[m.Id] = null;
        string url = m.Avatar != null
            ? $"https://cdn.discordapp.com/avatars/{m.Id}/{m.Avatar}.png?size=64"
            : $"https://cdn.discordapp.com/embed/avatars/{(ulong.TryParse(m.Id, out var n) ? (n >> 22) % 6 : 0)}.png";
        var img = await LoadImageAsync(url);
        if (img == null) return;
        _avatars[m.Id] = img;
        Render();
    }

    static async Task<ImageSource?> LoadImageAsync(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var bytes = await Http.Client.GetByteArrayAsync(url, cts.Token);
            return await Task.Run(() => (ImageSource?)Images.Decode(bytes, 64));
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────── the card ─────────────────────────────

    void Render() => Render(false);

    void Render(bool peek)
    {
        if (_channelId == null) return;
        bool isNew = _hub.Get(ActivityId) == null;
        var a = _hub.GetOrAdd(ActivityId, ActivityKind.Call, x =>
        {
            x.AppKey = AppKey;
            x.Source = "Discord";
            x.Glyph = "\uE7F6"; // headphones
            x.IsLive = true;
            x.Priority = 100;
            x.OnOpen = OpenDiscord;
        });

        // Who's talking: speaking now, or stopped a moment ago (people pause between words).
        var now = DateTime.UtcNow;
        var talking = _members.Values
            .Where(m => m.Id != _selfId)
            .Where(m => m.Speaking || now - m.StoppedAt < TimeSpan.FromMilliseconds(900))
            .OrderByDescending(m => m.TalkingSince)
            .ToList();
        bool selfTalking = _selfId != null && _members.TryGetValue(_selfId, out var me) && me.Speaking;

        string place = _channelName is { Length: > 0 } ? _channelName : _members.Count <= 2 ? "Call" : "Group call";
        a.Title = place;
        int others = _members.Count;
        string subtitle = talking.Count switch
        {
            0 => others == 0 ? (_guildName ?? "Voice") : $"{others} {(others == 1 ? "person" : "people")} here{(_guildName != null ? " \u00B7 " + _guildName : "")}",
            1 => $"{talking[0].Name} is talking",
            2 => $"{talking[0].Name} and {talking[1].Name} are talking",
            _ => $"{talking[0].Name} and {talking.Count - 1} others are talking",
        };
        if (_selfDeaf) subtitle += " \u00B7 you're deafened";
        else if (_selfMute) subtitle += " \u00B7 you're muted";
        a.Subtitle = subtitle;

        if (S.DiscordShowSpeaker && talking.Count > 0)
        {
            var lead = talking[0];
            a.CompactText = talking.Count > 1 ? $"{lead.Name} +{talking.Count - 1}" : lead.Name;
            a.Image = _avatars.TryGetValue(lead.Id, out var img) && img != null ? img : _guildImage;
        }
        else
        {
            a.CompactText = null;
            a.Image = _guildImage;
        }

        a.Accent = _selfMute || _selfDeaf ? Red : talking.Count > 0 || selfTalking ? Talking : Blurple;
        var t = Fmt.Clock(now - _joinedAt);
        a.Trailing = t;
        a.Badge = t;
        BuildActions(a);

        if (talking.Any(m => !m.Speaking)) _clock.Interval = TimeSpan.FromMilliseconds(300); // clear "talking" promptly
        else _clock.Interval = TimeSpan.FromSeconds(1);

        if (isNew && peek) _hub.Peek(a);
    }

    void BuildActions(Activity a)
    {
        var key = $"{_selfMute}{_selfDeaf}{SignedIn}";
        if (key == _actionsKey && a.HasActions) return;
        _actionsKey = key;
        a.Actions = new List<IslandAction>
        {
            _selfMute
                ? new IslandAction("\uE720", "Unmute", () => SetVoice(mute: false), primary: true)
                : new IslandAction("\uEC54", "Mute", () => SetVoice(mute: true), primary: true),
            _selfDeaf
                ? new IslandAction("\uE7F6", "Undeafen", () => SetVoice(deaf: false))
                : new IslandAction("\uE74F", "Deafen", () => SetVoice(deaf: true)),
            new IslandAction(Glyphs.Open, null, OpenDiscord),
        };
    }

    async void SetVoice(bool? mute = null, bool? deaf = null)
    {
        var rpc = _rpc;
        if (rpc == null) return;
        try
        {
            object args = mute != null ? new { mute = mute.Value } : new { deaf = deaf!.Value };
            var r = await rpc.CallAsync("SET_VOICE_SETTINGS", args);
            _selfMute = Bool(r, "mute");
            _selfDeaf = Bool(r, "deaf");
            Render();
        }
        catch (Exception ex)
        {
            Log.Info($"discord set voice: {ex.Message}");
        }
    }

    static void OpenDiscord() => Ui.OpenUrl("discord://");

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public void Dispose()
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
        _cts.Cancel();
        try { _rpc?.Dispose(); } catch { }
        LeaveChannel();
        SetStatus("Off", false);
    }

    // ───────────────────────────── Discord's local RPC pipe ─────────────────────────────

    sealed class DiscordError : Exception
    {
        public DiscordError(string message) : base(message) { }
    }

    /// <summary>
    /// \\.\pipe\discord-ipc-N. Each frame: opcode (int32 LE), length (int32 LE), UTF-8 JSON.
    /// Opcodes: 0 handshake, 1 frame, 2 close, 3 ping, 4 pong.
    /// </summary>
    sealed class Rpc : IDisposable
    {
        readonly NamedPipeClientStream _pipe;
        readonly SemaphoreSlim _write = new(1, 1);
        readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string, JsonElement>? Event;
        public Task Closed => _closed.Task;
        public string? UserId { get; private set; }
        public string? UserName { get; private set; }

        Rpc(NamedPipeClientStream pipe) => _pipe = pipe;

        public static async Task<Rpc?> ConnectAsync(string clientId, CancellationToken ct)
        {
            for (int i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(300, ct);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    pipe.Dispose();
                    continue;
                }

                var rpc = new Rpc(pipe);
                try
                {
                    await rpc.WriteAsync(0, new { v = 1, client_id = clientId });
                    var (op, json) = await rpc.ReadAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (op != 1 || Str(root, "evt") != "READY")
                    {
                        var msg = Str(root, "message") ?? (root.TryGetProperty("data", out var d) ? Str(d, "message") : null);
                        rpc.Dispose();
                        throw new DiscordError("Discord refused the connection" + (msg != null ? ": " + msg : ".") + " Check the application ID.");
                    }
                    if (root.TryGetProperty("data", out var data) && data.TryGetProperty("user", out var user)) rpc.SetUser(user);
                    _ = rpc.ReadLoopAsync();
                    return rpc;
                }
                catch (DiscordError)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Log.Info($"discord pipe {i}: {ex.Message}");
                    rpc.Dispose();
                }
            }
            return null;
        }

        public void SetUser(JsonElement user)
        {
            UserId = Str(user, "id") ?? UserId;
            UserName = Str(user, "global_name") is { Length: > 0 } g ? g : Str(user, "username") ?? UserName;
        }

        public async Task<JsonElement> CallAsync(string cmd, object args, TimeSpan? timeout = null, string? evt = null)
        {
            var nonce = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[nonce] = tcs;
            try
            {
                await WriteAsync(1, evt == null ? new { cmd, args, nonce } : (object)new { cmd, args, evt, nonce });
                return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                throw new DiscordError(cmd == "AUTHORIZE" ? "Sign-in timed out — press Connect to try again." : $"Discord didn't answer {cmd}.");
            }
            finally
            {
                lock (_pending) _pending.Remove(nonce);
            }
        }

        public Task CallAsync(string cmd, object args, string evt) => CallAsync(cmd, args, null, evt);

        async Task ReadLoopAsync()
        {
            try
            {
                while (true)
                {
                    var (op, json) = await ReadAsync(CancellationToken.None);
                    if (op == 2) break; // close
                    if (op == 3) { await WriteRawAsync(4, json); continue; } // ping → pong
                    if (op != 1) continue;

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
                    var evt = Str(root, "evt");
                    var nonce = Str(root, "nonce");

                    if (nonce != null)
                    {
                        TaskCompletionSource<JsonElement>? tcs;
                        lock (_pending) _pending.TryGetValue(nonce, out tcs);
                        if (tcs == null) continue;
                        if (evt == "ERROR") tcs.TrySetException(new DiscordError(Str(data, "message") ?? "Discord returned an error."));
                        else tcs.TrySetResult(data);
                    }
                    else if (Str(root, "cmd") == "DISPATCH" && evt != null)
                    {
                        Event?.Invoke(evt, data);
                    }
                }
            }
            catch
            {
                // pipe closed (Discord quit or restarted)
            }
            finally
            {
                lock (_pending)
                    foreach (var p in _pending.Values) p.TrySetException(new DiscordError("Discord closed the connection."));
                _closed.TrySetResult();
            }
        }

        async Task<(int op, string json)> ReadAsync(CancellationToken ct)
        {
            var header = new byte[8];
            await _pipe.ReadExactlyAsync(header, ct);
            int op = BitConverter.ToInt32(header, 0);
            int len = BitConverter.ToInt32(header, 4);
            if (len < 0 || len > 4_000_000) throw new IOException("bad frame");
            var body = new byte[len];
            await _pipe.ReadExactlyAsync(body, ct);
            return (op, Encoding.UTF8.GetString(body));
        }

        Task WriteAsync(int op, object payload) => WriteRawAsync(op, JsonSerializer.Serialize(payload));

        async Task WriteRawAsync(int op, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[8 + body.Length];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), op);
            BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
            body.CopyTo(frame, 8);
            await _write.WaitAsync();
            try
            {
                await _pipe.WriteAsync(frame);
                await _pipe.FlushAsync();
            }
            finally
            {
                _write.Release();
            }
        }

        public void Dispose()
        {
            try { _pipe.Dispose(); } catch { }
            _closed.TrySetResult();
        }
    }
}

/// <summary>
/// The Discord sign-in, in %AppData%\Isle\discord.json — kept out of settings.json so exporting
/// settings never shares it. The secret and tokens are encrypted for this Windows account (DPAPI).
/// </summary>
public sealed class DiscordTokens
{
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAt { get; set; }

    static string File => Path.Combine(Paths.DataDir, "discord.json");

    sealed class Stored
    {
        public string Secret { get; set; } = "";
        public string Access { get; set; } = "";
        public string Refresh { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
    }

    public static DiscordTokens Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new DiscordTokens();
            var s = JsonSerializer.Deserialize<Stored>(System.IO.File.ReadAllText(File)) ?? new Stored();
            return new DiscordTokens
            {
                ClientSecret = Unprotect(s.Secret),
                AccessToken = Unprotect(s.Access),
                RefreshToken = Unprotect(s.Refresh),
                ExpiresAt = s.ExpiresAt,
            };
        }
        catch (Exception ex)
        {
            Log.Error("discord tokens load", ex);
            return new DiscordTokens();
        }
    }

    public static void Save(DiscordTokens t)
    {
        try
        {
            var s = new Stored { Secret = Protect(t.ClientSecret), Access = Protect(t.AccessToken), Refresh = Protect(t.RefreshToken), ExpiresAt = t.ExpiresAt };
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(s));
        }
        catch (Exception ex)
        {
            Log.Error("discord tokens save", ex);
        }
    }

    /// <summary>Saves a new client secret; a different secret means signing in again.</summary>
    public static void SetSecret(string secret)
    {
        var t = Load();
        secret = secret.Trim();
        if (t.ClientSecret == secret) return;
        Save(new DiscordTokens { ClientSecret = secret });
    }

    static string Protect(string plain) =>
        plain.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
