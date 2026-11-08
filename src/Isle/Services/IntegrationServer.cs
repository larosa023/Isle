using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Isle.Core;

namespace Isle.Services;

/// <summary>
/// A tiny HTTP API on 127.0.0.1 so your own apps (Amplite, Savor, Fullcourt, scripts) can put
/// live activities on the island. Desktop callers are always allowed; browser callers must come
/// from an origin listed in settings (or a localhost dev server). See docs/integration.md.
/// </summary>
public sealed class IntegrationServer : IModule
{
    const int MaxBody = 512 * 1024;
    const int MaxExternal = 32;

    public sealed class ActionDto
    {
        public string? Label { get; set; }
        public string? Glyph { get; set; }
        public string? Url { get; set; }
        public string? Callback { get; set; }
        public bool? Dismiss { get; set; }
        public bool? Primary { get; set; }
    }

    public sealed class ActivityDto
    {
        public string? App { get; set; }
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public string? Glyph { get; set; }
        public string? Image { get; set; }
        public string? Accent { get; set; }
        public double? Progress { get; set; }
        public string? ProgressLeft { get; set; }
        public string? ProgressRight { get; set; }
        public string? Trailing { get; set; }
        public string? Badge { get; set; }
        public bool? Live { get; set; }
        public int? Priority { get; set; }
        public double? Ttl { get; set; }
        public bool? Peek { get; set; }
        public string? Url { get; set; }
        public List<ActionDto>? Actions { get; set; }
    }

    public sealed class NotifyDto
    {
        public string? App { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? Glyph { get; set; }
        public string? Image { get; set; }
        public string? Accent { get; set; }
        public string? Url { get; set; }
        public double? Seconds { get; set; }
    }

    public sealed class FullcourtFavoriteDto
    {
        public string? Id { get; set; }
        public string? Abbr { get; set; }
        public string? Name { get; set; }
    }

    public sealed class FullcourtSyncDto
    {
        public string? League { get; set; }
        public FullcourtFavoriteDto? Favorite { get; set; }
        public string? Origin { get; set; }
    }

    public sealed class FullcourtTrackDto
    {
        public string? League { get; set; }
        public string? Game { get; set; }
        public bool? On { get; set; }
    }

    public sealed class TimerDto
    {
        public double? Seconds { get; set; }
        public double? Minutes { get; set; }
        public string? Label { get; set; }
    }

    sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)
    {
        public string? Origin => Headers.TryGetValue("origin", out var o) ? o : null;
    }

    sealed record Response(int Status, object? Body);

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    readonly ActivityHub _hub;
    static AppSettings S => AppSettings.Current;
    readonly TimerService _timers;
    readonly CancellationTokenSource _cts = new();
    TcpListener? _listener;

