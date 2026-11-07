using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;
using Microsoft.Win32;

namespace Isle.Services;

/// <summary>
/// Detects calls by watching which apps are using the microphone — the same signal Windows uses
/// for its privacy indicator. Works with Teams, Discord, Zoom, Slack, Phone Link, WhatsApp, Meet in
/// a browser, etc. without any integration, and shows a running call timer.
/// </summary>
public sealed class CallService : IModule
{
    const string MicKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    static readonly Color Amber = Color.FromRgb(0xFF, 0x9F, 0x0A);

    // (substring of exe/package name, friendly name, is a calling app)
    static readonly (string match, string name, bool call)[] Known =
    {
        ("ms-teams", "Microsoft Teams", true), ("msteams", "Microsoft Teams", true), ("teams", "Microsoft Teams", true),
        ("discord", "Discord", true), ("zoom", "Zoom", true), ("slack", "Slack", true),
        ("yourphone", "Phone Link", true), ("phoneexperiencehost", "Phone Link", true),
        ("whatsapp", "WhatsApp", true), ("skype", "Skype", true), ("webex", "Webex", true),
        ("ciscocollabhost", "Webex", true), ("telegram", "Telegram", true), ("signal", "Signal", true),
        ("messenger", "Messenger", true), ("facetime", "FaceTime", true), ("ringcentral", "RingCentral", true),
        ("googlemeet", "Google Meet", true),
        ("chrome", "Chrome", false), ("msedge", "Edge", false), ("firefox", "Firefox", false),
        ("brave", "Brave", false), ("opera", "Opera", false), ("obs64", "OBS", false), ("obs", "OBS", false),
    };

    readonly ActivityHub _hub;
    readonly AppSettings _s;
    readonly DispatcherTimer _poll;
    readonly Dictionary<string, DateTime> _active = new(StringComparer.OrdinalIgnoreCase); // name -> started (UTC)

    public CallService(ActivityHub hub, AppSettings settings)
    {
        _hub = hub;
        _s = settings;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _poll.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _poll.Start();
        Poll();
    }

    void Poll()
    {
        var users = new Dictionary<string, (bool call, DateTime started)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(MicKey);
            if (root != null)
            {
                foreach (var name in root.GetSubKeyNames())
                {
                    if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                    {
                        using var desktop = root.OpenSubKey(name);
                        if (desktop == null) continue;
                        foreach (var sub in desktop.GetSubKeyNames())
                            Inspect(desktop, sub, Path.GetFileNameWithoutExtension(sub.Replace('#', '\\')), users);
                    }
                    else
                    {
                        Inspect(root, name, name, users); // packaged (Store) apps
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("calls poll", ex);
            return;
        }

        // Ended
        foreach (var name in _active.Keys.Where(n => !users.ContainsKey(n)).ToList())
        {
            _active.Remove(name);
            _hub.Remove(IdFor(name));
        }

        // Started / ongoing
        foreach (var (name, info) in users)
        {
            var id = IdFor(name);
            if (!_active.ContainsKey(name))
            {
                _active[name] = info.started;
                var a = new Activity(id, ActivityKind.Call)
                {
                    Title = info.call ? "On a call" : "Microphone in use",
                    Source = name,
                    Subtitle = name,
                    Glyph = info.call ? Glyphs.Phone : Glyphs.Mic,
                    Accent = info.call ? Green : Amber,
                    Priority = info.call ? 100 : 30,
                    IsLive = true,
                };
                _hub.Add(a);
                if (info.call) _hub.Peek(a);
            }

            var current = _hub.Get(id);
            if (current == null) continue;
            var elapsed = DateTime.UtcNow - _active[name];
            var text = Fmt.Clock(elapsed);
            current.Trailing = text;
            current.Badge = text;
        }

        _poll.Interval = _active.Count > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2);
    }

    static string IdFor(string name) => "call:" + name.ToLowerInvariant();

    void Inspect(RegistryKey parent, string sub, string ident, Dictionary<string, (bool, DateTime)> users)
    {
        using var key = parent.OpenSubKey(sub);
        if (key == null) return;
        if (key.GetValue("LastUsedTimeStop") is not long stop || stop != 0) return;
        if (key.GetValue("LastUsedTimeStart") is not long start || start <= 0) return;

        var lower = ident.ToLowerInvariant();
        if (lower.Contains("isle")) return;

        var known = Known.FirstOrDefault(k => lower.Contains(k.match));
        bool isCall = known.match != null && known.call;
        if (!isCall && !_s.ShowAnyMicUse) return;

        var name = known.match != null ? known.name : Prettify(ident);
        DateTime started;
        try { started = DateTime.FromFileTimeUtc(start); }
        catch { started = DateTime.UtcNow; }
        if (started > DateTime.UtcNow || DateTime.UtcNow - started > TimeSpan.FromDays(2)) started = DateTime.UtcNow;

        if (users.TryGetValue(name, out var existing))
            users[name] = (existing.Item1 || isCall, existing.Item2 < started ? existing.Item2 : started);
        else
            users[name] = (isCall, started);
    }

    static string Prettify(string ident)
    {
        var name = ident;
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : ident;
    }

    public void Dispose()
    {
        _poll.Stop();
        _hub.RemoveWhere(a => a.Kind == ActivityKind.Call);
        _active.Clear();
    }
}