    public IntegrationServer(ActivityHub hub, AppSettings settings, TimerService timers)
    {
        _hub = hub;
                _timers = timers;
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, S.ApiPort);
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
            Log.Info($"local API listening on http://127.0.0.1:{S.ApiPort}");
        }
        catch (Exception ex)
        {
            Log.Error($"local API could not bind port {S.ApiPort}", ex);
        }
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client), ct);
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();

                var req = await ReadAsync(stream, cts.Token).ConfigureAwait(false);
                if (req == null) return;

                var origin = req.Origin;
                bool fromBrowser = origin != null;
                if (fromBrowser && !OriginAllowed(origin!))
                {
                    await WriteAsync(stream, new Response(403, new { error = "origin not allowed — add it in Isle settings" }), null, cts.Token).ConfigureAwait(false);
                    return;
                }

                Response resp;
                try { resp = await RouteAsync(req, fromBrowser).ConfigureAwait(false); }
                catch (JsonException ex) { resp = new Response(400, new { error = "invalid JSON: " + ex.Message }); }

                await WriteAsync(stream, resp, origin, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // client went away
            }
            catch (Exception ex)
            {
                Log.Error("local API", ex);
            }
        }
    }

    bool OriginAllowed(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        if (u.IsLoopback) return true; // local dev servers
        var norm = origin.TrimEnd('/');
        if (FullcourtLink.Origins.Any(o => string.Equals(o, norm, StringComparison.OrdinalIgnoreCase))) return true;
        return S.AllowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), norm, StringComparison.OrdinalIgnoreCase));
    }

    async Task<Response> RouteAsync(Request req, bool fromBrowser)
    {
        var path = req.Path.TrimEnd('/');
        var method = req.Method;

        if (method == "OPTIONS") return new Response(204, null);

        if (method == "GET" && (path == "" || path == "/v1/status"))
        {
            int count = await Ui.InvokeAsync(() => _hub.Items.Count).ConfigureAwait(false);
            return new Response(200, new { name = "Isle", version = AppInfo.VersionString, activities = count });
        }

        if (method == "GET" && path == "/v1/activities")
        {
            var list = await Ui.InvokeAsync(() => _hub.Items
                .Select(a => new { id = a.Id, kind = a.Kind.ToString().ToLowerInvariant(), source = a.Source, title = a.Title, subtitle = a.Subtitle })
                .ToList()).ConfigureAwait(false);
            return new Response(200, list);
        }

        if (method == "POST" && path == "/v1/activities")
        {
            var dto = JsonSerializer.Deserialize<ActivityDto>(req.Body, Json);
            if (dto == null || string.IsNullOrWhiteSpace(dto.App) || string.IsNullOrWhiteSpace(dto.Title))
                return new Response(400, new { error = "app and title are required" });

            var image = await LoadImageAsync(dto.Image, fromBrowser).ConfigureAwait(false);
            var id = await Ui.InvokeAsync(() => Upsert(dto, image, fromBrowser)).ConfigureAwait(false);
            return id == null
                ? new Response(429, new { error = $"too many activities (max {MaxExternal})" })
                : new Response(200, new { id });
        }

        var del = Regex.Match(path, "^/v1/activities/([^/]+)/([^/]+)$");
        if (method == "DELETE" && del.Success)
        {
            var id = ExternalId(Uri.UnescapeDataString(del.Groups[1].Value), Uri.UnescapeDataString(del.Groups[2].Value));
            await Ui.InvokeAsync(() => _hub.Remove(id)).ConfigureAwait(false);
            return new Response(200, new { removed = id });
        }

        if (method == "POST" && path == "/v1/notify")
        {
            var dto = JsonSerializer.Deserialize<NotifyDto>(req.Body, Json);
            if (dto == null || string.IsNullOrWhiteSpace(dto.App) || string.IsNullOrWhiteSpace(dto.Title))
                return new Response(400, new { error = "app and title are required" });
            var image = await LoadImageAsync(dto.Image, fromBrowser).ConfigureAwait(false);
            await Ui.InvokeAsync(() => Notify(dto, image, fromBrowser)).ConfigureAwait(false);
            return new Response(200, new { ok = true });
        }

        // Fullcourt (site or Windows app) tells Isle your favorite team and which games to track.
        if (method == "POST" && path == "/v1/fullcourt/sync")
        {
            var dto = JsonSerializer.Deserialize<FullcourtSyncDto>(req.Body, Json);
            if (dto?.League == null) return new Response(400, new { error = "league is required" });
            await Ui.InvokeAsync(() =>
            {
                AppRegistry.Seen("api:fullcourt", "Fullcourt", "api");
                FullcourtLink.SyncFavorite(dto.League, dto.Favorite?.Abbr, dto.Favorite?.Name, dto.Origin ?? req.Origin ?? "app");
            }).ConfigureAwait(false);
            return new Response(200, new { ok = true, isle = AppInfo.VersionString, following = AppSettings.Current.SportsUseFullcourt });
        }

        if (method == "POST" && path == "/v1/fullcourt/track")
        {
            var dto = JsonSerializer.Deserialize<FullcourtTrackDto>(req.Body, Json);
            if (dto?.League == null || string.IsNullOrWhiteSpace(dto.Game)) return new Response(400, new { error = "league and game are required" });
            bool on = dto.On != false;
            await Ui.InvokeAsync(() => FullcourtLink.Track(dto.League, dto.Game!, on)).ConfigureAwait(false);
            return new Response(200, new { ok = true, tracking = on, sportsOn = AppSettings.Current.Kind("sports").Enabled && AppSettings.Current.SportsUseFullcourt });
        }

        if (method == "GET" && path == "/v1/fullcourt/track")
        {
            var list = await Ui.InvokeAsync(() => FullcourtLink.Tracked.Select(t => new { league = t.League, game = t.Game }).ToList()).ConfigureAwait(false);
            return new Response(200, list);
        }

        // Same commands as the command line: {"cmd": "--timer 5m --label Tea"} or {"cmd": "toggle-hide"}
        if (method == "POST" && path == "/v1/command")
        {
            using var doc = JsonDocument.Parse(req.Body.Length == 0 ? "{}"u8.ToArray() : req.Body);
            var cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
            if (fromBrowser && cmd.Contains("quit", StringComparison.OrdinalIgnoreCase))
                return new Response(403, new { error = "websites can't quit Isle" });
            var args = SplitArgs(cmd);
            if (args.Count == 0) return new Response(400, new { error = "cmd is required" });
            if (!args[0].StartsWith("--")) args[0] = "--" + args[0];
            var result = await Ui.InvokeAsync(() => App.Instance.HandleCommand(args)).ConfigureAwait(false);
            return result == "ok" ? new Response(200, new { ok = true }) : new Response(400, new { error = result });
        }

        if (method == "POST" && path == "/v1/timers")
        {
            var dto = JsonSerializer.Deserialize<TimerDto>(req.Body, Json) ?? new TimerDto();
            var seconds = dto.Seconds ?? (dto.Minutes ?? 0) * 60;
            if (seconds <= 0) return new Response(400, new { error = "seconds or minutes is required" });
            var id = await Ui.InvokeAsync(() => _timers.Start(TimeSpan.FromSeconds(seconds), dto.Label)).ConfigureAwait(false);
            return new Response(200, new { id });
        }

        return new Response(404, new { error = "not found" });
    }

    /// <summary>Splits a command line, keeping "quoted words" together.</summary>
    static List<string> SplitArgs(string text)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(text, "\"([^\"]*)\"|(\\S+)"))
            list.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return list;
    }

    static string Slug(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9._-]+", "-");
    static string ExternalId(string app, string id) => $"ext:{Slug(app)}:{Slug(id)}";

    string? Upsert(ActivityDto dto, ImageSource? image, bool fromBrowser)
    {
        var app = dto.App!.Trim();
        var id = ExternalId(app, dto.Id ?? "default");
        var a = _hub.Get(id);
        bool isNew = a == null;
        if (a == null)
        {
            if (_hub.All.Count(x => x.Kind == ActivityKind.Custom) >= MaxExternal) return null;
            a = new Activity(id, ActivityKind.Custom);
        }

        a.AppKey = "api:" + Slug(app);
        AppRegistry.Seen(a.AppKey, app, "api");
        a.Source = app;
        a.Title = dto.Title!;
        a.Subtitle = dto.Subtitle;
        a.Glyph = Glyphs.Parse(dto.Glyph) ?? Glyphs.Star;
        if (image != null) a.Image = image;
        else if (dto.Image == "") a.Image = null;
        a.Accent = ThemeManager.TryParseColor(dto.Accent, out var c) ? c : null;
        a.Progress = dto.Progress is double p ? Math.Clamp(p, 0, 1) : null;
        a.ProgressLeft = dto.ProgressLeft;
        a.ProgressRight = dto.ProgressRight;
        a.Trailing = dto.Trailing;
        a.Badge = dto.Badge;
        a.IsLive = dto.Live == true;
        a.Priority = Math.Clamp(dto.Priority ?? 55, 0, 90); // never outranks a call
        a.ExpiresAt = DateTime.UtcNow + TimeSpan.FromSeconds(dto.Ttl is double t && t > 0 ? Math.Min(t, 86400) : 86400);
        a.OnOpen = dto.Url != null ? () => OpenSafe(dto.Url, fromBrowser) : null;

        var actions = new List<IslandAction>();
        foreach (var ad in (dto.Actions ?? new List<ActionDto>()).Take(4))
        {
            var label = ad.Label;
            var url = ad.Url;
            var callback = ad.Callback;
            bool dismiss = ad.Dismiss == true;
            actions.Add(new IslandAction(Glyphs.Parse(ad.Glyph) ?? "", label, () =>
            {
                if (url != null) OpenSafe(url, fromBrowser);
                if (callback != null) _ = CallbackAsync(callback, app, dto.Id ?? "default", label);
                if (dismiss) _hub.Remove(id);
            }, ad.Primary == true));
        }
        a.Actions = actions;

        if (isNew) _hub.Add(a); else _hub.Reorder();
        if (dto.Peek == true || (isNew && dto.Peek != false)) _hub.Peek(a);
        return id;
    }

    void Notify(NotifyDto dto, ImageSource? image, bool fromBrowser)
    {
        var appKey = "api:" + Slug(dto.App!);
        AppRegistry.Seen(appKey, dto.App!.Trim(), "api");
        // Filed under "From your apps" (not Windows notifications, which are off by default).
        var a = new Activity("ext-notify:" + Guid.NewGuid().ToString("N"), ActivityKind.Custom)
        {
            AppKey = appKey,
            Source = dto.App!.Trim(),
            Title = dto.Title!,
            Subtitle = dto.Body,
            Glyph = Glyphs.Parse(dto.Glyph) ?? Glyphs.Bell,
            Image = image,
            Accent = ThemeManager.TryParseColor(dto.Accent, out var c) ? c : null,
            Priority = 85,
        };
        if (dto.Url != null)
        {
            var url = dto.Url;
            a.OnOpen = () => OpenSafe(url, fromBrowser);
        }
        a.SetTtl(TimeSpan.FromSeconds(Math.Clamp(dto.Seconds ?? 5, 2, 30)));
        _hub.Add(a);
        _hub.Peek(a);
    }

    static void OpenSafe(string url, bool fromBrowser)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
        var scheme = u.Scheme.ToLowerInvariant();
        if (scheme is "file" or "javascript" or "vbscript" or "data" or "ms-msdt" or "search-ms") return;
        if (fromBrowser && scheme is not ("http" or "https")) return; // websites may only open web links
        Ui.OpenUrl(url);
    }

    static async Task CallbackAsync(string callback, string app, string id, string? label)
    {
        // Callbacks may only go back to a process on this PC.
        if (!Uri.TryCreate(callback, UriKind.Absolute, out var u) || !u.IsLoopback || u.Scheme is not ("http" or "https")) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var content = new StringContent(JsonSerializer.Serialize(new { app, id, action = label }), Encoding.UTF8, "application/json");
            using var _ = await Http.Local.PostAsync(u, content, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("API callback " + callback, ex);
        }
    }

    static async Task<ImageSource?> LoadImageAsync(string? src, bool fromBrowser)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        try
        {
            byte[]? bytes = null;
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = src.IndexOf(',');
                if (comma > 0 && src[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                    bytes = Convert.FromBase64String(src[(comma + 1)..]);
            }
            else if (Uri.TryCreate(src, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var resp = await Http.Client.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode && (resp.Content.Headers.ContentLength ?? 0) <= 4_000_000)
                {
                    bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    if (bytes.Length > 4_000_000) bytes = null;
                }
            }
            else if (!fromBrowser && File.Exists(src) && new FileInfo(src).Length <= 4_000_000)
            {
                bytes = await File.ReadAllBytesAsync(src).ConfigureAwait(false);
            }
            return bytes == null ? null : Images.Decode(bytes);
        }
        catch (Exception ex)
        {
            Log.Error("API image", ex);
            return null;
        }
    }

    static async Task<Request?> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        int filled = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length) return null; // headers too large
            int n = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (n == 0) return null;
            filled += n;
            headerEnd = IndexOf(buffer, filled, HeaderTerminator);
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        int length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
        if (length < 0 || length > MaxBody) return null;

        var body = new byte[length];
        int already = Math.Min(filled - (headerEnd + 4), length);
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        while (already < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(already), ct).ConfigureAwait(false);
            if (n == 0) return null;
            already += n;
        }

        var target = first[1];
        int q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        return new Request(first[0].ToUpperInvariant(), path, headers, body);
    }

    static readonly byte[] HeaderTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");

    static int IndexOf(byte[] data, int count, byte[] pattern) =>
        data.AsSpan(0, count).IndexOf(pattern);

    static async Task WriteAsync(NetworkStream stream, Response resp, string? allowedOrigin, CancellationToken ct)
    {
        var body = resp.Body == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(resp.Body, Json);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {resp.Status} {Reason(resp.Status)}\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n");
        if (allowedOrigin != null)
        {
            sb.Append($"Access-Control-Allow-Origin: {allowedOrigin}\r\n");
            sb.Append("Vary: Origin\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            sb.Append("Access-Control-Allow-Private-Network: true\r\n"); // Chrome private network access
            sb.Append("Access-Control-Max-Age: 600\r\n");
        }
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        _ => "Error",
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _hub.RemoveWhere(a => a.Kind == ActivityKind.Custom);
    }
}
